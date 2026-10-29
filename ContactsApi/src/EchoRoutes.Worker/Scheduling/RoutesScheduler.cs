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
using EchoRoutes.Worker.Valhalla;
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

/// <summary>What one Owner's turn ran</summary>
public enum BatchKind
{
    /// <summary>Nothing was due</summary>
    None,
    /// <summary>Stage A over the change feed</summary>
    Assess,
    /// <summary>Stage B (waiting records, or an improve job)</summary>
    Match,
    /// <summary>An assess job</summary>
    Job,
    /// <summary>A page of the orphan sweep</summary>
    Sweep
}

/// <summary>
/// The round-robin scheduler (AD-30, AD-36 rule 4). Each turn: interactive jobs first
/// (at most one batch per Owner), then one bounded batch per Owner: a queued batch
/// job, else the change feed when due, else the orphan sweep when due, else the
/// records waiting for a match. Running batches stop between records when interactive
/// work arrives; the interactive jobs run, then the turn goes on. A 429 from the
/// backend pauses the turn.
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
    private readonly EngineBreaker? _breaker;
    private readonly TimeSpan[] _backoff;
    private readonly Dictionary<string, OwnerState> _state = new();
    private List<string> _owners = new();
    private DateTime _ownersAt = DateTime.MinValue;
    private DateTime _prunedAt = DateTime.MinValue;

    /// <summary>Finished jobs are kept this long (the status and the jobs API read them)</summary>
    public static readonly TimeSpan JobRetention = TimeSpan.FromDays(7);

    /// <summary>Creates the scheduler</summary>
    public RoutesScheduler(IServiceScopeFactory scopes, IRoutesBackend backend, WakeSignal wake, IPause pause,
        IOptions<RoutesOptions> options, ILogger<RoutesScheduler> logger, EngineBreaker? breaker = null)
    {
        _scopes = scopes;
        _backend = backend;
        _wake = wake;
        _pause = pause;
        _options = options.Value;
        _logger = logger;
        _breaker = breaker;
        _backoff = _options.Backoff();
    }

    private TimeSpan Poll => TimeSpan.FromSeconds(_options.PollSeconds);

    private TimeSpan Pause => TimeSpan.FromMilliseconds(_options.PauseMs);

    private TimeSpan MatchPause => TimeSpan.FromMilliseconds(_options.MatchPauseMs);

    private bool EngineOpen => _breaker?.IsOpen != true;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await MigrateAsync(stoppingToken);
        await FillLinesAsync(stoppingToken);
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

            var interactiveDone = new HashSet<string>();
            // Interactive work goes before background work (AD-8)
            var did = await InteractivePassAsync(interactiveDone, ct);
            foreach (var owner in _owners)
            {
                if (_wake.InteractivePending)
                {
                    // A running batch stopped after its record for this: run it, then go on
                    did |= await InteractivePassAsync(interactiveDone, ct);
                }

                var kind = await RunOwnerBatchAsync(owner, ct);
                if (kind != BatchKind.None)
                {
                    did = true;
                    await _pause.DelayAsync(kind == BatchKind.Match ? MatchPause : Pause, ct);
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

    /// <summary>One interactive batch of each Owner with a due interactive job, at most one per Owner per turn</summary>
    private async Task<bool> InteractivePassAsync(HashSet<string> done, CancellationToken ct)
    {
        _wake.TakeInteractive();
        List<string> owners;
        using (var scope = _scopes.CreateScope())
        {
            var now = DateTime.UtcNow;
            owners = await scope.ServiceProvider.GetRequiredService<RoutesDbContext>().Jobs.AsNoTracking()
                .Where(j => j.Priority == RouteJobPriorities.Interactive
                            && (j.State == RouteJobStates.Queued || j.State == RouteJobStates.Running)
                            && (j.NextAttemptAt == null || j.NextAttemptAt <= now))
                .Select(j => j.OwnerId).Distinct().ToListAsync(ct);
        }

        var did = false;
        foreach (var owner in owners.Where(o => !done.Contains(o)).OrderBy(o => o, StringComparer.Ordinal))
        {
            done.Add(owner);
            if (await RunJobBatchAsync(owner, RouteJobPriorities.Interactive, ct) != BatchKind.None)
            {
                did = true;
            }
        }

        return did;
    }

    /// <summary>One bounded batch of the Owner: a queued batch job, the feed, the sweep, or waiting matches</summary>
    private async Task<BatchKind> RunOwnerBatchAsync(string owner, CancellationToken ct)
    {
        var job = await RunJobBatchAsync(owner, RouteJobPriorities.Batch, ct);
        if (job != BatchKind.None)
        {
            return job;
        }

        var state = StateOf(owner);
        if ((state.FeedDue || DateTime.UtcNow >= state.NextPoll) && await RunFeedBatchAsync(owner, state, ct))
        {
            return BatchKind.Assess;
        }

        if (await RunSweepBatchAsync(owner, ct))
        {
            return BatchKind.Sweep;
        }

        return EngineOpen && await RunMatchBatchAsync(owner, ct) ? BatchKind.Match : BatchKind.None;
    }

    private async Task<bool> RunFeedBatchAsync(string owner, OwnerState state, CancellationToken ct)
    {
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
            // Fusion is running for this Owner: look again shortly (409, AD-33 rule 5)
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

    /// <summary>Stage B over the Owner's waiting records (a backfill resumes here after a restart)</summary>
    private async Task<bool> RunMatchBatchAsync(string owner, CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var runner = scope.ServiceProvider.GetService<MatchRunner>();
            if (runner == null)
            {
                return false;
            }

            var result = await runner.RunPendingBatchAsync(owner, _options.MatchBatch, () => _wake.InteractivePending, ct);
            if (result.Processed > 0)
            {
                _logger.LogInformation("Routes: matched {Matched}, low confidence {Low}, failed {Failed}, retrying {Retrying} of {Seen}",
                    result.Tally.Matched, result.Tally.LowConfidence, result.Tally.Failed, result.Tally.Retrying, result.Seen);
            }

            return result.Seen > 0;
        }
        catch (Exception ex) when (ex is not BackendBusyException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Routes: a match batch failed");
            await RecordErrorAsync(owner, ex.Message, ct);
            return false;
        }
    }

    /// <summary>
    /// A page of the weekly orphan sweep (AD-34 rule 7): the derived rows of records
    /// the backend no longer has are deleted. Decisions stay.
    /// </summary>
    private async Task<bool> RunSweepBatchAsync(string owner, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RoutesDbContext>();
        var cursor = await db.Cursors.FirstOrDefaultAsync(c => c.OwnerId == owner, ct);
        var now = DateTime.UtcNow;
        if (cursor == null || (cursor.SweepAfter == null && (cursor.SweptAt ?? cursor.UpdatedAt) + TimeSpan.FromDays(_options.SweepDays) > now))
        {
            return false;
        }

        var after = cursor.SweepAfter ?? Guid.Empty;
        var ids = await db.Quality.AsNoTracking()
            .Where(q => q.OwnerId == owner && q.ActivityId.CompareTo(after) > 0)
            .OrderBy(q => q.ActivityId).Select(q => q.ActivityId)
            .Take(RoutesFeedLimits.ExistsMax).ToListAsync(ct);
        var gone = 0;
        if (ids.Count > 0)
        {
            var exist = (await _backend.ExistsAsync(owner, ids, ct)).ToHashSet();
            var missing = ids.Where(id => !exist.Contains(id)).ToList();
            if (missing.Count > 0)
            {
                gone = await scope.ServiceProvider.GetRequiredService<MatchRunner>().DeleteDerivedAsync(owner, missing, ct);
            }
        }

        if (ids.Count < RoutesFeedLimits.ExistsMax)
        {
            cursor.SweptAt = now;
            cursor.SweepAfter = null;
        }
        else
        {
            cursor.SweepAfter = ids[^1];
        }

        await db.SaveChangesAsync(ct);
        if (gone > 0)
        {
            _logger.LogInformation("Routes: the orphan sweep removed the derived rows of {Count} deleted records", gone);
        }

        return true;
    }

    /// <summary>
    /// One batch of the Owner's oldest due job of this priority. A failed batch is
    /// retried after the backoff (1 min, 10 min, 1 h by default), then the job fails;
    /// an improve job waits while the engine breaker is open.
    /// </summary>
    private async Task<BatchKind> RunJobBatchAsync(string owner, string priority, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RoutesDbContext>();
        var now = DateTime.UtcNow;
        var job = await db.Jobs
            .Where(j => j.OwnerId == owner && (j.State == RouteJobStates.Queued || j.State == RouteJobStates.Running) && j.Priority == priority
                        && (j.NextAttemptAt == null || j.NextAttemptAt <= now))
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (job == null)
        {
            return BatchKind.None;
        }

        var jobScope = JsonSerializer.Deserialize<JobScope>(job.Scope, Json) ?? new JobScope(null, null);
        var improve = job.Kind == RouteJobKinds.Improve;
        if (improve && jobScope.All != true && !EngineOpen)
        {
            // Matching is paused: the job waits for the breaker, without using an attempt
            job.NextAttemptAt = _breaker!.OpenUntil;
            await db.SaveChangesAsync(CancellationToken.None);
            return BatchKind.None;
        }

        job.State = RouteJobStates.Running;
        job.StartedAt ??= now;
        var interactive = priority == RouteJobPriorities.Interactive;
        try
        {
            if (jobScope.All == true)
            {
                // Everything (the backfill): the feed starts over from an empty cursor; what
                // needs a match is then matched from the waiting records
                await db.Cursors.Where(c => c.OwnerId == owner).ExecuteUpdateAsync(s => s.SetProperty(c => c.Cursor, (string?)null), ct);
                StateOf(owner).FeedDue = true;
                Finish(job, RouteJobStates.Done);
            }
            else if (jobScope.From != null && jobScope.To != null)
            {
                var size = interactive ? InteractiveBatch : improve ? _options.MatchBatch : _options.AssessBatch;
                var (pageCursor, offset) = SplitJobCursor(job.Cursor);
                var page = await _backend.RangeAsync(owner, jobScope.From.Value, jobScope.To.Value, pageCursor, size, interactive, ct);
                // Records of this page finished before it stopped early are neither redone nor counted again
                var todo = page.Ids.Skip(offset).ToList();
                var done = await ProcessIdsAsync(scope.ServiceProvider, job, owner, todo, interactive, improve, jobScope.Force == true, ct);
                job.Done += done;
                if (done < todo.Count)
                {
                    // Stopped early (interactive work, or the breaker): this page is read again
                    // from the same cursor, after the records already done
                    job.Cursor = JoinJobCursor(pageCursor, offset + done);
                }
                else
                {
                    job.Cursor = page.Next;
                    if (!page.More)
                    {
                        job.Total = job.Done;
                        Finish(job, RouteJobStates.Done);
                    }
                }
            }
            else
            {
                var ids = jobScope.ActivityIds ?? new List<Guid>();
                var slice = ids.Skip(job.Done).Take(interactive ? InteractiveBatch : improve ? _options.MatchBatch : _options.AssessBatch).ToList();
                job.Done += await ProcessIdsAsync(scope.ServiceProvider, job, owner, slice, interactive, improve, jobScope.Force == true, ct);
                if (job.Done >= ids.Count)
                {
                    Finish(job, RouteJobStates.Done);
                }
            }

            job.Attempts = 0;
            if (job.State == RouteJobStates.Running)
            {
                job.NextAttemptAt = null;
            }
        }
        catch (Exception ex) when (ex is not BackendBusyException and not OperationCanceledException)
        {
            job.Attempts++;
            job.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            if (job.Attempts <= _backoff.Length)
            {
                job.NextAttemptAt = DateTime.UtcNow + _backoff[job.Attempts - 1];
            }
            else
            {
                Finish(job, RouteJobStates.Failed);
            }
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return improve ? BatchKind.Match : BatchKind.Job;
    }

    /// <summary>Assesses (and for improve, matches) ids; returns how many of them are done, in their order</summary>
    private async Task<int> ProcessIdsAsync(IServiceProvider sp, RouteJob job, string owner, List<Guid> ids, bool interactive, bool improve,
        bool force, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        if (improve)
        {
            var result = await ImproveIdsAsync(sp, owner, ids, interactive, force, ct);
            job.Counts = AddCounts(job.Counts, result.Tally);
            return result.Processed;
        }

        var assessed = await sp.GetRequiredService<AssessRunner>().AssessIdsAsync(owner, ids, interactive, ct);
        job.Counts = AddCounts(job.Counts, new MatchTally { Assessed = assessed.Assessed });
        return ids.Count;
    }

    /// <summary>A range job's cursor: the backend's page cursor, and how many of that page are done ("cursor#n")</summary>
    internal static (string? Cursor, int Offset) SplitJobCursor(string? cursor)
    {
        var at = cursor?.LastIndexOf('#') ?? -1;
        if (at < 0 || !int.TryParse(cursor![(at + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n))
        {
            return (cursor, 0);
        }

        var page = cursor[..at];
        return (page.Length == 0 ? null : page, n);
    }

    internal static string JoinJobCursor(string? cursor, int offset) =>
        $"{cursor}#{offset.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static void Finish(RouteJob job, string state)
    {
        job.State = state;
        job.FinishedAt = DateTime.UtcNow;
        job.NextAttemptAt = null;
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

    /// <summary>
    /// Owners from the backend, every poll interval. An error or an empty list is taken
    /// as transient: the known Owners are kept, and nothing is deleted. An Owner missing
    /// from a non-empty list is gone (no Atlas data left), and all of their routes data
    /// goes with their cursor: quality, matched routes, decisions, settings and jobs.
    /// </summary>
    private async Task RefreshOwnersAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _ownersAt < Poll && _owners.Count > 0)
        {
            return;
        }

        List<string> owners;
        try
        {
            owners = (await _backend.OwnersAsync(ct)).Distinct().ToList();
        }
        catch (Exception ex) when (_owners.Count > 0 && ex is not OperationCanceledException and not BackendBusyException)
        {
            _logger.LogWarning(ex, "Routes: the owner list could not be read; keeping the {Count} known owners", _owners.Count);
            _ownersAt = DateTime.UtcNow;
            return;
        }

        _ownersAt = DateTime.UtcNow;
        if (owners.Count == 0)
        {
            if (_owners.Count > 0)
            {
                _logger.LogWarning("Routes: the backend listed no owners; keeping the {Count} known owners and their data", _owners.Count);
            }

            return;
        }

        _owners = owners;
        foreach (var gone in _state.Keys.Except(owners).ToList())
        {
            _state.Remove(gone);
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RoutesDbContext>();
        await DeleteGoneOwnersAsync(db, owners, ct);
        await PruneJobsAsync(db, ct);
    }

    /// <summary>Deletes every routes row of Owners the service keeps data for who are not in <paramref name="owners"/></summary>
    private async Task DeleteGoneOwnersAsync(RoutesDbContext db, List<string> owners, CancellationToken ct)
    {
        // Owners with a cursor or a job: every Owner the service ever worked for
        var known = await db.Cursors.Select(c => c.OwnerId).Union(db.Jobs.Select(j => j.OwnerId)).ToListAsync(ct);
        var gone = known.Except(owners).ToList();
        if (gone.Count == 0)
        {
            return;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Quality.Where(x => gone.Contains(x.OwnerId)).ExecuteDeleteAsync(ct);
        await db.MatchedRoutes.Where(x => gone.Contains(x.OwnerId)).ExecuteDeleteAsync(ct);
        await db.Decisions.Where(x => gone.Contains(x.OwnerId)).ExecuteDeleteAsync(ct);
        await db.Settings.Where(x => gone.Contains(x.OwnerId)).ExecuteDeleteAsync(ct);
        await db.Jobs.Where(x => gone.Contains(x.OwnerId)).ExecuteDeleteAsync(ct);
        await db.Cursors.Where(x => gone.Contains(x.OwnerId)).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        _logger.LogInformation("Routes: removed all routes data of {Count} owners no longer served", gone.Count);
    }

    /// <summary>Finished jobs older than <see cref="JobRetention"/> go, at most once an hour</summary>
    private async Task PruneJobsAsync(RoutesDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now - _prunedAt < TimeSpan.FromHours(1))
        {
            return;
        }

        _prunedAt = now;
        var before = now - JobRetention;
        var removed = await db.Jobs
            .Where(j => (j.State == RouteJobStates.Done || j.State == RouteJobStates.Failed) && j.FinishedAt != null && j.FinishedAt < before)
            .ExecuteDeleteAsync(ct);
        if (removed > 0)
        {
            _logger.LogInformation("Routes: removed {Count} jobs finished more than {Days} days ago", removed, JobRetention.TotalDays);
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

    /// <summary>
    /// Matched rows stored before the list and density lines existed get them now, 200
    /// at a time with the batch pause between pages (until then reads fall back to the
    /// full line). A failure is logged and left for the next start.
    /// </summary>
    private async Task FillLinesAsync(CancellationToken ct)
    {
        try
        {
            var total = 0;
            while (!ct.IsCancellationRequested)
            {
                using var scope = _scopes.CreateScope();
                var runner = scope.ServiceProvider.GetService<MatchRunner>();
                var filled = runner == null ? 0 : await runner.FillLinesAsync(200, ct);
                if (filled == 0)
                {
                    break;
                }

                total += filled;
                await _pause.DelayAsync(Pause, ct);
            }

            if (total > 0)
            {
                _logger.LogInformation("Routes: stored the list and density lines of {Count} matched routes", total);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Routes: filling the list and density lines failed; reads use the full line meanwhile");
        }
    }

    private TimeSpan NextPollIn()
    {
        var at = _state.Values.Select(s => s.NextPoll).DefaultIfEmpty(DateTime.UtcNow + Poll).Min();
        if (_breaker?.OpenUntil is { } open && open < at)
        {
            // The breaker closes before the next poll: waiting matches resume then
            at = open;
        }

        var next = at - DateTime.UtcNow;
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

    private sealed class OwnerState
    {
        /// <summary>Read the feed at the next turn (woken, or the last page said more)</summary>
        public bool FeedDue { get; set; } = true;

        public DateTime NextPoll { get; set; } = DateTime.MinValue;
    }
}
