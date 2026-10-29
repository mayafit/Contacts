/**
 * @fileoverview From Valhalla's answers to a stored Matched Route (AD-35 rules 6 and 7,
 * rule routes.v3): the trace_attributes answers become a skeleton (vertices, a kind per
 * segment, the anchors and recorded points on it); every unbridged stretch between
 * two anchors can then be filled with a /route answer (GapFiller); the result is the
 * line at precision 5 with at most N vertices, its pieces {fromM, toM, kind}, the
 * roads it follows (AD-37), the confidence and the status. Pure.
 * @module EchoRoutes.Worker.Matching
 */
using System.Globalization;
using ContactsApi.Core.Interfaces;
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Routes;

namespace EchoRoutes.Worker.Matching;

/// <summary>How the confidence came out, stored as matched_routes.MetricsAfter (AD-35 rule 6, routes.v3)</summary>
public record MatchMetrics
{
    /// <summary>The rule that computed it</summary>
    public string Rule { get; init; } = RouteQuality.RuleVersion;

    /// <summary>How the factors combine</summary>
    public string Formula { get; init; } = RouteAssembler.Formula;

    /// <summary>Share of the stored line that is observed or inferred (gap length counts against it)</summary>
    public double Coverage { get; init; }

    /// <summary>max((anchors − off-road) / points sent, routed-only weight × the accepted share of the stretches' line)</summary>
    public double AnchorQuality { get; init; }

    /// <summary>Detour ratio of the part not filled by routes, and speed against the type's maximum (every filled leg passed its own tests)</summary>
    public double Plausibility { get; init; }

    /// <summary>1 − 0.15 × the engine's breaks that no accepted route bridged</summary>
    public double Continuity { get; init; }

    /// <summary>Anchors over points sent</summary>
    public double MatchedShare { get; init; }

    /// <summary>1 − off-road anchors over anchors, at AnchorToleranceM</summary>
    public double Proximity { get; init; }

    /// <summary>An anchor farther than this from its road is off-road (per source: Timeline 100 m)</summary>
    public double AnchorToleranceM { get; init; }

    public int PointsSent { get; init; }
    public int Anchors { get; init; }
    public int OffRoad { get; init; }

    /// <summary>Every break the engine made</summary>
    public int EngineBreaks { get; init; }

    /// <summary>Breaks bridged by an accepted route (not penalised)</summary>
    public int BridgedBreaks { get; init; }

    public int Calls { get; init; }
    public int CutGaps { get; init; }
    public double TrackM { get; init; }
    /// <summary>The line over the recorded track, both without the filled stretches</summary>
    public double LengthRatio { get; init; }
    public double ObservedM { get; init; }
    public double InferredM { get; init; }
    public double GapM { get; init; }
    public double ObservedShare { get; init; }
    public double InferredShare { get; init; }

    /// <summary>Unbridged stretches found (each one /route call at most)</summary>
    public int Stretches { get; init; }

    /// <summary>Stretches filled by an accepted route, and their routed length</summary>
    public int Filled { get; init; }
    public double FilledM { get; init; }
    public int FillRejectedTime { get; init; }
    public int FillRejectedLength { get; init; }

    /// <summary>Stretches whose route back-tracked near an end (a U-turn); routes.v2 counted them in FillRejectedLength</summary>
    public int FillRejectedReversal { get; init; }

    /// <summary>Stretches asked once more without headings after the headed route was rejected</summary>
    public int FillRetried { get; init; }
    public int FillNoRoute { get; init; }
    public int FillTimeouts { get; init; }

    /// <summary>Stretches not routed: over the per-drive cap (they stay gaps)</summary>
    public int FillSkipped { get; init; }

    /// <summary>Stretches not routed because the record's filling stopped early (timeouts, or interactive work waiting)</summary>
    public int FillDeferred { get; init; }

    /// <summary>No point matched: the line is built only from routes between the recorded points</summary>
    public bool RoutedOnly { get; init; }
}

/// <summary>A record's match</summary>
/// <param name="Polyline">Google encoded, precision 5; null when nothing matched</param>
/// <param name="Pieces">Metres along the stored line</param>
/// <param name="Reason">Why it is not matched (null when it is): the weakest factor in words</param>
/// <param name="Roads">The roads the line follows, in travel order (AD-37); null without a line</param>
public record MatchResult(string Status, double Confidence, string? Polyline, List<RoutePiece> Pieces, double LengthM,
    MatchMetrics Metrics, string? Reason, List<RouteRoad>? Roads = null);

