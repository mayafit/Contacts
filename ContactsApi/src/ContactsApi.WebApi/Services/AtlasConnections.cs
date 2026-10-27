/**
 * @fileoverview Connected data sources (Sports Tracker): connecting, the
 * background sync and its progress
 * @module ContactsApi.WebApi.Services
 */
using System.Threading.Channels;
using ContactsApi.Infrastructure.Atlas;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ContactsApi.WebApi.Services;

/// <summary>A connected source as the app shows it (never the secret)</summary>
public record SourceConnectionInfo(
    string Provider, string? Account, string Status, DateTime? LastSyncAt, string? LastError,
    int ItemsTotal, int ItemsDone, int ItemsImported);

/// <summary>Body of a Sports Tracker connect: user name and password, or a session key</summary>
public record ConnectSportsTrackerRequest(string? Username, string? Password, string? SessionKey);

/// <summary>Owners waiting for a sync (one at a time, in the background)</summary>
public class AtlasSyncQueue
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();

    /// <summary>Asks for a sync of the owner's connections</summary>
    public void Enqueue(string owner) => _queue.Writer.TryWrite(owner);

    /// <summary>The waiting owners</summary>
    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct) => _queue.Reader.ReadAllAsync(ct);
}

/// <summary>Connecting, listing and removing source connections</summary>
public class AtlasConnectionService
{
    /// <summary>Provider name of Sports Tracker</summary>
    public const string SportsTracker = "sports-tracker";

    private readonly AtlasDbContext _db;
    private readonly SportsTrackerClient _client;
    private readonly IDataProtector _protector;
    private readonly AtlasSyncQueue _queue;

    /// <summary>Creates the service</summary>
    public AtlasConnectionService(AtlasDbContext db, IHttpClientFactory http, IDataProtectionProvider protection, AtlasSyncQueue queue)
    {
        _db = db;
        _client = new SportsTrackerClient(http.CreateClient(SportsTrackerClient.HttpClientName));
        _protector = Protector(protection);
        _queue = queue;
    }

    /// <summary>The protector for source secrets</summary>
    public static IDataProtector Protector(IDataProtectionProvider protection) => protection.CreateProtector("Atlas.SourceSecrets.v1");

    /// <summary>The owner's connections</summary>
    public async Task<List<SourceConnectionInfo>> ListAsync(string owner, CancellationToken ct = default) =>
        (await _db.Connections.AsNoTracking().Where(c => c.OwnerId == owner).OrderBy(c => c.Provider).ToListAsync(ct))
        .Select(Info).ToList();

    /// <summary>
    /// Connects Sports Tracker (signs in with the password, which is not kept, or
    /// takes a session key), checks the key, stores it encrypted and starts a sync
    /// </summary>
    /// <exception cref="SportsTrackerException">Sign-in refused, key invalid, or Sports Tracker unreachable</exception>
    public async Task<SourceConnectionInfo> ConnectSportsTrackerAsync(string owner, ConnectSportsTrackerRequest req, CancellationToken ct = default)
    {
        var key = req.SessionKey?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrEmpty(req.Password))
            {
                throw new SportsTrackerException("SPORTS_TRACKER_LOGIN_REFUSED", "Enter your Sports Tracker user name and password, or a session key.");
            }

            key = await _client.LoginAsync(req.Username.Trim(), req.Password, ct);
        }

        // Check the key before keeping it
        await _client.ListWorkoutsAsync(key, ct);

        var conn = await _db.Connections.FirstOrDefaultAsync(c => c.OwnerId == owner && c.Provider == SportsTracker, ct);
        if (conn == null)
        {
            conn = new SourceConnection { OwnerId = owner, Provider = SportsTracker };
            _db.Connections.Add(conn);
        }

        conn.Account = string.IsNullOrWhiteSpace(req.Username) ? conn.Account : req.Username.Trim();
        conn.SecretProtected = _protector.Protect(key);
        conn.Status = "syncing";
        conn.LastError = null;
        conn.ItemsDone = 0;
        conn.ItemsTotal = 0;
        conn.ItemsImported = 0;
        conn.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _queue.Enqueue(owner);
        return Info(conn);
    }

    /// <summary>Syncs again with the stored key</summary>
    public async Task<SourceConnectionInfo> SyncAsync(string owner, CancellationToken ct = default)
    {
        var conn = await _db.Connections.FirstOrDefaultAsync(c => c.OwnerId == owner && c.Provider == SportsTracker, ct)
                   ?? throw new AtlasNotFoundException("Sports Tracker connection");
        if (conn.SecretProtected == null || conn.Status == "expired")
        {
            throw new SportsTrackerException("SPORTS_TRACKER_KEY_EXPIRED", "The Sports Tracker session expired. Connect again.");
        }

        if (conn.Status != "syncing")
        {
            conn.Status = "syncing";
            conn.LastError = null;
            conn.ItemsDone = 0;
            conn.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _queue.Enqueue(owner);
        }

        return Info(conn);
    }

    /// <summary>Forgets the connection and its key (imported data stays)</summary>
    public async Task<bool> DisconnectAsync(string owner, CancellationToken ct = default) =>
        await _db.Connections.Where(c => c.OwnerId == owner && c.Provider == SportsTracker).ExecuteDeleteAsync(ct) > 0;

    /// <summary>A connection as the app shows it</summary>
    public static SourceConnectionInfo Info(SourceConnection c) =>
        new(c.Provider, c.Account, c.Status, c.LastSyncAt, c.LastError, c.ItemsTotal, c.ItemsDone, c.ItemsImported);
}

