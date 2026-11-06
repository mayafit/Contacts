/**
 * @fileoverview Atlas plumbing: database readiness / migrations and personal access tokens
 * @module ContactsApi.WebApi.Services
 */
using System.Security.Cryptography;
using System.Text;
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ContactsApi.WebApi.Services;

/// <summary>Whether the Atlas database is configured and migrated</summary>
public class AtlasReadiness
{
    /// <summary>A connection string is configured</summary>
    public bool Configured { get; init; }

    /// <summary>Migrations applied; the API can serve</summary>
    public volatile bool Ready;

    /// <summary>Last migration error, for the status endpoint</summary>
    public string? Error { get; set; }

    /// <summary>Migrations are pending and the API does not apply them (it waits for the migrate init container)</summary>
    public volatile bool MigrationsPending;
}

/// <summary>/health/ready: unready while Atlas migrations are pending (AD-49 rule 3); /health (liveness) ignores it</summary>
public class AtlasMigrationsCheck : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    /// <summary>The tag of the readiness-only checks</summary>
    public const string ReadyTag = "ready";

    private readonly AtlasReadiness _readiness;

    /// <summary>Creates the check</summary>
    public AtlasMigrationsCheck(AtlasReadiness readiness) => _readiness = readiness;

    /// <inheritdoc />
    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(_readiness.MigrationsPending
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("Atlas migrations are pending")
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy());
}

/// <summary>
/// Applies the Atlas migrations at startup, retrying while PostgreSQL is not
/// reachable; or, once the migrate-only init container applies them (AD-49 rule 3,
/// RowLevelSecurity__MigrateOnStartup=false), only waits until none is pending.
/// Contacts keep working meanwhile; Atlas answers 503 until ready.
/// </summary>
public class AtlasMigrator : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly AtlasReadiness _readiness;
    private readonly RowLevelSecurityOptions _rls;
    private readonly ILogger<AtlasMigrator> _logger;

    /// <summary>Creates the migrator</summary>
    public AtlasMigrator(IServiceProvider services, AtlasReadiness readiness, RowLevelSecurityOptions rls, ILogger<AtlasMigrator> logger)
    {
        _services = services;
        _readiness = readiness;
        _rls = rls;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_readiness.Configured)
        {
            _logger.LogWarning("Atlas disabled: no database connection string");
            return;
        }

        var delay = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Migrations and the readiness check have no Owner (AD-49 rule 4)
                using var scope = _services.GetRequiredService<IServiceScopeFactory>().CreateUnscopedScope();
                var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
                if (_rls.MigrateOnStartup)
                {
                    // Some migrations fix data across all track points: more than the default 30 s
                    db.Database.SetCommandTimeout(TimeSpan.FromMinutes(20));
                    await EchoMigrator.MigrateAsync(db, Infrastructure.Atlas.Migrations.AtlasRowLevelSecurity.Id, _rls.Policies, _logger, stoppingToken);
                }
                else
                {
                    var pending = await EchoMigrator.BlockingPendingAsync(db, Infrastructure.Atlas.Migrations.AtlasRowLevelSecurity.Id, _rls.Policies, stoppingToken);
                    _readiness.MigrationsPending = pending.Count > 0;
                    if (pending.Count > 0)
                    {
                        throw new InvalidOperationException($"{pending.Count} migrations wait for the migrate init container");
                    }
                }

                _readiness.MigrationsPending = false;
                _readiness.Ready = true;
                _readiness.Error = null;
                _logger.LogInformation("Atlas database ready");
                await BackfillFusionAsync(stoppingToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_readiness.Ready)
                {
                    return;
                }

                _readiness.Error = ex.Message;
                _logger.LogWarning("Atlas database not ready ({Message}); retrying in {Delay}", ex.Message, delay);
                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromSeconds(Math.Min(120, delay.TotalSeconds * 2));
            }
        }
    }

    /// <summary>
    /// Data imported before fusion existed: fuse every owner's activities once
    /// (when no fused activity exists yet), each under its own scope (AD-49 rule 5).
    /// Failures are logged; the app works on.
    /// </summary>
    private async Task BackfillFusionAsync(CancellationToken ct)
    {
        try
        {
            var scopes = _services.GetRequiredService<IServiceScopeFactory>();
            List<string> owners;
            using (var scope = scopes.CreateUnscopedScope())
            {
                owners = await scope.ServiceProvider.GetRequiredService<AtlasDbContext>().SysOwnersAsync(SysOwnerKinds.FusionBackfill, ct);
            }

            foreach (var owner in owners)
            {
                using var scope = scopes.CreateOwnerScope(owner);
                var result = await scope.ServiceProvider.GetRequiredService<AtlasService>().FuseAsync(owner, ct);
                _logger.LogInformation("Fusion backfill: {Fused} fused activities from {Activities} records", result.Fused, result.Activities);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Fusion backfill failed");
        }
    }
}

/// <summary>A token as listed (never the secret)</summary>
/// <param name="CanWrite">Whether the update and delete tools may be used (the <c>write</c> scope)</param>
/// <param name="Legacy">A token from before expiry existed: it keeps write access until it expires</param>
public record AccessTokenInfo(Guid Id, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt,
    DateTime ExpiresAt, bool CanWrite, bool Legacy);