/// <summary>An anchor or a recorded point on the skeleton</summary>
/// <param name="Vertex">Its vertex in the skeleton</param>
/// <param name="Point">Its index in the recorded points</param>
/// <param name="BreakBefore">The engine broke the path between the anchor before and this one</param>
internal sealed record SkeletonNode(int Vertex, int Point, DateTime T, bool Anchor, bool BreakBefore);

/// <summary>
/// The line as the engine's answers leave it: vertices, the kind of the segment into
/// each vertex, and the anchors (input points placed on the matched shape) and the
/// recorded points that no call matched, in order
/// </summary>
public sealed class RouteSkeleton
{
    internal readonly List<(double Lat, double Lon)> V = new();
    internal readonly List<string> Kind = new();
    /// <summary>The road names of the segment into each vertex (AD-37); null where unknown</summary>
    internal readonly List<string[]?> Road = new();
    internal readonly List<SkeletonNode> Nodes = new();
    internal readonly List<double> AnchorFromTrace = new();

    /// <summary>The recorded track's length up to each point</summary>
    internal double[] TrackCum = Array.Empty<double>();

    public int Sent { get; internal set; }
    public int Anchors { get; internal set; }
    public int Calls { get; internal set; }
    public int CutGaps { get; internal set; }
    public int Breaks { get; internal set; }

    internal void Add((double Lat, double Lon) v, string kind, string[]? road = null)
    {
        V.Add(v);
        Kind.Add(kind);
        Road.Add(road);
    }

    internal void AddRecorded(IReadOnlyList<RoutePoint> points, int i, string kind)
    {
        var p = points[i];
        Add((p.Lat, p.Lon), kind);
        Nodes.Add(new SkeletonNode(V.Count - 1, i, p.T, false, false));
    }
}

/// <summary>Assembles the calls' answers (and the gap fills) into one line</summary>
public static class RouteAssembler
{
    /// <summary>The confidence formula (AD-35 rule 6, routes.v2 and later), stored with the metrics</summary>
    public const string Formula = "min(coverage, anchorQuality, plausibility, continuity)";

    /// <summary>A matched point farther than this from its road is off-road (AD-35 rule 6); Timeline uses a larger tolerance</summary>
    public static double OffRoadM(string? costing) => costing is "bicycle" or "pedestrian" ? 30 : 50;

    /// <summary>
    /// Builds the match without gap filling. <paramref name="answers"/> holds an answer
    /// per call; a call missing from it (or null) got no match, and its points stay as
    /// recorded (a gap).
    /// </summary>
    public static MatchResult Assemble(IReadOnlyList<RoutePoint> points, IReadOnlyList<TraceSegment> plan,
        IReadOnlyDictionary<TraceCall, TraceResponse?> answers, RouteProfile profile, string? costing, int maxVertices) =>
        Assemble(points, Build(points, plan, answers, profile), null, profile, costing, maxVertices, OffRoadM(costing), 0);

    /// <summary>The skeleton of the engine's answers</summary>
    public static RouteSkeleton Build(IReadOnlyList<RoutePoint> points, IReadOnlyList<TraceSegment> plan,
        IReadOnlyDictionary<TraceCall, TraceResponse?> answers, RouteProfile profile)
    {
        var sk = new RouteSkeleton { TrackCum = new double[points.Count] };
        for (var i = 1; i < points.Count; i++)
        {
            sk.TrackCum[i] = sk.TrackCum[i - 1] + Geo.Haversine(points[i - 1].Lat, points[i - 1].Lon, points[i].Lat, points[i].Lon);
        }

        var straightJump = profile.StraightJumpM ?? 500;
        for (var si = 0; si < plan.Count; si++)
        {
            var segment = plan[si];
            if (si > 0)
            {
                sk.CutGaps++;
            }

            // The kind of the next joint: a cut gap between segments, else the split rule
            var joint = si > 0 ? PieceKinds.Gap : null;
            var next = segment.Start;
            foreach (var call in segment.Calls)
            {
                // Points the plan left out of every call stay as recorded
                for (; next < call.Start; next++)
                {
                    sk.AddRecorded(points, next, PieceKinds.Gap);
                    joint = PieceKinds.Gap;
                }

                joint ??= PieceKinds.Observed;
                if (call.Start > segment.Start && joint != PieceKinds.Gap)
                {
                    // A split (200 km / 2,000 points) joint: not a gap, not counted (AD-32 rule 2)
                    var a = points[call.Start - 1];
                    var b = points[call.Start];
                    joint = Geo.Haversine(a.Lat, a.Lon, b.Lat, b.Lon) > straightJump ? PieceKinds.Inferred : PieceKinds.Observed;
                }

                sk.Calls++;
                sk.Sent += call.Count;
                var answer = answers.GetValueOrDefault(call);
                var placed = answer != null ? Place(answer, call.Count) : null;
                if (placed == null || placed.Count == 0)
                {
                    for (var i = call.Start; i < call.End; i++)
                    {
                        sk.AddRecorded(points, i, i == call.Start ? joint : PieceKinds.Gap);
                    }
                }
                else
                {
                    sk.Anchors += placed.Count;
                    AddCall(sk, points, call, answer!, placed, joint, straightJump);
                }

                joint = null;
                next = call.End;
            }

            for (; next < segment.Start + segment.Count; next++)
            {
                sk.AddRecorded(points, next, next == segment.Start ? joint ?? PieceKinds.Gap : PieceKinds.Gap);
            }
        }

        return sk;
    }

