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
                    if (pending.Count > 0)
                    {
                        throw new InvalidOperationException($"{pending.Count} migrations wait for the migrate init container");
                    }
                }

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
public record AccessTokenInfo(Guid Id, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt);

/// <summary>
/// Personal access tokens for MCP clients. The token is shown once; only its
/// SHA-256 is stored.
/// </summary>
public class AccessTokenService
{
    private readonly AtlasDbContext _db;

    /// <summary>Creates the service</summary>
    public AccessTokenService(AtlasDbContext db) => _db = db;

    /// <summary>Creates a token</summary>
    /// <param name="owner">Google account id</param>
    /// <param name="name">What it is for ("Claude Desktop")</param>
    /// <returns>The token (only now) and its listing</returns>
    public async Task<(string Token, AccessTokenInfo Info)> CreateAsync(string owner, string name)
    {
        var token = "atl_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var row = new AccessToken
        {
            OwnerId = owner,
            Name = string.IsNullOrWhiteSpace(name) ? "MCP client" : name.Trim()[..Math.Min(100, name.Trim().Length)],
            TokenHash = Hash(token),
            Prefix = token[..8]
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

    /// <summary>
    /// The owner a token belongs to, or null. The Owner is not known yet, so the lookup
    /// goes through atlas.sys_resolve_access_token (AD-49 rule 5), which also stamps
    /// LastUsedAt at most hourly; the caller marks the work unscoped.
    /// </summary>
    public async Task<string?> ValidateAsync(string token)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith("atl_", StringComparison.Ordinal))
        {
            return null;
        }

        return await _db.SysResolveAccessTokenAsync(Hash(token));
    }

    private static AccessTokenInfo Info(AccessToken t) => new(t.Id, t.Name, t.Prefix, t.CreatedAt, t.LastUsedAt);

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
