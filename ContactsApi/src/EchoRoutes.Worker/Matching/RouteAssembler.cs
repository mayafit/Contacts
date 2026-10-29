/**
 * @fileoverview From Valhalla's answers to a stored Matched Route (AD-35 rules 6 and 7):
 * anchors placed on the shape, pieces {fromM, toM, kind}, the confidence and status,
 * and the line re-encoded at precision 5 with at most N vertices. Pure.
 * @module EchoRoutes.Worker.Matching
 */
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Routes;

namespace EchoRoutes.Worker.Matching;

/// <summary>How the confidence came out, stored as matched_routes.MetricsAfter</summary>
public record MatchMetrics(
    double MatchedShare, double Proximity, double Plausibility, double Continuity,
    int PointsSent, int Anchors, int OffRoad, int EngineBreaks, int Calls, int CutGaps,
    double TrackM, double LengthRatio, double ObservedM, double InferredM, double GapM,
    double ObservedShare, double InferredShare);

/// <summary>A record's match</summary>
/// <param name="Polyline">Google encoded, precision 5; null when nothing matched</param>
/// <param name="Pieces">Metres along the stored line</param>
/// <param name="Reason">Why it failed (null when it did not)</param>
public record MatchResult(string Status, double Confidence, string? Polyline, List<RoutePiece> Pieces, double LengthM,
    MatchMetrics Metrics, string? Reason);

/// <summary>Assembles the calls' answers into one line</summary>
public static class RouteAssembler
{
    /// <summary>A matched point farther than this from its road is off-road (AD-35 rule 6)</summary>
    public static double OffRoadM(string? costing) => costing is "bicycle" or "pedestrian" ? 30 : 50;

    /// <summary>
    /// Builds the match. <paramref name="answers"/> holds an answer per call; a call
    /// missing from it (or null) got no match from the engine, and its points stay as
    /// recorded (a gap).
    /// </summary>
    public static MatchResult Assemble(IReadOnlyList<RoutePoint> points, IReadOnlyList<TraceSegment> plan,
        IReadOnlyDictionary<TraceCall, TraceResponse?> answers, RouteProfile profile, string? costing, int maxVertices)
    {
        var line = new LineBuilder();
        var straightJump = profile.StraightJumpM ?? 500;
        var offRoadM = OffRoadM(costing);
        int sent = 0, anchors = 0, offRoad = 0, breaks = 0, calls = 0, cutGaps = 0;

        for (var si = 0; si < plan.Count; si++)
        {
            var segment = plan[si];
            if (si > 0)
            {
                cutGaps++;
            }

            // The kind of the next joint: a cut gap between segments, else the split rule
            var joint = si > 0 ? PieceKinds.Gap : null;
            var next = segment.Start;
            foreach (var call in segment.Calls)
            {
                // Points the plan left out of every call stay as recorded
                for (; next < call.Start; next++)
                {
                    line.Add(points[next], PieceKinds.Gap);
                    joint = PieceKinds.Gap;
                }

                joint ??= PieceKinds.Observed;
                if (call.Start > segment.Start && joint != PieceKinds.Gap)
                {
                    // A split (600 km / 2,000 points) joint: not a gap, not counted (AD-32 rule 2)
                    var a = points[call.Start - 1];
                    var b = points[call.Start];
                    joint = Geo.Haversine(a.Lat, a.Lon, b.Lat, b.Lon) > straightJump ? PieceKinds.Inferred : PieceKinds.Observed;
                }

                calls++;
                sent += call.Count;
                var answer = answers.GetValueOrDefault(call);
                var placed = answer != null ? Place(answer, call.Count) : null;
                if (placed == null || placed.Count == 0)
                {
                    for (var i = call.Start; i < call.End; i++)
                    {
                        line.Add(points[i], i == call.Start ? joint : PieceKinds.Gap);
                    }
                }
                else
                {
                    anchors += placed.Count;
                    offRoad += placed.Count(p => (p.Label.FromTrace ?? 0) > offRoadM);
                    breaks += AddCall(line, points, call, answer!, placed, joint, straightJump);
                }

                joint = null;
                next = call.End;
            }

            for (; next < segment.Start + segment.Count; next++)
            {
                line.Add(points[next], next == segment.Start ? joint ?? PieceKinds.Gap : PieceKinds.Gap);
            }
        }

        // The confidence (AD-35 rule 6)
        double trackM = 0;
        for (var i = 1; i < points.Count; i++)
        {
            trackM += Geo.Haversine(points[i - 1].Lat, points[i - 1].Lon, points[i].Lat, points[i].Lon);
        }

        var lengthM = line.LengthM;
        var matchedShare = sent > 0 ? (double)anchors / sent : 0;
        var proximity = anchors > 0 ? 1 - (double)offRoad / anchors : 0;
        var ratio = trackM > 0 ? lengthM / trackM : 1;
        var plausibility = ratio <= 1.5 ? 1 : ratio >= 2.5 ? 0 : 2.5 - ratio;
        var duration = points.Count > 1 ? (points[^1].T - points[0].T).TotalSeconds : 0;
        if (duration > 0 && profile.MaxSpeedKmh.HasValue && lengthM / duration * 3.6 > profile.MaxSpeedKmh.Value)
        {
            plausibility = 0;
        }

        var continuity = Math.Max(0, 1 - 0.15 * breaks);
        var confidence = Math.Round(matchedShare * proximity * plausibility * continuity, 3);

        var (polyline, pieces, storedM) = line.Store(maxVertices);
        double Sum(string kind) => pieces.Where(p => p.Kind == kind).Sum(p => p.ToM - p.FromM);
        var observed = Sum(PieceKinds.Observed);
        var inferred = Sum(PieceKinds.Inferred);
        var gap = Sum(PieceKinds.Gap);
        var metrics = new MatchMetrics(Math.Round(matchedShare, 3), Math.Round(proximity, 3), Math.Round(plausibility, 3), Math.Round(continuity, 3),
            sent, anchors, offRoad, breaks, calls, cutGaps, Math.Round(trackM, 1), Math.Round(ratio, 3),
            Math.Round(observed, 1), Math.Round(inferred, 1), Math.Round(gap, 1),
            storedM > 0 ? Math.Round(observed / storedM, 3) : 0, storedM > 0 ? Math.Round(inferred / storedM, 3) : 0);

        if (anchors == 0)
        {
            return new MatchResult(RouteStatuses.Failed, 0, null, new List<RoutePiece>(), 0, metrics,
                calls == 0 ? "no_call: no part of the track had 2 points between long gaps" : "no_match: the engine matched no point");
        }

        var status = RouteStatuses.Of(confidence);
        return new MatchResult(status, confidence, polyline, pieces, Math.Round(storedM, 1), metrics,
            status == RouteStatuses.Failed ? $"low confidence {confidence:0.###}" : null);
    }

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