    /// <summary>
    /// The stretches a route may fill: between two consecutive anchors (or the first or
    /// last recorded point) wherever the skeleton has a gap: a cut gap, an engine break,
    /// a leg skipping an unmatched point across a break, or recorded points no call
    /// matched. A drive with no anchor at all is routed point to point (at most
    /// MaxRoutedOnlyLegs legs). Each carries the matched shape's heading into its first
    /// anchor and out of its last one, where the shape has one.
    /// </summary>
    public static List<GapStretch> Stretches(RouteSkeleton sk, GapFillSettings g)
    {
        var result = new List<GapStretch>();
        if (!g.Enabled || sk.Nodes.Count < 2)
        {
            return result;
        }

        var routedOnly = sk.Anchors == 0;
        if (routedOnly && sk.Nodes.Count - 1 > g.MaxRoutedOnlyLegs)
        {
            return result;
        }

        var ends = Enumerable.Range(0, sk.Nodes.Count)
            .Where(i => routedOnly || sk.Nodes[i].Anchor || i == 0 || i == sk.Nodes.Count - 1).ToList();
        for (var j = 1; j < ends.Count; j++)
        {
            var a = sk.Nodes[ends[j - 1]];
            var b = sk.Nodes[ends[j]];
            if (b.Vertex <= a.Vertex)
            {
                continue;
            }

            var gap = false;
            double lineM = 0;
            for (var i = a.Vertex + 1; i <= b.Vertex; i++)
            {
                gap |= sk.Kind[i] == PieceKinds.Gap;
                lineM += Geo.Haversine(sk.V[i - 1].Lat, sk.V[i - 1].Lon, sk.V[i].Lat, sk.V[i].Lon);
            }

            if (!gap)
            {
                continue;
            }

            var breaks = 0;
            for (var n = ends[j - 1] + 1; n <= ends[j]; n++)
            {
                breaks += sk.Nodes[n].BreakBefore ? 1 : 0;
            }

            var from = sk.V[a.Vertex];
            var to = sk.V[b.Vertex];
            var trackM = sk.TrackCum.Length > b.Point ? sk.TrackCum[b.Point] - sk.TrackCum[a.Point] : 0;
            result.Add(new GapStretch(a.Vertex, b.Vertex, from, to, Math.Max(0, (b.T - a.T).TotalSeconds),
                Geo.Haversine(from.Lat, from.Lon, to.Lat, to.Lon), lineM, breaks, routedOnly, Math.Max(0, trackM),
                a.Anchor ? Heading(sk, a.Vertex, -1) : null, b.Anchor ? Heading(sk, b.Vertex, +1) : null));
        }

        return result;
    }

    /// <summary>How far along the matched shape a heading is measured</summary>
    private const double HeadingM = 30;

    /// <summary>
    /// The direction of travel at a vertex along the matched (non-gap) shape: arriving at
    /// it (<paramref name="dir"/> −1) or leaving it (+1), over up to 30 m; null when the
    /// shape there is a gap or shorter than 10 m
    /// </summary>
    private static double? Heading(RouteSkeleton sk, int vertex, int dir)
    {
        double m = 0;
        var j = vertex;
        while (m < HeadingM)
        {
            // The segment between j and j + dir; its kind is the kind of its later vertex
            var k = j + dir;
            if (k < 0 || k >= sk.V.Count || sk.Kind[Math.Max(j, k)] == PieceKinds.Gap)
            {
                break;
            }

            m += Geo.Haversine(sk.V[j].Lat, sk.V[j].Lon, sk.V[k].Lat, sk.V[k].Lon);
            j = k;
        }

        if (m < 10)
        {
            return null;
        }

        return dir < 0 ? GapFiller.Bearing(sk.V[j], sk.V[vertex]) : GapFiller.Bearing(sk.V[vertex], sk.V[j]);
    }

