/**
 * @fileoverview Map coverage: the region outlines (.poly, the Osmosis polygon format
 * Geofabrik publishes) that the routing graph was built from
 * @module ContactsApi.Infrastructure.Routes
 */
using System.Globalization;

namespace ContactsApi.Infrastructure.Routes;

/// <summary>
/// The routing graph's regions. A track is covered when one of its points lies
/// inside an outline; one outside every outline is outside_coverage (AD-32 rule 6).
/// </summary>
public class RegionCoverage
{
    private readonly List<Region> _regions;

    /// <summary>Creates coverage from parsed regions</summary>
    public RegionCoverage(IEnumerable<Region> regions)
    {
        _regions = regions.ToList();
    }

    /// <summary>Region names (the .poly files' first lines)</summary>
    public IReadOnlyList<string> Names => _regions.Select(r => r.Name).ToList();

    /// <summary>Whether there is any outline at all</summary>
    public bool Any => _regions.Count > 0;

    /// <summary>One region: outer rings and holes, as (lon, lat) vertices</summary>
    public record Region(string Name, List<(double Lon, double Lat)[]> Outer, List<(double Lon, double Lat)[]> Holes)
    {
        internal (double MinLon, double MinLat, double MaxLon, double MaxLat) Box { get; } = BoxOf(Outer);
    }

    /// <summary>Loads every .poly of a directory, or one .poly file</summary>
    public static RegionCoverage Load(string path)
    {
        var files = Directory.Exists(path)
            ? Directory.GetFiles(path, "*.poly").OrderBy(f => f, StringComparer.Ordinal).ToArray()
            : File.Exists(path) ? new[] { path } : Array.Empty<string>();
        return new RegionCoverage(files.Select(f => Parse(File.ReadAllText(f))));
    }

    /// <summary>Parses an Osmosis .poly: a name, then sections of "lon lat" lines, each ended by END; "!" marks a hole</summary>
    public static Region Parse(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0)
        {
            throw new FormatException("Empty .poly file.");
        }

        var outer = new List<(double, double)[]>();
        var holes = new List<(double, double)[]>();
        var i = 1;
        while (i < lines.Count && !lines[i].Equals("END", StringComparison.OrdinalIgnoreCase))
        {
            var hole = lines[i].StartsWith('!');
            i++;
            var ring = new List<(double, double)>();
            while (i < lines.Count && !lines[i].Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                var parts = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                ring.Add((double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture)));
                i++;
            }

            i++;
            if (ring.Count >= 3)
            {
                (hole ? holes : outer).Add(ring.ToArray());
            }
        }

        return new Region(lines[0], outer, holes);
    }

    /// <summary>Whether a point lies inside a region</summary>
    public bool Contains(double lat, double lon) =>
        _regions.Any(r => InBox(r.Box, lon, lat) && r.Outer.Any(o => Inside(o, lon, lat)) && !r.Holes.Any(h => Inside(h, lon, lat)));

    /// <summary>Whether a track is covered: one of up to 64 evenly spaced points (both ends included) is inside</summary>
    public bool Covers(IReadOnlyList<RoutePoint> points)
    {
        if (points.Count == 0)
        {
            return false;
        }

        var samples = Math.Min(64, points.Count);
        for (var k = 0; k < samples; k++)
        {
            var p = points[samples == 1 ? 0 : (int)Math.Round(k * (points.Count - 1) / (double)(samples - 1))];
            if (Contains(p.Lat, p.Lon))
            {
                return true;
            }
        }

        return false;
    }

    private static bool InBox((double MinLon, double MinLat, double MaxLon, double MaxLat) b, double lon, double lat) =>
        lon >= b.MinLon && lon <= b.MaxLon && lat >= b.MinLat && lat <= b.MaxLat;

    /// <summary>Even-odd ray casting</summary>
    private static bool Inside((double Lon, double Lat)[] ring, double lon, double lat)
    {
        var inside = false;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            var (xi, yi) = ring[i];
            var (xj, yj) = ring[j];
            if ((yi > lat) != (yj > lat) && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static (double, double, double, double) BoxOf(List<(double Lon, double Lat)[]> rings)
    {
        var all = rings.SelectMany(r => r).ToList();
        return all.Count == 0
            ? (0, 0, -1, -1)
            : (all.Min(p => p.Lon), all.Min(p => p.Lat), all.Max(p => p.Lon), all.Max(p => p.Lat));
    }
}
