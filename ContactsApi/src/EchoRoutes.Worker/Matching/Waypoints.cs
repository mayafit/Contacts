/**
 * @fileoverview The points the Owner marked on the map (AD-38 rule 4): each one placed
 * on the leg of the recorded track it lies nearest, in the order marked, and timed by
 * distance between that leg's two real points. Pure.
 * @module EchoRoutes.Worker.Matching
 */
using ContactsApi.Infrastructure.Routes;

namespace EchoRoutes.Worker.Matching;

/// <summary>Places marked points in a drive's timeline</summary>
public static class Waypoints
{
    /// <summary>Most points the Owner can mark on one drive</summary>
    public const int Max = 50;

    /// <summary>A point farther than this from the recorded track is refused (metres)</summary>
    public const double MaxFromTrackM = 5000;

    /// <summary>Why the marked points cannot be used; null when they can</summary>
    public static string? Check(IReadOnlyList<RoutePoint> points, IReadOnlyList<(double Lat, double Lon)> marks)
    {
        if (marks.Count == 0 || marks.Count > Max)
        {
            return $"Mark 1 to {Max} points.";
        }

        if (marks.Any(m => !double.IsFinite(m.Lat) || !double.IsFinite(m.Lon) || Math.Abs(m.Lat) > 90 || Math.Abs(m.Lon) > 180))
        {
            return "A marked point is not a position.";
        }

        if (points.Count < 2)
        {
            return "The drive has fewer than 2 recorded points.";
        }

        var track = points.Select(p => (p.Lat, p.Lon)).ToList();
        var cum = SimilarRoutes.Cumulative(track);
        return marks.Any(m => SimilarRoutes.Project(track, cum, m.Lat, m.Lon).Dist > MaxFromTrackM)
            ? $"A marked point is more than {MaxFromTrackM / 1000:0} km from the recorded track."
            : null;
    }

    /// <summary>
    /// The drive's points with the marked ones inserted as synthetic points: each on the
    /// leg it lies nearest (by its projection on the straight legs), in the order marked
    /// within a leg, timed linearly by the distance along real point → marks → real point
    /// </summary>
    public static List<RoutePoint> Place(IReadOnlyList<RoutePoint> points, IReadOnlyList<(double Lat, double Lon)> marks)
    {
        var track = points.Select(p => (p.Lat, p.Lon)).ToList();
        var cum = SimilarRoutes.Cumulative(track);
        // The leg of each mark: the one its projection falls on (the last leg for the end)
        var onLeg = new List<(double Lat, double Lon)>[Math.Max(1, points.Count - 1)];
        foreach (var m in marks)
        {
            var pos = SimilarRoutes.Project(track, cum, m.Lat, m.Lon).Pos;
            var leg = Math.Clamp(Array.BinarySearch(cum, pos) is var k && k >= 0 ? k : ~k - 1, 0, onLeg.Length - 1);
            (onLeg[leg] ??= new()).Add(m);
        }

        var result = new List<RoutePoint>(points.Count + marks.Count);
        for (var i = 0; i < points.Count; i++)
        {
            result.Add(points[i]);
            if (i >= onLeg.Length || onLeg[i] == null || i + 1 >= points.Count)
            {
                continue;
            }

            // The path real → marks (in the order marked) → next real, and its length
            var path = new List<(double Lat, double Lon)> { (points[i].Lat, points[i].Lon) };
            path.AddRange(onLeg[i]);
            path.Add((points[i + 1].Lat, points[i + 1].Lon));
            var pathCum = SimilarRoutes.Cumulative(path);
            var total = pathCum[^1];
            var elapsedS = Math.Max(0, (points[i + 1].T - points[i].T).TotalSeconds);
            for (var k = 1; k < path.Count - 1; k++)
            {
                var t = total > 0 ? pathCum[k] / total : (double)k / (path.Count - 1);
                result.Add(new RoutePoint(points[i].T.AddSeconds(elapsedS * t), Math.Round(path[k].Lat, 6), Math.Round(path[k].Lon, 6), true));
            }
        }

        return result;
    }
}
