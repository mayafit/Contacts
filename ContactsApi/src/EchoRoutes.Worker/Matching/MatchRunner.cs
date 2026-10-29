/**
 * @fileoverview Stage B for one Owner at a time (story 12.4): which records wait for a
 * match, matching them one engine call at a time, writing matched_routes, per-record
 * backoff and the circuit breaker (AD-34, AD-35 rules 6 and 7, AD-36 rule 3)
 * @module EchoRoutes.Worker.Matching
 */
using System.Diagnostics;
using System.Text.Json;
using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker.Assess;
using EchoRoutes.Worker.Backend;
using EchoRoutes.Worker.Valhalla;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EchoRoutes.Worker.Matching;

/// <summary>Outcomes of a batch, per kind (job counts)</summary>
public class MatchTally
{
    public int Assessed { get; set; }
    public int Matched { get; set; }
    public int LowConfidence { get; set; }
    public int Failed { get; set; }
    /// <summary>Engine failure: retried later (per-record backoff)</summary>
    public int Retrying { get; set; }
    /// <summary>Did not need it, or already matched</summary>
    public int Skipped { get; set; }
    /// <summary>no_track, type_never, rail, unknown, outside_coverage</summary>
    public int NotMatchable { get; set; }
    /// <summary>The Owner chose the original (a reject decision)</summary>
    public int KeptOriginal { get; set; }
    /// <summary>The record no longer exists</summary>
    public int Gone { get; set; }

    /// <summary>Adds an outcome by status</summary>
    public void Add(string outcome)
    {
        switch (outcome)
        {
            case RouteStatuses.Matched: Matched++; break;
            case RouteStatuses.LowConfidence: LowConfidence++; break;
            case RouteStatuses.Failed: Failed++; break;
            default: Retrying++; break;
        }
    }

    /// <summary>Adds another tally</summary>
    public void Add(MatchTally o)
    {
        Assessed += o.Assessed; Matched += o.Matched; LowConfidence += o.LowConfidence; Failed += o.Failed;
        Retrying += o.Retrying; Skipped += o.Skipped; NotMatchable += o.NotMatchable; KeptOriginal += o.KeptOriginal; Gone += o.Gone;
    }
}

/// <summary>What one stage B batch did</summary>
/// <param name="Seen">Records it took</param>
/// <param name="Processed">Records it finished (the rest stay queued)</param>
/// <param name="Stopped">It stopped early: interactive work arrived, or the breaker opened</param>
public record MatchBatchResult(int Seen, int Processed, MatchTally Tally, bool Stopped);