    /// <summary>
    /// Builds the match from the skeleton and the fills' outcomes (null: none tried).
    /// <paramref name="toleranceM"/>: an anchor farther from its road is off-road.
    /// <paramref name="routedOnlyWeight"/>: the anchor quality of a drive built only from routes.
    /// </summary>
    public static MatchResult Assemble(IReadOnlyList<RoutePoint> points, RouteSkeleton sk, IReadOnlyList<FillOutcome>? fills,
        RouteProfile profile, string? costing, int maxVertices, double toleranceM, double routedOnlyWeight)
    {
        fills ??= Array.Empty<FillOutcome>();
        var accepted = fills.Where(f => f.Accepted).OrderBy(f => f.Stretch.From).ToList();

        // The line: the skeleton, with every accepted stretch replaced by its route
        var line = new LineBuilder();
        var bridged = 0;
        double filledM = 0;
        if (sk.V.Count > 0)
        {
            line.Add(sk.V[0], PieceKinds.Observed);
            var i = 0;
            var fi = 0;
            while (true)
            {
                if (fi < accepted.Count && accepted[fi].Stretch.From == i)
                {
                    var f = accepted[fi++];
                    var from = line.LengthM;
                    var shape = f.Route!.Shape;
                    var roads = f.Route.Roads;
                    for (var k = 0; k < shape.Count; k++)
                    {
                        line.Add(shape[k], k == 0 ? Connector(sk.V[i], shape[0], f.Stretch.RoutedOnly) : PieceKinds.Inferred,
                            k > 0 && roads != null && k < roads.Count ? roads[k] : null);
                    }

                    line.Add(sk.V[f.Stretch.To], Connector(shape[^1], sk.V[f.Stretch.To], f.Stretch.RoutedOnly));
                    filledM += line.LengthM - from;
                    bridged += f.Stretch.Breaks;
                    i = f.Stretch.To;
                    continue;
                }

                if (++i >= sk.V.Count)
                {
                    break;
                }

                line.Add(sk.V[i], sk.Kind[i], sk.Road[i]);
            }
        }

        var trackM = sk.TrackCum.Length > 0 ? sk.TrackCum[^1] : 0;

        // The factors (AD-35 rule 6, routes.v2)
        var routedOnly = sk.Anchors == 0 && accepted.Count > 0;
        var lengthM = line.LengthM;
        var offRoad = sk.AnchorFromTrace.Count(d => d > toleranceM);
        var matchedShare = sk.Sent > 0 ? (double)sk.Anchors / sk.Sent : 0;
        var proximity = sk.Anchors > 0 ? 1 - (double)offRoad / sk.Anchors : 0;
        // No cliff between zero anchors and one: accepted routes carry the routed-only
        // weight, in proportion to the stretches' line they replaced
        var stretchM = fills.Sum(f => f.Stretch.LineM);
        var acceptedShare = stretchM > 0 ? accepted.Sum(f => f.Stretch.LineM) / stretchM : accepted.Count > 0 ? 1 : 0;
        var anchorQuality = Math.Max(sk.Sent > 0 ? (double)(sk.Anchors - offRoad) / sk.Sent : 0, routedOnlyWeight * acceptedShare);
        // The detour ratio of the part not filled: a filled leg passed its own length test
        var restLineM = lengthM - filledM;
        var restTrackM = trackM - accepted.Sum(f => f.Stretch.TrackM);
        var ratio = restTrackM > 1 && restLineM > 1 ? restLineM / restTrackM : 1;
        var plausibility = ratio <= 1.5 ? 1 : ratio >= 2.5 ? 0 : 2.5 - ratio;
        var duration = points.Count > 1 ? (points[^1].T - points[0].T).TotalSeconds : 0;
        var tooFast = duration > 0 && profile.MaxSpeedKmh.HasValue && lengthM / duration * 3.6 > profile.MaxSpeedKmh.Value;
        if (tooFast)
        {
            plausibility = 0;
        }

        var continuity = Math.Max(0, 1 - 0.15 * (sk.Breaks - bridged));

        var (polyline, pieces, storedM, roadList) = line.Store(maxVertices);
        double Sum(string kind) => pieces.Where(p => p.Kind == kind).Sum(p => p.ToM - p.FromM);
        var observed = Sum(PieceKinds.Observed);
        var inferred = Sum(PieceKinds.Inferred);
        var gap = Sum(PieceKinds.Gap);
        var coverage = storedM > 0 ? Math.Clamp((observed + inferred) / storedM, 0, 1) : 0;
        var confidence = Math.Round(Math.Min(Math.Min(coverage, anchorQuality), Math.Min(plausibility, continuity)), 3);

        int Count(string result) => fills.Count(f => f.Result == result);
        var metrics = new MatchMetrics
        {
            Coverage = Math.Round(coverage, 3),
            AnchorQuality = Math.Round(anchorQuality, 3),
            Plausibility = Math.Round(plausibility, 3),
            Continuity = Math.Round(continuity, 3),
            MatchedShare = Math.Round(matchedShare, 3),
            Proximity = Math.Round(proximity, 3),
            AnchorToleranceM = toleranceM,
            PointsSent = sk.Sent,
            Anchors = sk.Anchors,
            OffRoad = offRoad,
            EngineBreaks = sk.Breaks,
            BridgedBreaks = bridged,
            Calls = sk.Calls,
            CutGaps = sk.CutGaps,
            TrackM = Math.Round(trackM, 1),
            LengthRatio = Math.Round(ratio, 3),
            ObservedM = Math.Round(observed, 1),
            InferredM = Math.Round(inferred, 1),
            GapM = Math.Round(gap, 1),
            ObservedShare = storedM > 0 ? Math.Round(observed / storedM, 3) : 0,
            InferredShare = storedM > 0 ? Math.Round(inferred / storedM, 3) : 0,
            Stretches = fills.Count,
            Filled = accepted.Count,
            FilledM = Math.Round(filledM, 1),
            FillRejectedTime = Count(GapFiller.RejectedTime),
            FillRejectedLength = Count(GapFiller.RejectedLength) + Count(GapFiller.TooLong),
            FillRejectedReversal = Count(GapFiller.RejectedReversal),
            FillRetried = fills.Count(f => f.Retried),
            FillNoRoute = Count(GapFiller.NoRoute),
            FillTimeouts = Count(GapFiller.Timeout),
            FillSkipped = Count(GapFiller.Skipped),
            FillDeferred = Count(GapFiller.Deferred),
            RoutedOnly = routedOnly
        };

        if (sk.Anchors == 0 && !routedOnly)
        {
            return new MatchResult(RouteStatuses.Failed, 0, null, new List<RoutePiece>(), 0, metrics,
                sk.Calls == 0 ? "no_call: no part of the track had 2 points between long gaps" : "no_match: the engine matched no point");
        }

        var status = RouteStatuses.Of(confidence);
        string? reason = null;
        if (status != RouteStatuses.Matched)
        {
            var why = Why(coverage, anchorQuality, plausibility, continuity, routedOnly, tooFast, ratio, lengthM, duration,
                sk, offRoad, bridged, fills, gap);
            reason = $"low confidence {confidence.ToString("0.###", CultureInfo.InvariantCulture)}: {why}";
        }

        return new MatchResult(status, confidence, polyline, pieces, Math.Round(storedM, 1), metrics, reason, roadList);
    }

