/**
 * @fileoverview Rebuilding a drive from a similar one (AD-38 rule 3): which of the
 * Owner's matched drives that run along the drive's own points (either way round) to
 * borrow from, and the synthetic points borrowed from its line, timed by the drive's
 * real points. Pure.
 * @module EchoRoutes.Worker.Matching
 */
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Routes;

namespace EchoRoutes.Worker.Matching;

/// <summary>A matched drive whose line runs near this one's points (AD-38 rule 3)</summary>
/// <param name="Polyline">Its matched line, Google encoded at precision 5, in the direction it was driven</param>
/// <param name="Pieces">Its pieces (metres along its line); a gap piece is a straight leg, not a road, and is never borrowed</param>
/// <param name="EndsNear">Both its ends lie within <see cref="SimilarRoutes.EndpointM"/> of this drive's ends, either way round</param>
public record SimilarCandidate(Guid ActivityId, DateTime Start, string Polyline, IReadOnlyList<RoutePiece>? Pieces = null, bool EndsNear = false);

/// <summary>The basis line in the drive's direction, its cumulative metres, and its gap stretches (never borrowed)</summary>
public record BasisLine(List<(double Lat, double Lon)> Line, double[] Cum, List<(double From, double To)> Gaps)
{
    /// <summary>The position lies on a gap piece of the basis</summary>
    public bool InGap(double pos) => Gaps.Any(g => pos >= g.From && pos <= g.To);
}

/// <summary>The similar drive a rebuild borrows from, and how strong the resemblance is</summary>
/// <param name="Reversed">This drive went the basis's way backwards</param>
/// <param name="Fit">Share of the drive's (non-stray) real points within <see cref="SimilarRoutes.NearM"/> of its line</param>
/// <param name="Candidates">Drives scored</param>
/// <param name="Agreeing">Drives that fit</param>
/// <param name="Examples">The best few of the basis's way, best first</param>
/// <param name="SameWay">Drives among the best <see cref="SimilarRoutes.WayPool"/> that take the basis's way</param>
public record SimilarBasis(Guid ActivityId, DateTime Start, bool Reversed, double Fit, int Candidates, int Agreeing,
    List<SimilarExample> Examples, int SameWay = 0);

/// <summary>One of the similar drives, for the panel</summary>
public record SimilarExample(Guid ActivityId, DateTime Start, bool Reversed);

/// <summary>The choice of a similar drive, and the borrowing of points from it</summary>
public static class SimilarRoutes
{
    /// <summary>Ends this near count in a candidate's favour, and find rows without corridor cells yet (metres)</summary>
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

    /// <summary>Most candidates scored</summary>
    public const int MaxCandidates = 200;

    /// <summary>Bases tried, best first, until one gives a confident match</summary>
    public const int Tries = 3;

    /// <summary>A point reached and left faster than this × the type's maximum speed is stray</summary>
    public const double StraySpeedFactor = 1.5;

    /// <summary>Slack on each leg's time when judging stray points (Timeline times are rounded to the minute)</summary>
    public const double StraySlackS = 60;

    /// <summary>The best candidates grouped into ways</summary>
    public const int WayPool = 30;

    /// <summary>Spacing of the samples that compare two candidates' spans (metres)</summary>
    public const double WaySampleM = 250;

    /// <summary>Share of a candidate's span within <see cref="NearM"/> of a way's first line for it to take that way</summary>
    public const double SameWayShare = 0.9;

    /// <summary>A way's value over its best score, at the size of the largest way</summary>
    public const double WayBonus = 0.1;

    /// <summary>A candidate's fit and score: its line in the drive's direction, the clean points' positions on it (travel order), its span</summary>
    private sealed record Scored(SimilarCandidate Candidate, bool Reversed, List<(double Lat, double Lon)> Line, double[] Cum, double Fit,
        double SpanFrom, double SpanTo, double Score);

    /// <summary>
    /// Which real points are stray: reached from the point before and left to the point
    /// after (the first and last: their one leg) only faster than <see cref="StraySpeedFactor"/>
    /// × the type's maximum, with <see cref="StraySlackS"/> of slack on each leg. None
    /// without a maximum or with fewer than three points.
    /// </summary>
    public static bool[] Stray(IReadOnlyList<RoutePoint> points, double? maxSpeedKmh)
    {
        var stray = new bool[points.Count];
        if (maxSpeedKmh is not > 0 || points.Count < 3)
        {
            return stray;
        }

        var maxMs = maxSpeedKmh.Value * StraySpeedFactor / 3.6;
        bool Fast(int a, int b) =>
            Geo.Haversine(points[a].Lat, points[a].Lon, points[b].Lat, points[b].Lon)
            / (Math.Max(0, (points[b].T - points[a].T).TotalSeconds) + StraySlackS) > maxMs;
        for (var i = 0; i < points.Count; i++)
        {
            stray[i] = (i == 0 || Fast(i - 1, i)) && (i == points.Count - 1 || Fast(i, i + 1));
        }

        return stray;
    }

