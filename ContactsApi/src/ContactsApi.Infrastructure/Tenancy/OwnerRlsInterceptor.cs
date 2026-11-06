/**
 * @fileoverview Copies the scope's Owner into the Postgres session variable app.owner_id
 * (AD-49 rule 4): on open, before any command whose connection carries another value,
 * again after a rollback, and RESET on close
 * @module ContactsApi.Infrastructure.Tenancy
 */
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ContactsApi.Infrastructure.Tenancy;

/// <summary>
/// Sets <c>app.owner_id</c> for the scope's <see cref="OwnerScope"/> on every connection
/// a DbContext of the scope uses (registered scoped; add it to every DbContext with
/// <c>AddInterceptors(sp.GetRequiredService&lt;OwnerRlsInterceptor&gt;())</c>).
/// <list type="bullet">
/// <item>When a connection opens, before EF issues anything (the scope's Owner, or '').</item>
/// <item>Before every command whose connection does not carry the scope's current Owner
/// (a worker that switches Owner inside one scope).</item>
/// <item>Again after a rollback or a rollback to a savepoint, which revert a
/// <c>set_config</c> made inside the transaction to its previous value; a transaction
/// disposed without a commit counts as rolled back.</item>
/// <item><c>RESET app.owner_id</c> when the connection closes; Npgsql's <c>DISCARD ALL</c>
/// on return to the pool is the second safety net.</item>
/// </list>
/// With no Owner in scope it throws <see cref="OwnerScopeMissingException"/> before the
/// command (<see cref="OwnerRlsMode.Enforce"/>) or logs a warning with the stack and runs
/// it with '' (<see cref="OwnerRlsMode.Shadow"/>), unless the scope declared
/// <see cref="OwnerScope.AllowUnscoped"/>. An Owner change inside an open transaction
/// always throws.
/// </summary>
public sealed class OwnerRlsInterceptor : DbConnectionInterceptor, IDbCommandInterceptor, IDbTransactionInterceptor
{
    /// <summary>The statement that sets the variable (a bound parameter, session level)</summary>
    public const string SetSql = "SELECT set_config('app.owner_id', $1, false)";

    /// <summary>The statement run when a connection closes</summary>
    public const string ResetSql = "RESET app.owner_id";

    /// <summary>The log line the shadow-mode gate counts (AD-49 rule 8 step 2)</summary>
    public const string MissingMessage = "RLS: owner scope missing for a database command";

    private readonly OwnerScope _scope;
    private readonly RowLevelSecurityOptions _options;
    private readonly ILogger<OwnerRlsInterceptor> _logger;
    private readonly Dictionary<DbConnection, Applied> _connections = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<DbContext> _contexts = new(ReferenceEqualityComparer.Instance);

    /// <summary>Creates the interceptor for one DI scope</summary>
    public OwnerRlsInterceptor(OwnerScope scope, RowLevelSecurityOptions options, ILogger<OwnerRlsInterceptor> logger)
    {
        _scope = scope;
        _options = options;
        _logger = logger;
        _scope.AddTransactionProbe(AnyTransactionOpen);
    }

    private bool Active => _options.Mode != OwnerRlsMode.Off;

    // ---- Connections