    /// <summary>The weakest factor in words, for the Owner ("Fix this route")</summary>
    private static string Why(double coverage, double anchorQuality, double plausibility, double continuity, bool routedOnly,
        bool tooFast, double ratio, double lengthM, double duration, RouteSkeleton sk, int offRoad, int bridged,
        IReadOnlyList<FillOutcome> fills, double gapM)
    {
        var min = Math.Min(Math.Min(coverage, anchorQuality), Math.Min(plausibility, continuity));
        if (plausibility <= min)
        {
            return tooFast
                ? $"the road line would need {lengthM / duration * 3.6:0} km/h"
                : $"the road line is {ratio.ToString("0.#", CultureInfo.InvariantCulture)}× the recorded track";
        }

        if (coverage <= min)
        {
            var worst = fills.Where(f => !f.Accepted).OrderByDescending(f => f.Stretch.LineM).FirstOrDefault();
            if (worst == null)
            {
                return $"{Distance(gapM)} of the line has no road match";
            }

            var d = Distance(worst.Stretch.LineM);
            return worst.Result switch
            {
                GapFiller.RejectedTime => $"a {d} stretch with no road that fits the {Elapsed(worst.Stretch.ElapsedS)} elapsed",
                GapFiller.RejectedLength or GapFiller.RejectedReversal or GapFiller.NoRoute or GapFiller.TooLong => $"a {d} straight jump that no road explains",
                GapFiller.Skipped => $"a {d} stretch with no road match (too many stretches to route)",
                GapFiller.Deferred => $"a {d} stretch with no road match yet (routing it resumes shortly)",
                _ => $"a {d} stretch with no road match (the engine did not answer in time)"
            };
        }

        if (continuity <= min)
        {
            var n = sk.Breaks - bridged;
            return $"the engine broke the path {n} time{(n == 1 ? string.Empty : "s")}";
        }

        return routedOnly
            ? "no recorded point matched a road; the line is routed between them"
            : $"only {sk.Anchors - offRoad} of {sk.Sent} points lie near a road";
    }

