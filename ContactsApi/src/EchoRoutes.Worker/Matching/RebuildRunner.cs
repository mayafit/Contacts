/**
 * @fileoverview The rebuild job (AD-38 rules 1 to 6): one record rebuilt on demand from
 * similar drives or the Owner's marked points, its result kept on the job as a
 * suggestion, and the suggestion written as the record's match when the Owner uses it
 * @module EchoRoutes.Worker.Matching
 */
using System.Text.Json;
using ContactsApi.Core.Interfaces;
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker.Backend;
using EchoRoutes.Worker.Valhalla;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EchoRoutes.Worker.Matching;

/// <summary>What a rebuild ended with</summary>
public static class RebuildOutcomes
{
    /// <summary>A suggestion is ready</summary>
    public const string Suggested = "suggested";

    /// <summary>No similar drive fits</summary>
    public const string NoSimilar = "no_similar";

    /// <summary>The marked points cannot be used (the reason says why)</summary>
    public const string Invalid = "invalid";

    /// <summary>The record cannot be matched (no track, a type never matched)</summary>
    public const string NotMatchable = "not_matchable";

    /// <summary>The record no longer exists</summary>
    public const string Gone = "gone";

    /// <summary>The engine placed none of the (real or marked) points on a road</summary>
    public const string NoMatch = "no_match";
}

/// <summary>A rebuild's result, as stored on the job and returned to the panel</summary>
public record RebuildSuggestion
{
    public string Method { get; init; } = RebuildMethods.Similar;
    public string Outcome { get; init; } = RebuildOutcomes.Suggested;
    /// <summary>Why there is no suggestion (invalid, not matchable)</summary>
    public string? Message { get; init; }
    public Guid ActivityId { get; init; }
    public string? SourceKey { get; init; }
    public string? InputHash { get; init; }
    public DateTime SourceUpdatedAt { get; init; }
    public int SourcePointCount { get; init; }
    public string? FusedType { get; init; }
    public string? Costing { get; init; }
    public DateTime StartedAt { get; init; }
    public double[]? StartPoint { get; init; }
    public double[]? EndPoint { get; init; }
    public string? Status { get; init; }
    public double? Confidence { get; init; }
    /// <summary>The stored match's confidence before (null without one), so the panel can say whether this is better</summary>
    public double? PreviousConfidence { get; init; }
    public string? PreviousStatus { get; init; }
    public string? Reason { get; init; }
    public string? Polyline { get; init; }
    public List<RoutePiece>? Pieces { get; init; }
    public List<RouteRoad>? Roads { get; init; }
    public double? LengthM { get; init; }
    public MatchMetrics? Metrics { get; init; }
    public SimilarBasis? Basis { get; init; }
    /// <summary>The marked points (waypoints only), as [[lat, lon]…]</summary>
    public List<double[]>? Waypoints { get; init; }
    /// <summary>Synthetic points added, and matches tried</summary>
    public int Added { get; init; }
    public int Tried { get; init; }
    public string? EngineVersion { get; init; }
    public string? GraphVersion { get; init; }
}

/// <summary>The similar-drive pool: the candidates, and the rows that have no corridor cells yet</summary>
public record SimilarPool(List<SimilarCandidate> Candidates, int Preparing);

/// <summary>Runs rebuild jobs and applies their suggestions</summary>
public class RebuildRunner
{
    /// <summary>The least confidence of a drive a similar one may lend points (AD-38 rule 3)</summary>
    public const double PoolMinConfidence = 0.8;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly RoutesDbContext _db;
    private readonly IRoutesBackend _backend;
    private readonly IValhallaClient _engine;
    private readonly EngineBreaker _breaker;
    private readonly GraphInfoSource _graph;
    private readonly MatchSettings _settings;

    /// <summary>Creates the runner</summary>
    public RebuildRunner(RoutesDbContext db, IRoutesBackend backend, IValhallaClient engine, EngineBreaker breaker, GraphInfoSource graph,
        IOptions<RoutesOptions> options)
    {
        _db = db;
        _backend = backend;
        _engine = engine;
        _breaker = breaker;
        _graph = graph;
        _settings = MatchSettings.From(options.Value);
    }

