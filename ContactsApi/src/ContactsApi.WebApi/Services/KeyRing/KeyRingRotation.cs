/**
 * @fileoverview Retiring the exposed Data Protection keys (AD-50 rule 2): a new wrapped key,
 * every protected value re-protected with it, then the old keys revoked and deleted. A
 * one-shot, gated by configuration, idempotent and resumable; it logs counts only.
 * @module ContactsApi.WebApi.Services.KeyRing
 */
using ContactsApi.Infrastructure.Atlas;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using ContactsApi.Infrastructure.Tenancy;
using StackExchange.Redis;

namespace ContactsApi.WebApi.Services.KeyRing;

/// <summary>What the rotation does</summary>
public enum RotationMode
{
    /// <summary>Nothing (the default)</summary>
    Off,

    /// <summary>Count what would change; write nothing</summary>
    DryRun,

    /// <summary>A new wrapped key if the current one is not wrapped, then re-protect every value</summary>
    Reprotect,

    /// <summary>Reprotect, then revoke and delete every other key, only when no value still needs one</summary>
    Retire
}

/// <summary>One protected value</summary>
/// <param name="Id">Where it is (a Redis key or a row id); logged never</param>
/// <param name="Protected">The protected string (base64url Data Protection payload)</param>
/// <param name="Owner">The Owner a database row belongs to (its writes run under that Owner, AD-49)</param>
public record ProtectedValue(string Id, string Protected, string? Owner = null);

/// <summary>A place that holds values protected with one purpose</summary>
public interface IProtectedValueSource
{
    /// <summary>A name for the counts in the log</summary>
    string Kind { get; }

    /// <summary>The Data Protection purpose the values were protected with</summary>
    string Purpose { get; }

    /// <summary>Every value</summary>
    IAsyncEnumerable<ProtectedValue> ReadAllAsync(CancellationToken ct);

    /// <summary>Replaces a value only if it still holds what was read; false when it changed meanwhile</summary>
    Task<bool> ReplaceAsync(ProtectedValue value, string newProtected, CancellationToken ct);
}

/// <summary>Counts for one kind of value</summary>
public class KindCounts
{
    /// <summary>Values already under the current key</summary>
    public int Current { get; set; }

    /// <summary>Values re-protected (or, in a dry run, that would be)</summary>
    public int Reprotected { get; set; }

    /// <summary>Values that changed while being re-protected (left for the next run)</summary>
    public int ChangedMeanwhile { get; set; }

    /// <summary>Values that cannot be read although their key is in the ring (block retiring)</summary>
    public int Unreadable { get; set; }

    /// <summary>Values whose key is no longer in the ring (already unreadable; do not block)</summary>
    public int Lost { get; set; }
}

/// <summary>What a run did</summary>
public class RotationReport
{
    /// <summary>The mode</summary>
    public RotationMode Mode { get; init; }

    /// <summary>Why the run stopped before doing anything, or null</summary>
    public string? Refused { get; set; }

    /// <summary>Whether a new key was created (or, in a dry run, would be)</summary>
    public bool KeyCreated { get; set; }

    /// <summary>The key values are protected with after the run</summary>
    public Guid CurrentKey { get; set; }

    /// <summary>Counts per kind</summary>
    public Dictionary<string, KindCounts> Kinds { get; } = new();

    /// <summary>Keys revoked and deleted (Retire)</summary>
    public int KeysRetired { get; set; }

    /// <summary>Why the keys were not retired, or null</summary>
    public string? RetireBlocked { get; set; }
}

/// <summary>The rotation itself (no hosting: the tests drive it directly)</summary>
public class KeyRingRotator
{
    private const string ProbePurpose = "ContactsApi.KeyRing.Probe.v1";

    private readonly IDataProtectionProvider _provider;
    private readonly IKeyManager _keys;
    private readonly Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository _repository;
    private readonly DataProtectionCertificates _certificates;
    private readonly IReadOnlyList<IProtectedValueSource> _sources;
    private readonly ILogger _logger;

    /// <summary>Creates the rotator</summary>
    public KeyRingRotator(IDataProtectionProvider provider, IKeyManager keys,
        Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository repository, DataProtectionCertificates certificates,
        IReadOnlyList<IProtectedValueSource> sources, ILogger logger)
    {
        _provider = provider;
        _keys = keys;
        _repository = repository;
        _certificates = certificates;
        _sources = sources;
        _logger = logger;
    }

