/**
 * @fileoverview Activity analysis from a full track: moving time, splits,
 * heart-rate zones, aerobic decoupling and best efforts
 * @module ContactsApi.Infrastructure.Atlas
 */
namespace ContactsApi.Infrastructure.Atlas;

/// <summary>What the track of one activity says about the effort (see the contract in docs/planning/epic-11-fusion.md)</summary>
/// <param name="MaxSpeedKmh">The highest 30-second average, not a single-point spike</param>
/// <param name="Pace">Show pace (min/km) instead of speed: runs, walks, hikes</param>
/// <param name="SplitM">The split length used</param>
public record ActivityAnalysis(
    double MovingS, double StoppedS, double DistanceM, double? AvgMovingSpeedKmh, double? MaxSpeedKmh, double? AvgCadence,
    bool Pace, List<SplitStat> Splits, double SplitM, HrZones? Zones, HrDrift? Drift, List<BestEffort> BestEfforts,
    SimilarActivities? Similar);

/// <summary>One split (per km, per 5 km…); <paramref name="TimeS"/> is the moving time over it</summary>
public record SplitStat(int Index, double DistanceM, double TimeS, double SpeedKmh, double PaceSPerKm, int? AvgHr, double ClimbM);

/// <summary>Time in the five heart-rate zones</summary>
/// <param name="MaxHr">The user's own max (from their history) or this activity's</param>
/// <param name="MaxHrSource">history or activity</param>
/// <param name="Bounds">Lower bounds (bpm) of zones 2–5: 60/70/80/90 % of the max</param>
/// <param name="Seconds">Time in zones 1–5</param>
public record HrZones(int MaxHr, string MaxHrSource, int[] Bounds, double[] Seconds, int AvgHr, int MaxHrHere);

/// <summary>Speed and heart rate over half of the moving time</summary>
public record DriftHalf(double SpeedKmh, int AvgHr);

/// <summary>Aerobic decoupling: how much the speed per heartbeat fell in the second half</summary>
/// <param name="Verdict">steady (under 5 %), drifting (5–10 %), fading (over 10 %)</param>
public record HrDrift(DriftHalf FirstHalf, DriftHalf SecondHalf, double DecouplingPct, string Verdict);

/// <summary>The fastest stretch of a distance within the activity</summary>
/// <param name="StartOffsetS">Where it started, seconds from the activity start</param>
/// <param name="AllTimeRank">1 = the user's fastest of this distance for the type; null when unknown</param>
public record BestEffort(double DistanceM, double TimeS, double SpeedKmh, double PaceSPerKm, double StartOffsetS, int? AllTimeRank);

/// <summary>The activity against the user's others</summary>
/// <param name="SameRoute">Other activities on the same route, newest first (at most 20)</param>
/// <param name="RankBySpeed">1 = the fastest on the route, this one included</param>
/// <param name="OfRoute">Activities on the route, this one included</param>
public record SimilarActivities(List<SimilarActivity> SameRoute, int? RankBySpeed, int OfRoute, double? AvgSpeedKmh, int? AvgHr,
    SameTypeRank SameType);

/// <summary>Percentiles (0–100) among the user's activities of the same type in the last 365 days</summary>
public record SameTypeRank(int Count, double? SpeedPercentile, double? DistancePercentile);

/// <summary>Another activity on the same route</summary>
public record SimilarActivity(Guid Id, DateTime StartTime, double DurationS, double? SpeedKmh, int? AvgHr, double? DistanceM);

/// <summary>Analysis of one track (pure: no database)</summary>
public static class TrackAnalysis
{
    /// <summary>A full-resolution track point</summary>
    public sealed record Sample(DateTime T, double Lat, double Lon, double? Ele, int? Hr, double? Speed, int? Cad);

    /// <summary>The analysis, and for every point its distance from the start (m) and speed (m/s)</summary>
    public sealed record Result(ActivityAnalysis Analysis, double[] Distance, double?[] Speed);

    /// <summary>Show pace instead of speed</summary>
    public static bool ShowsPace(string type) => type is "run" or "walk" or "hike";

    /// <summary>The split length for a type (m)</summary>
    public static double SplitLength(string type) => type switch
    {
        "run" or "walk" or "hike" => 1000,
        "swim" => 100,
        "flight" => 100_000,
        "drive" or "transit" => 10_000,
        _ => 5000
    };

