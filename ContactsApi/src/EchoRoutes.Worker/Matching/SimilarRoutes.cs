/**
 * @fileoverview Rebuilding a drive from a similar one (AD-38 rule 3): which of the
 * Owner's matched drives between the same places (or reversed) fits the drive's own
 * points, and the synthetic points borrowed from its line, timed by the drive's real
 * points. Pure.
 * @module EchoRoutes.Worker.Matching
 */
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Routes;

namespace EchoRoutes.Worker.Matching;

/// <summary>A matched drive that starts and ends near this one's ends (or reversed)</summary>
/// <param name="Polyline">Its matched line, Google encoded at precision 5</param>
/// <param name="Reversed">It starts near this drive's end and ends near its start</param>
/// <param name="Pieces">Its pieces (metres along its line); a gap piece is a straight leg, not a road, and is never borrowed</param>
public record SimilarCandidate(Guid ActivityId, DateTime Start, string Polyline, bool Reversed, IReadOnlyList<RoutePiece>? Pieces = null);

/// <summary>The basis line in the drive's direction, its cumulative metres, and its gap stretches (never borrowed)</summary>
public record BasisLine(List<(double Lat, double Lon)> Line, double[] Cum, List<(double From, double To)> Gaps)
{
    /// <summary>The position lies on a gap piece of the basis</summary>
    public bool InGap(double pos) => Gaps.Any(g => pos >= g.From && pos <= g.To);
}

/// <summary>The similar drive a rebuild borrows from, and how strong the resemblance is</summary>
/// <param name="Fit">Share of the drive's real points within <see cref="SimilarRoutes.NearM"/> of its line</param>
/// <param name="Candidates">Candidates scored</param>
/// <param name="Agreeing">Candidates that fit</param>
/// <param name="Examples">The best few, best first</param>
public record SimilarBasis(Guid ActivityId, DateTime Start, bool Reversed, double Fit, int Candidates, int Agreeing,
    List<SimilarExample> Examples);

/// <summary>One of the similar drives, for the panel</summary>
public record SimilarExample(Guid ActivityId, DateTime Start, bool Reversed);

/// <summary>The choice of a similar drive, and the borrowing of points from it</summary>
public static class SimilarRoutes
{
    /// <summary>Both ends of a candidate lie within this of the drive's ends (metres)</summary>
    public const double EndpointM = 500;

    /// <summary>A real point within this of a line lies along it (twice the Timeline gps_accuracy)</summary>
    public const double NearM = 150;

    /// <summary>The least fit a candidate needs</summary>
    public const double MinFit = 0.6;

    /// <summary>Positions along a candidate may go back this far (GPS noise), not more</summary>
    public const double BackM = 150;

    /// <summary>
    /// Spacings tried for the borrowed points along the basis line (metres): dense first;
    /// sparser leaves the engine room where the basis runs on the other carriageway (a
    /// reversed drive) or takes another ramp. The rebuild keeps the more confident one.
    /// </summary>
    public static readonly double[] Steps = { 200, 600 };

    /// <summary>A leg shorter than this along the basis line borrows nothing (metres)</summary>
    public const double MinLegM = 300;

    /// <summary>Most candidates scored (the nearest ends)</summary>
    public const int MaxCandidates = 200;

    /// <summary>A candidate's fit: its line (in the drive's direction), each real point's position on it (in travel order), and the fit</summary>
    private sealed record Scored(SimilarCandidate Candidate, List<(double Lat, double Lon)> Line, double[] Cum, (double Pos, double Dist)[] At,
        double Fit, double HourDiff);

    /// <summary>Bases tried, best first, until one gives a confident match</summary>
    public const int Tries = 3;