/// <summary>
/// Runs syncs in the background: lists the Sports Tracker workouts, downloads the
/// track of each one not imported yet, and ingests them in batches (fusion runs
/// after every batch). Progress is written to the connection row as it goes.
/// </summary>
public class AtlasSyncWorker : BackgroundService
{
    private const int BatchActivities = 20;
    private const int BatchPoints = 25_000;
    private readonly IServiceScopeFactory _scopes;
    private readonly AtlasSyncQueue _queue;
    private readonly AtlasReadiness _readiness;
    private readonly ILogger<AtlasSyncWorker> _logger;

    /// <summary>Creates the worker</summary>
    public AtlasSyncWorker(IServiceScopeFactory scopes, AtlasSyncQueue queue, AtlasReadiness readiness, ILogger<AtlasSyncWorker> logger)
    {
        _scopes = scopes;
        _queue = queue;
        _readiness = readiness;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Syncs cut short by a restart: mark them so the user can start again
        while (!_readiness.Ready && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AtlasDbContext>();
            await db.Connections.Where(c => c.Provider == AtlasConnectionService.SportsTracker && c.Status == "syncing")
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "error")
                    .SetProperty(c => c.LastError, "The sync was interrupted by a restart. Sync again to continue."), stoppingToken);
        }

        await foreach (var owner in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await SyncSportsTrackerAsync(owner, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Sports Tracker sync failed");
            }
        }
    }

    private async Task SyncSportsTrackerAsync(string owner, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AtlasDbContext>();
        var atlas = sp.GetRequiredService<AtlasService>();
        var client = new SportsTrackerClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(SportsTrackerClient.HttpClientName));
        var protector = AtlasConnectionService.Protector(sp.GetRequiredService<IDataProtectionProvider>());

        var conn = await db.Connections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.OwnerId == owner && c.Provider == AtlasConnectionService.SportsTracker, ct);
        if (conn?.SecretProtected == null)
        {
            return;
        }

        var id = conn.Id;
        Task Update(Action<SourceConnection> change)
        {
            // Progress goes straight to the row (the ingest clears tracked entities)
            var copy = new SourceConnection();
            change(copy);
            return db.Connections.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, copy.Status)
                .SetProperty(c => c.ItemsTotal, copy.ItemsTotal)
                .SetProperty(c => c.ItemsDone, copy.ItemsDone)
                .SetProperty(c => c.ItemsImported, copy.ItemsImported)
                .SetProperty(c => c.LastError, copy.LastError)
                .SetProperty(c => c.LastSyncAt, copy.LastSyncAt)
                .SetProperty(c => c.ImportId, copy.ImportId)
                .SetProperty(c => c.UpdatedAt, DateTime.UtcNow), ct);
        }

        int total = 0, done = 0, imported = 0, noTrack = 0;
        var importId = conn.ImportId;
        string status = "syncing";
        string? error = null;
        DateTime? lastSync = conn.LastSyncAt;
        Task Progress() => Update(c =>
        {
            c.Status = status;
            c.ItemsTotal = total;
            c.ItemsDone = done;
            c.ItemsImported = imported;
            c.LastError = error;
            c.LastSyncAt = lastSync;
            c.ImportId = importId;
        });

        try
        {
            var key = protector.Unprotect(conn.SecretProtected);
            var workouts = await client.ListWorkoutsAsync(key, ct);
            total = workouts.Count;
            await Progress();

            var existing = (await db.Activities.AsNoTracking()
                    .Where(a => a.OwnerId == owner && a.Source == AtlasConnectionService.SportsTracker && a.ExternalId != null)
                    .Select(a => a.ExternalId!).ToListAsync(ct))
                .ToHashSet();
            var batch = new List<ActivityInput>();
            var points = 0;

            async Task Flush()
            {
                if (batch.Count == 0)
                {
                    return;
                }

                var result = await atlas.IngestAsync(owner, new IngestRequest
                {
                    ImportId = importId, Source = AtlasConnectionService.SportsTracker, FileName = "Sports Tracker sync",
                    Activities = batch.ToList()
                }, ct);
                importId = result.ImportId;
                imported += batch.Count;
                batch.Clear();
                points = 0;
            }

            foreach (var w in workouts.OrderBy(w => w.Start))
            {
                if (!existing.Contains(w.Key))
                {
                    ActivityInput? activity = null;
                    try
                    {
                        activity = SportsTrackerClient.ToActivity(w, await client.ExportGpxAsync(key, w.Key, ct));
                    }
                    catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException
                                               || (ex is SportsTrackerException se && se.Code != "SPORTS_TRACKER_KEY_INVALID"))
                    {
                        // Not imported: the next sync tries again
                        noTrack++;
                    }

                    if (activity == null)
                    {
                        done++;
                        continue;
                    }

                    batch.Add(activity);
                    points += activity.Points?.Count ?? 0;
                    if (batch.Count >= BatchActivities || points >= BatchPoints)
                    {
                        await Flush();
                    }

                    await Task.Delay(150, ct);
                }

                done++;
                if (done % 5 == 0)
                {
                    await Progress();
                }
            }

            await Flush();
            status = "idle";
            lastSync = DateTime.UtcNow;
            error = noTrack > 0 ? $"{noTrack} workouts could not be downloaded. Sync again to retry them." : null;
            _logger.LogInformation("Sports Tracker sync: {Total} workouts, {Imported} imported, {NoTrack} without track", total, imported, noTrack);
        }
        catch (SportsTrackerException ex) when (ex.Code == "SPORTS_TRACKER_KEY_INVALID")
        {
            status = "expired";
            error = "The Sports Tracker session expired. Connect again to continue.";
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            status = "expired";
            error = "The stored Sports Tracker session can no longer be read. Connect again.";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            status = "error";
            error = ex is SportsTrackerException ? ex.Message : "The sync failed. Try again later.";
            _logger.LogError(ex, "Sports Tracker sync failed");
        }

        await Progress();
    }
}
