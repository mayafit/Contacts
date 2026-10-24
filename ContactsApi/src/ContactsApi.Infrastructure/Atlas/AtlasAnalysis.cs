/**
 * @fileoverview Activity analysis in context: the user's max heart rate, best
 * efforts against all their others, and the same route and type compared
 * @module ContactsApi.Infrastructure.Atlas
 */
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ContactsApi.Infrastructure.Atlas;

/// <summary>
/// An activity's best efforts (fastest 1 km, 5 km…), kept so that every new one can be
/// ranked against all the others without reading their tracks. Filled when an activity
/// is opened and by the fusion service in the background; stale rows (another type or
/// track) are recomputed.
/// </summary>
public class ActivityEffort
{
    /// <summary>The activity (the primary of a fused one)</summary>
    public Guid ActivityId { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    /// <summary>The (fused) type the efforts were computed for</summary>
    public string Type { get; set; } = string.Empty;
    /// <summary>The track's point count when computed (a re-import changes it)</summary>
    public int PointCount { get; set; }
    /// <summary>JSON: [{ "d": metres, "t": seconds }]</summary>
    public string Efforts { get; set; } = "[]";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public partial class AtlasService
{
    private static readonly string[] EffortTypes = { "run", "walk", "hike", "ride" };

    private sealed record EffortTime(double D, double T);

    /// <summary>
    /// The user's max heart rate: the 98th percentile of the highest heart rates of their
    /// activities (a single strap glitch does not count); null with fewer than 5
    /// </summary>
    private async Task<int?> HistoryMaxHrAsync(string owner, CancellationToken ct)
    {
        var values = await _db.Activities.AsNoTracking()
            .Where(a => a.OwnerId == owner && a.MaxHeartRate >= 100 && a.MaxHeartRate <= 230)
            .Select(a => a.MaxHeartRate!.Value)
            .ToListAsync(ct);
        if (values.Count < 5)
        {
            return null;
        }

        values.Sort();
        return values[(int)Math.Floor(0.98 * (values.Count - 1))];
    }

    /// <summary>Analyses an activity's full track and puts it in the context of the user's other activities</summary>
    /// <param name="hr">Heart rate by time (full resolution)</param>
    /// <param name="hrGapS">The heart-rate series' spacing</param>
    private async Task<TrackAnalysis.Result?> AnalyzeAsync(string owner, AtlasActivity a, ActivitySummary summary,
        List<TrackAnalysis.Sample> points, List<SeriesPoint> hr, double hrGapS, CancellationToken ct)
    {
        var result = TrackAnalysis.Analyze(points, summary.Type, hr, hrGapS, await HistoryMaxHrAsync(owner, ct));
        if (result == null)
        {
            return null;
        }

        var efforts = await RankEffortsAsync(owner, a.Id, summary.Type, a.PointCount, result.Analysis.BestEfforts, ct);
        var similar = await SimilarAsync(owner, a, summary, ct);
        return result with { Analysis = result.Analysis with { BestEfforts = efforts, Similar = similar } };
    }

    /// <summary>Stores the activity's best efforts and ranks each against the user's others of the type</summary>
    private async Task<List<BestEffort>> RankEffortsAsync(string owner, Guid id, string type, int pointCount, List<BestEffort> efforts,
        CancellationToken ct)
    {
        if (!EffortTypes.Contains(type))
        {
            return efforts;
        }

        await SaveEffortsAsync(owner, id, type, pointCount, efforts, ct);
        var others = (await _db.Efforts.AsNoTracking()
                .Where(e => e.OwnerId == owner && e.Type == type && e.ActivityId != id)
                .Where(e => _db.Activities.Any(x => x.Id == e.ActivityId && (x.ClusterId == null || x.ClusterId == x.Id)))
                .Select(e => e.Efforts)
                .ToListAsync(ct))
            .SelectMany(json => JsonSerializer.Deserialize<List<EffortTime>>(json, JsonCamel) ?? new())
            .ToList();
        return efforts.Select(e => e with { AllTimeRank = 1 + others.Count(o => o.D == e.DistanceM && o.T < e.TimeS) }).ToList();
    }

    /// <returns>Whether the row is up to date</returns>
    private async Task<bool> SaveEffortsAsync(string owner, Guid id, string type, int pointCount, List<BestEffort> efforts, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(efforts.Select(e => new EffortTime(e.DistanceM, e.TimeS)), JsonCamel);
        var row = await _db.Efforts.FirstOrDefaultAsync(e => e.ActivityId == id, ct);
        if (row != null && row.Type == type && row.PointCount == pointCount && row.Efforts == json)
        {
            return true;
        }

        if (row == null)
        {
            _db.Efforts.Add(new ActivityEffort { ActivityId = id, OwnerId = owner, Type = type, PointCount = pointCount, Efforts = json });
        }
        else
        {
            row.Type = type;
            row.PointCount = pointCount;
            row.Efforts = json;
            row.UpdatedAt = DateTime.UtcNow;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex)
        {
            // Opened twice at once, or deleted meanwhile: the other writer's row is as good
            _logger.LogDebug(ex, "Best efforts not saved");
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>
    /// Computes best efforts for activities that have none yet, or stale ones (all owners).
    /// The fusion service calls it in small batches while idle.
    /// </summary>
    /// <returns>How many activities it saved (0 when nothing is left, or nothing could be saved)</returns>
    public async Task<int> AnalyzePendingEffortsAsync(int limit, CancellationToken ct = default)
    {
        var todo = await _db.Activities.AsNoTracking()
            .Where(a => (a.ClusterId == null || a.ClusterId == a.Id) && a.PointCount > 1)
            .Select(a => new { a.Id, a.OwnerId, a.PointCount, Type = a.Fusion != null && a.Fusion.Type != null ? a.Fusion.Type : a.Type })
            .Where(a => EffortTypes.Contains(a.Type))
            .Where(a => !_db.Efforts.Any(e => e.ActivityId == a.Id && e.Type == a.Type && e.PointCount == a.PointCount))
            .OrderBy(a => a.Id)
            .Take(limit)
            .ToListAsync(ct);
        var saved = 0;
        foreach (var a in todo)
        {
            var points = await _db.TrackPoints.AsNoTracking()
                .Where(p => p.ActivityId == a.Id)
                .OrderBy(p => p.Time)
                .Select(p => new TrackAnalysis.Sample(p.Time, p.Lat, p.Lon, null, null, null, null))
                .ToListAsync(ct);
            var efforts = points.Count < 2
                ? new List<BestEffort>()
                : TrackAnalysis.BestEfforts(points.Select(p => (p.T - points[0].T).TotalSeconds).ToArray(), TrackAnalysis.Distances(points), a.Type);
            if (await SaveEffortsAsync(a.OwnerId, a.Id, a.Type, a.PointCount, efforts, ct))
            {
                saved++;
            }
        }

        _db.ChangeTracker.Clear();
        return saved;
    }

    /// <summary>
    /// The same route (start and end within 300 m, distance ±10 %, 80 % of the track
    /// within 200 m of the other) and the same type over the last 365 days
    /// </summary>
    private async Task<SimilarActivities?> SimilarAsync(string owner, AtlasActivity a, ActivitySummary s, CancellationToken ct)
    {
        if (s.DistanceM is not > 0 || a.Polyline == null || !a.MinLat.HasValue || !a.MaxLat.HasValue || !a.MinLon.HasValue || !a.MaxLon.HasValue)
        {
            return null;
        }

        var duration = s.DurationS is > 0 ? s.DurationS.Value : (s.EndTime - s.StartTime).TotalSeconds;
        double? speed = duration > 0 ? s.DistanceM.Value / duration * 3.6 : null;
        var track = DecodePolyline(a.Polyline);
        if (track.Count < 2)
        {
            return null;
        }

        var lo = s.DistanceM.Value * 0.9;
        var hi = s.DistanceM.Value * 1.1;
        var candidates = await FilterActivities(owner, new AtlasQuery { Types = new List<string> { s.Type } })
            .Where(x => x.Id != a.Id && x.PointCount > 1 && x.Polyline != null)
            .Where(x => x.MaxLat >= a.MinLat && x.MinLat <= a.MaxLat && x.MaxLon >= a.MinLon && x.MinLon <= a.MaxLon)
            .Select(x => new
            {
                x.Id,
                Start = x.Fusion != null ? x.Fusion.StartTime : x.StartTime,
                End = x.Fusion != null ? x.Fusion.EndTime : x.EndTime,
                x.DurationS,
                DistanceM = x.DistanceM ?? (x.Fusion != null ? x.Fusion.DistanceM : null),
                AvgHr = x.AvgHeartRate ?? (x.Fusion != null ? x.Fusion.AvgHeartRate : null),
                x.Polyline
            })
            .Where(x => x.DistanceM >= lo && x.DistanceM <= hi)
            .Take(2000)
            .ToListAsync(ct);

        var probe = track.Where((_, i) => i % Math.Max(1, track.Count / 60) == 0).ToList();
        var route = new List<SimilarActivity>();
        foreach (var c in candidates)
        {
            var other = DecodePolyline(c.Polyline!);
            if (other.Count < 2
                || Geo.Haversine(track[0].Lat, track[0].Lon, other[0].Lat, other[0].Lon) > 300
                || Geo.Haversine(track[^1].Lat, track[^1].Lon, other[^1].Lat, other[^1].Lon) > 300
                || probe.Count(p => NearTrack(p, other, 200)) < probe.Count * 0.8)
            {
                continue;
            }

            var seconds = c.DurationS is > 0 ? c.DurationS.Value : (c.End - c.Start).TotalSeconds;
            route.Add(new SimilarActivity(c.Id, c.Start, Math.Round(seconds),
                seconds > 0 ? Math.Round(c.DistanceM!.Value / seconds * 3.6, 1) : null, c.AvgHr, c.DistanceM));
        }

        var speeds = route.Where(r => r.SpeedKmh.HasValue).Select(r => r.SpeedKmh!.Value).ToList();
        var hrs = route.Where(r => r.AvgHr.HasValue).Select(r => r.AvgHr!.Value).ToList();
        if (speed.HasValue)
        {
            speeds.Add(speed.Value);
        }

        if (s.AvgHeartRate.HasValue)
        {
            hrs.Add(s.AvgHeartRate.Value);
        }

        int? rank = route.Count > 0 && speed.HasValue ? 1 + route.Count(r => r.SpeedKmh > speed) : null;
        var sameRoute = route.OrderByDescending(r => r.StartTime).Take(20).ToList();

        // The same type over the last year (believable ones only)
        var now = DateTime.UtcNow;
        var year = (await InsightRows(owner, CopyQuery(new AtlasQuery { Types = new List<string> { s.Type } }, now.AddDays(-365), now)).ToListAsync(ct))
            .Where(r => r.Id != a.Id && r.DistanceM is > 0 && Hours(r) > 0 && Plausible(r))
            .ToList();
        double? Percentile(Func<InsightRow, double> value, double? mine) =>
            year.Count >= 3 && mine.HasValue ? Math.Round(100.0 * year.Count(r => value(r) < mine.Value) / year.Count) : null;
        var sameType = new SameTypeRank(year.Count,
            Percentile(r => r.DistanceM!.Value / 1000 / Hours(r), speed),
            Percentile(r => r.DistanceM!.Value, s.DistanceM));

        return new SimilarActivities(sameRoute, rank, route.Count + 1,
            route.Count > 0 && speeds.Count > 0 ? Math.Round(speeds.Average(), 1) : null,
            route.Count > 0 && hrs.Count > 0 ? (int)Math.Round(hrs.Average()) : null,
            sameType);
    }

    /// <summary>Whether a point is within <paramref name="meters"/> of a polyline (its segments, not only its vertices)</summary>
    private static bool NearTrack((double Lat, double Lon) p, List<(double Lat, double Lon)> track, double meters)
    {
        var kx = Math.Cos(p.Lat * Math.PI / 180) * 111_320;
        const double ky = 110_574;
        for (var i = 1; i < track.Count; i++)
        {
            // Local flat projection around the point (metres)
            double ax = (track[i - 1].Lon - p.Lon) * kx, ay = (track[i - 1].Lat - p.Lat) * ky;
            double bx = (track[i].Lon - p.Lon) * kx, by = (track[i].Lat - p.Lat) * ky;
            double dx = bx - ax, dy = by - ay;
            var len = dx * dx + dy * dy;
            var f = len > 0 ? Math.Clamp(-(ax * dx + ay * dy) / len, 0, 1) : 0;
            var x = ax + f * dx;
            var y = ay + f * dy;
            if (x * x + y * y <= meters * meters)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Decodes a Google encoded polyline (precision 5)</summary>
    internal static List<(double Lat, double Lon)> DecodePolyline(string encoded)
    {
        var points = new List<(double, double)>();
        int index = 0;
        long lat = 0, lon = 0;
        long Next()
        {
            long result = 0;
            var shift = 0;
            int b;
            do
            {
                if (index >= encoded.Length)
                {
                    return 0;
                }

                b = encoded[index++] - 63;
                result |= (long)(b & 0x1f) << shift;
                shift += 5;
            }
            while (b >= 0x20);

            return (result & 1) != 0 ? ~(result >> 1) : result >> 1;
        }

        while (index < encoded.Length)
        {
            lat += Next();
            lon += Next();
            points.Add((lat / 1e5, lon / 1e5));
        }

        return points;
    }
}