    /// <summary>The key id in a protected string's header, or null when it is not a payload</summary>
    public static Guid? KeyIdOf(string protectedValue)
    {
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(protectedValue);
            // Magic header 0x09F0C9F0, then the key id
            return bytes.Length >= 20 && bytes[0] == 0x09 && bytes[1] == 0xF0 && bytes[2] == 0xC9 && bytes[3] == 0xF0
                ? new Guid(bytes.AsSpan(4, 16))
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The key new values are protected with now</summary>
    public Guid DefaultKeyId() =>
        new(_provider.CreateProtector(ProbePurpose).Protect(new byte[] { 1 }).AsSpan(4, 16));

    /// <summary>Runs the rotation in a mode</summary>
    public async Task<RotationReport> RunAsync(RotationMode mode, CancellationToken ct)
    {
        var report = new RotationReport { Mode = mode };
        if (mode == RotationMode.Off)
        {
            return report;
        }

        if (!_certificates.Enabled && mode != RotationMode.DryRun)
        {
            report.Refused = "DataProtection__Certificate is not configured: a new key would not be wrapped";
            return report;
        }

        var ring = KeyRingInspector.Keys(_repository);
        var current = DefaultKeyId();
        if (!ring.GetValueOrDefault(current))
        {
            report.KeyCreated = true;
            if (mode != RotationMode.DryRun)
            {
                var now = DateTimeOffset.UtcNow;
                _keys.CreateNewKey(now, now.AddDays(90));
                current = await WaitForNewDefaultKeyAsync(current, ct);
                if (!KeyRingInspector.Keys(_repository).GetValueOrDefault(current))
                {
                    report.Refused = "the new key is not wrapped";
                    return report;
                }
            }
        }

        report.CurrentKey = current;
        ring = KeyRingInspector.Keys(_repository);
        foreach (var source in _sources)
        {
            report.Kinds[source.Kind] = await ReprotectAsync(source, mode, report.KeyCreated && mode == RotationMode.DryRun ? null : current, ring, ct);
        }

        if (mode == RotationMode.Retire)
        {
            Retire(report, current);
        }

        return report;
    }

    private async Task<Guid> WaitForNewDefaultKeyAsync(Guid previous, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var current = DefaultKeyId();
            if (current != previous)
            {
                return current;
            }

            await Task.Delay(500, ct);
        }

        throw new InvalidOperationException("The new Data Protection key did not become the default key");
    }

    /// <param name="target">The key values must end under; null in a dry run that would create one</param>
    private async Task<KindCounts> ReprotectAsync(IProtectedValueSource source, RotationMode mode, Guid? target,
        IReadOnlyDictionary<Guid, bool> ring, CancellationToken ct)
    {
        var counts = new KindCounts();
        var protector = _provider.CreateProtector(source.Purpose);
        await foreach (var value in source.ReadAllAsync(ct))
        {
            var keyId = KeyIdOf(value.Protected);
            if (keyId != null && keyId == target)
            {
                counts.Current++;
                continue;
            }

            string plain;
            try
            {
                plain = protector.Unprotect(value.Protected);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                if (keyId != null && ring.ContainsKey(keyId.Value))
                {
                    counts.Unreadable++;
                }
                else
                {
                    counts.Lost++;
                }

                continue;
            }

            if (mode == RotationMode.DryRun)
            {
                counts.Reprotected++;
                continue;
            }

            var again = protector.Protect(plain);
            if (KeyIdOf(again) != target)
            {
                throw new InvalidOperationException("Re-protection did not use the new key; stopping before any write");
            }

            if (await source.ReplaceAsync(value, again, ct))
            {
                counts.Reprotected++;
            }
            else
            {
                counts.ChangedMeanwhile++;
            }
        }

        return counts;
    }

    private void Retire(RotationReport report, Guid current)
    {
        var pending = report.Kinds.Values.Sum(k => k.Unreadable + k.ChangedMeanwhile);
        if (pending > 0)
        {
            report.RetireBlocked = $"{pending} values still need an old key (unreadable or changed during the run); run again";
            return;
        }

        // XmlKeyManager deletes keys when its repository is deletable (RedisKeyRingRepository is)
        Func<Func<IKey, bool>, bool>? delete = _keys switch
        {
            IDeletableKeyManager { CanDeleteKeys: true } d => d.DeleteKeys,
            XmlKeyManager { CanDeleteKeys: true } x => x.DeleteKeys,
            _ => null
        };
        if (delete == null)
        {
            report.RetireBlocked = "the key ring repository cannot delete keys";
            return;
        }

        var old = _keys.GetAllKeys().Where(k => k.KeyId != current).ToList();
        foreach (var key in old.Where(k => !k.IsRevoked))
        {
            _keys.RevokeKey(key.KeyId, "AD-50 rule 2: exposed key retired after re-protection");
        }

        if (!delete(k => k.KeyId != current))
        {
            report.RetireBlocked = "the old keys were revoked but could not be deleted";
            return;
        }

        report.KeysRetired = old.Count;
    }
}