/// <summary>
/// Stage B. Work waiting for a match is not a queue of its own: it is every assessed
/// record that needs improvement and has no current match (or a due retry), with no
/// reject decision. So a restart loses nothing, and a backfill resumes where it was.
/// </summary>
public class MatchRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly RoutesDbContext _db;
    private readonly IRoutesBackend _backend;
    private readonly IValhallaClient _engine;
    private readonly EngineBreaker _breaker;
    private readonly GraphInfoSource _graph;
    private readonly AssessRunner _assess;
    private readonly TimeProvider _clock;
    private readonly RoutesOptions _options;
    private readonly MatchSettings _settings;
    private readonly TimeSpan[] _backoff;
    private readonly ILogger<MatchRunner> _logger;

    /// <summary>Creates the runner</summary>
    public MatchRunner(RoutesDbContext db, IRoutesBackend backend, IValhallaClient engine, EngineBreaker breaker, GraphInfoSource graph,
        AssessRunner assess, TimeProvider clock, IOptions<RoutesOptions> options, ILogger<MatchRunner>? logger = null)
    {
        _logger = logger ?? NullLogger<MatchRunner>.Instance;
        _db = db;
        _backend = backend;
        _engine = engine;
        _breaker = breaker;
        _graph = graph;
        _assess = assess;
        _clock = clock;
        _options = options.Value;
        _settings = MatchSettings.From(_options);
        _backoff = _options.Backoff();
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>The Owner's records waiting for a match (newest change first, so fresh imports go first)</summary>
    public IQueryable<RouteQualityRow> Pending(string owner)
    {
        var now = Now;
        const string rule = RouteQuality.RuleVersion;
        return from q in _db.Quality.AsNoTracking()
            where q.OwnerId == owner && q.NeedsImprovement && q.Matchable == Matchability.Yes
            from m in _db.MatchedRoutes.Where(m => m.OwnerId == owner && m.ActivityId == q.ActivityId).DefaultIfEmpty()
            where m == null || m.InputHash != q.InputHash || m.FusedType != q.Type || m.RuleVersion != rule
                  || (m.NextAttemptAt != null && m.NextAttemptAt <= now)
            where !_db.Decisions.Any(d => d.OwnerId == owner && d.SourceKey == q.SourceKey && d.Kind == RouteDecisionKinds.Reject)
            select q;
    }

    /// <summary>How many of the Owner's records wait for a match</summary>
    public Task<int> PendingCountAsync(string owner, CancellationToken ct) => Pending(owner).CountAsync(ct);

    /// <summary>
    /// One bounded batch of the Owner's waiting records. <paramref name="yield"/> is
    /// asked between records: when it says so, the batch stops after the current record.
    /// </summary>
    public async Task<MatchBatchResult> RunPendingBatchAsync(string owner, int limit, Func<bool> yield, CancellationToken ct)
    {
        var ids = await Pending(owner).OrderByDescending(q => q.SourceUpdatedAt).ThenBy(q => q.ActivityId)
            .Select(q => q.ActivityId).Take(Math.Max(1, limit)).ToListAsync(ct);
        if (ids.Count == 0)
        {
            return new MatchBatchResult(0, 0, new MatchTally(), false);
        }

        var tracks = new List<RouteTrack>(ids.Count);
        foreach (var chunk in ids.Chunk(RoutesFeedLimits.TracksMax))
        {
            tracks.AddRange(await _backend.TracksAsync(owner, chunk, false, ct));
        }

        var tally = new MatchTally();
        // Records the backend no longer has: their derived rows go now (else they would wait forever)
        var gone = ids.Except(tracks.Select(t => t.Id)).ToList();
        if (gone.Count > 0)
        {
            await DeleteDerivedAsync(owner, gone, ct);
            tally.Gone = gone.Count;
        }

        var result = await ImproveAsync(owner, tracks, false, yield, ct);
        result.Tally.Gone += tally.Gone;
        return result with { Seen = ids.Count, Processed = result.Processed + gone.Count };
    }

    /// <summary>
    /// Assesses the tracks and matches those that need it (or all matchable ones when
    /// <paramref name="force"/>; a reject decision is never overridden), saving after
    /// every record
    /// </summary>
    public async Task<MatchBatchResult> ImproveAsync(string owner, List<RouteTrack> tracks, bool force, Func<bool> yield, CancellationToken ct)
    {
        var tally = new MatchTally();
        if (tracks.Count == 0)
        {
            return new MatchBatchResult(0, 0, tally, false);
        }

        var assessed = await _assess.AssessTracksAsync(owner, tracks, ct);
        tally.Assessed = assessed.Count;
        var keys = tracks.Select(t => t.SourceKey).Distinct().ToList();
        var ids = tracks.Select(t => t.Id).ToList();
        var rejected = (await _db.Decisions.AsNoTracking()
                .Where(d => d.OwnerId == owner && keys.Contains(d.SourceKey) && d.Kind == RouteDecisionKinds.Reject)
                .Select(d => d.SourceKey).ToListAsync(ct))
            .ToHashSet();
        var existing = await _db.MatchedRoutes.Where(m => m.OwnerId == owner && ids.Contains(m.ActivityId)).ToDictionaryAsync(m => m.ActivityId, ct);
        var engineVersion = await _engine.VersionAsync(ct);

        var processed = 0;
        foreach (var t in tracks)
        {
            if (processed > 0 && (yield() || _breaker.IsOpen))
            {
                return new MatchBatchResult(tracks.Count, processed, tally, true);
            }

            processed++;
            if (!assessed.TryGetValue(t.Id, out var a))
            {
                // Stage A could not assess it (logged there): it backs off like a failed match
                tally.Add(await RecordFailureAsync(owner, t, existing, "the record could not be assessed: "
                    + (_assess.Failures.FirstOrDefault(f => f.Id == t.Id).Reason ?? "unknown error"), ct));
                continue;
            }

            var costing = ValhallaTrace.CostingFor(t.Type, t.SourceType);
            if (rejected.Contains(t.SourceKey))
            {
                tally.KeptOriginal++;
                continue;
            }

            if (a.Matchable != Matchability.Yes || costing == null)
            {
                tally.NotMatchable++;
                continue;
            }

            var hash = RouteQuality.InputHash(t.Points);
            var row = existing.GetValueOrDefault(t.Id);
            var current = row != null && row.InputHash == hash && row.FusedType == t.Type && row.RuleVersion == RouteQuality.RuleVersion;
            var retryDue = row?.NextAttemptAt != null && row.NextAttemptAt <= Now;
            if (!force && (!a.NeedsImprovement || (current && !retryDue)))
            {
                tally.Skipped++;
                continue;
            }

            if (_breaker.IsOpen)
            {
                // Matching is paused: this record waits (it is still pending)
                return new MatchBatchResult(tracks.Count, processed - 1, tally, true);
            }

            if (row == null)
            {
                row = new MatchedRouteRow { OwnerId = owner, ActivityId = t.Id };
                _db.MatchedRoutes.Add(row);
                existing[t.Id] = row;
            }

            try
            {
                tally.Add(await MatchOneAsync(row, current, t, hash, costing, engineVersion, ct));
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not BackendBusyException)
            {
                // Not the engine: a bug or a row that cannot be stored. The record backs off
                // with the reason, and the batch goes on (it must never head every batch)
                _logger.LogWarning(ex, "Routes: matching record {ActivityId} of {Owner} failed", t.Id, owner);
                tally.Add(await RecordFailureAsync(owner, t, existing, (ex.InnerException ?? ex).Message, ct, hash, costing));
            }
        }

        return new MatchBatchResult(tracks.Count, processed, tally, false);
    }

    /// <summary>Matches one record and fills its row; returns the status, or "retrying"</summary>
    private async Task<string> MatchOneAsync(MatchedRouteRow row, bool current, RouteTrack t, string hash, string costing, string? engineVersion,
        CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var plan = TracePlanner.Plan(t.Points, _settings);
        var answers = new Dictionary<TraceCall, TraceResponse?>();
        string? refusal = null;
        foreach (var call in plan.SelectMany(s => s.Calls))
        {
            try
            {
                using var doc = await _engine.TraceAttributesAsync(ValhallaTrace.BuildRequest(t.Points, call, costing, t.Source, _settings), ct);
                answers[call] = ValhallaTrace.Parse(doc.RootElement);
                _breaker.Success();
            }
            catch (EngineRejectedException ex)
            {
                // The engine is alive; this part of the trace has no match (a gap)
                _breaker.Success();
                answers[call] = null;
                refusal ??= ex.Message;
            }
            catch (EngineUnavailableException ex)
            {
                // The whole engine down (no connection, or the breaker opened or its probe
                // failed) is no attempt of this record; a timeout or an error is
                var down = _breaker.Failure(ex.Message) | ex.Unreachable;
                return down ? EngineDown(row, current, t, hash, costing, ex.Message) : Failure(row, current, t, hash, costing, ex.Message);
            }
        }

        var profile = RouteQuality.ProfileFor(t.Type);
        var result = RouteAssembler.Assemble(t.Points, plan, answers, profile, costing, _settings.MaxStoredVertices);
        Identify(row, t, hash, costing);
        row.EngineVersion = engineVersion;
        row.GraphVersion = _graph.Current?.Version;
        row.Status = result.Status;
        row.Confidence = result.Confidence;
        row.Polyline = result.Polyline;
        // Reads take a pre-simplified line (lists 400 vertices, density 80), never the full one
        (row.ListPolyline, row.DensityPolyline) = result.Polyline != null ? RouteLines.Derive(result.Polyline) : (null, null);
        row.Pieces = JsonSerializer.Serialize(result.Pieces, Json);
        row.MatchedDistanceM = result.Polyline != null ? result.LengthM : null;
        row.MetricsAfter = JsonSerializer.Serialize(result.Metrics, Json);
        row.Reason = Truncate(result.Reason != null && refusal != null ? $"{result.Reason}; {refusal}" : result.Reason);
        row.Attempts = 0;
        row.NextAttemptAt = null;
        row.ComputedAt = Now;
        watch.Stop();
        _breaker.RecordTime(watch.Elapsed);
        return result.Status;
    }

    /// <summary>
    /// The whole engine is down: the record waits for it (until the breaker closes, or one
    /// backoff step) without using an attempt, so an outage never fails records for good.
    /// A current good match is kept as it is.
    /// </summary>
    private string EngineDown(MatchedRouteRow row, bool current, RouteTrack t, string hash, string costing, string reason)
    {
        if (current && row.Polyline != null && row.Status != RouteStatuses.Failed)
        {
            return "retrying";
        }

        if (!current)
        {
            row.Attempts = 0;
        }

        Identify(row, t, hash, costing);
        row.Status = RouteStatuses.Failed;
        row.Confidence = null;
        row.Polyline = null;
        row.ListPolyline = null;
        row.DensityPolyline = null;
        row.Pieces = "[]";
        row.MatchedDistanceM = null;
        row.ComputedAt = Now;
        var wait = Now + (_backoff.Length > 0 ? _backoff[0] : TimeSpan.FromMinutes(1));
        row.NextAttemptAt = _breaker.OpenUntil is { } open && open > wait ? open : wait;
        row.Reason = Truncate($"{reason} (the engine is unavailable; not counted as an attempt)");
        return "retrying";
    }

    /// <summary>
    /// An engine failure of this record: back off (1 min, 10 min, 1 h by default), then
    /// failed. A current good match is kept as it is (a forced re-match that failed).
    /// </summary>
    private string Failure(MatchedRouteRow row, bool current, RouteTrack t, string hash, string costing, string reason)
    {
        if (current && row.Polyline != null && row.Status != RouteStatuses.Failed)
        {
            return "retrying";
        }

        if (!current)
        {
            row.Attempts = 0;
        }

        Identify(row, t, hash, costing);
        row.Attempts++;
        row.Status = RouteStatuses.Failed;
        row.Confidence = null;
        row.Polyline = null;
        row.ListPolyline = null;
        row.DensityPolyline = null;
        row.Pieces = "[]";
        row.MatchedDistanceM = null;
        row.ComputedAt = Now;
        if (row.Attempts <= _backoff.Length)
        {
            row.NextAttemptAt = Now + _backoff[row.Attempts - 1];
            row.Reason = Truncate($"{reason} (attempt {row.Attempts}; retried after {_backoff[row.Attempts - 1].TotalMinutes:0} min)");
            return "retrying";
        }

        row.NextAttemptAt = null;
        row.Reason = Truncate($"{reason} (gave up after {row.Attempts} attempts)");
        return RouteStatuses.Failed;
    }

    /// <summary>
    /// Stores a failed row for a record whose match broke outside the engine: the same
    /// backoff as an engine failure, then failed. The row is written on its own (the
    /// tracked one is dropped), with every text clipped to its column, so the failure
    /// itself is always stored.
    /// </summary>
    private async Task<string> RecordFailureAsync(string owner, RouteTrack t, Dictionary<Guid, MatchedRouteRow> existing, string reason,
        CancellationToken ct, string? hash = null, string? costing = null)
    {
        if (existing.Remove(t.Id, out var tracked))
        {
            _db.Entry(tracked).State = EntityState.Detached;
        }

        MatchedRouteRow? row = null;
        try
        {
            row = await _db.MatchedRoutes.FirstOrDefaultAsync(m => m.OwnerId == owner && m.ActivityId == t.Id, ct);
            if (row == null)
            {
                row = new MatchedRouteRow { OwnerId = owner, ActivityId = t.Id };
                _db.MatchedRoutes.Add(row);
            }

            hash ??= RouteQuality.InputHash(t.Points);
            var current = row.InputHash == hash && row.FusedType == t.Type && row.RuleVersion == RouteQuality.RuleVersion;
            var outcome = Failure(row, current, t, hash, Clip(costing, 16) ?? string.Empty, reason);
            row.SourceKey = Clip(row.SourceKey, 256)!;
            row.FusedType = Clip(row.FusedType, 32)!;
            await _db.SaveChangesAsync(ct);
            existing[t.Id] = row;
            return outcome;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Routes: the failure of record {ActivityId} of {Owner} could not be stored", t.Id, owner);
            if (row != null)
            {
                _db.Entry(row).State = EntityState.Detached;
            }

            return RouteStatuses.Failed;
        }
    }

    private static string? Clip(string? s, int max) => s == null ? null : s.Length > max ? s[..max] : s;

    /// <summary>The record's values at match time: the read-time check of AD-34 rule 3</summary>
    private static void Identify(MatchedRouteRow row, RouteTrack t, string hash, string costing)
    {
        row.SourceKey = t.SourceKey;
        row.InputHash = hash;
        row.SourceUpdatedAt = t.UpdatedAt;
        row.SourcePointCount = t.PointCount;
        row.FusedType = t.Type;
        row.Costing = costing;
        row.Engine = "valhalla";
        row.RuleVersion = RouteQuality.RuleVersion;
    }

    /// <summary>
    /// Fills the list and density lines of rows stored before they existed, a page at a
    /// time; returns how many rows it filled (0: none are left)
    /// </summary>
    public async Task<int> FillLinesAsync(int page, CancellationToken ct)
    {
        var rows = await _db.MatchedRoutes
            .Where(m => m.Polyline != null && (m.ListPolyline == null || m.DensityPolyline == null))
            .OrderBy(m => m.Id).Take(Math.Max(1, page)).ToListAsync(ct);
        foreach (var row in rows)
        {
            (row.ListPolyline, row.DensityPolyline) = RouteLines.Derive(row.Polyline!);
        }

        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return rows.Count;
    }

    /// <summary>Deletes the derived rows of records that no longer exist (decisions stay)</summary>
    public async Task<int> DeleteDerivedAsync(string owner, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var list = ids.ToList();
        var q = await _db.Quality.Where(r => r.OwnerId == owner && list.Contains(r.ActivityId)).ExecuteDeleteAsync(ct);
        var m = await _db.MatchedRoutes.Where(r => r.OwnerId == owner && list.Contains(r.ActivityId)).ExecuteDeleteAsync(ct);
        return Math.Max(q, m);
    }

    private static string? Truncate(string? s) => s == null ? null : s.Length > 500 ? s[..500] : s;
}