    /// <summary>Distances of best efforts for a type (m)</summary>
    public static double[] EffortDistances(string type) => type switch
    {
        "run" or "walk" or "hike" => new double[] { 1000, 5000, 10000, 21097 },
        "ride" => new double[] { 5000, 10000, 20000, 40000 },
        _ => Array.Empty<double>()
    };

    private static double MovingKmh(string type) => type switch
    {
        "run" or "walk" or "hike" => 1,
        "swim" => 0.5,
        _ => 3
    };

    /// <summary>Faster than this (km/h, over 30 s or more) is a GPS error for the type</summary>
    private static double SpeedCapKmh(string type) => type switch
    {
        "walk" or "hike" => 15,
        "run" => 30,
        "swim" => 10,
        "ride" => 100,
        "ski" => 160,
        "flight" => 1200,
        _ => 250
    };

    private const int MaxSplits = 200;

    /// <summary>Analyses a track</summary>
    /// <param name="points">The full track, in time order</param>
    /// <param name="hr">Heart rate by time (from this track, another source's track or health samples), in time order</param>
    /// <param name="hrGapS">How far from a heart-rate value it still counts (the series' sample spacing)</param>
    /// <param name="historyMaxHr">The user's max heart rate from their history, when known</param>
    /// <returns>Null without at least two points over some time</returns>
    public static Result? Analyze(IReadOnlyList<Sample> points, string type, IReadOnlyList<SeriesPoint> hr, double hrGapS, int? historyMaxHr)
    {
        var n = points.Count;
        if (n < 2)
        {
            return null;
        }

        var t0 = points[0].T;
        var t = points.Select(p => (p.T - t0).TotalSeconds).ToArray();
        if (t[^1] <= 0)
        {
            return null;
        }

        var d = Distances(points);
        var speed = Speeds(points, t, d);
        var heart = new HeartRate(hr, t0, hrGapS);
        var cap = SpeedCapKmh(type);

        // Moving: segments faster than a walking crawl
        var threshold = MovingKmh(type);
        var moving = new bool[n];
        double movingS = 0, movingM = 0;
        for (var i = 1; i < n; i++)
        {
            var dt = t[i] - t[i - 1];
            if (dt > 0 && (d[i] - d[i - 1]) / dt * 3.6 > threshold)
            {
                moving[i] = true;
                movingS += dt;
                movingM += d[i] - d[i - 1];
            }
        }

        var cadence = points.Where((p, i) => moving[i] && p.Cad is > 0).Select(p => (double)p.Cad!.Value).ToList();
        var splitM = SplitLength(type);
        var zones = Zones(hr, t0, t[^1], hrGapS, historyMaxHr);
        var analysis = new ActivityAnalysis(
            Math.Round(movingS),
            Math.Round(Math.Max(0, t[^1] - movingS)),
            Math.Round(d[^1]),
            movingS > 0 ? Math.Round(movingM / movingS * 3.6, 1) : null,
            MaxSpeed(t, d, cap),
            cadence.Count > 0 ? Math.Round(cadence.Average()) : null,
            ShowsPace(type),
            Splits(points, t, d, moving, heart, splitM),
            splitM,
            zones,
            Drift(t, d, moving, movingS, heart),
            BestEfforts(t, d, type),
            null);
        return new Result(analysis, d, speed);
    }

    /// <summary>Distance from the start at every point (m)</summary>
    public static double[] Distances(IReadOnlyList<Sample> points)
    {
        var d = new double[points.Count];
        for (var i = 1; i < points.Count; i++)
        {
            d[i] = d[i - 1] + Geo.Haversine(points[i - 1].Lat, points[i - 1].Lon, points[i].Lat, points[i].Lon);
        }

        return d;
    }

    /// <summary>Recorded speed, else the average over ±10 s around the point (m/s)</summary>
    private static double?[] Speeds(IReadOnlyList<Sample> points, double[] t, double[] d)
    {
        var n = points.Count;
        var speed = new double?[n];
        int lo = 0, hi = 0;
        for (var i = 0; i < n; i++)
        {
            while (t[i] - t[lo] > 10)
            {
                lo++;
            }

            hi = Math.Max(hi, i);
            while (hi + 1 < n && t[hi + 1] - t[i] <= 10)
            {
                hi++;
            }

            // Sparse tracks: at least the neighbours
            var a = Math.Min(lo, Math.Max(0, i - 1));
            var b = Math.Max(hi, Math.Min(n - 1, i + 1));
            var dt = t[b] - t[a];
            speed[i] = points[i].Speed ?? (dt > 0 ? Math.Round((d[b] - d[a]) / dt, 2) : null);
        }

        return speed;
    }