    /// <summary>Runs a rebuild of one record; returns the suggestion (or why there is none)</summary>
    /// <exception cref="StageBEngineException">The engine failed</exception>
    public async Task<RebuildSuggestion> RunAsync(string owner, Guid activityId, string method, List<(double Lat, double Lon)>? marks,
        CancellationToken ct)
    {
        var t = (await _backend.TracksAsync(owner, new[] { activityId }, true, ct)).FirstOrDefault();
        if (t == null)
        {
            return new RebuildSuggestion { Method = method, Outcome = RebuildOutcomes.Gone, ActivityId = activityId };
        }

        var costing = ValhallaTrace.CostingFor(t.Type, t.SourceType);
        if (costing == null || t.Points.Count < 2)
        {
            return new RebuildSuggestion
            {
                Method = method, Outcome = RebuildOutcomes.NotMatchable, ActivityId = activityId,
                Message = t.Points.Count < 2 ? "The activity has no track to rebuild." : $"A {t.Type} is not placed on roads."
            };
        }

        var previous = await _db.MatchedRoutes.AsNoTracking().Where(m => m.OwnerId == owner && m.ActivityId == activityId)
            .Select(m => new { m.Status, m.Confidence, m.Polyline }).FirstOrDefaultAsync(ct);
        RebuildOutcome? outcome;
        if (method == RebuildMethods.Waypoints)
        {
            var why = Waypoints.Check(t.Points, marks ?? new());
            if (why != null)
            {
                return new RebuildSuggestion { Method = method, Outcome = RebuildOutcomes.Invalid, ActivityId = activityId, Message = why };
            }

            outcome = await Rebuilder.WaypointsAsync(t.Points, marks!, t.Type, costing, t.Source, _settings, _engine, _breaker, ct);
        }
        else
        {
            var pool = await PoolAsync(owner, t, ct);
            outcome = await Rebuilder.SimilarAsync(t.Points, pool.Candidates, t.Type, costing, t.Source, _settings, _engine, _breaker, ct);
            if (outcome == null)
            {
                var preparing = pool.Preparing > 0
                    ? $" {pool.Preparing} of your drives are still being prepared for this; try again in a few minutes."
                    : string.Empty;
                return new RebuildSuggestion
                {
                    Method = method, Outcome = RebuildOutcomes.NoSimilar, ActivityId = activityId,
                    Message = (pool.Candidates.Count == 0
                        ? "No confidently matched drive runs along this drive's points."
                        : $"None of the {pool.Candidates.Count} drives that pass near this drive's points follows them closely enough.") + preparing
                };
            }
        }

        var r = outcome.Result;
        return new RebuildSuggestion
        {
            Method = method,
            Outcome = r.Polyline == null ? RebuildOutcomes.NoMatch : RebuildOutcomes.Suggested,
            Message = r.Polyline == null ? "The engine placed none of the points on a road." : null,
            ActivityId = activityId,
            SourceKey = t.SourceKey,
            InputHash = RouteQuality.InputHash(t.Points),
            SourceUpdatedAt = t.UpdatedAt,
            SourcePointCount = t.PointCount,
            FusedType = t.Type,
            Costing = costing,
            StartedAt = t.Start,
            StartPoint = new[] { Math.Round(t.Points[0].Lat, 6), Math.Round(t.Points[0].Lon, 6) },
            EndPoint = new[] { Math.Round(t.Points[^1].Lat, 6), Math.Round(t.Points[^1].Lon, 6) },
            Status = r.Status,
            Confidence = r.Confidence,
            PreviousConfidence = previous?.Polyline != null ? previous.Confidence : null,
            PreviousStatus = previous?.Status,
            Reason = r.Reason,
            Polyline = r.Polyline,
            Pieces = r.Pieces,
            Roads = r.Roads,
            LengthM = r.Polyline != null ? r.LengthM : null,
            Metrics = r.Metrics,
            Basis = outcome.Basis,
            Waypoints = method == RebuildMethods.Waypoints ? marks!.Select(m => new[] { Math.Round(m.Lat, 6), Math.Round(m.Lon, 6) }).ToList() : null,
            Added = outcome.Added,
            Tried = outcome.Tried,
            EngineVersion = await _engine.VersionAsync(ct),
            GraphVersion = _graph.Current?.Version
        };
    }