/// <summary>
/// Values in Redis written through <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>
/// under one key prefix. A value is replaced in place (the hash's <c>data</c> field), only if
/// it still holds what was read; its expiry is untouched.
/// </summary>
public class RedisProtectedValues : IProtectedValueSource
{
    /// <summary>The IDistributedCache instance name the app uses</summary>
    public const string InstanceName = "ContactsApi_";

    private readonly IConnectionMultiplexer _redis;
    private readonly string _pattern;

    /// <summary>Creates the source</summary>
    public RedisProtectedValues(IConnectionMultiplexer redis, string kind, string keyPrefix, string purpose)
    {
        _redis = redis;
        Kind = kind;
        Purpose = purpose;
        _pattern = InstanceName + keyPrefix + "*";
    }

    /// <inheritdoc />
    public string Kind { get; }

    /// <inheritdoc />
    public string Purpose { get; }

    /// <inheritdoc />
    public async IAsyncEnumerable<ProtectedValue> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var database = _redis.GetDatabase();
        foreach (var server in _redis.GetServers().Where(s => !s.IsReplica))
        {
            await foreach (var key in server.KeysAsync(database.Database, _pattern, pageSize: 250).WithCancellation(ct))
            {
                var type = await database.KeyTypeAsync(key);
                RedisValue value = type switch
                {
                    RedisType.Hash => await database.HashGetAsync(key, "data"),
                    RedisType.String => await database.StringGetAsync(key),
                    _ => RedisValue.Null
                };
                if (!value.IsNullOrEmpty)
                {
                    yield return new ProtectedValue(key.ToString(), value.ToString());
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> ReplaceAsync(ProtectedValue value, string newProtected, CancellationToken ct)
    {
        var database = _redis.GetDatabase();
        RedisKey key = value.Id;
        var transaction = database.CreateTransaction();
        if (await database.KeyTypeAsync(key) == RedisType.Hash)
        {
            transaction.AddCondition(Condition.HashEqual(key, "data", value.Protected));
            _ = transaction.HashSetAsync(key, "data", newProtected);
        }
        else
        {
            transaction.AddCondition(Condition.StringEqual(key, value.Protected));
            _ = transaction.StringSetAsync(key, newProtected, null, keepTtl: true);
        }

        return await transaction.ExecuteAsync();
    }
}

/// <summary>
/// <c>atlas.source_connections.SecretProtected</c>: the Sports Tracker session keys and OneDrive
/// refresh tokens. Under row-level security (AD-49 rule 5) the Owners come from the definer
/// function <c>sys_owners('source_connections')</c> (ids only, unscoped); every read and write
/// then runs in a fresh DI scope of one Owner.
/// </summary>
public class SourceSecretValues : IProtectedValueSource
{
    private readonly IServiceScopeFactory _scopes;

    /// <summary>Creates the source</summary>
    public SourceSecretValues(IServiceScopeFactory scopes) => _scopes = scopes;

    /// <inheritdoc />
    public string Kind => "source-connection-secrets";

    /// <inheritdoc />
    public string Purpose => AtlasConnectionService.ProtectorPurpose;

    /// <inheritdoc />
    public async IAsyncEnumerable<ProtectedValue> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        List<string> owners;
        using (var unscoped = _scopes.CreateUnscopedScope())
        {
            owners = await unscoped.ServiceProvider.GetRequiredService<AtlasDbContext>()
                .SysOwnersAsync(SysOwnerKinds.SourceConnections, ct);
        }

        foreach (var owner in owners)
        {
            using var scope = _scopes.CreateOwnerScope(owner);
            var rows = await scope.ServiceProvider.GetRequiredService<AtlasDbContext>().Connections.AsNoTracking()
                .Where(c => c.OwnerId == owner && c.SecretProtected != null)
                .Select(c => new { c.Id, c.SecretProtected })
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                yield return new ProtectedValue(row.Id.ToString(), row.SecretProtected!, owner);
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> ReplaceAsync(ProtectedValue value, string newProtected, CancellationToken ct)
    {
        var owner = value.Owner ?? throw new InvalidOperationException("A source connection value without its Owner");
        using var scope = _scopes.CreateOwnerScope(owner);
        var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
        var id = Guid.Parse(value.Id);
        return await db.Connections
            .Where(c => c.Id == id && c.OwnerId == owner && c.SecretProtected == value.Protected)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.SecretProtected, newProtected), ct) == 1;
    }
}

/// <summary>
/// The hosted one-shot. <c>DataProtection__Rotation</c> = <c>dry-run</c>, <c>reprotect</c> or
/// <c>retire</c> runs it once after startup (then set it back to <c>off</c>); it logs counts only.
/// </summary>
public class KeyRingRotation : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly RotationMode _mode;
    private readonly ILogger<KeyRingRotation> _logger;

    /// <summary>Creates the service</summary>
    public KeyRingRotation(IServiceProvider services, IConfiguration configuration, ILogger<KeyRingRotation> logger)
    {
        _services = services;
        _logger = logger;
        _mode = ParseMode(configuration[DataProtectionConfig.Rotation]);
    }

    /// <summary>The mode from its configured text (unknown text is Off)</summary>
    public static RotationMode ParseMode(string? text) => (text ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "dry-run" or "dryrun" => RotationMode.DryRun,
        "reprotect" => RotationMode.Reprotect,
        "retire" => RotationMode.Retire,
        _ => RotationMode.Off
    };

    /// <summary>Every place that holds Data-Protection-protected values (AD-50 rule 2)</summary>
    public static IReadOnlyList<IProtectedValueSource> Sources(IServiceProvider services)
    {
        var redis = services.GetRequiredService<IConnectionMultiplexer>();
        var sources = new List<IProtectedValueSource>
        {
            new RedisProtectedValues(redis, "google-refresh-tokens", "refresh-token:", GoogleTokenService.RefreshTokenPurpose),
            new RedisProtectedValues(redis, "activity-snapshots", "activity:", ActivityStore.Purpose),
            new RedisProtectedValues(redis, "own-addresses", "self-emails:", ActivityStore.Purpose),
            new RedisProtectedValues(redis, "contact-histories", "activity-history:", ActivityStore.HistoryPurpose),
            new RedisProtectedValues(redis, "other-contacts-dismissals", "other-contacts-dismissed:", OtherContactsStore.Purpose),
            new RedisProtectedValues(redis, "public-profiles", "profiles:", ProfileStore.Purpose)
        };
        if (services.GetRequiredService<AtlasReadiness>().Configured)
        {
            sources.Add(new SourceSecretValues(services.GetRequiredService<IServiceScopeFactory>()));
        }

        return sources;
    }

    /// <summary>Registers the service (it does nothing unless a mode is set)</summary>
    public static void Register(IServiceCollection services, IConfiguration configuration) =>
        services.AddHostedService<KeyRingRotation>();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_mode == RotationMode.Off)
        {
            return;
        }