    /// <summary>The highest average over 30–90 s (km/h), ignoring impossible ones</summary>
    private static double? MaxSpeed(double[] t, double[] d, double cap)
    {
        double? best = null;
        var j = 0;
        for (var i = 0; i < t.Length; i++)
        {
            j = Math.Max(j, i);
            while (j < t.Length && t[j] - t[i] < 30)
            {
                j++;
            }

            if (j >= t.Length)
            {
                break;
            }

            var dt = t[j] - t[i];
            if (dt > 90)
            {
                continue;
            }

            var kmh = (d[j] - d[i]) / dt * 3.6;
            if (kmh <= cap && (best == null || kmh > best))
            {
                best = kmh;
            }
        }

        return best.HasValue ? Math.Round(best.Value, 1) : null;
    }

    private static List<SplitStat> Splits(IReadOnlyList<Sample> points, double[] t, double[] d, bool[] moving, HeartRate heart, double splitM)
    {
        var splits = new List<SplitStat>();
        double distance = 0, time = 0, hrSum = 0, hrTime = 0, climb = 0;
        double? reference = points[0].Ele;

        void Emit()
        {
            splits.Add(new SplitStat(splits.Count + 1, Math.Round(distance), Math.Round(time),
                time > 0 ? Math.Round(distance / time * 3.6, 1) : 0,
                distance > 0 ? Math.Round(time / distance * 1000) : 0,
                hrTime >= time * 0.5 && hrTime > 0 ? (int)Math.Round(hrSum / hrTime) : null,
                Math.Round(climb)));
            distance = time = hrSum = hrTime = climb = 0;
        }

        for (var i = 1; i < points.Count && splits.Count < MaxSplits; i++)
        {
            // Climb with a 3 m dead band (GPS elevation noise)
            if (points[i].Ele is { } e)
            {
                if (reference == null || reference - e >= 3)
                {
                    reference = e;
                }
                else if (e - reference >= 3)
                {
                    climb += e - reference.Value;
                    reference = e;
                }
            }

            var dd = d[i] - d[i - 1];
            var dt = moving[i] ? t[i] - t[i - 1] : 0;
            var bpm = heart.At((t[i] + t[i - 1]) / 2);
            while (dd > 0 && distance + dd >= splitM && splits.Count < MaxSplits)
            {
                var fraction = (splitM - distance) / dd;
                distance = splitM;
                time += dt * fraction;
                if (bpm.HasValue)
                {
                    hrSum += bpm.Value * dt * fraction;
                    hrTime += dt * fraction;
                }

                Emit();
                dd *= 1 - fraction;
                dt *= 1 - fraction;
            }

            distance += dd;
            time += dt;
            if (bpm.HasValue)
            {
                hrSum += bpm.Value * dt;
                hrTime += dt;
            }
        }

        // The rest, unless it is a few metres
        if (distance >= splitM * 0.1 && splits.Count < MaxSplits)
        {
            Emit();
        }

        return splits;
    }

    private static HrZones? Zones(IReadOnlyList<SeriesPoint> hr, DateTime t0, double totalS, double gapS, int? historyMaxHr)
    {
        var series = hr.Where(p => p.V > 30 && (p.T - t0).TotalSeconds >= -gapS && (p.T - t0).TotalSeconds <= totalS + gapS).ToList();
        if (series.Count < 3)
        {
            return null;
        }

        var maxHere = (int)Math.Round(series.Max(p => p.V));
        var max = historyMaxHr ?? maxHere;
        if (max < 100)
        {
            return null;
        }

        var bounds = new[] { 0.6, 0.7, 0.8, 0.9 }.Select(f => (int)Math.Round(max * f)).ToArray();
        var seconds = new double[5];
        double sum = 0, weight = 0;
        for (var i = 0; i < series.Count; i++)
        {
            // Each value counts until the next one, at most the spacing of the series
            var w = i + 1 < series.Count ? Math.Min((series[i + 1].T - series[i].T).TotalSeconds, gapS) : 0;
            var zone = bounds.Count(b => series[i].V >= b);
            seconds[zone] += w;
            sum += series[i].V * w;
            weight += w;
        }

        var avg = weight > 0 ? sum / weight : series.Average(p => p.V);
        return new HrZones(max, historyMaxHr.HasValue ? "history" : "activity", bounds, seconds.Select(x => Math.Round(x)).ToArray(),
            (int)Math.Round(avg), maxHere);
    }