    /// <summary>
    /// The similar-drive pool (AD-38 rule 3): the Owner's own confident automatic matches of
    /// the same type, not rejected, whose corridor cells cover at least the least fit of this
    /// record's non-stray points (rows without cells yet: both ends within the endpoint
    /// distance, either way round), the best by cover, ends and time of day, at most the
    /// scored count, one per drive. Preparing counts the rows that have no cells yet.
    /// </summary>
    public async Task<SimilarPool> PoolAsync(string owner, RouteTrack t, CancellationToken ct)
    {
        var clean = SimilarRoutes.Clean(t.Points, RouteQuality.ProfileFor(t.Type).MaxSpeedKmh);
        if (clean.Count < 2)
        {
            return new SimilarPool(new(), 0);
        }

        var pointCells = clean.Select(p => RouteCells.Of(p.Lat, p.Lon)).ToArray();
        var cells = pointCells.Distinct().ToArray();
        var (s, e) = (clean[0], clean[^1]);
        var dLat = SimilarRoutes.EndpointM / 110_540;
        double DLon(double lat) => SimilarRoutes.EndpointM / (111_320 * Math.Max(0.1, Math.Cos(lat * Math.PI / 180)));
        double sLat0 = s.Lat - dLat, sLat1 = s.Lat + dLat, sLon0 = s.Lon - DLon(s.Lat), sLon1 = s.Lon + DLon(s.Lat);
        double eLat0 = e.Lat - dLat, eLat1 = e.Lat + dLat, eLon0 = e.Lon - DLon(e.Lat), eLon1 = e.Lon + DLon(e.Lat);
        var eligible = _db.MatchedRoutes.AsNoTracking()
            .Where(m => m.OwnerId == owner && m.ActivityId != t.Id && m.Status == RouteStatuses.Matched && m.Confidence >= PoolMinConfidence
                        && m.Method == RebuildMethods.Auto && m.FusedType == t.Type && m.Polyline != null)
            .Where(m => !_db.Decisions.Any(d => d.OwnerId == owner && d.SourceKey == m.SourceKey && d.Kind == RouteDecisionKinds.Reject));
        // By the way (an indexed overlap of the cells); rows not filled yet by their ends
        var rows = await eligible
            .Where(m => (m.Cells != null && m.Cells.Any(c => cells.Contains(c)))
                        || (m.Cells == null && m.StartLat != null && m.StartLon != null && m.EndLat != null && m.EndLon != null
                            && ((m.StartLat >= sLat0 && m.StartLat <= sLat1 && m.StartLon >= sLon0 && m.StartLon <= sLon1
                                 && m.EndLat >= eLat0 && m.EndLat <= eLat1 && m.EndLon >= eLon0 && m.EndLon <= eLon1)
                                || (m.StartLat >= eLat0 && m.StartLat <= eLat1 && m.StartLon >= eLon0 && m.StartLon <= eLon1
                                    && m.EndLat >= sLat0 && m.EndLat <= sLat1 && m.EndLon >= sLon0 && m.EndLon <= sLon1))))
            .Select(m => new { m.ActivityId, m.StartedAt, m.ComputedAt, m.StartLat, m.StartLon, m.EndLat, m.EndLon, m.Cells })
            .ToListAsync(ct);
        var preparing = await eligible.CountAsync(m => m.Cells == null, ct);

        bool Near(double? lat, double? lon, RoutePoint p) =>
            lat.HasValue && lon.HasValue && Geo.Haversine(lat.Value, lon.Value, p.Lat, p.Lon) <= SimilarRoutes.EndpointM;
        var nearest = new List<(Guid Id, DateTime Start, bool EndsNear, double Cover)>();
        foreach (var m in rows)
        {
            var endsNear = (Near(m.StartLat, m.StartLon, s) && Near(m.EndLat, m.EndLon, e))
                           || (Near(m.StartLat, m.StartLon, e) && Near(m.EndLat, m.EndLon, s));
            // The share of points in its cells bounds its fit from above (RouteCells)
            var cover = m.Cells == null
                ? endsNear ? 1 : 0
                : pointCells.Count(c => Array.BinarySearch(m.Cells, c) >= 0) / (double)pointCells.Length;
            if (cover >= SimilarRoutes.MinFit)
            {
                nearest.Add((m.ActivityId, m.StartedAt ?? m.ComputedAt, endsNear, cover));
            }
        }

        nearest = nearest.OrderByDescending(x => x.Cover).ThenByDescending(x => x.EndsNear)
            .ThenBy(x => SimilarRoutes.HourDiff(clean[0].T, x.Start)).Take(SimilarRoutes.MaxCandidates).ToList();
        // The lines of the best only (a years-long commute has thousands of rows)
        var ids = nearest.Select(x => x.Id).ToList();
        var lines = await _db.MatchedRoutes.AsNoTracking()
            .Where(m => m.OwnerId == owner && ids.Contains(m.ActivityId))
            .Select(m => new { m.ActivityId, m.Polyline, m.Pieces })
            .ToDictionaryAsync(m => m.ActivityId, ct);
        var candidates = nearest.Where(x => lines.TryGetValue(x.Id, out var l) && l.Polyline != null)
            .Select(x => new SimilarCandidate(x.Id, x.Start, lines[x.Id].Polyline!,
                JsonSerializer.Deserialize<List<RoutePiece>>(lines[x.Id].Pieces, Json), x.EndsNear))
            .ToList();
        return new SimilarPool(candidates, preparing);
    }