    private static string Distance(double m) =>
        m >= 1000 ? $"{(m / 1000).ToString("0.#", CultureInfo.InvariantCulture)} km" : $"{m:0} m";

    private static string Elapsed(double s) => s < 90 ? $"{s:0} s" : $"{s / 60:0} min";

    /// <summary>An anchor: an input point placed on the call's shape</summary>
    private sealed record Anchor(int Index, TraceLabel Label, double PosM);

    /// <summary>Anchors of a call, in input order, positions along the call's own shape (monotonic)</summary>
    private static List<Anchor>? Place(TraceResponse answer, int count)
    {
        if (answer.Shape.Count < 1 || answer.Labels.Count != count)
        {
            return null;
        }

        var cum = Cumulative(answer.Shape);
        var anchors = new List<Anchor>();
        double last = 0;
        for (var i = 0; i < answer.Labels.Count; i++)
        {
            var l = answer.Labels[i];
            if (!l.IsAnchor)
            {
                continue;
            }

            var edge = answer.Edges[l.EdgeIndex!.Value];
            var b = Math.Clamp(edge.BeginShape, 0, answer.Shape.Count - 1);
            var e = Math.Clamp(edge.EndShape, b, answer.Shape.Count - 1);
            double pos;
            if (l.Lat.HasValue && l.Lon.HasValue && e > b)
            {
                // The snapped point projected on the edge's stretch of the shape: exact even
                // where Valhalla trims the shape of a partial first or last edge
                pos = Project(answer.Shape, cum, b, e, l.Lat.Value, l.Lon.Value);
            }
            else
            {
                pos = cum[b] + Math.Clamp(l.AlongEdge ?? 0, 0, 1) * (cum[e] - cum[b]);
            }

            pos = Math.Max(pos, last);
            last = pos;
            anchors.Add(new Anchor(i, l, pos));
        }

        return anchors;
    }

    /// <summary>Adds a call's shape with a vertex at every anchor, the kind of every segment, and the anchors</summary>
    private static void AddCall(RouteSkeleton sk, IReadOnlyList<RoutePoint> points, TraceCall call, TraceResponse answer,
        List<Anchor> anchors, string joint, double straightJump)
    {
        var kinds = new string[anchors.Count];
        var breaks = new bool[anchors.Count];
        for (var k = 1; k < anchors.Count; k++)
        {
            var a = anchors[k - 1];
            var b = anchors[k];
            var broke = a.Label.BeginDiscontinuity || b.Label.EndDiscontinuity || ShapeBreak(answer.Edges, a.Label.EdgeIndex!.Value, b.Label.EdgeIndex!.Value);
            if (b.Index > a.Index + 1 && (broke || Discontinuity(answer.Labels, a.Index + 1, b.Index - 1)))
            {
                // A leg skipping an unmatched point across a break: a gap (counted in matchedShare only)
                kinds[k] = PieceKinds.Gap;
            }
            else if (b.Index > a.Index + 1)
            {
                // A leg skipping an unmatched point on a continuous matched shape: the shape
                // is the road, and no route is asked for (counted in matchedShare only)
                var p = points[call.Start + a.Index];
                var q = points[call.Start + b.Index];
                kinds[k] = Geo.Haversine(p.Lat, p.Lon, q.Lat, q.Lon) > straightJump ? PieceKinds.Inferred : PieceKinds.Observed;
            }
            else if (a.Label.BeginDiscontinuity || b.Label.EndDiscontinuity || ShapeBreak(answer.Edges, a.Label.EdgeIndex!.Value, b.Label.EdgeIndex!.Value))
            {
                // The engine broke the path here: a gap, and a doubt about the match
                kinds[k] = PieceKinds.Gap;
                breaks[k] = true;
                sk.Breaks++;
            }
            else
            {
                var p = points[call.Start + a.Index];
                var q = points[call.Start + b.Index];
                kinds[k] = Geo.Haversine(p.Lat, p.Lon, q.Lat, q.Lon) > straightJump ? PieceKinds.Inferred : PieceKinds.Observed;
            }
        }

        // The shape before the first anchor and after the last belongs to the pieces beside it
        var firstKind = anchors.Count > 1 ? kinds[1] : PieceKinds.Observed;
        var lastKind = anchors.Count > 1 ? kinds[^1] : PieceKinds.Observed;
        var shape = answer.Shape;
        var cum = Cumulative(shape);
        var road = SegmentRoads(answer);
        sk.Add(shape[0], joint);
        var si = 0;
        double lastPos = 0;
        for (var k = 0; k < anchors.Count; k++)
        {
            var kind = k == 0 ? firstKind : kinds[k];
            var pos = anchors[k].PosM;
            while (si + 1 < shape.Count && cum[si + 1] < pos - 0.01)
            {
                sk.Add(shape[++si], kind, road[si]);
                lastPos = cum[si];
            }

            if (pos - lastPos > 0.01 && si + 1 < shape.Count)
            {
                if (cum[si + 1] <= pos + 0.01)
                {
                    sk.Add(shape[++si], kind, road[si]);
                    lastPos = cum[si];
                }
                else
                {
                    // A vertex at the anchor, on the shape's segment
                    var t = (pos - cum[si]) / (cum[si + 1] - cum[si]);
                    sk.Add((shape[si].Lat + t * (shape[si + 1].Lat - shape[si].Lat), shape[si].Lon + t * (shape[si + 1].Lon - shape[si].Lon)), kind,
                        road[si + 1]);
                    lastPos = pos;
                }
            }

            var point = call.Start + anchors[k].Index;
            sk.Nodes.Add(new SkeletonNode(sk.V.Count - 1, point, points[point].T, true, breaks[k]));
            sk.AnchorFromTrace.Add(anchors[k].Label.FromTrace ?? 0);
        }

        while (si + 1 < shape.Count)
        {
            sk.Add(shape[++si], lastKind, road[si]);
        }
    }