    /// <summary>The real points that are not stray, in order</summary>
    public static List<RoutePoint> Clean(IReadOnlyList<RoutePoint> points, double? maxSpeedKmh)
    {
        var real = points.Where(p => !p.Synthetic).ToList();
        var stray = Stray(real, maxSpeedKmh);
        return real.Where((_, i) => !stray[i]).ToList();
    }

    /// <summary>The kind of day: Friday, Saturday, or a working day (the Owner's week in Israel)</summary>
    public static int DayKind(DateTime t) => t.DayOfWeek switch { DayOfWeek.Friday => 1, DayOfWeek.Saturday => 2, _ => 0 };

    /// <summary>
    /// The bases to try, best first (at most <paramref name="take"/>), each with its line in
    /// the drive's direction; empty when no candidate fits (AD-38 rule 3). Each candidate is
    /// scored both ways round; the fit, pace, time of day, kind of day, detour and ends make
    /// its score; the best <see cref="WayPool"/> are grouped into ways, and the best of each
    /// way is tried first, the ways most drives take ahead in a close call.
    /// </summary>
    public static List<(SimilarBasis Basis, BasisLine Line)> Rank(IReadOnlyList<RoutePoint> points,
        IReadOnlyList<SimilarCandidate> candidates, int take = Tries, double? maxSpeedKmh = null)
    {
        var clean = Clean(points, maxSpeedKmh);
        if (clean.Count < 2 || candidates.Count == 0)
        {
            return new();
        }

        // The drive's own path (its straight legs), for the detour
        var path = new double[clean.Count];
        for (var i = 1; i < clean.Count; i++)
        {
            path[i] = path[i - 1] + Geo.Haversine(clean[i - 1].Lat, clean[i - 1].Lon, clean[i].Lat, clean[i].Lon);
        }

        var pool = candidates.Take(MaxCandidates).ToList();
        var kept = new List<Scored>();
        foreach (var c in pool)
        {
            var s = Score(c, clean, path, maxSpeedKmh);
            if (s != null)
            {
                kept.Add(s);
            }
        }

        if (kept.Count == 0)
        {
            return new();
        }

        var ways = Ways(kept.OrderByDescending(s => s.Score).ThenByDescending(s => s.Candidate.Start).Take(WayPool).ToList());
        var largest = ways.Max(w => w.Count);
        ways = ways.OrderByDescending(w => w[0].Score + WayBonus * w.Count / largest).ThenByDescending(w => w[0].Candidate.Start).ToList();
        var order = ways.Select(w => (Best: w[0], Way: w)).Concat(ways[0].Skip(1).Select(s => (Best: s, Way: ways[0]))).ToList();
        var scored = pool.Select(c => c.ActivityId).Distinct().Count();
        var agreeing = kept.Select(s => s.Candidate.ActivityId).Distinct().Count();
        return order.Take(Math.Max(1, take))
            .Select(o => (new SimilarBasis(o.Best.Candidate.ActivityId, o.Best.Candidate.Start, o.Best.Reversed, Math.Round(o.Best.Fit, 3),
                    scored, agreeing,
                    o.Way.Take(3).Select(s => new SimilarExample(s.Candidate.ActivityId, s.Candidate.Start, s.Reversed)).ToList(), o.Way.Count),
                new BasisLine(o.Best.Line, o.Best.Cum, Gaps(o.Best.Candidate, o.Best.Reversed, o.Best.Cum[^1]))))
            .ToList();
    }

    /// <summary>A candidate's better direction, its span and score; null when it does not fit or is too fast</summary>
    private static Scored? Score(SimilarCandidate c, List<RoutePoint> clean, double[] path, double? maxSpeedKmh)
    {
        var line = ValhallaTrace.DecodePolyline(c.Polyline, 5);
        if (line.Count < 2)
        {
            return null;
        }

        (bool Reversed, List<(double Lat, double Lon)> Line, double[] Cum, (double Pos, double Dist)[] At, double Fit)? best = null;
        foreach (var reversed in new[] { false, true })
        {
            var l = reversed ? Enumerable.Reverse(line).ToList() : line;
            var cum = Cumulative(l);
            var at = ProjectAlong(l, cum, clean);
            var fit = at.Count(a => a.Dist <= NearM) / (double)at.Length;
            if (fit >= MinFit && (best == null || fit > best.Value.Fit))
            {
                best = (reversed, l, cum, at, fit);
            }
        }

        if (best == null)
        {
            return null;
        }

        var b = best.Value;
        var along = Enumerable.Range(0, b.At.Length).Where(k => b.At[k].Dist <= NearM).ToList();
        var (first, last) = (along[0], along[^1]);
        var (from, to) = (b.At[first].Pos, Math.Max(b.At[first].Pos, b.At[last].Pos));
        var spanM = to - from;
        var elapsedS = (clean[last].T - clean[first].T).TotalSeconds;
        // The drive could not have taken this way in its own time
        if (maxSpeedKmh is > 0 && spanM > 0 && spanM / (Math.Max(0, elapsedS) + StraySlackS) * 3.6 > maxSpeedKmh.Value)
        {
            return null;
        }

        var own = path[last] - path[first];
        var detour = own > 500 ? spanM / own : 1;
        var score = b.Fit
                    - 0.15 * Math.Min(HourDiff(clean[0].T, c.Start), 3) / 3
                    - 0.05 * (DayKind(clean[0].T) != DayKind(c.Start) ? 1 : 0)
                    - 0.15 * Math.Clamp((detour - 1.3) / 0.5, 0, 1)
                    + 0.05 * (c.EndsNear ? 1 : 0);
        return new Scored(c, b.Reversed, b.Line, b.Cum, b.Fit, from, to, score);
    }