        try
        {
            // Postgres values need the migrated database
            var readiness = _services.GetRequiredService<AtlasReadiness>();
            for (var waited = 0; readiness.Configured && !readiness.Ready && waited < 600; waited += 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }

            if (readiness.Configured && !readiness.Ready)
            {
                _logger.LogError("Key ring rotation ({Mode}) not run: the Atlas database is not ready", _mode);
                return;
            }

            var rotator = new KeyRingRotator(
                _services.GetRequiredService<IDataProtectionProvider>(),
                _services.GetRequiredService<IKeyManager>(),
                KeyRingInspector.Repository(_services),
                _services.GetRequiredService<DataProtectionCertificates>(),
                Sources(_services),
                _logger);
            var report = await rotator.RunAsync(_mode, stoppingToken);
            Log(report);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Key ring rotation ({Mode}) failed; nothing after the failure was changed. Safe to run again", _mode);
        }
    }

    private void Log(RotationReport report)
    {
        if (report.Refused != null)
        {
            _logger.LogError("Key ring rotation ({Mode}) refused: {Reason}", report.Mode, report.Refused);
            return;
        }

        _logger.LogWarning("Key ring rotation ({Mode}): new key {Created}", report.Mode,
            report.KeyCreated ? (report.Mode == RotationMode.DryRun ? "would be created" : "created") : "not needed");
        foreach (var (kind, c) in report.Kinds)
        {
            _logger.LogWarning(
                "Key ring rotation ({Mode}) {Kind}: current {Current}, re-protected {Reprotected}, changed meanwhile {Changed}, unreadable {Unreadable}, already lost {Lost}",
                report.Mode, kind, c.Current, c.Reprotected, c.ChangedMeanwhile, c.Unreadable, c.Lost);
        }

        if (report.Mode == RotationMode.Retire)
        {
            if (report.RetireBlocked != null)
            {
                _logger.LogError("Key ring rotation (Retire): old keys kept: {Reason}", report.RetireBlocked);
            }
            else
            {
                _logger.LogWarning("Key ring rotation (Retire): {Count} old keys revoked and deleted; every session ends", report.KeysRetired);
            }
        }
    }
}