    /// <summary>
    /// Uses a rebuild's suggestion as the record's match (AD-38 rule 6): the Owner's job, done,
    /// of this record, whose input is unchanged. Returns the suggestion, or why it cannot be
    /// used ("not_found", or a conflict in words).
    /// </summary>
    public async Task<(RebuildSuggestion? Suggestion, string? Conflict)> ApplyAsync(string owner, Guid jobId, Guid activityId, CancellationToken ct)
    {
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.OwnerId == owner && j.Id == jobId && j.Kind == RouteJobKinds.Rebuild, ct);
        if (job == null)
        {
            return (null, "not_found");
        }

        var r = job.Result == null ? null : JsonSerializer.Deserialize<RebuildSuggestion>(job.Result, Json);
        if (r != null && r.ActivityId != activityId)
        {
            return (null, "not_found");
        }

        if (job.State != RouteJobStates.Done || r == null || r.Outcome != RebuildOutcomes.Suggested || r.Polyline == null)
        {
            return (r, "There is no suggestion to use.");
        }

        // The drive as it is now, not as last assessed
        var t = (await _backend.TracksAsync(owner, new[] { r.ActivityId }, true, ct)).FirstOrDefault();
        if (t == null)
        {
            return (null, "not_found");
        }

        if (RouteQuality.InputHash(t.Points) != r.InputHash || t.Type != r.FusedType)
        {
            return (r, "The drive changed after the rebuild; rebuild it again.");
        }

        try
        {
            await WriteAsync(owner, r, ct);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            return (r, "The route changed at the same time; try again.");
        }