/// <summary>A token that checked out: whose it is and what it may do</summary>
public record ValidatedToken(Guid Id, string OwnerId, bool CanWrite);

/// <summary>
/// Personal access tokens for MCP clients (AD-18, amended by AD-50 rule 9). The token is
/// shown once; only its SHA-256 is stored. Tokens expire (default 90 days, at most 365) and
/// are read-only unless created with "allow changes" (the <c>write</c> scope).
/// </summary>
public class AccessTokenService
{
    /// <summary>Default lifetime of a new token</summary>
    public const int DefaultDays = 90;

    /// <summary>Longest lifetime of a token</summary>
    public const int MaxDays = 365;

    /// <summary>The scope that allows the update and delete tools</summary>
    public const string WriteScope = "write";

    /// <summary>Marks tokens that existed before expiry (the migration gives them write until they expire)</summary>
    public const string LegacyScope = "legacy";

    private readonly AtlasDbContext _db;
    private readonly OwnerScope? _owners;
    private readonly TimeProvider _time;

    /// <summary>Creates the service</summary>
    /// <param name="db">The Atlas database</param>
    /// <param name="owners">The request's Owner scope (DI); without it a check knows only the Owner</param>
    /// <param name="time">The clock (tests)</param>
    public AccessTokenService(AtlasDbContext db, OwnerScope? owners = null, TimeProvider? time = null)
    {
        _db = db;
        _owners = owners;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Creates a token</summary>
    /// <param name="owner">Google account id</param>
    /// <param name="name">What it is for ("Claude Desktop")</param>
    /// <param name="allowWrite">"Allow changes": the update and delete tools</param>
    /// <param name="days">Lifetime in days (default 90, clamped to 1..365)</param>
    /// <returns>The token (only now) and its listing</returns>
    public async Task<(string Token, AccessTokenInfo Info)> CreateAsync(string owner, string name, bool allowWrite = false, int? days = null)
    {
        var token = "atl_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = _time.GetUtcNow().UtcDateTime;
        var row = new AccessToken
        {
            OwnerId = owner,
            Name = string.IsNullOrWhiteSpace(name) ? "MCP client" : name.Trim()[..Math.Min(100, name.Trim().Length)],
            TokenHash = Hash(token),
            Prefix = token[..8],
            CreatedAt = now,
            ExpiresAt = now.AddDays(Math.Clamp(days ?? DefaultDays, 1, MaxDays)),
            Scopes = allowWrite ? "atlas " + WriteScope : "atlas"
        };
        _db.AccessTokens.Add(row);
        await _db.SaveChangesAsync();
        return (token, Info(row));
    }

    /// <summary>The owner's tokens</summary>
    public async Task<List<AccessTokenInfo>> ListAsync(string owner) =>
        (await _db.AccessTokens.AsNoTracking().Where(t => t.OwnerId == owner).OrderByDescending(t => t.CreatedAt).ToListAsync())
        .Select(Info).ToList();

    /// <summary>Revokes a token</summary>
    public async Task<bool> RevokeAsync(string owner, Guid id) =>
        await _db.AccessTokens.Where(t => t.Id == id && t.OwnerId == owner).ExecuteDeleteAsync() > 0;

    /// <summary>The owner a token belongs to, or null (unknown, or expired: refused like a revoked one)</summary>
    public async Task<string?> ValidateAsync(string token) => (await CheckAsync(token))?.OwnerId;

    /// <summary>
    /// The token's owner and rights, or null when it is unknown or expired. The Owner is not
    /// known yet, so it is resolved through atlas.sys_resolve_access_token (AD-49 rule 5; it
    /// refuses an expired token and stamps LastUsedAt at most hourly), unscoped. With an
    /// <see cref="OwnerScope"/>, the request's Owner is then set to it and the token's row is
    /// read under that Owner, for its id (the rate limit), expiry and write scope.
    /// </summary>
    public async Task<ValidatedToken?> CheckAsync(string token)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith("atl_", StringComparison.Ordinal))
        {
            return null;
        }

        var hash = Hash(token);
        string? owner;
        using (_owners?.AllowUnscoped())
        {
            owner = await _db.SysResolveAccessTokenAsync(hash);
        }

        if (owner == null || _owners == null)
        {
            // Without a scope to read the row under, only the Owner is known (read-only)
            return owner == null ? null : new ValidatedToken(Guid.Empty, owner, false);
        }

        _owners.Set(owner);
        var row = await _db.AccessTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash && t.OwnerId == owner);
        if (row == null || row.ExpiresAt <= _time.GetUtcNow().UtcDateTime)
        {
            return null;
        }

        return new ValidatedToken(row.Id, row.OwnerId, HasScope(row, WriteScope));
    }

    private static bool HasScope(AccessToken t, string scope) =>
        t.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal);

    private static AccessTokenInfo Info(AccessToken t) =>
        new(t.Id, t.Name, t.Prefix, t.CreatedAt, t.LastUsedAt, t.ExpiresAt, HasScope(t, WriteScope), HasScope(t, LegacyScope));

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