    /// <summary>Adds a call's shape and its pieces; returns the engine's breaks in it</summary>
    private static int AddCall(LineBuilder line, IReadOnlyList<RoutePoint> points, TraceCall call, TraceResponse answer,
        List<Anchor> anchors, string joint, double straightJump)
    {
        var offset = line.Add(answer.Shape[0], joint);
        line.AddRaw(answer.Shape.Skip(1));
        var callM = line.LengthM - offset;
        var breaks = 0;
        var pieces = new List<RoutePiece>();
        for (var k = 1; k < anchors.Count; k++)
        {
            var a = anchors[k - 1];
            var b = anchors[k];
            string kind;
            if (b.Index > a.Index + 1)
            {
                // A leg touching an unmatched point keeps the original (counted in matchedShare only)
                kind = PieceKinds.Gap;
            }
            else if (a.Label.BeginDiscontinuity || b.Label.EndDiscontinuity || ShapeBreak(answer.Edges, a.Label.EdgeIndex!.Value, b.Label.EdgeIndex!.Value))
            {
                // The engine broke the path here: a gap, and a doubt about the match
                kind = PieceKinds.Gap;
                breaks++;
            }
            else
            {
                var p = points[call.Start + a.Index];
                var q = points[call.Start + b.Index];
                kind = Geo.Haversine(p.Lat, p.Lon, q.Lat, q.Lon) > straightJump ? PieceKinds.Inferred : PieceKinds.Observed;
            }

            pieces.Add(new RoutePiece(offset + a.PosM, offset + b.PosM, kind));
        }

        // The shape before the first anchor and after the last belongs to the pieces beside it
        var firstKind = pieces.Count > 0 ? pieces[0].Kind : PieceKinds.Observed;
        var lastKind = pieces.Count > 0 ? pieces[^1].Kind : PieceKinds.Observed;
        line.Piece(offset, offset + anchors[0].PosM, firstKind);
        foreach (var p in pieces)
        {
            line.Piece(p.FromM, p.ToM, p.Kind);
        }

        line.Piece(offset + anchors[^1].PosM, offset + callM, lastKind);
        return breaks;
    }

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

    /// <summary>The line being built, with pieces in metres along it</summary>
    private sealed class LineBuilder
    {
        private readonly List<(double Lat, double Lon)> _points = new();
        private readonly List<double> _cum = new();
        private readonly List<RoutePiece> _pieces = new();

        public double LengthM => _cum.Count > 0 ? _cum[^1] : 0;

        /// <summary>Adds a vertex; the segment to it is a piece of <paramref name="kind"/>. Returns its position.</summary>
        public double Add(RoutePoint p, string kind) => Add((p.Lat, p.Lon), kind);

        public double Add((double Lat, double Lon) p, string kind)
        {
            var from = LengthM;
            AddRaw(new[] { p });
            Piece(from, LengthM, kind);
            return LengthM;
        }

        public void AddRaw(IEnumerable<(double Lat, double Lon)> points)
        {
            foreach (var p in points)
            {
                _cum.Add(_points.Count == 0 ? 0 : LengthM + Geo.Haversine(_points[^1].Lat, _points[^1].Lon, p.Lat, p.Lon));
                _points.Add(p);
            }
        }

        public void Piece(double from, double to, string kind)
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
        public (string Polyline, List<RoutePiece> Pieces, double LengthM) Store(int max)
        {
            if (_points.Count == 0)
            {
                return (string.Empty, new List<RoutePiece>(), 0);
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

            return (Geo.EncodePolyline(stored), pieces, newCum.Length > 0 ? newCum[^1] : 0);
        }
    }
}
