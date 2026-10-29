/**
 * @fileoverview Cutting a track into engine calls (AD-35 rule 4, AD-32 rule 2): long
 * gaps cut first (they become gap pieces), then calls of at most N points and K km
 * @module EchoRoutes.Worker.Matching
 */
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Routes;

namespace EchoRoutes.Worker.Matching;

/// <summary>One engine call: a run of the track's points</summary>
/// <param name="Start">Index of its first point in the track</param>
/// <param name="Count">Its points</param>
public record TraceCall(int Start, int Count)
{
    /// <summary>Index after its last point</summary>
    public int End => Start + Count;
}

/// <summary>A part of the track between two cut gaps; sent only with at least 2 points</summary>
public record TraceSegment(int Start, int Count, List<TraceCall> Calls);

/// <summary>The engine-facing numbers of one match (all from configuration)</summary>
public record MatchSettings(
    double SplitGapM, TimeSpan SplitGap, int MaxPointsPerCall, double MaxMPerCall,
    double BreakageDistanceM, double SearchRadiusM, double GpsAccuracyTimelineM, double GpsAccuracyDenseM, int MaxStoredVertices)
{
    /// <summary>From the service's options</summary>
    public static MatchSettings From(RoutesOptions o) => new(
        o.SplitGapM, TimeSpan.FromMinutes(o.SplitGapMinutes), Math.Max(2, o.MaxPointsPerCall), o.MaxKmPerCall * 1000,
        o.BreakageDistanceM, o.SearchRadiusM, o.GpsAccuracyTimelineM, o.GpsAccuracyDenseM, Math.Max(2, o.MaxStoredVertices))
    {
        GapFill = GapFillSettings.From(o),
        OffRoadTimelineM = o.OffRoadTimelineM
    };

    /// <summary>Gap filling by /route (AD-35 rule 7)</summary>
    public GapFillSettings GapFill { get; init; } = GapFillSettings.From(new RoutesOptions());

    /// <summary>A Google Timeline anchor is off-road only beyond this</summary>
    public double OffRoadTimelineM { get; init; } = 100;

    /// <summary>
    /// An anchor farther than this from its road is off-road (AD-35 rule 6): the costing's
    /// 50 m (30 m on foot or bike), and for coarse Timeline points the larger Timeline tolerance
    /// </summary>
    public double AnchorToleranceFor(string source, string? costing) =>
        string.Equals(source, "google-timeline", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(RouteAssembler.OffRoadM(costing), OffRoadTimelineM)
            : RouteAssembler.OffRoadM(costing);

    /// <summary>The per-source GPS error (AD-35 rule 4): Timeline is coarse, device recordings are dense</summary>
    public double GpsAccuracyFor(string source) =>
        string.Equals(source, "google-timeline", StringComparison.OrdinalIgnoreCase) ? GpsAccuracyTimelineM : GpsAccuracyDenseM;

    /// <summary>search_radius, never below gps_accuracy</summary>
    public double SearchRadiusFor(string source) => Math.Max(SearchRadiusM, GpsAccuracyFor(source));
}

/// <summary>Plans the engine calls of a track</summary>
public static class TracePlanner
{
    /// <summary>
    /// Segments between gaps longer than the split distance or time, each cut into
    /// calls of at most MaxPointsPerCall points and MaxMPerCall metres (never a call of
    /// one point). A segment of one point is not sent.
    /// </summary>
    public static List<TraceSegment> Plan(IReadOnlyList<RoutePoint> points, MatchSettings s)
    {
        var segments = new List<TraceSegment>();
        var start = 0;
        for (var i = 1; i <= points.Count; i++)
        {
            if (i < points.Count && !IsGap(points[i - 1], points[i], s))
            {
                continue;
            }

            var count = i - start;
            segments.Add(new TraceSegment(start, count, count >= 2 ? Chunk(points, start, count, s) : new List<TraceCall>()));
            start = i;
        }

        return segments;
    }

    /// <summary>Whether the leg between two points is cut before the call</summary>
    public static bool IsGap(RoutePoint a, RoutePoint b, MatchSettings s) =>
        b.T - a.T > s.SplitGap || Geo.Haversine(a.Lat, a.Lon, b.Lat, b.Lon) > s.SplitGapM;

    private static List<TraceCall> Chunk(IReadOnlyList<RoutePoint> points, int start, int count, MatchSettings s)
    {
        var calls = new List<TraceCall>();
        var callStart = start;
        double metres = 0;
        for (var i = start + 1; i < start + count; i++)
        {
            var leg = Geo.Haversine(points[i - 1].Lat, points[i - 1].Lon, points[i].Lat, points[i].Lon);
            var n = i - callStart + 1;
            if (n > s.MaxPointsPerCall || metres + leg > s.MaxMPerCall)
            {
                calls.Add(new TraceCall(callStart, i - callStart));
                callStart = i;
                metres = 0;
                continue;
            }

            metres += leg;
        }

        calls.Add(new TraceCall(callStart, start + count - callStart));
        // Never a call of one point: borrow one from the call before
        if (calls.Count > 1 && calls[^1].Count < 2)
        {
            var prev = calls[^2];
            calls[^2] = prev with { Count = prev.Count - 1 };
            calls[^1] = new TraceCall(prev.End - 1, calls[^1].Count + 1);
        }

        return calls.Where(c => c.Count >= 2).ToList();
    }
}