        return (r, null);
    }

    /// <summary>Writes a suggestion as the record's match, with its marked points, in one transaction</summary>
    private async Task WriteAsync(string owner, RebuildSuggestion r, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var row = await _db.MatchedRoutes.FirstOrDefaultAsync(m => m.OwnerId == owner && m.ActivityId == r.ActivityId, ct);
        if (row == null)
        {
            row = new MatchedRouteRow { OwnerId = owner, ActivityId = r.ActivityId };
            _db.MatchedRoutes.Add(row);
        }

        row.SourceKey = r.SourceKey!;
        row.InputHash = r.InputHash!;
        row.SourceUpdatedAt = r.SourceUpdatedAt;
        row.SourcePointCount = r.SourcePointCount;
        row.FusedType = r.FusedType!;
        row.Costing = r.Costing;
        row.Engine = "valhalla";
        row.EngineVersion = r.EngineVersion;
        row.GraphVersion = r.GraphVersion;
        row.RuleVersion = RouteQuality.RuleVersion;
        row.Status = r.Status!;
        row.Confidence = r.Confidence;
        row.Polyline = r.Polyline;
        (row.ListPolyline, row.DensityPolyline) = RouteLines.Derive(r.Polyline);
        row.Cells = RouteCells.Line(r.Polyline);
        row.Pieces = JsonSerializer.Serialize(r.Pieces ?? new(), Json);
        row.Roads = RouteRoads.Serialize(r.Roads ?? new());
        row.RoadNames = (r.Roads ?? new()).Select(x => x.Name).ToArray();
        row.MatchedDistanceM = r.LengthM;
        row.MetricsAfter = r.Metrics == null ? null : JsonSerializer.Serialize(r.Metrics, Json);
        row.Reason = r.Reason?.Length > 500 ? r.Reason[..500] : r.Reason;
        row.Method = r.Method;
        row.Basis = r.Basis == null ? null : JsonSerializer.Serialize(r.Basis, Json);
        row.StartedAt = r.StartedAt;
        (row.StartLat, row.StartLon) = r.StartPoint is { Length: 2 } sp ? ((double?)sp[0], (double?)sp[1]) : (null, null);
        (row.EndLat, row.EndLon) = r.EndPoint is { Length: 2 } ep ? ((double?)ep[0], (double?)ep[1]) : (null, null);
        row.Attempts = 0;
        row.NextAttemptAt = null;
        row.ComputedAt = DateTime.UtcNow;

        if (r.Method == RebuildMethods.Waypoints && r.Waypoints != null)
        {
            // The Owner's points: a decision, used by every later match of the record
            var marks = await _db.Waypoints.FirstOrDefaultAsync(w => w.OwnerId == owner && w.SourceKey == r.SourceKey, ct);
            if (marks == null)
            {
                marks = new RouteWaypoints { OwnerId = owner, SourceKey = r.SourceKey! };
                _db.Waypoints.Add(marks);
            }

            marks.Points = RouteMarks.Serialize(r.Waypoints.Select(p => (p[0], p[1])));
            marks.CreatedAt = DateTime.UtcNow;
        }
        else if (r.Method == RebuildMethods.Similar)
        {
            // A similar rebuild replaces a route through marked points: those points go
            var marks = await _db.Waypoints.FirstOrDefaultAsync(w => w.OwnerId == owner && w.SourceKey == r.SourceKey, ct);
            if (marks != null)
            {
                _db.Waypoints.Remove(marks);
            }
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Clears the points the Owner marked for a record ("clear marked points"): the record
    /// is then matched again as the matcher's own, soon (a due retry). Returns whether there were any.
    /// </summary>
    public async Task<bool> ClearMarksAsync(string owner, Guid activityId, CancellationToken ct)
    {
        var key = await _db.MatchedRoutes.Where(m => m.OwnerId == owner && m.ActivityId == activityId).Select(m => m.SourceKey)
                      .FirstOrDefaultAsync(ct)
                  ?? await _db.Quality.Where(q => q.OwnerId == owner && q.ActivityId == activityId).Select(q => q.SourceKey).FirstOrDefaultAsync(ct);
        if (key == null)
        {
            return false;
        }

        var removed = await _db.Waypoints.Where(w => w.OwnerId == owner && w.SourceKey == key).ExecuteDeleteAsync(ct);
        if (removed > 0)
        {
            await _db.MatchedRoutes.Where(m => m.OwnerId == owner && m.ActivityId == activityId && m.Method == RebuildMethods.Waypoints)
                .ExecuteUpdateAsync(x => x.SetProperty(m => m.NextAttemptAt, DateTime.UtcNow), ct);
        }

        return removed > 0;
    }
}
