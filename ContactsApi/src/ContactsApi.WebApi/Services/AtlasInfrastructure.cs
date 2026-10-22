/**
 * @fileoverview Atlas plumbing: database readiness / migrations and personal access tokens
 * @module ContactsApi.WebApi.Services
 */
using System.Security.Cryptography;
using System.Text;
using ContactsApi.Infrastructure.Atlas;
using Microsoft.EntityFrameworkCore;

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
/// reachable. Contacts keep working meanwhile; Atlas answers 503 until ready.
/// </summary>
public class AtlasMigrator : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly AtlasReadiness _readiness;
    private readonly ILogger<AtlasMigrator> _logger;

    /// <summary>Creates the migrator</summary>
    public AtlasMigrator(IServiceProvider services, AtlasReadiness readiness, ILogger<AtlasMigrator> logger)
    {
        _services = services;
        _readiness = readiness;
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
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
                await db.Database.MigrateAsync(stoppingToken);
                _readiness.Ready = true;
                _readiness.Error = null;
                _logger.LogInformation("Atlas database ready");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _readiness.Error = ex.Message;
                _logger.LogWarning("Atlas database not ready ({Message}); retrying in {Delay}", ex.Message, delay);
                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromSeconds(Math.Min(120, delay.TotalSeconds * 2));
            }
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

    /// <summary>The owner a token belongs to, or null</summary>
    public async Task<string?> ValidateAsync(string token)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith("atl_", StringComparison.Ordinal))
        {
            return null;
        }

        var hash = Hash(token);
        var row = await _db.AccessTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (row == null)
        {
            return null;
        }

        if (row.LastUsedAt == null || DateTime.UtcNow - row.LastUsedAt > TimeSpan.FromHours(1))
        {
            row.LastUsedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return row.OwnerId;
    }

    private static AccessTokenInfo Info(AccessToken t) => new(t.Id, t.Name, t.Prefix, t.CreatedAt, t.LastUsedAt);

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