    /// <summary>The names of the edge each shape segment lies on: the segment into vertex j, for j in (begin, end] of its edge</summary>
    private static string[]?[] SegmentRoads(TraceResponse answer)
    {
        var road = new string[]?[answer.Shape.Count];
        foreach (var e in answer.Edges)
        {
            if (e.Names == null)
            {
                continue;
            }

            for (var j = Math.Max(e.BeginShape + 1, 1); j <= e.EndShape && j < road.Length; j++)
            {
                road[j] = e.Names;
            }
        }

        return road;
    }

    /// <summary>Whether any label in [from, to] marks a route discontinuity</summary>
    private static bool Discontinuity(List<TraceLabel> labels, int from, int to)
    {
        for (var i = Math.Max(0, from); i <= to && i < labels.Count; i++)
        {
            if (labels[i].BeginDiscontinuity || labels[i].EndDiscontinuity)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A routed-only connector longer than this (metres) is a gap</summary>
    public const double MaxConnectorM = 20;

    /// <summary>
    /// The kind of the straight connector between a recorded point and a route's end: a
    /// routed-only drive's point more than 20 m off the road is joined by a gap, not inferred
    /// </summary>
    private static string Connector((double Lat, double Lon) a, (double Lat, double Lon) b, bool routedOnly) =>
        routedOnly && Geo.Haversine(a.Lat, a.Lon, b.Lat, b.Lon) > MaxConnectorM ? PieceKinds.Gap : PieceKinds.Inferred;

    /// <summary>Whether the shape jumps between the edges of two anchors (a path Valhalla restarted)</summary>
    private static bool ShapeBreak(List<TraceEdge> edges, int from, int to)
    {
        for (var t = from; t < to && t + 1 < edges.Count; t++)
        {
            if (edges[t].EndShape != edges[t + 1].BeginShape)
            {
                return true;
            }
        }

        return false;
    }

    private static double[] Cumulative(IReadOnlyList<(double Lat, double Lon)> shape)
    {
        var cum = new double[shape.Count];
        for (var i = 1; i < shape.Count; i++)
        {
            cum[i] = cum[i - 1] + Geo.Haversine(shape[i - 1].Lat, shape[i - 1].Lon, shape[i].Lat, shape[i].Lon);
        }

        return cum;
    }

    private static double Project(IReadOnlyList<(double Lat, double Lon)> shape, double[] cum, int b, int e, double lat, double lon)
    {
        var best = double.MaxValue;
        var pos = cum[b];
        var kx = Math.Cos(lat * Math.PI / 180);
        for (var i = b; i < e; i++)
        {
            var (aLat, aLon) = shape[i];
            var (bLat, bLon) = shape[i + 1];
            double dx = (bLon - aLon) * kx, dy = bLat - aLat;
            double px = (lon - aLon) * kx, py = lat - aLat;
            var len2 = dx * dx + dy * dy;
            var t = len2 > 0 ? Math.Clamp((px * dx + py * dy) / len2, 0, 1) : 0;
            double ex = px - t * dx, ey = py - t * dy;
            var d = ex * ex + ey * ey;
            if (d < best)
            {
                best = d;
                pos = cum[i] + t * (cum[i + 1] - cum[i]);
            }
        }

        return pos;
    }

    /// <summary>The line being built, with pieces in metres along it and the metres on each road</summary>
    private sealed class LineBuilder
    {
        private readonly List<(double Lat, double Lon)> _points = new();
        private readonly List<double> _cum = new();
        private readonly List<RoutePiece> _pieces = new();
        private readonly Dictionary<string, (double M, double FromM)> _roads = new(StringComparer.Ordinal);

        public double LengthM => _cum.Count > 0 ? _cum[^1] : 0;

        /// <summary>
        /// Adds a vertex; the segment to it is a piece of <paramref name="kind"/>, on the
        /// roads <paramref name="road"/> names. A gap's straight leg is on no road.
        /// </summary>
        public void Add((double Lat, double Lon) p, string kind, string[]? road = null)
        {
            var from = LengthM;
            _cum.Add(_points.Count == 0 ? 0 : LengthM + Geo.Haversine(_points[^1].Lat, _points[^1].Lon, p.Lat, p.Lon));
            _points.Add(p);
            Piece(from, LengthM, kind);
            if (road != null && kind != PieceKinds.Gap && LengthM > from)
            {
                foreach (var name in road)
                {
                    _roads[name] = _roads.TryGetValue(name, out var r) ? (r.M + LengthM - from, r.FromM) : (LengthM - from, from);
                }
            }
        }

        private void Piece(double from, double to, string kind)
        {
            if (to - from <= 1e-6)
            {
                return;
            }

            if (_pieces.Count > 0 && _pieces[^1].Kind == kind && Math.Abs(_pieces[^1].ToM - from) < 1e-6)
            {
                _pieces[^1] = _pieces[^1] with { ToM = to };
                return;
            }

            _pieces.Add(new RoutePiece(from, to, kind));
        }

        /// <summary>
        /// Rounds to precision 5, simplifies to at most <paramref name="max"/> vertices, and
        /// carries each piece over to metres along the stored line
        /// </summary>
        public (string Polyline, List<RoutePiece> Pieces, double LengthM, List<RouteRoad> Roads) Store(int max)
        {
            if (_points.Count == 0)
            {
                return (string.Empty, new List<RoutePiece>(), 0, new List<RouteRoad>());
            }

            var rounded = _points.Select(p => (Math.Round(p.Lat, 5), Math.Round(p.Lon, 5))).ToList();
            var keep = Geo.SimplifyIndices(rounded, max);
            var stored = keep.Select(i => rounded[i]).ToList();
            var newCum = Cumulative(stored);
            var oldCum = keep.Select(i => _cum[i]).ToArray();
            double Map(double m)
            {
                var k = Array.BinarySearch(oldCum, m);
                if (k >= 0)
                {
                    return newCum[k];
                }

                k = ~k;
                if (k <= 0)
                {
                    return 0;
                }

                if (k >= oldCum.Length)
                {
                    return newCum[^1];
                }

                var span = oldCum[k] - oldCum[k - 1];
                return newCum[k - 1] + (span > 0 ? (m - oldCum[k - 1]) / span : 0) * (newCum[k] - newCum[k - 1]);
            }

            var pieces = new List<RoutePiece>();
            foreach (var p in _pieces)
            {
                var from = Math.Round(Map(p.FromM), 1);
                var to = Math.Round(Map(p.ToM), 1);
                if (to <= from)
                {
                    continue;
                }

                if (pieces.Count > 0 && pieces[^1].Kind == p.Kind && Math.Abs(pieces[^1].ToM - from) < 0.11)
                {
                    pieces[^1] = pieces[^1] with { ToM = to };
                }
                else
                {
                    pieces.Add(new RoutePiece(from, to, p.Kind));
                }
            }

            // Each road's metres as built (the true length on it); where it starts, on the stored line
            var roads = RouteRoads.Keep(_roads.Select(r => (r.Key, r.Value.M, Map(r.Value.FromM))));
            return (Geo.EncodePolyline(stored), pieces, newCum.Length > 0 ? newCum[^1] : 0, roads);
        }
    }
}