    /// <summary>
    /// The candidates (best first) grouped into ways: each one not yet in a way starts one,
    /// and each later one whose span lies at least <see cref="SameWayShare"/> along the
    /// first one's line joins it
    /// </summary>
    private static List<List<Scored>> Ways(List<Scored> best)
    {
        var ways = new List<List<Scored>>();
        var taken = new bool[best.Count];
        for (var i = 0; i < best.Count; i++)
        {
            if (taken[i])
            {
                continue;
            }

            var way = new List<Scored> { best[i] };
            taken[i] = true;
            for (var j = i + 1; j < best.Count; j++)
            {
                if (!taken[j] && SameWay(best[i], best[j]))
                {
                    way.Add(best[j]);
                    taken[j] = true;
                }
            }

            ways.Add(way);
        }

        return ways;
    }

    /// <summary>The other's span, sampled every <see cref="WaySampleM"/>, lies along the first one's line</summary>
    private static bool SameWay(Scored first, Scored other)
    {
        var n = Math.Max(1, (int)Math.Ceiling((other.SpanTo - other.SpanFrom) / WaySampleM));
        var near = 0;
        for (var k = 0; k <= n; k++)
        {
            var (lat, lon) = PointAt(other.Line, other.Cum, other.SpanFrom + (other.SpanTo - other.SpanFrom) * k / n);
            if (Project(first.Line, first.Cum, lat, lon).Dist <= NearM)
            {
                near++;
            }
        }

        return near >= SameWayShare * (n + 1);
    }

    /// <summary>A candidate's gap pieces in the drive's direction (mirrored for a reversed one)</summary>
    private static List<(double From, double To)> Gaps(SimilarCandidate c, bool reversed, double lengthM) =>
        (c.Pieces ?? Array.Empty<RoutePiece>()).Where(p => p.Kind == PieceKinds.Gap)
            .Select(p => reversed ? (lengthM - p.ToM, lengthM - p.FromM) : (p.FromM, p.ToM))
            .ToList();

    /// <summary>
    /// The drive's points with synthetic points borrowed from the basis line: on each leg
    /// between two consecutive non-stray real points that both lie along it (projected in
    /// travel order, as the fit is), every <paramref name="stepM"/> between their positions,
    /// timed linearly by distance, never on the basis's own gap pieces. A leg shorter than
    /// <see cref="MinLegM"/> along the line, or faster than <paramref name="maxSpeedKmh"/>,
    /// borrows nothing; nothing is borrowed before the first point or after the last
    /// (a partial overlap takes only its part). Every input point is kept, in time order.
    /// </summary>
    public static List<RoutePoint> Borrow(IReadOnlyList<RoutePoint> points, BasisLine basis, double? maxSpeedKmh, double stepM)
    {
        var line = basis.Line;
        var cum = basis.Cum;
        var clean = Clean(points, maxSpeedKmh);
        var at = ProjectAlong(line, cum, clean);
        var borrowed = new List<RoutePoint>();
        for (var i = 0; i + 1 < clean.Count; i++)
        {
            if (at[i].Dist > NearM || at[i + 1].Dist > NearM)
            {
                continue;
            }

            var from = at[i].Pos;
            var to = at[i + 1].Pos;
            var alongM = to - from;
            var elapsedS = (clean[i + 1].T - clean[i].T).TotalSeconds;
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
                borrowed.Add(new RoutePoint(clean[i].T.AddSeconds(elapsedS * (s - from) / alongM), Math.Round(lat, 6), Math.Round(lon, 6), true));
            }
        }

        // Stable: a borrowed point never goes before a real point of the same time
        return points.Concat(borrowed).OrderBy(p => p.T).ToList();
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