    /// <summary>
    /// The candidates that fit the drive's real points, best first (at most
    /// <paramref name="take"/>), each with its line in the drive's direction; empty when
    /// none fits. Every one carries the same counts and examples.
    /// </summary>
    public static List<(SimilarBasis Basis, BasisLine Line)> Rank(IReadOnlyList<RoutePoint> points,
        IReadOnlyList<SimilarCandidate> candidates, int take = Tries)
    {
        var real = points.Where(p => !p.Synthetic).ToList();
        if (real.Count < 2 || candidates.Count == 0)
        {
            return new();
        }

        var kept = new List<Scored>();
        foreach (var c in candidates.Take(MaxCandidates))
        {
            var line = ValhallaTrace.DecodePolyline(c.Polyline, 5);
            if (line.Count < 2)
            {
                continue;
            }

            if (c.Reversed)
            {
                line.Reverse();
            }

            var cum = Cumulative(line);
            var at = ProjectAlong(line, cum, real);
            var fit = at.Count(a => a.Dist <= NearM) / (double)at.Length;
            if (fit < MinFit)
            {
                continue;
            }

            kept.Add(new Scored(c, line, cum, at, fit, HourDiff(real[0].T, c.Start)));
            if (kept.Count >= MaxCandidates)
            {
                break;
            }
        }

        var ranked = kept.OrderByDescending(s => Math.Round(s.Fit, 2)).ThenBy(s => s.HourDiff).ThenByDescending(s => s.Candidate.Start).ToList();
        // Counted per drive: a round trip is a candidate in both directions
        var examples = ranked.DistinctBy(s => s.Candidate.ActivityId).Take(3)
            .Select(s => new SimilarExample(s.Candidate.ActivityId, s.Candidate.Start, s.Candidate.Reversed)).ToList();
        var scored = candidates.Take(MaxCandidates).Select(c => c.ActivityId).Distinct().Count();
        var agreeing = kept.Select(s => s.Candidate.ActivityId).Distinct().Count();
        return ranked.Take(Math.Max(1, take))
            .Select(s => (new SimilarBasis(s.Candidate.ActivityId, s.Candidate.Start, s.Candidate.Reversed, Math.Round(s.Fit, 3), scored, agreeing,
                examples), new BasisLine(s.Line, s.Cum, Gaps(s.Candidate, s.Cum[^1]))))
            .ToList();
    }

    /// <summary>A candidate's gap pieces in the drive's direction (mirrored for a reversed one)</summary>
    private static List<(double From, double To)> Gaps(SimilarCandidate c, double lengthM) =>
        (c.Pieces ?? Array.Empty<RoutePiece>()).Where(p => p.Kind == PieceKinds.Gap)
            .Select(p => c.Reversed ? (lengthM - p.ToM, lengthM - p.FromM) : (p.FromM, p.ToM))
            .ToList();

    /// <summary>
    /// The drive's points with synthetic points borrowed from the basis line: on each leg
    /// between two consecutive real points that both lie along it (projected in travel
    /// order, as the fit is), every <paramref name="stepM"/> between their positions,
    /// timed linearly by distance, never on the basis's own gap pieces. A leg shorter
    /// than <see cref="MinLegM"/> along the line, or faster than <paramref name="maxSpeedKmh"/>,
    /// borrows nothing.
    /// </summary>
    public static List<RoutePoint> Borrow(IReadOnlyList<RoutePoint> points, BasisLine basis, double? maxSpeedKmh, double stepM)
    {
        var line = basis.Line;
        var cum = basis.Cum;
        var at = ProjectAlong(line, cum, points);
        var result = new List<RoutePoint>(points.Count * 4);
        for (var i = 0; i < points.Count; i++)
        {
            result.Add(points[i]);
            if (i + 1 >= points.Count || points[i].Synthetic || points[i + 1].Synthetic
                || at[i].Dist > NearM || at[i + 1].Dist > NearM)
            {
                continue;
            }

            var from = at[i].Pos;
            var to = at[i + 1].Pos;
            var alongM = to - from;
            var elapsedS = (points[i + 1].T - points[i].T).TotalSeconds;
            if (alongM < MinLegM || elapsedS <= 0 || (maxSpeedKmh is > 0 && alongM / elapsedS * 3.6 > maxSpeedKmh.Value))
            {
                continue;
            }

            for (var s = from + stepM; s < to - stepM / 2; s += stepM)
            {
                if (basis.InGap(s))
                {
                    continue;
                }

                var (lat, lon) = PointAt(line, cum, s);
                result.Add(new RoutePoint(points[i].T.AddSeconds(elapsedS * (s - from) / alongM), Math.Round(lat, 6), Math.Round(lon, 6), true));
            }
        }

        return result;
    }

    /// <summary>The difference between two times of day, in hours (0 to 12)</summary>
    public static double HourDiff(DateTime a, DateTime b)
    {
        var d = Math.Abs(a.TimeOfDay.TotalHours - b.TimeOfDay.TotalHours) % 24;
        return Math.Min(d, 24 - d);
    }

