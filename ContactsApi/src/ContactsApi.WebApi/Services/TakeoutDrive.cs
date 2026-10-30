/**
 * @fileoverview Reviews from a scheduled Takeout on Google Drive (AD-40): the
 * connection, the daily check that picks up new exports, and its worker
 * @module ContactsApi.WebApi.Services
 */
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Channels;
using ContactsApi.Infrastructure.Atlas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ContactsApi.WebApi.Services;

/// <summary>Takeout pickup settings (Takeout__* in the config map)</summary>
public class TakeoutOptions
{
    /// <summary>A larger export is not downloaded (MB)</summary>
    public int MaxZipMb { get; set; } = 500;
}

/// <summary>The Drive connection as the app shows it</summary>
/// <param name="Granted">The Owner's Google sign-in allows reading Drive</param>
/// <param name="LastExport">The newest export handled, and the reviews it brought</param>
/// <param name="Stale">No new export for 70 days: the Takeout schedule may have ended</param>
public record TakeoutDriveStatus(bool Granted, SourceConnectionInfo? Connection, TakeoutSeen? LastExport, bool Stale);

/// <summary>An export already handled (AD-40 rule 3)</summary>
/// <param name="Reviews">Reviews read from it; null when none could be (Note says why)</param>
public record TakeoutSeen(string Id, string Name, DateTime Modified, int? Reviews, string? Note);

/// <summary>The connection's cursor: the exports handled, newest first</summary>
public record TakeoutCursor(List<TakeoutSeen> Seen);

/// <summary>The Owner's Google access for Drive: their stored sign-in (AD-40 rule 2)</summary>
public interface ITakeoutTokens
{
    /// <summary>The stored sign-in allows reading Drive</summary>
    Task<bool> GrantedAsync(string owner);

    /// <summary>An access token; without one, whether the sign-in was refused (sign in again) rather than Google unreachable</summary>
    Task<(string? Token, bool Refused)> AccessTokenAsync(string owner);
}

/// <summary>The Owner's stored Google sign-in (the refresh token the login keeps)</summary>
public class GoogleTakeoutTokens : ITakeoutTokens
{
    private readonly GoogleTokenService _tokens;

    /// <summary>Creates the adapter</summary>
    public GoogleTakeoutTokens(GoogleTokenService tokens) => _tokens = tokens;

    /// <inheritdoc />
    public async Task<bool> GrantedAsync(string owner) => (await _tokens.StoredGrantScopesAsync(owner))?.Contains(TakeoutScopes.Drive) == true;

    /// <inheritdoc />
    public Task<(string? Token, bool Refused)> AccessTokenAsync(string owner) => _tokens.GetAccessTokenOrWhyAsync(owner);
}

/// <summary>Owners waiting for a Drive check</summary>
public class TakeoutDriveQueue
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();

    /// <summary>Asks for a check of the owner's Drive</summary>
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

/// <summary>Connects an Owner's Drive and picks up their Takeout exports (AD-40)</summary>
public class TakeoutDriveService
{
    /// <summary>The connection's provider</summary>
    public const string Provider = "google-drive-takeout";

    /// <summary>Exports kept in the seen list</summary>
    public const int SeenKept = 100;

    /// <summary>Reviews per ingest batch</summary>
    public const int Batch = 4000;

    /// <summary>No new export for this long: the schedule may have ended</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(70);

    /// <summary>The note of lost access</summary>
    public const string SignInAgain = "Sign in to Google again and allow Drive access.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly AtlasDbContext _db;
    private readonly AtlasService _atlas;
    private readonly ITakeoutTokens _tokens;
    private readonly GoogleDriveTakeout _drive;
    private readonly TakeoutDriveQueue _queue;
    private readonly TakeoutOptions _options;
    private readonly ILogger<TakeoutDriveService> _logger;

