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
            outcome = await Rebuilder.SimilarAsync(t.Points, pool, t.Type, costing, t.Source, _settings, _engine, _breaker, ct);
            if (outcome == null)
            {
                return new RebuildSuggestion
                {
                    Method = method, Outcome = RebuildOutcomes.NoSimilar, ActivityId = activityId,
                    Message = pool.Count == 0
                        ? "No confidently matched drive starts and ends near this one."
                        : $"None of the {pool.Count} drives between the same places follows this drive's own points."
                };
            }
        }

        var r = outcome.Result;
        return new RebuildSuggestion
        {
            Method = method,
            Outcome = r.Polyline == null ? RebuildOutcomes.NoSimilar : RebuildOutcomes.Suggested,
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
    /// the same type, not rejected, whose start and end lie within the endpoint distance of
    /// this record's start and end (or end and start), nearest first, at most the scored count
    /// </summary>
    public async Task<List<SimilarCandidate>> PoolAsync(string owner, RouteTrack t, CancellationToken ct)
    {
        var (s, e) = (t.Points[0], t.Points[^1]);
        var dLat = SimilarRoutes.EndpointM / 110_540;
        double DLon(double lat) => SimilarRoutes.EndpointM / (111_320 * Math.Max(0.1, Math.Cos(lat * Math.PI / 180)));
        double sLat0 = s.Lat - dLat, sLat1 = s.Lat + dLat, sLon0 = s.Lon - DLon(s.Lat), sLon1 = s.Lon + DLon(s.Lat);
        double eLat0 = e.Lat - dLat, eLat1 = e.Lat + dLat, eLon0 = e.Lon - DLon(e.Lat), eLon1 = e.Lon + DLon(e.Lat);
        var rows = await _db.MatchedRoutes.AsNoTracking()
            .Where(m => m.OwnerId == owner && m.ActivityId != t.Id && m.Status == RouteStatuses.Matched && m.Confidence >= PoolMinConfidence
                        && m.Method == RebuildMethods.Auto && m.FusedType == t.Type && m.Polyline != null
                        && m.StartLat != null && m.StartLon != null && m.EndLat != null && m.EndLon != null
                        && ((m.StartLat >= sLat0 && m.StartLat <= sLat1 && m.StartLon >= sLon0 && m.StartLon <= sLon1
                             && m.EndLat >= eLat0 && m.EndLat <= eLat1 && m.EndLon >= eLon0 && m.EndLon <= eLon1)
                            || (m.StartLat >= eLat0 && m.StartLat <= eLat1 && m.StartLon >= eLon0 && m.StartLon <= eLon1
                                && m.EndLat >= sLat0 && m.EndLat <= sLat1 && m.EndLon >= sLon0 && m.EndLon <= sLon1)))
            .Where(m => !_db.Decisions.Any(d => d.OwnerId == owner && d.SourceKey == m.SourceKey && d.Kind == RouteDecisionKinds.Reject))
            .Select(m => new { m.ActivityId, m.StartedAt, m.ComputedAt, m.Polyline, m.Pieces, m.StartLat, m.StartLon, m.EndLat, m.EndLon })
            .ToListAsync(ct);
        var pool = new List<(SimilarCandidate C, double D)>();
        foreach (var m in rows)
        {
            var same = Geo.Haversine(m.StartLat!.Value, m.StartLon!.Value, s.Lat, s.Lon) + Geo.Haversine(m.EndLat!.Value, m.EndLon!.Value, e.Lat, e.Lon);
            var reversed = Geo.Haversine(m.StartLat.Value, m.StartLon.Value, e.Lat, e.Lon) + Geo.Haversine(m.EndLat.Value, m.EndLon.Value, s.Lat, s.Lon);
            var isReversed = reversed < same;
            var (a, b) = isReversed
                ? (Geo.Haversine(m.StartLat.Value, m.StartLon.Value, e.Lat, e.Lon), Geo.Haversine(m.EndLat.Value, m.EndLon.Value, s.Lat, s.Lon))
                : (Geo.Haversine(m.StartLat.Value, m.StartLon.Value, s.Lat, s.Lon), Geo.Haversine(m.EndLat.Value, m.EndLon.Value, e.Lat, e.Lon));
            if (a > SimilarRoutes.EndpointM || b > SimilarRoutes.EndpointM)
            {
                continue;
            }

            pool.Add((new SimilarCandidate(m.ActivityId, m.StartedAt ?? m.ComputedAt, m.Polyline!, isReversed,
                JsonSerializer.Deserialize<List<RoutePiece>>(m.Pieces, Json)), a + b));
        }

        return pool.OrderBy(x => x.D).Take(SimilarRoutes.MaxCandidates).Select(x => x.C).ToList();
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

        var quality = await _db.Quality.AsNoTracking().Where(q => q.OwnerId == owner && q.ActivityId == r.ActivityId)
            .Select(q => q.InputHash).FirstOrDefaultAsync(ct);
        if (quality != null && quality != r.InputHash)
        {
            return (r, "The drive changed after the rebuild; rebuild it again.");
        }

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
            await _db.Waypoints.Where(w => w.OwnerId == owner && w.SourceKey == r.SourceKey).ExecuteDeleteAsync(ct);
        }

        await _db.SaveChangesAsync(ct);
        return (r, null);
    }
}