    /// <summary>
    /// The points' positions along a line, in travel order. Each point takes the first
    /// stretch of the line, from the furthest position so far less <see cref="BackM"/>,
    /// that passes within <see cref="NearM"/> of it, and the nearest spot of that stretch.
    /// So a line that passes a place twice (a round trip, a loop) is read in order, and a
    /// drive the other way round does not fit. A point no such stretch passes keeps its
    /// nearest distance (over <see cref="NearM"/>: not along the line) and moves nothing on.
    /// </summary>
    internal static (double Pos, double Dist)[] ProjectAlong(IReadOnlyList<(double Lat, double Lon)> line, double[] cum,
        IReadOnlyList<RoutePoint> points)
    {
        var at = new (double Pos, double Dist)[points.Count];
        var furthest = 0.0;
        for (var k = 0; k < points.Count; k++)
        {
            var floor = furthest - BackM;
            var kx = Math.Cos(points[k].Lat * Math.PI / 180) * 111_320;
            const double ky = 110_540;
            var found = false;
            var best = (Pos: 0.0, Dist: double.MaxValue);
            var nearest = (Pos: 0.0, Dist: double.MaxValue);
            for (var i = 1; i < line.Count; i++)
            {
                if (cum[i] < floor)
                {
                    continue;
                }

                var here = Segment(line, cum, i, points[k].Lat, points[k].Lon, kx, ky);
                if (here.Dist < nearest.Dist)
                {
                    nearest = here;
                }

                if (here.Dist <= NearM)
                {
                    found = true;
                    if (here.Dist < best.Dist)
                    {
                        best = here;
                    }
                }
                else if (found)
                {
                    // The first stretch within reach has ended
                    break;
                }
            }

            if (found)
            {
                at[k] = (Math.Max(best.Pos, floor), best.Dist);
                furthest = Math.Max(furthest, best.Pos);
            }
            else
            {
                at[k] = (nearest.Pos, nearest.Dist == double.MaxValue ? double.MaxValue : Math.Max(nearest.Dist, NearM + 1));
            }
        }

        return at;
    }

    /// <summary>A point's position on segment i-1 → i of a line and its distance from it (equirectangular)</summary>
    private static (double Pos, double Dist) Segment(IReadOnlyList<(double Lat, double Lon)> line, double[] cum, int i, double lat, double lon,
        double kx, double ky)
    {
        double ax = (line[i - 1].Lon - lon) * kx, ay = (line[i - 1].Lat - lat) * ky;
        double bx = (line[i].Lon - lon) * kx, by = (line[i].Lat - lat) * ky;
        double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        var t = len2 > 0 ? Math.Clamp(-(ax * dx + ay * dy) / len2, 0, 1) : 0;
        double ex = ax + t * dx, ey = ay + t * dy;
        return (cum[i - 1] + t * (cum[i] - cum[i - 1]), Math.Sqrt(ex * ex + ey * ey));
    }

    internal static double[] Cumulative(IReadOnlyList<(double Lat, double Lon)> line)
    {
        var cum = new double[line.Count];
        for (var i = 1; i < line.Count; i++)
        {
            cum[i] = cum[i - 1] + Geo.Haversine(line[i - 1].Lat, line[i - 1].Lon, line[i].Lat, line[i].Lon);
        }

        return cum;
    }

    /// <summary>A point's position along a line (metres) and its distance from it (metres), equirectangular</summary>
    internal static (double Pos, double Dist) Project(IReadOnlyList<(double Lat, double Lon)> line, double[] cum, double lat, double lon)
    {
        var kx = Math.Cos(lat * Math.PI / 180) * 111_320;
        const double ky = 110_540;
        var best = double.MaxValue;
        var pos = 0.0;
        for (var i = 1; i < line.Count; i++)
        {
            double ax = (line[i - 1].Lon - lon) * kx, ay = (line[i - 1].Lat - lat) * ky;
            double bx = (line[i].Lon - lon) * kx, by = (line[i].Lat - lat) * ky;
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
            var t = len2 > 0 ? Math.Clamp(-(ax * dx + ay * dy) / len2, 0, 1) : 0;
            double ex = ax + t * dx, ey = ay + t * dy;
            var d = ex * ex + ey * ey;
            if (d < best)
            {
                best = d;
                pos = cum[i - 1] + t * (cum[i] - cum[i - 1]);
            }
        }

        return (pos, Math.Sqrt(best));
    }

    /// <summary>The point at <paramref name="s"/> metres along a line</summary>
    internal static (double Lat, double Lon) PointAt(IReadOnlyList<(double Lat, double Lon)> line, double[] cum, double s)
    {
        var i = Array.BinarySearch(cum, s);
        if (i >= 0)
        {
            return line[i];
        }

        i = ~i;
        if (i <= 0)
        {
            return line[0];
        }

        if (i >= line.Count)
        {
            return line[^1];
        }

        var span = cum[i] - cum[i - 1];
        var t = span > 0 ? (s - cum[i - 1]) / span : 0;
        return (line[i - 1].Lat + t * (line[i].Lat - line[i - 1].Lat), line[i - 1].Lon + t * (line[i].Lon - line[i - 1].Lon));
    }
}
