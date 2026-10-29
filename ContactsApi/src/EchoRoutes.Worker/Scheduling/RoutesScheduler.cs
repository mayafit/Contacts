/**
 * @fileoverview The service's scheduler (AD-30, AD-36 rule 4): one bounded batch per
 * Owner per turn, round-robin, interactive jobs first, pauses between batches, polls
 * each Owner's change feed and wakes early on /wake
 * @module EchoRoutes.Worker.Scheduling
 */
using System.Collections.Concurrent;
using System.Text.Json;
using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker.Assess;
using EchoRoutes.Worker.Backend;
using EchoRoutes.Worker.Matching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EchoRoutes.Worker.Scheduling;

/// <summary>Waits between batches (a seam, so tests can count the pauses without waiting)</summary>
public interface IPause
{
    /// <summary>Waits for the time</summary>
    Task DelayAsync(TimeSpan time, CancellationToken ct);
}

/// <summary>Real waiting</summary>
public class TaskPause : IPause
{
    /// <inheritdoc />
    public Task DelayAsync(TimeSpan time, CancellationToken ct) => Task.Delay(time, ct);
}

/// <summary>Wakes the scheduler: an Owner has new fused data, or a job was queued</summary>
public class WakeSignal
{
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ConcurrentDictionary<string, byte> _owners = new();

    /// <summary>Marks the Owner's feed due and wakes the scheduler</summary>
    public void Wake(string owner)
    {
        _owners[owner] = 0;
        Poke();
    }

    /// <summary>Wakes the scheduler (a job was queued)</summary>
    public void Poke()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    private int _interactive;

    /// <summary>An interactive job was queued: running batches stop after their current record</summary>
    public void Interactive()
    {
        Interlocked.Exchange(ref _interactive, 1);
        Poke();
    }

    /// <summary>Interactive work is waiting (asked between records)</summary>
    public bool InteractivePending => Volatile.Read(ref _interactive) == 1;

    /// <summary>Clears the hint (the scheduler is about to run the interactive jobs)</summary>
    public void TakeInteractive() => Interlocked.Exchange(ref _interactive, 0);

    /// <summary>Takes the Owners woken since the last call</summary>
    public List<string> TakeWoken()
    {
        var owners = _owners.Keys.ToList();
        foreach (var o in owners)
        {
            _owners.TryRemove(o, out _);
        }

        return owners;
    }

    /// <summary>Waits for a wake or the timeout</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _wake.WaitAsync(timeout, ct);
}

/// <summary>A job's scope: ids (the backend resolves any query into ids), a time range, or everything</summary>
/// <param name="Force">improve only: skip the "needs improvement" gate (never a reject decision)</param>
public record JobScope(List<Guid>? ActivityIds, bool? All, DateTime? From = null, DateTime? To = null, bool? Force = null);

/// <summary>
/// The round-robin scheduler. Each turn: interactive jobs first (one batch per Owner),
/// then one bounded batch per Owner (a queued job, else its change feed when due),
/// with a pause after every batch. A 429 from the backend pauses the turn.
/// </summary>
public class RoutesScheduler : BackgroundService
{
    /// <summary>Ids of an interactive job's batch (the whole job: ≤ 50)</summary>
    public const int InteractiveBatch = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IServiceScopeFactory _scopes;
    private readonly IRoutesBackend _backend;
    private readonly WakeSignal _wake;
    private readonly IPause _pause;
    private readonly RoutesOptions _options;
    private readonly ILogger<RoutesScheduler> _logger;
    private readonly Dictionary<string, OwnerState> _state = new();
    private List<string> _owners = new();
    private DateTime _ownersAt = DateTime.MinValue;

    /// <summary>Creates the scheduler</summary>
    public RoutesScheduler(IServiceScopeFactory scopes, IRoutesBackend backend, WakeSignal wake, IPause pause,
        IOptions<RoutesOptions> options, ILogger<RoutesScheduler> logger)
    {
        _scopes = scopes;
        _backend = backend;
        _wake = wake;
        _pause = pause;
        _options = options.Value;
        _logger = logger;
    }

    private TimeSpan Poll => TimeSpan.FromSeconds(_options.PollSeconds);

    private TimeSpan Pause => TimeSpan.FromMilliseconds(_options.PauseMs);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await MigrateAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            bool did;
            try
            {
                did = await TurnAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Route scheduler turn failed");
                did = false;
            }