    /// <inheritdoc />
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Track(eventData.Context);
        if (Active)
        {
            Apply(connection, null, Desired(null, warn: false));
        }
    }

    /// <inheritdoc />
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Track(eventData.Context);
        if (Active)
        {
            await ApplyAsync(connection, null, Desired(null, warn: false), cancellationToken);
        }
    }

    /// <inheritdoc />
    public override InterceptionResult ConnectionClosing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        // Whether or not the connection is still tracked: after a rollback the interceptor
        // forgot it, but the session may still carry the Owner applied before the transaction
        Forget(connection);
        if (Active && connection.State == ConnectionState.Open)
        {
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = ResetSql;
                cmd.ExecuteNonQuery();
            }
            catch (DbException ex)
            {
                // Best effort: DISCARD ALL on return to the pool clears it too
                _logger.LogDebug(ex, "RLS: RESET app.owner_id failed on close");
            }
        }

        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        // Whether or not the connection is still tracked: after a rollback the interceptor
        // forgot it, but the session may still carry the Owner applied before the transaction
        Forget(connection);
        if (Active && connection.State == ConnectionState.Open)
        {
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = ResetSql;
                await cmd.ExecuteNonQueryAsync();
            }
            catch (DbException ex)
            {
                _logger.LogDebug(ex, "RLS: RESET app.owner_id failed on close");
            }
        }

        return result;
    }

    // ---- Commands

    /// <inheritdoc />
    public InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Ensure(command, eventData);
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(command, eventData, cancellationToken);
        return result;
    }

    /// <inheritdoc />
    public InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Ensure(command, eventData);
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(command, eventData, cancellationToken);
        return result;
    }

    /// <inheritdoc />
    public InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Ensure(command, eventData);
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(command, eventData, cancellationToken);
        return result;
    }

    // ---- Transactions

    /// <inheritdoc />
    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Committed(transaction);

    /// <inheritdoc />
    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Committed(transaction);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Invalidate(eventData.Context);

    /// <inheritdoc />
    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Invalidate(eventData.Context);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => Invalidate(eventData.Context);

    /// <inheritdoc />
    public Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Invalidate(eventData.Context);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void RolledBackToSavepoint(DbTransaction transaction, TransactionEventData eventData) => Invalidate(eventData.Context);

    /// <inheritdoc />
    public Task RolledBackToSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default)
    {
        Invalidate(eventData.Context);
        return Task.CompletedTask;
    }

    // ---- The work

    private void Ensure(DbCommand command, CommandEventData eventData)
    {
        Track(eventData.Context);
        if (!Active || command.Connection is not { } connection)
        {
            return;
        }

        var desired = Desired(command, warn: true);
        if (NeedsApply(connection, command.Transaction, desired))
        {
            Apply(connection, command.Transaction, desired);
        }
    }

    private async Task EnsureAsync(DbCommand command, CommandEventData eventData, CancellationToken ct)
    {
        Track(eventData.Context);
        if (!Active || command.Connection is not { } connection)
        {
            return;
        }

        var desired = Desired(command, warn: true);
        if (NeedsApply(connection, command.Transaction, desired))
        {
            await ApplyAsync(connection, command.Transaction, desired, ct);
        }
    }

    /// <summary>The value the connection must carry: the Owner, or '' (unscoped, or shadow mode)</summary>
    private string Desired(DbCommand? command, bool warn)
    {
        if (_scope.OwnerId is { } owner)
        {
            return owner;
        }

        if (_scope.UnscopedAllowed || !warn)
        {
            // On open nothing has run yet: '' matches no row, and a command still decides below
            return string.Empty;
        }

        if (_options.Mode == OwnerRlsMode.Enforce)
        {
            throw new OwnerScopeMissingException();
        }

        var text = command?.CommandText ?? string.Empty;
        _logger.LogWarning("{Message} (it would throw in Enforce mode). Command: {Command}{NewLine}{Stack}",
            MissingMessage, text.Length > 300 ? text[..300] : text, Environment.NewLine, Environment.StackTrace);
        return string.Empty;
    }

    private bool NeedsApply(DbConnection connection, DbTransaction? transaction, string desired)
    {
        lock (_connections)
        {
            if (!_connections.TryGetValue(connection, out var applied))
            {
                return true;
            }

            if (applied.Transaction != null && !ReferenceEquals(applied.Transaction, transaction))
            {
                // Set inside a transaction that ended without a commit we saw (disposed, so
                // rolled back): the value reverted, whatever it was
                _connections.Remove(connection);
                return true;
            }

            if (string.Equals(applied.Value, desired, StringComparison.Ordinal))
            {
                return false;
            }

            if (transaction != null)
            {
                // AD-49 rule 4: never two Owners in one transaction
                throw new OwnerScopeChangeException();
            }

            return true;
        }
    }

    private void Apply(DbConnection connection, DbTransaction? transaction, string value)
    {
        using var cmd = SetCommand(connection, transaction, value);
        cmd.ExecuteNonQuery();
        Remember(connection, transaction, value);
    }

    private async Task ApplyAsync(DbConnection connection, DbTransaction? transaction, string value, CancellationToken ct)
    {
        await using var cmd = SetCommand(connection, transaction, value);
        await cmd.ExecuteNonQueryAsync(ct);
        Remember(connection, transaction, value);
    }

    private static DbCommand SetCommand(DbConnection connection, DbTransaction? transaction, string value)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = SetSql;
        if (cmd is NpgsqlCommand npgsql)
        {
            npgsql.Parameters.Add(new NpgsqlParameter { Value = value });
        }
        else
        {
            var p = cmd.CreateParameter();
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        return cmd;
    }

    private void Remember(DbConnection connection, DbTransaction? transaction, string value)
    {
        lock (_connections)
        {
            _connections[connection] = new Applied(value, transaction);
        }
    }

    private bool Forget(DbConnection connection)
    {
        lock (_connections)
        {
            return _connections.Remove(connection);
        }
    }

    private void Committed(DbTransaction transaction)
    {
        lock (_connections)
        {
            foreach (var (connection, applied) in _connections.ToList())
            {
                if (ReferenceEquals(applied.Transaction, transaction))
                {
                    // Committed: the value now holds for the session
                    _connections[connection] = applied with { Transaction = null };
                }
            }
        }
    }

    private void Invalidate(DbContext? context)
    {
        lock (_connections)
        {
            DbConnection? connection = null;
            try
            {
                connection = context?.Database.GetDbConnection();
            }
            catch (ObjectDisposedException)
            {
            }

            if (connection != null)
            {
                _connections.Remove(connection);
            }
            else
            {
                _connections.Clear();
            }
        }
    }

    private void Track(DbContext? context)
    {
        if (context == null)
        {
            return;
        }

        lock (_contexts)
        {
            _contexts.Add(context);
        }
    }

    private bool AnyTransactionOpen()
    {
        lock (_contexts)
        {
            foreach (var context in _contexts.ToList())
            {
                try
                {
                    if (context.Database.CurrentTransaction != null)
                    {
                        return true;
                    }
                }
                catch (ObjectDisposedException)
                {
                    _contexts.Remove(context);
                }
            }
        }

        return false;
    }

    private sealed record Applied(string Value, DbTransaction? Transaction);
}
