/**
 * @fileoverview Builds activity snapshots in the background
 * @module ContactsApi.WebApi.Services
 */
using System.Threading.Channels;
using ContactsApi.Infrastructure.Activity;
using ContactsApi.Infrastructure.Google;

namespace ContactsApi.WebApi.Services;

/// <summary>OAuth scopes the activity snapshot needs</summary>
public static class ActivityScopes
{
    /// <summary>Gmail headers and labels, never content</summary>
    public const string Gmail = "https://www.googleapis.com/auth/gmail.metadata";

    /// <summary>Calendar events, read only</summary>
    public const string Calendar = "https://www.googleapis.com/auth/calendar.events.readonly";

    /// <summary>Gmail message content (opt-in, for signatures)</summary>
    public const string GmailRead = "https://www.googleapis.com/auth/gmail.readonly";

    /// <summary>Session key holding the scopes granted at the last sign-in</summary>
    public const string SessionKey = "GrantedScopes";
}

/// <summary>A request to bring one user's snapshot up to date</summary>
/// <param name="UserId">Google account id</param>
/// <param name="UserEmail">The signed-in address</param>
/// <param name="Full">Rescan everything instead of reading only what is new</param>
/// <param name="Gmail">Gmail access was granted</param>
/// <param name="Calendar">Calendar access was granted</param>
public record ActivityJob(string UserId, string UserEmail, bool Full, bool Gmail = true, bool Calendar = true);

/// <summary>
/// Queue of activity scans. A scan can take minutes (Gmail's quota allows about
/// 40 messages a second), so it runs here and the browser polls its progress.
/// </summary>
public class ActivityWorker : BackgroundService
{
    /// <summary>How far back a full scan reads</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(365);

    /// <summary>A scan whose heartbeat is older than this died (e.g. pod restart)</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(3);

    /// <summary>Full rescan this often; in between only new messages are read</summary>
    private static readonly TimeSpan FullScanEvery = TimeSpan.FromDays(7);

    private const int MaxMessages = 10_000;

    private readonly Channel<ActivityJob> _jobs = Channel.CreateUnbounded<ActivityJob>();
    private readonly ActivityStore _store;
    private readonly GoogleTokenService _tokens;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ActivityWorker> _logger;

    /// <summary>Creates the worker</summary>
    public ActivityWorker(ActivityStore store, GoogleTokenService tokens, ILoggerFactory loggerFactory)
    {
        _store = store;
        _tokens = tokens;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ActivityWorker>();
    }

    /// <summary>
    /// Starts a scan unless one is already running for the user.
    /// </summary>
    /// <param name="job">What to scan</param>
    /// <returns>The snapshot as it stands after queueing</returns>
    public async Task<ActivitySnapshot> EnqueueAsync(ActivityJob job)
    {
        var snapshot = await _store.GetAsync(job.UserId);
        if (IsRunning(snapshot))
        {
            return snapshot;
        }

        snapshot.Status = ActivityStatus.Running;
        snapshot.Progress = new ActivityProgress { Phase = "queued" };
        snapshot.HeartbeatAt = DateTime.UtcNow;
        snapshot.Error = null;
        snapshot.ErrorCode = null;
        await _store.SaveAsync(job.UserId, snapshot);
        await _jobs.Writer.WriteAsync(job);
        return snapshot;
    }