    private static HrDrift? Drift(double[] t, double[] d, bool[] moving, double movingS, HeartRate heart)
    {
        if (movingS < 20 * 60)
        {
            return null;
        }

        var dist = new double[2];
        var time = new double[2];
        var hrSum = new double[2];
        var hrTime = new double[2];
        double elapsed = 0;
        for (var i = 1; i < t.Length; i++)
        {
            if (!moving[i])
            {
                continue;
            }

            var dt = t[i] - t[i - 1];
            var half = elapsed + dt / 2 < movingS / 2 ? 0 : 1;
            elapsed += dt;
            dist[half] += d[i] - d[i - 1];
            time[half] += dt;
            if (heart.At((t[i] + t[i - 1]) / 2) is { } bpm)
            {
                hrSum[half] += bpm * dt;
                hrTime[half] += dt;
            }
        }

        if (time[0] <= 0 || time[1] <= 0 || hrTime[0] < time[0] * 0.5 || hrTime[1] < time[1] * 0.5)
        {
            return null;
        }

        var first = new DriftHalf(Math.Round(dist[0] / time[0] * 3.6, 1), (int)Math.Round(hrSum[0] / hrTime[0]));
        var second = new DriftHalf(Math.Round(dist[1] / time[1] * 3.6, 1), (int)Math.Round(hrSum[1] / hrTime[1]));
        var r1 = dist[0] / time[0] / (hrSum[0] / hrTime[0]);
        var r2 = dist[1] / time[1] / (hrSum[1] / hrTime[1]);
        var pct = Math.Round((r1 - r2) / r1 * 100, 1);
        return new HrDrift(first, second, pct, pct < 5 ? "steady" : pct <= 10 ? "drifting" : "fading");
    }

    /// <summary>The fastest stretch of each effort distance (elapsed time, stops included)</summary>
    public static List<BestEffort> BestEfforts(double[] t, double[] d, string type)
    {
        var efforts = new List<BestEffort>();
        var cap = SpeedCapKmh(type);
        foreach (var distance in EffortDistances(type).Where(x => x <= d[^1]))
        {
            double best = double.MaxValue;
            var bestStart = 0.0;
            var j = 0;
            for (var i = 0; i < t.Length; i++)
            {
                var target = d[i] + distance;
                while (j < t.Length && d[j] < target)
                {
                    j++;
                }

                if (j >= t.Length)
                {
                    break;
                }

                // The moment the distance is reached, between two points
                var end = d[j] > d[j - 1] ? t[j - 1] + (target - d[j - 1]) / (d[j] - d[j - 1]) * (t[j] - t[j - 1]) : t[j];
                var elapsed = end - t[i];
                if (elapsed > 0 && elapsed < best && distance / elapsed * 3.6 <= cap)
                {
                    best = elapsed;
                    bestStart = t[i];
                }
            }

            if (best < double.MaxValue)
            {
                efforts.Add(new BestEffort(distance, Math.Round(best), Math.Round(distance / best * 3.6, 1),
                    Math.Round(best / distance * 1000), Math.Round(bestStart), null));
            }
        }

        return efforts;
    }

    /// <summary>Heart rate at a moment: the nearest value within the series' spacing</summary>
    private sealed class HeartRate
    {
        private readonly double[] _t;
        private readonly double[] _v;
        private readonly double _gap;

        public HeartRate(IReadOnlyList<SeriesPoint> series, DateTime t0, double gap)
        {
            var valid = series.Where(p => p.V > 30).ToList();
            _t = valid.Select(p => (p.T - t0).TotalSeconds).ToArray();
            _v = valid.Select(p => p.V).ToArray();
            _gap = gap;
        }

        public double? At(double s)
        {
            if (_t.Length == 0)
            {
                return null;
            }

            var i = Array.BinarySearch(_t, s);
            if (i >= 0)
            {
                return _v[i];
            }

            i = ~i;
            var best = -1;
            var bestGap = double.MaxValue;
            foreach (var k in new[] { i - 1, i })
            {
                if (k >= 0 && k < _t.Length && Math.Abs(_t[k] - s) < bestGap)
                {
                    best = k;
                    bestGap = Math.Abs(_t[k] - s);
                }
            }

            return bestGap <= _gap ? _v[best] : null;
        }
    }
}