    /// <summary>Creates the service</summary>
    public TakeoutDriveService(AtlasDbContext db, AtlasService atlas, ITakeoutTokens tokens, IHttpClientFactory http, TakeoutDriveQueue queue,
        IOptions<TakeoutOptions> options, ILogger<TakeoutDriveService> logger)
    {
        _db = db;
        _atlas = atlas;
        _tokens = tokens;
        _drive = new GoogleDriveTakeout(http.CreateClient(GoogleDriveTakeout.HttpClientName));
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Whether the Owner's sign-in allows Drive, the connection, and the last export</summary>
    public async Task<TakeoutDriveStatus> StatusAsync(string owner, CancellationToken ct)
    {
        var conn = await _db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.OwnerId == owner && c.Provider == Provider, ct);
        // Google is asked only when it matters (not connected, or access lost), not on every poll
        var granted = conn != null && conn.Status != "expired" || await _tokens.GrantedAsync(owner);
        if (conn == null)
        {
            return new TakeoutDriveStatus(granted, null, null, false);
        }

        var last = Cursor(conn).Seen.FirstOrDefault();
        // Only once checked: a connection that saw no export yet is not stale
        var stale = conn.LastSyncAt != null && last != null && DateTime.UtcNow - last.Modified > StaleAfter;
        return new TakeoutDriveStatus(granted, Info(conn), last, stale);
    }

    /// <summary>Connects (or reconnects) the Owner's Drive and asks for a check</summary>
    /// <exception cref="ArgumentException">The sign-in does not allow Drive yet</exception>
    public async Task<TakeoutDriveStatus> ConnectAsync(string owner, string? email, CancellationToken ct)
    {
        if (!await _tokens.GrantedAsync(owner))
        {
            throw new ArgumentException("Allow Echo to read Google Drive first (Connect Google Drive).");
        }

        var conn = await _db.Connections.FirstOrDefaultAsync(c => c.OwnerId == owner && c.Provider == Provider, ct);
        if (conn == null)
        {
            conn = new SourceConnection { OwnerId = owner, Provider = Provider, Cursor = JsonSerializer.Serialize(new TakeoutCursor(new()), Json) };
            _db.Connections.Add(conn);
        }

        conn.Account = email;
        conn.Status = "idle";
        conn.LastError = null;
        conn.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _queue.Enqueue(owner);
        return await StatusAsync(owner, ct);
    }

    /// <summary>Asks for a check now</summary>
    /// <exception cref="AtlasNotFoundException">Not connected</exception>
    public async Task<TakeoutDriveStatus> SyncAsync(string owner, CancellationToken ct)
    {
        if (!await _db.Connections.AnyAsync(c => c.OwnerId == owner && c.Provider == Provider, ct))
        {
            throw new AtlasNotFoundException("Google Drive connection");
        }

        _queue.Enqueue(owner);
        return await StatusAsync(owner, ct);
    }

    /// <summary>Disconnects: no more checks, and the seen list goes (the imported reviews stay)</summary>
    public async Task<bool> DisconnectAsync(string owner, CancellationToken ct) =>
        await _db.Connections.Where(c => c.OwnerId == owner && c.Provider == Provider).ExecuteDeleteAsync(ct) > 0;

