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

    /// <summary>A waiting record and its place in the queue</summary>
    public sealed class PendingRow
    {
        public RouteQualityRow Q { get; init; } = null!;

        /// <summary>
        /// 0: new, changed or a due retry; 1: an older rule's low_confidence or failed match, or a
        /// well-recorded record never matched (matched for its roads, AD-37); 2: an older rule's matched one
        /// </summary>
        public int Rank { get; init; }
    }

    /// <summary>
    /// The Owner's records waiting for a match: those that need improvement, and every
    /// other matchable record of a matched type (AD-37). A row of an older rule version
    /// waits for a re-match (AD-34 rule 7), unless its re-match is backing off; a reject
    /// decision always wins.
    /// </summary>
    public IQueryable<PendingRow> PendingRows(string owner)
    {
        var now = Now;
        const string rule = RouteQuality.RuleVersion;
        // A well-recorded drive is matched too: its roads are needed for the road filter (AD-37)
        var matchedTypes = RouteQuality.MatchedTypes;
        return from q in _db.Quality.AsNoTracking()
            where q.OwnerId == owner && q.Matchable == Matchability.Yes && (q.NeedsImprovement || matchedTypes.Contains(q.Type))
            from m in _db.MatchedRoutes.Where(m => m.OwnerId == owner && m.ActivityId == q.ActivityId).DefaultIfEmpty()
            where m == null || m.InputHash != q.InputHash || m.FusedType != q.Type
                  || (((m.RuleVersion != rule && m.Method != RebuildMethods.Similar) || m.NextAttemptAt != null)
                      && (m.NextAttemptAt == null || m.NextAttemptAt <= now))
            where !_db.Decisions.Any(d => d.OwnerId == owner && d.SourceKey == q.SourceKey && d.Kind == RouteDecisionKinds.Reject)
            select new PendingRow
            {
                Q = q,
                Rank = m == null ? (q.NeedsImprovement ? 0 : 1)
                    : m.InputHash != q.InputHash || m.FusedType != q.Type || m.RuleVersion == rule ? 0
                    : m.Status == RouteStatuses.Matched ? 2 : 1
            };
    }

    /// <summary>The Owner's records waiting for a match</summary>
    public IQueryable<RouteQualityRow> Pending(string owner) => PendingRows(owner).Select(p => p.Q);

    /// <summary>How many of the Owner's records wait for a match</summary>
    public Task<int> PendingCountAsync(string owner, CancellationToken ct) => Pending(owner).CountAsync(ct);

    /// <summary>How many of the Owner's waiting records are re-matches for a new rule version</summary>
    public Task<int> RematchCountAsync(string owner, CancellationToken ct) => PendingRows(owner).CountAsync(p => p.Rank > 0, ct);

    /// <summary>
    /// One bounded batch of the Owner's waiting records. <paramref name="yield"/> is
    /// asked between records: when it says so, the batch stops after the current record.
    /// </summary>
    public async Task<MatchBatchResult> RunPendingBatchAsync(string owner, int limit, Func<bool> yield, CancellationToken ct)
    {
        // New and changed records first (fresh imports), then re-matches for a new rule:
        // low_confidence and failed before matched (AD-34 rule 7)
        var ids = await PendingRows(owner).OrderBy(p => p.Rank).ThenByDescending(p => p.Q.SourceUpdatedAt).ThenBy(p => p.Q.ActivityId)
            .Select(p => p.Q.ActivityId).Take(Math.Max(1, limit)).ToListAsync(ct);
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
        // The Owner's marked points: every match of those records goes through them (AD-38 rule 7)
        var marked = (await _db.Waypoints.AsNoTracking()
                .Where(w => w.OwnerId == owner && keys.Contains(w.SourceKey))
                .Select(w => new { w.SourceKey, w.Points }).ToListAsync(ct))
            .ToDictionary(w => w.SourceKey, w => RouteMarks.Parse(w.Points));
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
            var sameInput = row != null && row.InputHash == hash && row.FusedType == t.Type;
            // A used similar rebuild stays current across rule versions until its input changes (AD-38 rule 7)
            var current = sameInput && (row!.RuleVersion == RouteQuality.RuleVersion || row.Method == RebuildMethods.Similar);
            // A re-match for a new rule: the old line is served until its replacement is written
            var stale = sameInput && !current && Serves(row!);
            var retryDue = row?.NextAttemptAt != null && row.NextAttemptAt <= Now;
            // Every matchable record is matched, not only those that need improvement: the road
            // filter needs the roads of well-recorded drives too (AD-37)
            if (!force && current && !retryDue)
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
                tally.Add(await MatchOneAsync(row, current, stale, t, hash, costing, engineVersion, marked.GetValueOrDefault(t.SourceKey), yield, ct));
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
    /// <param name="marks">The Owner's marked points for the record (AD-38); null without them</param>
    private async Task<string> MatchOneAsync(MatchedRouteRow row, bool current, bool stale, RouteTrack t, string hash, string costing,
        string? engineVersion, List<(double Lat, double Lon)>? marks, Func<bool> yield, CancellationToken ct)
    {
        // Re-matches so far of a stored result whose filling timed out (see the end)
        var fillRetries = current && row.Status != RouteStatuses.Failed && row.Polyline != null ? row.Attempts : 0;
        var watch = Stopwatch.StartNew();
        MatchResult result;
        string? refusal;
        try
        {
            // Marked points that no longer fit the track (it changed) are left out, not refused
            if (marks != null && Waypoints.Check(t.Points, marks) != null)
            {
                marks = null;
            }

            var points = marks != null ? Waypoints.Place(t.Points, marks) : t.Points;
            (result, refusal) = await StageB.MatchAsync(points, t.Type, costing, t.Source, _settings, _engine, _breaker, yield, ct,
                marks != null ? SimilarRoutes.NearM : null);
        }
        catch (StageBEngineException ex)
        {
            return ex.Down ? EngineDown(row, current, stale, t, hash, costing, ex.Message) : Failure(row, current, stale, t, hash, costing, ex.Message);
        }

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
        // The roads it follows, for the panel and the road filter (AD-37)
        row.Roads = result.Polyline != null ? RouteRoads.Serialize(result.Roads ?? new()) : null;
        row.RoadNames = result.Polyline != null ? (result.Roads ?? new()).Select(r => r.Name).ToArray() : null;
        row.Method = marks != null ? RebuildMethods.Waypoints : RebuildMethods.Auto;
        row.Basis = null;
        row.MetricsAfter = JsonSerializer.Serialize(result.Metrics, Json);
        row.Reason = Truncate(result.Reason != null && refusal != null ? $"{result.Reason}; {refusal}" : result.Reason);
        row.Attempts = 0;
        row.NextAttemptAt = null;
        row.ComputedAt = Now;
        var soon = Now + (_backoff.Length > 0 ? _backoff[0] : TimeSpan.FromMinutes(1));
        if (result.Metrics.FillTimeouts > 0)
        {
            // The result is stored with its gaps, and re-matched soon a bounded number of
            // times (Attempts counts them); after the last one the gaps stay
            row.Attempts = fillRetries + 1;
            row.NextAttemptAt = row.Attempts <= _settings.GapFill.Retries ? soon : null;
        }
        else if (result.Metrics.FillDeferred > 0)
        {
            // Interactive work stopped the filling: re-matched soon, not counted
            row.Attempts = fillRetries;
            row.NextAttemptAt = soon;
        }

        watch.Stop();
        _breaker.RecordTime(watch.Elapsed);
        return result.Status;
    }

    /// <summary>
    /// The whole engine is down: the record waits for it (until the breaker closes, or one
    /// backoff step) without using an attempt, so an outage never fails records for good.
    /// A current good match is kept as it is.
    /// </summary>
    private string EngineDown(MatchedRouteRow row, bool current, bool stale, RouteTrack t, string hash, string costing, string reason)
    {
        var wait = Now + (_backoff.Length > 0 ? _backoff[0] : TimeSpan.FromMinutes(1));
        if (_breaker.OpenUntil is { } until && until > wait)
        {
            wait = until;
        }

        if (current && Serves(row))
        {
            return "retrying";
        }

        if (stale)
        {
            // The old rule's line stays; the re-match waits for the engine (not an attempt)
            row.NextAttemptAt = wait;
            row.Reason = Truncate($"{reason} (re-match for {RouteQuality.RuleVersion} waits for the engine)");
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
        row.Roads = null;
        row.RoadNames = null;
        row.ComputedAt = Now;
        row.NextAttemptAt = wait;
        row.Reason = Truncate($"{reason} (the engine is unavailable; not counted as an attempt)");
        return "retrying";
    }

    /// <summary>
    /// An engine failure of this record: back off (1 min, 10 min, 1 h by default), then
    /// failed. A line already stored for the same input is never wiped by a failure: a
    /// current one is kept as it is (a forced re-match that failed; a due fill retry backs
    /// off), and an older rule's one stays served while its re-match backs off, and for
    /// good once the backoff is spent.
    /// </summary>
    private string Failure(MatchedRouteRow row, bool current, bool stale, RouteTrack t, string hash, string costing, string reason)
    {
        if (current && Serves(row))
        {
            if (row.NextAttemptAt is { } due && due <= Now)
            {
                // A due re-match (a fill retry) failed: never hot-looped
                row.Attempts++;
                row.NextAttemptAt = row.Attempts <= _backoff.Length ? Now + _backoff[row.Attempts - 1] : null;
            }

            return "retrying";
        }

        if (stale && row.Attempts < _backoff.Length)
        {
            // A re-match for a new rule failed: the old line stays served, and the re-match
            // backs off like any attempt
            row.Attempts++;
            row.NextAttemptAt = Now + _backoff[row.Attempts - 1];
            row.Reason = Truncate($"{reason} (re-match attempt {row.Attempts}; retried after {_backoff[row.Attempts - 1].TotalMinutes:0} min)");
            return "retrying";
        }

        if (stale)
        {
            // The backoff is spent: the old rule's line, pieces and status stay, and the row
            // takes the current rule version so it is not re-matched again (until its input
            // changes or a newer rule comes)
            var old = row.RuleVersion;
            row.Attempts++;
            row.RuleVersion = RouteQuality.RuleVersion;
            // The similar-drive pool still needs its start and ends (AD-38 rule 3)
            SetEnds(row, t);
            row.NextAttemptAt = null;
            row.Reason = Truncate($"{reason} (gave up after {row.Attempts} attempts; the {old} line is kept)");
            return RouteStatuses.Failed;
        }

        if (!current && !stale)
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
        row.Roads = null;
        row.RoadNames = null;
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
            var sameInput = row.InputHash == hash && row.FusedType == t.Type;
            var current = sameInput && (row.RuleVersion == RouteQuality.RuleVersion || row.Method == RebuildMethods.Similar);
            // An older rule's line for the same input stays served (never nulled by a failure)
            var stale = sameInput && !current && Serves(row);
            var outcome = Failure(row, current, stale, t, hash, Clip(costing, 16) ?? string.Empty, reason);
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

    /// <summary>The row has a line that is drawn or offered (not a failure)</summary>
    private static bool Serves(MatchedRouteRow row) => row.Polyline != null && row.Status != RouteStatuses.Failed;

    private static string? Clip(string? s, int max) => s == null ? null : s.Length > max ? s[..max] : s;

    /// <summary>The record's values at match time: the read-time check of AD-34 rule 3</summary>
    private static void Identify(MatchedRouteRow row, RouteTrack t, string hash, string costing)
    {
        SetEnds(row, t);
        row.SourceKey = t.SourceKey;
        row.InputHash = hash;
        row.SourceUpdatedAt = t.UpdatedAt;
        row.SourcePointCount = t.PointCount;
        row.FusedType = t.Type;
        row.Costing = costing;
        row.Engine = "valhalla";
        row.RuleVersion = RouteQuality.RuleVersion;
    }

    /// <summary>The record's start time and first and last point: what the similar-drive pool reads (AD-38 rule 3)</summary>
    private static void SetEnds(MatchedRouteRow row, RouteTrack t)
    {
        row.StartedAt = t.Start;
        if (t.Points.Count > 0)
        {
            (row.StartLat, row.StartLon) = (Math.Round(t.Points[0].Lat, 6), Math.Round(t.Points[0].Lon, 6));
            (row.EndLat, row.EndLon) = (Math.Round(t.Points[^1].Lat, 6), Math.Round(t.Points[^1].Lon, 6));
        }
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