    /// <summary>Whether a live scan is in progress</summary>
    /// <param name="snapshot">Snapshot</param>
    /// <returns>True while running and not stale</returns>
    public static bool IsRunning(ActivitySnapshot snapshot) =>
        snapshot.Status == ActivityStatus.Running
        && snapshot.HeartbeatAt != null
        && DateTime.UtcNow - snapshot.HeartbeatAt < StaleAfter;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Several users can scan at once; each scan is paced to its own user's quota
        await Task.WhenAll(Enumerable.Range(0, Concurrency).Select(_ => ConsumeAsync(stoppingToken)));
    }

    /// <summary>Scans run in parallel (one per user at a time)</summary>
    private const int Concurrency = 3;

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Activity scan crashed");
            }
        }
    }

    private async Task RunAsync(ActivityJob job, CancellationToken ct)
    {
        var snapshot = await _store.GetAsync(job.UserId);
        var lastBeat = DateTime.MinValue;

        async Task Beat(string phase, int done)
        {
            snapshot.Progress = new ActivityProgress { Phase = phase, Done = done };
            snapshot.HeartbeatAt = DateTime.UtcNow;
            if (DateTime.UtcNow - lastBeat > TimeSpan.FromSeconds(2))
            {
                lastBeat = DateTime.UtcNow;
                await _store.SaveAsync(job.UserId, snapshot);
            }
        }

        try
        {
            // Asked again for every request: a long scan outlives one access token
            async Task<string> Token(CancellationToken _) =>
                await _tokens.GetAccessTokenForUserAsync(job.UserId)
                ?? throw new ActivityScanException("NOT_CONNECTED", "Your Google session ended. Sign in again.");
            var token = await Token(ct);

            // The contacts' addresses decide whose mail is kept
            await Beat("contacts", 0);
            var people = new GooglePeopleApiClient(token, _loggerFactory.CreateLogger<GooglePeopleApiClient>());
            var contactEmails = new List<string>();
            string? page = null;
            do
            {
                var (contacts, next) = await people.FetchAllContactsAsync(1000, page);
                contactEmails.AddRange(contacts.SelectMany(c => c.EmailAddresses ?? new()).Select(e => e.Value ?? string.Empty));
                page = next;
                await Beat("contacts", contactEmails.Count);
            }
            while (page != null);

            var client = new GoogleActivityClient(Token, _loggerFactory.CreateLogger<GoogleActivityClient>());
            var (mailbox, historyId) = job.Gmail ? await client.GetProfileAsync(ct) : (string.Empty, (ulong?)null);
            var mine = new[] { job.UserEmail, mailbox };
            var now = DateTime.UtcNow;

            var full = !job.Gmail
                || job.Full
                || snapshot.Directory == null
                || snapshot.Threads == null
                || snapshot.GmailHistoryId == null
                || snapshot.FullScanAt == null
                || now - snapshot.FullScanAt > FullScanEvery;
            ActivityAggregator? aggregator = null;
            if (job.Gmail && !full)
            {
                // Work on a copy: a scan that fails half-way must not leave the stored
                // data partly updated, or the retry would count those messages twice
                var copy = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, PersonActivity>>(
                    System.Text.Json.JsonSerializer.Serialize(snapshot.People))!;
                var directoryCopy = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, DirectoryEntry>>(
                    System.Text.Json.JsonSerializer.Serialize(snapshot.Directory))!;
                aggregator = new ActivityAggregator(contactEmails, mine, copy, directoryCopy);
                aggregator.SeedThreads(snapshot.Threads);
                var read = await client.ScanMailChangesAsync(
                    aggregator, snapshot.GmailHistoryId!.Value, historyId, n => Beat("gmail", n), ct);
                full = read == null;
                snapshot.MessagesScanned = read ?? 0;
            }

            if (full)
            {
                aggregator = new ActivityAggregator(contactEmails, mine);
                if (job.Gmail)
                {
                    var since = now - Window;
                    var scan = await client.ScanMailAsync(
                        aggregator, since, MaxMessages, historyId, n => Beat("gmail", n), ct);
                    snapshot.MessagesScanned = scan.Read;
                    // A mailbox bigger than the cap is covered only back to the oldest message read
                    snapshot.Since = scan.Truncated && scan.Oldest != null ? scan.Oldest : since;
                    snapshot.FullScanAt = now;
                }
                else
                {
                    snapshot.MessagesScanned = 0;
                    snapshot.Since = null;
                    snapshot.FullScanAt = null;
                }
            }

            aggregator!.ClearMeetings();
            if (job.Calendar)
            {
                await client.ScanCalendarAsync(aggregator, now - Window, now.AddDays(60), n => Beat("calendar", n), ct);
            }

            aggregator.PruneDirectory();
            aggregator.PruneThreads(DateTime.UtcNow);
            snapshot.Threads = aggregator.Threads;
            snapshot.People = aggregator.People;
            snapshot.Directory = aggregator.Directory;
            snapshot.GmailHistoryId = historyId;
            snapshot.Status = ActivityStatus.Ready;
            snapshot.UpdatedAt = DateTime.UtcNow;
            snapshot.Progress = null;
            snapshot.Error = null;
            snapshot.ErrorCode = null;
            // Counts only, no addresses: enough to see whether headers parse
            _logger.LogInformation(
                "Activity scan done ({Mode}, {Messages} messages, {People} addresses with activity, " +
                "{Directory} in directory, {Named} with names, {Undecoded} undecoded names, " +
                "{NoRecipients} messages without To/Cc, {ContactEmails} contact emails, since {Since:yyyy-MM-dd})",
                full ? "full" : "incremental", snapshot.MessagesScanned, snapshot.People.Count,
                aggregator.Directory.Count, aggregator.Directory.Values.Count(e => e.Names.Count > 0),
                aggregator.UndecodedNames, aggregator.MessagesWithoutRecipients, contactEmails.Count, snapshot.Since);
        }
        catch (ActivityScanException ex)
        {
            _logger.LogWarning("Activity scan stopped: {Code} {Message}", ex.Code, ex.Message);
            Fail(snapshot, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Activity scan failed");
            Fail(snapshot, "SCAN_FAILED", "Reading Gmail and Calendar failed. Try again later.");
        }

        await _store.SaveAsync(job.UserId, snapshot);
    }

    /// <summary>Marks the scan failed; the previous data stays usable</summary>
    private static void Fail(ActivitySnapshot snapshot, string code, string message)
    {
        snapshot.Status = ActivityStatus.Error;
        snapshot.ErrorCode = code;
        snapshot.Error = message;
        snapshot.Progress = null;
    }
}