    /// <summary>
    /// One check of the Owner's Drive (AD-40 rule 4). The check claims the connection first
    /// (one at a time, also across pods), and always ends with a status: idle, error, or
    /// expired when the sign-in no longer allows Drive. New Takeout zips are read oldest
    /// first; an export whose outcome would not change (read, too large, no reviews, not a
    /// zip) is seen and never read again; one that failed for a passing reason is left for
    /// the next check.
    /// </summary>
    public async Task CheckAsync(string owner, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var stuck = now - ClaimTimeout;
        var claimed = await _db.Connections
            .Where(c => c.OwnerId == owner && c.Provider == Provider && c.Status != "expired" && (c.Status != "syncing" || c.UpdatedAt < stuck))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "syncing").SetProperty(c => c.UpdatedAt, now), ct);
        if (claimed == 0)
        {
            return;
        }

        // The claim was written past the change tracker: read the row afresh
        _db.ChangeTracker.Clear();
        var conn = await _db.Connections.FirstOrDefaultAsync(c => c.OwnerId == owner && c.Provider == Provider, ct);
        if (conn == null)
        {
            return;
        }

        var status = "idle";
        string? error = null;
        try
        {
            var (token, refused) = await _tokens.AccessTokenAsync(owner);
            if (token == null)
            {
                throw refused
                    ? new GoogleDriveException(SignInAgain, unauthorized: true)
                    : new GoogleDriveException("Google could not be reached to renew the sign-in.", transient: true);
            }

            var cursor = Cursor(conn);
            var seen = cursor.Seen.Select(s => s.Id).ToHashSet();
            var fresh = (await _drive.ListAsync(token, ct)).Where(f => !seen.Contains(f.Id)).OrderBy(f => f.Modified).ToList();
            // The first check takes only the newest export (its parts): older ones are not downloaded
            if (cursor.Seen.Count == 0 && conn.LastSyncAt == null && fresh.Count > 0)
            {
                var newest = ExportSet(fresh[^1].Name);
                foreach (var old in fresh.Where(f => ExportSet(f.Name) != newest))
                {
                    cursor.Seen.Add(new TakeoutSeen(old.Id, old.Name, old.Modified, null, "older than the first export picked up"));
                }

                fresh = fresh.Where(f => ExportSet(f.Name) == newest).ToList();
                Save(conn, cursor);
            }

            string? lastNote = null;
            foreach (var file in fresh)
            {
                var read = await ReadAsync(owner, token, file, ct);
                if (read.Retry)
                {
                    // A passing failure: left unseen for the next check
                    status = "error";
                    error = $"{file.Name}: {read.Note} It is tried again at the next check.";
                    break;
                }

                cursor.Seen.Add(new TakeoutSeen(file.Id, file.Name, file.Modified, read.Reviews, read.Note));
                conn.ItemsImported += read.Added;
                if (read.Note != null)
                {
                    lastNote = $"{file.Name}: {read.Note}";
                }

                // Saved after every export: a restart never imports one twice
                Save(conn, cursor);
                await _db.SaveChangesAsync(ct);
            }

            error ??= lastNote;
        }
        catch (GoogleDriveException ex)
        {
            status = ex.Unauthorized ? "expired" : "error";
            error = ex.Unauthorized ? SignInAgain : $"{ex.Message} It is tried again at the next check.";
            _logger.LogWarning("Takeout on Drive: the check failed (unauthorized {Unauthorized}): {Message}", ex.Unauthorized, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: the next start resets the claim
            return;
        }
        catch (Exception ex)
        {
            status = "error";
            error = "The check failed. It is tried again at the next check.";
            _logger.LogError(ex, "Takeout on Drive: the check failed");
        }

        try
        {
            conn.Status = status;
            conn.LastError = error;
            conn.LastSyncAt = DateTime.UtcNow;
            conn.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException ex)
        {
            // Disconnected meanwhile: nothing to record
            _logger.LogInformation(ex, "Takeout on Drive: the connection went during a check");
        }
    }

    /// <summary>A claim older than this is a check that died (a crash): it may be claimed again</summary>
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromHours(1);

    /// <summary>The export a part belongs to: takeout-&lt;time&gt; of takeout-&lt;time&gt;-&lt;n&gt;.zip</summary>
    public static string ExportSet(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var dash = stem.LastIndexOf('-');
        return dash > "takeout".Length ? stem[..dash] : stem;
    }

    private static void Save(SourceConnection conn, TakeoutCursor cursor)
    {
        conn.Cursor = JsonSerializer.Serialize(new TakeoutCursor(cursor.Seen.OrderByDescending(s => s.Modified).Take(SeenKept).ToList()), Json);
        conn.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>What reading one export gave</summary>
    /// <param name="Reviews">Reviews read (null when none could be)</param>
    /// <param name="Added">Reviews that were new</param>
    /// <param name="Retry">A passing failure: try the export again later</param>
    private sealed record ExportRead(int? Reviews, int Added, string? Note, bool Retry = false);

    /// <summary>One export: downloaded to a temporary file, its reviews read and imported, the file deleted</summary>
    /// <exception cref="GoogleDriveException">The sign-in no longer allows Drive</exception>
    private async Task<ExportRead> ReadAsync(string owner, string token, TakeoutFile file, CancellationToken ct)
    {
        var max = (long)_options.MaxZipMb * 1024 * 1024;
        var tooLarge = $"larger than {_options.MaxZipMb} MB; choose only \"Maps (your places)\" for this schedule";
        if (file.Size > max)
        {
            return new ExportRead(null, 0, tooLarge);
        }

        var path = Path.Combine(Path.GetTempPath(), $"takeout-{Guid.NewGuid():N}.zip");
        try
        {
            try
            {
                await _drive.DownloadAsync(token, file.Id, path, max, ct);
            }
            catch (GoogleDriveException ex) when (!ex.Unauthorized)
            {
                return ex.Message == GoogleDriveTakeout.TooLarge
                    ? new ExportRead(null, 0, tooLarge)
                    : new ExportRead(null, 0, ex.Message, ex.Transient);
            }
            catch (IOException)
            {
                // The temporary disk is full or failing: a passing reason
                return new ExportRead(null, 0, "The download could not be stored.", true);
            }

            List<ReviewInput>? reviews;
            string? note;
            try
            {
                using var zip = ZipFile.OpenRead(path);
                (reviews, note) = GoogleReviewsParser.FromZip(zip);
            }
            catch (InvalidDataException)
            {
                return new ExportRead(null, 0, "not a readable zip");
            }

            if (reviews == null)
            {
                return new ExportRead(null, 0, note);
            }

            // Every export holds all the reviews: only the new ones are counted
            var before = await _db.Reviews.CountAsync(r => r.OwnerId == owner && r.Source == "google-maps", ct);
            Guid? importId = null;
            foreach (var chunk in reviews.Chunk(Batch))
            {
                var result = await _atlas.IngestAsync(owner, new IngestRequest
                {
                    ImportId = importId, Source = "google-maps", FileName = $"Drive: {file.Name}", Reviews = chunk.ToList()
                }, ct);
                importId = result.ImportId;
            }

            var added = await _db.Reviews.CountAsync(r => r.OwnerId == owner && r.Source == "google-maps", ct) - before;
            _logger.LogInformation("Takeout on Drive: {Count} reviews from an export, {Added} new", reviews.Count, added);
            return new ExportRead(reviews.Count, Math.Max(0, added), null);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Still held or already gone: the emptyDir goes with the pod
            }
        }
    }

    private static TakeoutCursor Cursor(SourceConnection conn)
    {
        try
        {
            return string.IsNullOrEmpty(conn.Cursor)
                ? new TakeoutCursor(new())
                : JsonSerializer.Deserialize<TakeoutCursor>(conn.Cursor, Json) ?? new TakeoutCursor(new());
        }
        catch (JsonException)
        {
            return new TakeoutCursor(new());
        }
    }

    private static SourceConnectionInfo Info(SourceConnection c) =>
        new(c.Provider, c.Account, c.Status, c.LastSyncAt, c.LastError, c.ItemsTotal, c.ItemsDone, c.ItemsImported);
}