            if (!did)
            {
                try
                {
                    await _wake.WaitAsync(NextPollIn(), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// One round-robin turn over every Owner. Returns whether any batch ran (when
    /// none did, the caller sleeps until the next poll or a wake).
    /// </summary>
    public async Task<bool> TurnAsync(CancellationToken ct)
    {
        try
        {
            await RefreshOwnersAsync(ct);
            foreach (var owner in _wake.TakeWoken())
            {
                StateOf(owner).FeedDue = true;
            }

            var did = false;
            // Interactive work goes before background work (AD-8), one batch per Owner
            foreach (var owner in _owners)
            {
                if (await RunJobBatchAsync(owner, RouteJobPriorities.Interactive, ct))
                {
                    did = true;
                }
            }

            foreach (var owner in _owners)
            {
                if (await RunOwnerBatchAsync(owner, ct))
                {
                    did = true;
                    await _pause.DelayAsync(Pause, ct);
                }
            }

            return did;
        }
        catch (BackendBusyException busy)
        {
            // Interactive reads in flight: this turn waits for them
            await _pause.DelayAsync(busy.RetryAfter, ct);
            return true;
        }
    }

    /// <summary>One bounded batch of the Owner: a queued batch job, else its change feed when due</summary>
    private async Task<bool> RunOwnerBatchAsync(string owner, CancellationToken ct)
    {
        if (await RunJobBatchAsync(owner, RouteJobPriorities.Batch, ct))
        {
            return true;
        }

        var state = StateOf(owner);
        if (!state.FeedDue && DateTime.UtcNow < state.NextPoll)
        {
            return false;
        }

        try
        {
            using var scope = _scopes.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<AssessRunner>();
            var result = await runner.RunFeedBatchAsync(owner, _options.AssessBatch, false, ct);
            state.FeedDue = result.More;
            state.NextPoll = DateTime.UtcNow + Poll;
            if (result.Assessed > 0)
            {
                _logger.LogInformation("Routes: assessed {Assessed}, skipped {Skipped} of {Seen} changed records",
                    result.Assessed, result.Skipped, result.Seen);
            }

            return result.Seen > 0;
        }
        catch (DerivationPendingException)
        {
            // Fusion is running for this Owner: look again shortly
            state.FeedDue = false;
            state.NextPoll = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            return false;
        }
        catch (Exception ex) when (ex is not BackendBusyException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Routes: the change feed batch failed");
            state.FeedDue = false;
            state.NextPoll = DateTime.UtcNow + Poll;
            await RecordErrorAsync(owner, ex.Message, ct);
            return false;
        }
    }

    /// <summary>One batch of the Owner's oldest queued job of this priority</summary>
    private async Task<bool> RunJobBatchAsync(string owner, string priority, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RoutesDbContext>();
        var job = await db.Jobs
            .Where(j => j.OwnerId == owner && (j.State == RouteJobStates.Queued || j.State == RouteJobStates.Running) && j.Priority == priority)
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (job == null)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        job.State = RouteJobStates.Running;
        job.StartedAt ??= now;
        var jobScope = JsonSerializer.Deserialize<JobScope>(job.Scope, Json) ?? new JobScope(null, null);
        try
        {
            if (jobScope.All == true)
            {
                // Everything: the feed starts over from an empty cursor (a backfill)
                await db.Cursors.Where(c => c.OwnerId == owner).ExecuteUpdateAsync(s => s.SetProperty(c => c.Cursor, (string?)null), ct);
                StateOf(owner).FeedDue = true;
                job.State = RouteJobStates.Done;
                job.FinishedAt = now;
            }
            else
            {
                var ids = jobScope.ActivityIds ?? new List<Guid>();
                var interactive = priority == RouteJobPriorities.Interactive;
                if (job.Kind == RouteJobKinds.Improve)
                {
                    var slice = ids.Skip(job.Done).Take(interactive ? InteractiveBatch : _options.MatchBatch).ToList();
                    var tally = await ImproveIdsAsync(scope.ServiceProvider, owner, slice, interactive, jobScope.Force == true, ct);
                    job.Done += tally.Processed;
                    job.Counts = AddCounts(job.Counts, tally.Tally);
                }
                else
                {
                    var slice = ids.Skip(job.Done).Take(interactive ? InteractiveBatch : _options.AssessBatch).ToList();
                    var runner = scope.ServiceProvider.GetRequiredService<AssessRunner>();
                    var result = slice.Count > 0
                        ? await runner.AssessIdsAsync(owner, slice, interactive, ct)
                        : new AssessBatchResult(0, 0, 0, false);
                    job.Done += slice.Count;
                    job.Counts = JsonSerializer.Serialize(new { assessed = Count(job.Counts, "assessed") + result.Assessed }, Json);
                }

                if (job.Done >= ids.Count)
                {
                    job.State = RouteJobStates.Done;
                    job.FinishedAt = DateTime.UtcNow;
                }
            }
        }
        catch (Exception ex) when (ex is not BackendBusyException and not OperationCanceledException)
        {
            job.Attempts++;
            job.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            if (job.Attempts >= 3)
            {
                job.State = RouteJobStates.Failed;
                job.FinishedAt = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Assesses and matches records named by id. Records the backend does not return
    /// (another Owner's, or deleted) count as processed. A batch job yields to
    /// interactive work between records; an interactive one never does.
    /// </summary>
    private async Task<MatchBatchResult> ImproveIdsAsync(IServiceProvider sp, string owner, List<Guid> ids, bool interactive, bool force,
        CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new MatchBatchResult(0, 0, new MatchTally(), false);
        }

        var tracks = new List<RouteTrack>(ids.Count);
        foreach (var chunk in ids.Chunk(RoutesFeedLimits.TracksMax))
        {
            tracks.AddRange(await _backend.TracksAsync(owner, chunk, interactive, ct));
        }

        // In the job's order, so a batch that stops early resumes at the right id
        var position = ids.Select((id, i) => (id, i)).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().i);
        tracks = tracks.OrderBy(t => position.GetValueOrDefault(t.Id)).ToList();
        var runner = sp.GetRequiredService<MatchRunner>();
        var result = await runner.ImproveAsync(owner, tracks, force, interactive ? () => false : () => _wake.InteractivePending, ct);
        result.Tally.Gone += ids.Count - tracks.Count;
        var processed = result.Stopped && result.Processed < tracks.Count ? position[tracks[result.Processed].Id] : ids.Count;
        return result with { Seen = ids.Count, Processed = processed };
    }

    private static string AddCounts(string json, MatchTally add)
    {
        var tally = string.IsNullOrEmpty(json) ? new MatchTally() : JsonSerializer.Deserialize<MatchTally>(json, Json) ?? new MatchTally();
        tally.Add(add);
        return JsonSerializer.Serialize(tally, Json);
    }

    /// <summary>Owners from the backend, every poll interval; dropped Owners lose their cursor</summary>
    private async Task RefreshOwnersAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _ownersAt < Poll && _owners.Count > 0)
        {
            return;
        }

        var owners = (await _backend.OwnersAsync(ct)).Distinct().ToList();
        _ownersAt = DateTime.UtcNow;
        _owners = owners;
        foreach (var gone in _state.Keys.Except(owners).ToList())
        {
            _state.Remove(gone);
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RoutesDbContext>();
        var removed = await db.Cursors.Where(c => !owners.Contains(c.OwnerId)).ExecuteDeleteAsync(ct);
        if (removed > 0)
        {
            _logger.LogInformation("Routes: removed the cursors of {Count} owners no longer served", removed);
        }
    }

    private async Task RecordErrorAsync(string owner, string error, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RoutesDbContext>();
        var message = error.Length > 500 ? error[..500] : error;
        await db.Cursors.Where(c => c.OwnerId == owner).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastError, message), ct);
    }

    private async Task MigrateAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<RoutesDbContext>().Database.MigrateAsync(ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Routes schema migration failed (attempt {Attempt}); retrying", attempt);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * attempt)), ct);
            }
        }
    }

    private TimeSpan NextPollIn()
    {
        var next = _state.Values.Select(s => s.NextPoll).DefaultIfEmpty(DateTime.UtcNow + Poll).Min() - DateTime.UtcNow;
        return next < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : next > Poll ? Poll : next;
    }

    private OwnerState StateOf(string owner)
    {
        if (!_state.TryGetValue(owner, out var s))
        {
            s = new OwnerState();
            _state[owner] = s;
        }

        return s;
    }

    private static int Count(string json, string key)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrEmpty(json) ? "{}" : json);
        return doc.RootElement.TryGetProperty(key, out var v) && v.TryGetInt32(out var n) ? n : 0;
    }

    private sealed class OwnerState
    {
        /// <summary>Read the feed at the next turn (woken, or the last page said more)</summary>
        public bool FeedDue { get; set; } = true;

        public DateTime NextPoll { get; set; } = DateTime.MinValue;
    }
}
