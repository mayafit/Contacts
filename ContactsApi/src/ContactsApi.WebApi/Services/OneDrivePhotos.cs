/**
 * @fileoverview OneDrive photos: connecting the account (Microsoft sign-in), the
 * background sync of the photos' details, and thumbnails through the server
 * @module ContactsApi.WebApi.Services
 */
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using ContactsApi.Infrastructure.Atlas;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ContactsApi.WebApi.Services;

/// <summary>The Entra app that reads OneDrive (MicrosoftOAuth__* from Vault and the config map)</summary>
public class OneDriveOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    /// <summary>{publicUrl}/api/atlas/connections/onedrive/callback</summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>Everything needed to connect is there</summary>
    public bool Configured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) && !string.IsNullOrWhiteSpace(RedirectUri);
}

/// <summary>The OneDrive connection as the app shows it</summary>
public record OneDriveStatus(bool Configured, SourceConnectionInfo? Connection, int Photos, int WithPosition);

/// <summary>Owners waiting for a OneDrive sync</summary>
public class PhotoSyncQueue
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();

    /// <summary>Asks for a sync of the owner's library</summary>
    public void Enqueue(string owner) => _queue.Writer.TryWrite(owner);

    /// <summary>The next owner, or null when none asked within the wait</summary>
    public async Task<string?> NextAsync(TimeSpan wait, CancellationToken ct)
    {
        if (_queue.Reader.TryRead(out var owner))
        {
            return owner;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        try
        {
            return await _queue.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}

/// <summary>
/// Access tokens per owner (an hour each), renewed from the stored refresh token.
/// Microsoft hands out a new refresh token with each renewal; it replaces the old one.
/// </summary>
public class OneDriveTokenCache
{
    private readonly ConcurrentDictionary<string, OneDriveTokens> _tokens = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>Remembers a fresh sign-in's token</summary>
    public void Put(string owner, OneDriveTokens tokens) => _tokens[owner] = tokens;

    /// <summary>
    /// Holds the owner's token lock (a new sign-in is stored under it, so a renewal
    /// running meanwhile cannot write the previous sign-in's token over it)
    /// </summary>
    public async Task<IDisposable> LockAsync(string owner, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(owner, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Release(gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                gate.Release();
            }
        }
    }

    /// <summary>Forgets the owner's token</summary>
    public void Forget(string owner) => _tokens.TryRemove(owner, out _);

    /// <summary>
    /// A valid access token (renewed when expired or when <paramref name="renew"/>).
    /// A refused renewal marks the connection expired.
    /// </summary>
    /// <exception cref="OneDriveException">Not connected, or the sign-in is no longer valid</exception>
    public async Task<string> GetAsync(string owner, AtlasDbContext db, IDataProtector protector, OneDriveClient client,
        OneDriveOptions options, bool renew, CancellationToken ct)
    {
        if (!renew && _tokens.TryGetValue(owner, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            return cached.AccessToken;
        }

        var gate = _locks.GetOrAdd(owner, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Another caller may have renewed it meanwhile
            if (_tokens.TryGetValue(owner, out cached) && cached.ExpiresAt > DateTime.UtcNow && (!renew || cached.ExpiresAt > DateTime.UtcNow.AddMinutes(55)))
            {
                return cached.AccessToken;
            }

            var conn = await db.Connections.AsNoTracking()
                .FirstOrDefaultAsync(c => c.OwnerId == owner && c.Provider == OneDrivePhotoService.Provider, ct);
            if (conn?.SecretProtected == null || conn.Status == "expired")
            {
                throw new OneDriveException("ONEDRIVE_SIGNIN_INVALID", "OneDrive is not connected. Connect it again.");
            }

            OneDriveTokens tokens;
            try
            {
                tokens = await client.RefreshAsync(options.ClientId, options.ClientSecret, protector.Unprotect(conn.SecretProtected), ct);
            }
            catch (Exception ex) when (ex is CryptographicException || ex is OneDriveException { Code: "ONEDRIVE_SIGNIN_INVALID" })
            {
                await db.Connections.Where(c => c.Id == conn.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, "expired")
                    .SetProperty(c => c.LastError, "The OneDrive sign-in is no longer valid. Connect again.")
                    .SetProperty(c => c.UpdatedAt, DateTime.UtcNow), ct);
                throw new OneDriveException("ONEDRIVE_SIGNIN_INVALID", "The OneDrive sign-in is no longer valid. Connect again.");
            }

            if (!string.IsNullOrEmpty(tokens.RefreshToken))
            {
                var secret = protector.Protect(tokens.RefreshToken);
                await db.Connections.Where(c => c.Id == conn.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.SecretProtected, secret), ct);
            }

            _tokens[owner] = tokens;
            return tokens.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>Connecting, syncing, disconnecting OneDrive, and its thumbnails</summary>
public class OneDrivePhotoService
{
    /// <summary>Provider name of OneDrive</summary>
    public const string Provider = "onedrive";

    /// <summary>Session key of the sign-in's state (CSRF)</summary>
    public const string StateKey = "OneDriveOAuthState";

    private readonly AtlasDbContext _db;
    private readonly AtlasService _atlas;
    private readonly OneDriveClient _client;
    private readonly IDataProtector _protector;
    private readonly OneDriveOptions _options;
    private readonly OneDriveTokenCache _tokens;
    private readonly PhotoSyncQueue _queue;

    /// <summary>Creates the service</summary>
    public OneDrivePhotoService(AtlasDbContext db, AtlasService atlas, IHttpClientFactory http, IDataProtectionProvider protection,
        IOptions<OneDriveOptions> options, OneDriveTokenCache tokens, PhotoSyncQueue queue)
    {
        _db = db;
        _atlas = atlas;
        _client = new OneDriveClient(http.CreateClient(OneDriveClient.HttpClientName));
        _protector = AtlasConnectionService.Protector(protection);
        _options = options.Value;
        _tokens = tokens;
        _queue = queue;
    }

    /// <summary>Whether it can be connected, the connection, and how many photos are known</summary>
    public async Task<OneDriveStatus> StatusAsync(string owner, CancellationToken ct)
    {
        var conn = await _db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.OwnerId == owner && c.Provider == Provider, ct);
        var (total, placed) = await _atlas.PhotoCountsAsync(owner, ct);
        return new OneDriveStatus(_options.Configured, conn == null ? null : AtlasConnectionService.Info(conn), total, placed);
    }

    /// <summary>Where the browser goes to sign in (the state is kept in the session)</summary>
    /// <exception cref="OneDriveException">The Microsoft app is not set up</exception>
    public string StartUrl(ISession session)
    {
        if (!_options.Configured)
        {
            throw new OneDriveException("ONEDRIVE_NOT_CONFIGURED", "OneDrive is not set up on the server yet.");
        }

        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        session.SetString(StateKey, state);
        return OneDriveClient.AuthorizeUrl(_options.ClientId, _options.RedirectUri, state);
    }

    /// <summary>
    /// Finishes the sign-in: exchanges the code, keeps the refresh token encrypted
    /// and starts the first sync
    /// </summary>
    public async Task CompleteAsync(string owner, string code, CancellationToken ct)
    {
        var tokens = await _client.RedeemCodeAsync(_options.ClientId, _options.ClientSecret, _options.RedirectUri, code, ct);
        if (string.IsNullOrEmpty(tokens.RefreshToken))
        {
            throw new OneDriveException("ONEDRIVE_FAILED", "Microsoft did not allow staying signed in. Connect again and accept all permissions.");
        }

        string? account = null;
        try
        {
            account = await _client.AccountAsync(tokens.AccessToken, ct);
        }
        catch (OneDriveException)
        {
            // The name is only shown; the connection works without it
        }

        using var held = await _tokens.LockAsync(owner, ct);
        var conn = await _db.Connections.FirstOrDefaultAsync(c => c.OwnerId == owner && c.Provider == Provider, ct);
        if (conn == null)
        {
            conn = new SourceConnection { OwnerId = owner, Provider = Provider };
            _db.Connections.Add(conn);
        }
        else if (conn.Account != null && account != null && !string.Equals(conn.Account, account, StringComparison.OrdinalIgnoreCase))
        {
            // Another account: its drive is listed from the start, the old photos go
            conn.Cursor = null;
            await _atlas.DeleteAllPhotosAsync(owner, Provider, ct);
        }

        conn.Account = account ?? conn.Account;
        conn.SecretProtected = _protector.Protect(tokens.RefreshToken);
        conn.Status = "syncing";
        conn.LastError = null;
        conn.ItemsDone = 0;
        conn.ItemsImported = 0;
        conn.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _tokens.Put(owner, tokens);
        _queue.Enqueue(owner);
    }

    /// <summary>Syncs again (what changed since the last sync)</summary>
    public async Task<OneDriveStatus> SyncAsync(string owner, CancellationToken ct)
    {
        var conn = await _db.Connections.FirstOrDefaultAsync(c => c.OwnerId == owner && c.Provider == Provider, ct)
                   ?? throw new AtlasNotFoundException("OneDrive connection");
        if (conn.SecretProtected == null || conn.Status == "expired")
        {
            throw new OneDriveException("ONEDRIVE_SIGNIN_INVALID", "The OneDrive sign-in is no longer valid. Connect again.");
        }

        if (conn.Status != "syncing")
        {
            conn.Status = "syncing";
            conn.LastError = null;
            conn.ItemsDone = 0;
            conn.ItemsImported = 0;
            conn.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _queue.Enqueue(owner);
        }

        return await StatusAsync(owner, ct);
    }

    /// <summary>Forgets the connection and the photos' details (the photos stay in OneDrive)</summary>
    public async Task<bool> DisconnectAsync(string owner, CancellationToken ct)
    {
        _tokens.Forget(owner);
        // The connection first: a sync running meanwhile sees it gone and stops (and clears up)
        var removed = await _db.Connections.Where(c => c.OwnerId == owner && c.Provider == Provider).ExecuteDeleteAsync(ct) > 0;
        await _atlas.DeleteAllPhotosAsync(owner, Provider, ct);
        return removed;
    }

    /// <summary>A photo's thumbnail from OneDrive: small, medium, large</summary>
    public async Task<(byte[] Bytes, string ContentType)?> ThumbnailAsync(string owner, Guid photoId, string size, CancellationToken ct)
    {
        var photo = await _atlas.GetPhotoAsync(owner, photoId, ct);
        var token = await AccessTokenAsync(owner, false, ct);
        try
        {
            return await _client.ThumbnailAsync(token, photo.ExternalId, size, ct);
        }
        catch (OneDriveException ex) when (ex.Code == "ONEDRIVE_TOKEN_EXPIRED")
        {
            return await _client.ThumbnailAsync(await AccessTokenAsync(owner, true, ct), photo.ExternalId, size, ct);
        }
    }

    private Task<string> AccessTokenAsync(string owner, bool renew, CancellationToken ct) =>
        _tokens.GetAsync(owner, _db, _protector, _client, _options, renew, ct);

    /// <summary>
    /// The sync itself (run by <see cref="PhotoSyncWorker"/>): the drive's changes
    /// since the last sync, or all of it the first time, page by page; then the
    /// clock check. Progress goes to the connection row.
    /// </summary>
    public async Task RunSyncAsync(string owner, ILogger logger, CancellationToken ct)
    {
        var conn = await _db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.OwnerId == owner && c.Provider == Provider, ct);
        if (conn?.SecretProtected == null || conn.Status == "expired")
        {
            return;
        }

        var id = conn.Id;
        var account = conn.Account;
        int done = 0, imported = 0, removed = 0;
        string status = "syncing";
        string? error = null;
        var lastSync = conn.LastSyncAt;
        var cursor = conn.Cursor;
        // Written only while the connection is still this account's (not after a disconnect or a new sign-in)
        Task<int> Progress(int? total = null) => _db.Connections.Where(c => c.Id == id && c.Account == account).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.Status, status)
            .SetProperty(c => c.ItemsDone, done)
            .SetProperty(c => c.ItemsImported, imported)
            .SetProperty(c => c.ItemsTotal, c => total ?? c.ItemsTotal)
            .SetProperty(c => c.LastError, error)
            .SetProperty(c => c.LastSyncAt, lastSync)
            .SetProperty(c => c.Cursor, cursor)
            .SetProperty(c => c.UpdatedAt, DateTime.UtcNow), ct);

        await Progress();
        try
        {
            var token = await AccessTokenAsync(owner, false, ct);
            var renewed = false;
            // Listing from the start: what it does not list any more is gone
            HashSet<string>? seen = cursor == null ? new() : null;
            var link = cursor;
            while (true)
            {
                OneDriveDeltaPage page;
                try
                {
                    page = await _client.DeltaAsync(token, link, ct);
                }
                catch (OneDriveException ex) when (ex.Code == "ONEDRIVE_TOKEN_EXPIRED" && !renewed)
                {
                    // Once per page: a token refused right after renewal is a real refusal
                    token = await AccessTokenAsync(owner, true, ct);
                    renewed = true;
                    continue;
                }
                catch (OneDriveException ex) when (ex.Code == "ONEDRIVE_RESYNC" && link != null)
                {
                    logger.LogInformation("OneDrive asked for a full listing");
                    link = null;
                    seen = new();
                    continue;
                }

                renewed = false;
                if (!await StillConnectedAsync(id, account, ct))
                {
                    logger.LogInformation("OneDrive was disconnected or connected to another account during the sync; it stops");
                    return;
                }

                imported += await _atlas.UpsertPhotosAsync(owner, Provider, page.Photos, ct);
                removed += await _atlas.RemovePhotosAsync(owner, Provider, page.Removed, ct);
                if (seen != null)
                {
                    seen.UnionWith(page.Photos.Select(p => p.Id));
                }

                done += page.Items;
                await Progress();
                if (page.NextLink != null)
                {
                    link = page.NextLink;
                    continue;
                }

                cursor = page.DeltaLink;
                break;
            }

            if (seen != null)
            {
                var known = await _db.Photos.AsNoTracking().Where(p => p.OwnerId == owner && p.Provider == Provider)
                    .Select(p => p.ExternalId).ToListAsync(ct);
                removed += await _atlas.RemovePhotosAsync(owner, Provider, known.Where(k => !seen.Contains(k)).ToList(), ct);
            }

            // The clock check reads the whole library: when photos changed, else once a day (the history grows)
            if (imported > 0 || removed > 0 || !Calibrated.TryGetValue(owner, out var at) || at < DateTime.UtcNow.AddDays(-1))
            {
                var clock = await _atlas.CalibratePhotoClocksAsync(owner, ct);
                Calibrated[owner] = DateTime.UtcNow;
                logger.LogInformation(
                    "OneDrive clock check: votes local {Local} / UTC {Utc} → photos {Photos}, videos {Videos}",
                    clock.LocalVotes, clock.UtcVotes, clock.LocalClock ? "local" : "UTC", clock.VideoLocalClock ? "local" : "UTC");
            }

            logger.LogInformation("OneDrive sync: {Items} items, {Imported} photos new or changed, {Removed} removed", done, imported, removed);
            status = "idle";
            lastSync = DateTime.UtcNow;
        }
        catch (OneDriveException ex) when (ex.Code == "ONEDRIVE_SIGNIN_INVALID")
        {
            status = "expired";
            error = ex.Message;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            status = "error";
            error = ex is OneDriveException ? ex.Message : "The sync failed. It is tried again later.";
            logger.LogError(ex, "OneDrive sync failed");
        }

        if (await Progress((await _atlas.PhotoCountsAsync(owner, CancellationToken.None)).Total) == 0
            && !await _db.Connections.AnyAsync(c => c.OwnerId == owner && c.Provider == Provider, CancellationToken.None))
        {
            // Disconnected meanwhile: what this sync added goes too
            await _atlas.DeleteAllPhotosAsync(owner, Provider, CancellationToken.None);
        }
    }

    /// <summary>When each owner's clock was last checked (the check reads the whole library)</summary>
    private static readonly ConcurrentDictionary<string, DateTime> Calibrated = new();

    private Task<bool> StillConnectedAsync(Guid id, string? account, CancellationToken ct) =>
        _db.Connections.AnyAsync(c => c.Id == id && c.Account == account && c.SecretProtected != null, ct);
}

