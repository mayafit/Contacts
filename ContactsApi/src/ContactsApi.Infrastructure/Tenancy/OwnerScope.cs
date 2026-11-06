/**
 * @fileoverview The ambient Owner of one unit of work (AD-49 rule 4): a request, or one
 * worker item. OwnerRlsInterceptor copies it into the session variable app.owner_id,
 * which the owner_isolation policies read.
 * @module ContactsApi.Infrastructure.Tenancy
 */
using System.Text.RegularExpressions;

namespace ContactsApi.Infrastructure.Tenancy;

/// <summary>
/// The Owner whose rows this DI scope may see (registered scoped: one per request, one
/// per worker unit of work). <see cref="Set"/> and <see cref="Use"/> are the only
/// writers; changing the Owner while a transaction is open throws. Work that has no
/// Owner (the readiness check, the sys_* discovery functions) says so explicitly with
/// <see cref="AllowUnscoped"/>.
/// </summary>
public sealed partial class OwnerScope
{
    private readonly List<Func<bool>> _transactionProbes = new();
    private string? _owner;
    private int _unscoped;

    /// <summary>The Owner of this scope, or null when none is set</summary>
    public string? OwnerId => _owner;

    /// <summary>Whether unscoped work was declared (<see cref="AllowUnscoped"/>)</summary>
    public bool UnscopedAllowed => Volatile.Read(ref _unscoped) > 0;

    /// <summary>Whether a value is a well-formed Owner id (a Google sub)</summary>
    public static bool IsValid(string? owner) => owner != null && ValidOwner().IsMatch(owner);

    /// <summary>Sets the Owner of this scope</summary>
    /// <exception cref="ArgumentException">Not a well-formed Owner id</exception>
    /// <exception cref="OwnerScopeChangeException">Another Owner while a transaction is open</exception>
    public void Set(string owner)
    {
        if (!IsValid(owner))
        {
            throw new ArgumentException("Invalid owner id.", nameof(owner));
        }

        if (!string.Equals(owner, _owner, StringComparison.Ordinal) && HasOpenTransaction)
        {
            throw new OwnerScopeChangeException();
        }

        _owner = owner;
    }

    /// <summary>Sets the Owner until the result is disposed, then restores the previous one</summary>
    public IDisposable Use(string owner)
    {
        var previous = _owner;
        Set(owner);
        // Never throws from Dispose: if a transaction is still open, the interceptor refuses
        // its next command (the connection carries the other Owner)
        return new Restore(() => _owner = previous);
    }

    /// <summary>
    /// Declares work without an Owner until the result is disposed: the readiness check,
    /// the migrate-only run and calls to the sys_* functions. With no Owner set, the
    /// interceptor then sets app.owner_id to '' (which matches no row) instead of
    /// refusing the command. An Owner already set stays in force.
    /// </summary>
    public IDisposable AllowUnscoped()
    {
        Interlocked.Increment(ref _unscoped);
        return new Restore(() => Interlocked.Decrement(ref _unscoped));
    }

    /// <summary>Throws unless this scope's Owner is <paramref name="owner"/> (a guard before destructive work)</summary>
    /// <exception cref="OwnerScopeMissingException">No Owner, or another one</exception>
    public void Require(string owner)
    {
        if (!string.Equals(_owner, owner, StringComparison.Ordinal))
        {
            throw new OwnerScopeMissingException(_owner == null
                ? "No owner is set for this work."
                : "This work runs under another owner's scope.");
        }
    }

    /// <summary>Whether a DbContext of this scope has a transaction open</summary>
    public bool HasOpenTransaction
    {
        get
        {
            lock (_transactionProbes)
            {
                return _transactionProbes.Any(p => p());
            }
        }
    }

    /// <summary>The interceptor reports its contexts' open transactions here</summary>
    internal void AddTransactionProbe(Func<bool> probe)
    {
        lock (_transactionProbes)
        {
            _transactionProbes.Add(probe);
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,64}$")]
    private static partial Regex ValidOwner();

    private sealed class Restore : IDisposable
    {
        private Action? _undo;

        public Restore(Action undo) => _undo = undo;

        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }
}

/// <summary>A database command would run with no Owner in scope (AD-49 rule 4: the app fails loud)</summary>
public class OwnerScopeMissingException : InvalidOperationException
{
    /// <summary>Creates the exception</summary>
    public OwnerScopeMissingException()
        : base("No owner is set for this database work (AD-49). Set the owner scope at the entry point.")
    {
    }

    /// <summary>Creates the exception with a message</summary>
    public OwnerScopeMissingException(string message) : base(message)
    {
    }
}

/// <summary>The Owner was changed while a transaction was open (AD-49 rule 4)</summary>
public class OwnerScopeChangeException : InvalidOperationException
{
    /// <summary>Creates the exception</summary>
    public OwnerScopeChangeException()
        : base("The owner cannot change while a transaction is open (AD-49).")
    {
    }
}