/// <summary>Checks connected Drives: at start, then those last checked over a day ago, and on request (AD-40 rule 5)</summary>
public class TakeoutDriveWorker : BackgroundService
{
    private static readonly TimeSpan Round = TimeSpan.FromHours(1);
    private static readonly TimeSpan Daily = TimeSpan.FromHours(24);
    private readonly IServiceScopeFactory _scopes;
    private readonly TakeoutDriveQueue _queue;
    private readonly AtlasReadiness _readiness;
    private readonly ILogger<TakeoutDriveWorker> _logger;

    /// <summary>Creates the worker</summary>
    public TakeoutDriveWorker(IServiceScopeFactory scopes, TakeoutDriveQueue queue, AtlasReadiness readiness, ILogger<TakeoutDriveWorker> logger)
    {
        _scopes = scopes;
        _queue = queue;
        _readiness = readiness;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!_readiness.Ready && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        await ResetClaimsAsync(stoppingToken);
        await EnqueueDueAsync(stoppingToken);
        var nextRound = DateTime.UtcNow + Round;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var wait = nextRound - DateTime.UtcNow;
                var owner = await _queue.NextAsync(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, stoppingToken);
                if (owner == null)
                {
                    nextRound = DateTime.UtcNow + Round;
                    await EnqueueDueAsync(stoppingToken);
                    continue;
                }

                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<TakeoutDriveService>().CheckAsync(owner, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Takeout on Drive: a check failed");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    /// <summary>A check cut short by a restart left its connection syncing: idle again, so it is checked</summary>
    private async Task ResetClaimsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var stuck = DateTime.UtcNow - TakeoutDriveService.ClaimTimeout;
            await scope.ServiceProvider.GetRequiredService<AtlasDbContext>().Connections
                .Where(c => c.Provider == TakeoutDriveService.Provider && c.Status == "syncing" && c.UpdatedAt < stuck)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, "idle").SetProperty(c => c.LastSyncAt, (DateTime?)null), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Takeout on Drive: resetting cut-short checks failed");
        }
    }

    /// <summary>Queues the connections last checked over a day ago (never checked: now)</summary>
    private async Task EnqueueDueAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var due = DateTime.UtcNow - Daily;
            var owners = await scope.ServiceProvider.GetRequiredService<AtlasDbContext>().Connections.AsNoTracking()
                .Where(c => c.Provider == TakeoutDriveService.Provider && c.Status != "expired" && (c.LastSyncAt == null || c.LastSyncAt < due))
                .Select(c => c.OwnerId).ToListAsync(ct);
            owners.ForEach(_queue.Enqueue);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Takeout on Drive: listing the connections failed");
        }
    }
}
