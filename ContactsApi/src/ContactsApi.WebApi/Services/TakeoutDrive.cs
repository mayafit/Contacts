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

    /// <summary>An access token, or null when the Owner must sign in again</summary>
    Task<string?> AccessTokenAsync(string owner);
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
    public Task<string?> AccessTokenAsync(string owner) => _tokens.GetAccessTokenForUserAsync(owner);
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
        var granted = await _tokens.GrantedAsync(owner);
        var conn = await _db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.OwnerId == owner && c.Provider == Provider, ct);
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
    /// One check of the Owner's Drive (AD-40 rule 4): new Takeout zips, oldest first, each
    /// read for its reviews once; lost access marks the connection expired
    /// </summary>
    public async Task CheckAsync(string owner, CancellationToken ct)
    {
        var conn = await _db.Connections.FirstOrDefaultAsync(c => c.OwnerId == owner && c.Provider == Provider, ct);
        if (conn == null || conn.Status == "expired")
        {
            return;
        }

        conn.Status = "syncing";
        conn.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        var cursor = Cursor(conn);
        try
        {
            var token = await _tokens.AccessTokenAsync(owner) ?? throw new GoogleDriveException(SignInAgain, true);
            var seen = cursor.Seen.Select(s => s.Id).ToHashSet();
            var fresh = (await _drive.ListAsync(token, ct)).Where(f => !seen.Contains(f.Id)).OrderBy(f => f.Modified).ToList();
            string? lastNote = null;
            foreach (var file in fresh)
            {
                var (reviews, note) = await ReadAsync(owner, token, file, ct);
                cursor.Seen.Insert(0, new TakeoutSeen(file.Id, file.Name, file.Modified, reviews, note));
                lastNote = note == null ? null : $"{file.Name}: {note}";
                if (reviews > 0)
                {
                    conn.ItemsImported += reviews.Value;
                }

                // Saved after every export: a restart never imports one twice
                conn.Cursor = JsonSerializer.Serialize(new TakeoutCursor(cursor.Seen.OrderByDescending(s => s.Modified).Take(SeenKept).ToList()), Json);
                conn.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }

            conn.Status = "idle";
            conn.LastError = fresh.Count > 0 ? lastNote : conn.LastError;
            conn.LastSyncAt = DateTime.UtcNow;
        }
        catch (GoogleDriveException ex)
        {
            conn.Status = ex.Unauthorized ? "expired" : "error";
            conn.LastError = ex.Unauthorized ? SignInAgain : ex.Message;
            conn.LastSyncAt = DateTime.UtcNow;
            _logger.LogWarning("Takeout on Drive: the check failed ({Unauthorized}): {Message}", ex.Unauthorized, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            conn.Status = "error";
            conn.LastError = "Google Drive could not be reached.";
            conn.LastSyncAt = DateTime.UtcNow;
            _logger.LogWarning(ex, "Takeout on Drive: Google Drive could not be reached");
        }

        conn.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>One export: downloaded to a temporary file, its reviews read and imported, the file deleted</summary>
    /// <returns>The reviews read (null when none could be), and why not</returns>
    private async Task<(int? Reviews, string? Note)> ReadAsync(string owner, string token, TakeoutFile file, CancellationToken ct)
    {
        var max = (long)_options.MaxZipMb * 1024 * 1024;
        if (file.Size > max)
        {
            return (null, $"larger than {_options.MaxZipMb} MB; choose only \"Maps (your places)\" for this schedule");
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
                return (null, ex.Message.Contains("larger", StringComparison.Ordinal)
                    ? $"larger than {_options.MaxZipMb} MB; choose only \"Maps (your places)\" for this schedule"
                    : ex.Message);
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
                return (null, "not a readable zip");
            }

            if (reviews == null)
            {
                return (null, note);
            }

            Guid? importId = null;
            foreach (var chunk in reviews.Chunk(Batch))
            {
                var result = await _atlas.IngestAsync(owner, new IngestRequest
                {
                    ImportId = importId, Source = "google-maps", FileName = $"Drive: {file.Name}", Reviews = chunk.ToList()
                }, ct);
                importId = result.ImportId;
            }

            _logger.LogInformation("Takeout on Drive: {Count} reviews from an export", reviews.Count);
            return (reviews.Count, null);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Gone already, or still held: the emptyDir goes with the pod
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