/// <summary>
/// Syncs OneDrive libraries in the background: when asked (connect, Sync again),
/// and every 30 minutes for every connected library so new photos come in on their
/// own. Placing the photos is the fusion service's (it knows when tracks change).
/// </summary>
public class PhotoSyncWorker : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(30);
    private readonly IServiceScopeFactory _scopes;
    private readonly PhotoSyncQueue _queue;
    private readonly AtlasReadiness _readiness;
    private readonly FusionSignal _signal;
    private readonly ILogger<PhotoSyncWorker> _logger;

    /// <summary>Creates the worker</summary>
    public PhotoSyncWorker(IServiceScopeFactory scopes, PhotoSyncQueue queue, AtlasReadiness readiness, FusionSignal signal,
        ILogger<PhotoSyncWorker> logger)
    {
        _scopes = scopes;
        _queue = queue;
        _readiness = readiness;
        _signal = signal;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!_readiness.Ready && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        // Every library once at start (a sync cut short by a restart goes on), then every 30 minutes
        await EnqueueAllAsync(stoppingToken);
        var nextRound = DateTime.UtcNow + Every;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var wait = nextRound - DateTime.UtcNow;
                var owner = await _queue.NextAsync(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, stoppingToken);
                if (owner == null)
                {
                    nextRound = DateTime.UtcNow + Every;
                    await EnqueueAllAsync(stoppingToken);
                    continue;
                }

                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<OneDrivePhotoService>().RunSyncAsync(owner, _logger, stoppingToken);
                // New photos wait for placement, which the fusion service does
                _signal.Notify();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OneDrive sync run failed");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task EnqueueAllAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var owners = await scope.ServiceProvider.GetRequiredService<AtlasDbContext>().Connections.AsNoTracking()
                .Where(c => c.Provider == OneDrivePhotoService.Provider && c.SecretProtected != null && c.Status != "expired")
                .Select(c => c.OwnerId).ToListAsync(ct);
            owners.ForEach(_queue.Enqueue);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Listing OneDrive connections failed");
        }
    }
}
