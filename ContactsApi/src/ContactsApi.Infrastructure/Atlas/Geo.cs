/**
 * @fileoverview Track geometry: distance, elevation gain, simplification, polylines
 * @module ContactsApi.Infrastructure.Atlas
 */
using System.Text;

namespace ContactsApi.Infrastructure.Atlas;

/// <summary>Geometry on tracks (WGS84, metres)</summary>
public static class Geo
{
    private const double EarthRadiusM = 6_371_008.8;

    /// <summary>Great-circle distance in metres</summary>
    public static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusM * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>Length of a track in metres</summary>
    public static double Length(IReadOnlyList<TrackPoint> points)
    {
        double total = 0;
        for (var i = 1; i < points.Count; i++)
        {
            total += Haversine(points[i - 1].Lat, points[i - 1].Lon, points[i].Lat, points[i].Lon);
        }

        return total;
    }

    /// <summary>
    /// Total climb in metres, ignoring changes smaller than <paramref name="threshold"/>
    /// (GPS elevation noise would otherwise add hundreds of metres).
    /// </summary>
    public static double? ElevationGain(IReadOnlyList<TrackPoint> points, double threshold = 3)
    {
        var elevations = points.Where(p => p.Elevation.HasValue).Select(p => p.Elevation!.Value).ToList();
        if (elevations.Count < 2)
        {
            return null;
        }

        double gain = 0;
        var reference = elevations[0];
        foreach (var e in elevations.Skip(1))
        {
            if (e - reference >= threshold)
            {
                gain += e - reference;
                reference = e;
            }
            else if (reference - e >= threshold)
            {
                reference = e;
            }
        }

        return Math.Round(gain, 1);
    }

    /// <summary>
    /// Douglas–Peucker simplification to at most <paramref name="max"/> points,
    /// tightening the tolerance until it fits.
    /// </summary>
    public static List<(double Lat, double Lon)> Simplify(IReadOnlyList<(double Lat, double Lon)> points, int max = 400)
    {
        if (points.Count <= max)
        {
            return points.ToList();
        }

        var tolerance = 0.00001; // ~1 m in degrees
        List<(double, double)> result;
        do
        {
            result = DouglasPeucker(points, tolerance);
            tolerance *= 2;
        }
        while (result.Count > max);

        return result;
    }

    private static List<(double Lat, double Lon)> DouglasPeucker(IReadOnlyList<(double Lat, double Lon)> pts, double tolerance)
    {
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            double maxDist = 0;
            var index = -1;
            for (var i = first + 1; i < last; i++)
            {
                var d = PerpendicularDistance(pts[i], pts[first], pts[last]);
                if (d > maxDist)
                {
                    maxDist = d;
                    index = i;
                }
            }

            if (index >= 0 && maxDist > tolerance)
            {
                keep[index] = true;
                stack.Push((first, index));
                stack.Push((index, last));
            }
        }

        return pts.Where((_, i) => keep[i]).ToList();
    }

    private static double PerpendicularDistance((double Lat, double Lon) p, (double Lat, double Lon) a, (double Lat, double Lon) b)
    {
        var dx = b.Lon - a.Lon;
        var dy = b.Lat - a.Lat;
        if (dx == 0 && dy == 0)
        {
            return Math.Sqrt(Math.Pow(p.Lon - a.Lon, 2) + Math.Pow(p.Lat - a.Lat, 2));
        }

        return Math.Abs(dy * p.Lon - dx * p.Lat + b.Lon * a.Lat - b.Lat * a.Lon) / Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Google encoded polyline (precision 5)</summary>
    public static string EncodePolyline(IEnumerable<(double Lat, double Lon)> points)
    {
        var sb = new StringBuilder();
        long prevLat = 0, prevLon = 0;
        foreach (var (lat, lon) in points)
        {
            var la = (long)Math.Round(lat * 1e5);
            var lo = (long)Math.Round(lon * 1e5);
            Encode(la - prevLat, sb);
            Encode(lo - prevLon, sb);
            prevLat = la;
            prevLon = lo;
        }

        return sb.ToString();
    }

    private static void Encode(long value, StringBuilder sb)
    {
        var v = value < 0 ? ~(value << 1) : value << 1;
        while (v >= 0x20)
        {
            sb.Append((char)((0x20 | (v & 0x1f)) + 63));
            v >>= 5;
        }

        sb.Append((char)(v + 63));
    }

    /// <summary>
    /// Fills an activity's derived fields from its points: times, distance,
    /// duration, climb, heart rate, bounding box and the simplified polyline.
    /// Values the source supplied are kept.
    /// </summary>
    public static void Summarize(AtlasActivity a)
    {
        var pts = a.Points.OrderBy(p => p.Time).ToList();
        a.PointCount = pts.Count;
        if (pts.Count == 0)
        {
            return;
        }

        // The track is the truth about when it happened: a source's own times can
        // leave out pauses (Sports Tracker gives start + moving time)
        if (a.StartTime == default || pts[0].Time < a.StartTime)
        {
            a.StartTime = pts[0].Time;
        }

        if (a.EndTime == default || pts[^1].Time > a.EndTime)
        {
            a.EndTime = pts[^1].Time;
        }

        a.DistanceM ??= Math.Round(Length(pts), 1);
        a.DurationS ??= Math.Max(0, (a.EndTime - a.StartTime).TotalSeconds);
        a.ElevationGainM ??= ElevationGain(pts);
        var hr = pts.Where(p => p.HeartRate is > 0).Select(p => p.HeartRate!.Value).ToList();
        if (hr.Count > 0)
        {
            a.AvgHeartRate ??= (int)Math.Round(hr.Average());
            a.MaxHeartRate ??= hr.Max();
        }

        a.MinLat = pts.Min(p => p.Lat);
        a.MaxLat = pts.Max(p => p.Lat);
        a.MinLon = pts.Min(p => p.Lon);
        a.MaxLon = pts.Max(p => p.Lon);
        a.Polyline = EncodePolyline(Simplify(pts.Select(p => (p.Lat, p.Lon)).ToList()));
    }
}
