using ContactsApi.Infrastructure.Atlas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContactsApi.Tests;

public class TrackAnalysisTests
{
    private static readonly DateTime T0 = new(2026, 5, 1, 6, 0, 0, DateTimeKind.Utc);

    // ~1 m of latitude
    private const double DegPerM = 1 / 111_195.0;

    /// <summary>A straight track north, a point every <paramref name="stepS"/> s at the given speeds (m/s) per segment</summary>
    private static List<TrackAnalysis.Sample> Track(IEnumerable<double> speeds, int stepS = 5, Func<int, int?>? hr = null, Func<int, double?>? ele = null)
    {
        var points = new List<TrackAnalysis.Sample> { new(T0, 32, 35, ele?.Invoke(0), hr?.Invoke(0), null, 80) };
        var m = 0.0;
        var i = 0;
        foreach (var v in speeds)
        {
            i++;
            m += v * stepS;
            points.Add(new TrackAnalysis.Sample(T0.AddSeconds(i * stepS), 32 + m * DegPerM, 35, ele?.Invoke(i), hr?.Invoke(i), null, 80));
        }

        return points;
    }

    private static List<SeriesPoint> Hr(List<TrackAnalysis.Sample> points) =>
        points.Where(p => p.Hr.HasValue).Select(p => new SeriesPoint(p.T, p.Hr!.Value)).ToList();

    [Fact]
    public void Splits_moving_time_and_stops()
    {
        // A run: 3 km at 4 m/s, a 5-minute stop, 2 km at 2.5 m/s
        var speeds = Enumerable.Repeat(4.0, 150).Concat(Enumerable.Repeat(0.0, 60)).Concat(Enumerable.Repeat(2.5, 160));
        var points = Track(speeds);
        var r = TrackAnalysis.Analyze(points, "run", new List<SeriesPoint>(), 60, null)!;
        var a = r.Analysis;

        Assert.True(a.Pace);
        Assert.Equal(1000, a.SplitM);
        Assert.Equal(5000, a.DistanceM, 0);
        Assert.Equal(750 + 800, a.MovingS, 0);
        Assert.Equal(300, a.StoppedS, 0);
        Assert.Equal(5, a.Splits.Count);
        Assert.Equal(250, a.Splits[0].PaceSPerKm, 0);         // 4:10 /km
        Assert.Equal(400, a.Splits[4].PaceSPerKm, 0);         // 6:40 /km
        Assert.Equal(14.4, a.MaxSpeedKmh!.Value, 1);
        Assert.Equal(80, a.AvgCadence);
        Assert.Null(a.Zones);
        Assert.Null(a.Drift);
        // Distances and speeds for every point
        Assert.Equal(points.Count, r.Distance.Length);
        Assert.Equal(4.0, r.Speed[10]!.Value, 1);
        Assert.Equal(5000, r.Distance[^1], 0);
    }

    [Fact]
    public void Best_efforts_find_the_fastest_stretch_with_stops_included()
    {
        // 2 km slow, 1 km fast, 3 km slow: the best 1 km is the fast one, starting at 2 km
        var speeds = Enumerable.Repeat(2.0, 200).Concat(Enumerable.Repeat(5.0, 40)).Concat(Enumerable.Repeat(2.0, 300));
        var a = TrackAnalysis.Analyze(Track(speeds), "run", new List<SeriesPoint>(), 60, null)!.Analysis;

        var km = Assert.Single(a.BestEfforts, e => e.DistanceM == 1000);
        Assert.Equal(200, km.TimeS, 0);
        Assert.Equal(1000, km.StartOffsetS, 0);
        var five = Assert.Single(a.BestEfforts, e => e.DistanceM == 5000);
        Assert.True(five.TimeS > 2000);
        Assert.DoesNotContain(a.BestEfforts, e => e.DistanceM == 10000);
    }

    [Fact]
    public void Heart_rate_zones_use_the_history_max_and_drift_compares_halves()
    {
        // 40 minutes at the same speed, heart rate rising from 130 to 170
        var points = Track(Enumerable.Repeat(8.0, 480), hr: i => 130 + i / 12);
        var a = TrackAnalysis.Analyze(points, "ride", Hr(points), 60, 190)!.Analysis;

        Assert.False(a.Pace);
        Assert.NotNull(a.Zones);
        Assert.Equal("history", a.Zones!.MaxHrSource);
        Assert.Equal(new[] { 114, 133, 152, 171 }, a.Zones.Bounds);
        Assert.InRange(a.Zones.Seconds.Sum(), 2390, 2400);
        Assert.Equal(0, a.Zones.Seconds[0]);
        Assert.True(a.Zones.Seconds[2] > a.Zones.Seconds[1]);

        Assert.NotNull(a.Drift);
        Assert.Equal(a.Drift!.FirstHalf.SpeedKmh, a.Drift.SecondHalf.SpeedKmh, 1);
        Assert.True(a.Drift.SecondHalf.AvgHr > a.Drift.FirstHalf.AvgHr);
        Assert.InRange(a.Drift.DecouplingPct, 10, 30);
        Assert.Equal("fading", a.Drift.Verdict);

        // Without history: the activity's own max
        var own = TrackAnalysis.Analyze(points, "ride", Hr(points), 60, null)!.Analysis.Zones!;
        Assert.Equal("activity", own.MaxHrSource);
        Assert.Equal(170, own.MaxHr);
    }

    [Fact]
    public void Heart_rate_from_sparse_health_samples_counts_within_its_spacing()
    {
        // Samples every 4 minutes from another source, none on the track
        var points = Track(Enumerable.Repeat(6.0, 480));
        var health = Enumerable.Range(0, 11).Select(i => new SeriesPoint(T0.AddMinutes(i * 4), 140)).ToList();
        var a = TrackAnalysis.Analyze(points, "ride", health, 300, 180)!.Analysis;

        Assert.Equal(2400, a.Zones!.Seconds.Sum(), 0);
        Assert.Equal(140, a.Zones.AvgHr);
        Assert.Equal("steady", a.Drift!.Verdict);
        Assert.All(a.Splits, s => Assert.Equal(140, s.AvgHr));
    }

    [Fact]
    public void Climb_per_split_ignores_elevation_noise_and_gps_spikes_do_not_make_max_speed()
    {
        // A ride with a steady climb of 0.5 m per point and ±1 m noise, plus one 2 km GPS jump
        var speeds = Enumerable.Repeat(6.0, 400).ToList();
        speeds[200] = 400;
        var a = TrackAnalysis.Analyze(Track(speeds, ele: i => i * 0.5 + (i % 2 == 0 ? 1 : -1)), "ride", new List<SeriesPoint>(), 60, null)!.Analysis;

        Assert.Equal(5000, a.SplitM);
        Assert.InRange(a.Splits.Sum(s => s.ClimbM), 190, 205);
        Assert.True(a.MaxSpeedKmh <= 100);
    }

    [Fact]
    public void Too_little_track_gives_no_analysis()
    {
        Assert.Null(TrackAnalysis.Analyze(new List<TrackAnalysis.Sample> { new(T0, 32, 35, null, null, null, null) }, "run",
            new List<SeriesPoint>(), 60, null));
        Assert.Null(TrackAnalysis.Analyze(new List<TrackAnalysis.Sample> { new(T0, 32, 35, null, null, null, null), new(T0, 32.1, 35, null, null, null, null) },
            "run", new List<SeriesPoint>(), 60, null));
    }

    [Fact]
    public void Decodes_what_it_encodes()
    {
        var points = new List<(double Lat, double Lon)> { (32.08123, 34.78456), (32.09, 34.8), (31.5, 35.2), (-12.3, -77.01) };
        var decoded = AtlasService.DecodePolyline(Geo.EncodePolyline(points));
        Assert.Equal(points.Count, decoded.Count);
        for (var i = 0; i < points.Count; i++)
        {
            Assert.Equal(points[i].Lat, decoded[i].Lat, 5);
            Assert.Equal(points[i].Lon, decoded[i].Lon, 5);
        }
    }
}

/// <summary>Against a real PostgreSQL when ATLAS_TEST_DB is set (skipped otherwise)</summary>
public class ActivityAnalysisDbTests
{
    [Fact]
    public async Task Analysis_ranks_efforts_compares_routes_and_feeds_insights()
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        if (string.IsNullOrEmpty(cs))
        {
            return;
        }

        await using var db = new AtlasDbContext(new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations", "atlas")).Options);
        await db.Database.MigrateAsync();
        var owner = "analysis-" + Guid.NewGuid();
        var svc = new AtlasService(db, NullLogger<AtlasService>.Instance);
        var day = DateTime.UtcNow.Date.AddDays(-60).AddHours(6);

        // The same 6 km route north, run five times, each faster (the last the fastest), with heart rate
        List<PointInput> Run(DateTime t0, double mps, double lon = 35.0) =>
            Enumerable.Range(0, 301).Select(i => new PointInput(t0.AddSeconds(i * 6000 / mps / 300), 32 + i * 20 * 1 / 111_195.0, lon,
                100 + i * 0.1, 140 + i / 20, null, 170)).ToList();
        var runs = Enumerable.Range(0, 5).Select(i => new ActivityInput($"run-{i}", "running", $"Run {i}", null, null,
            Points: Run(day.AddDays(i * 7), 3.0 + i * 0.2))).ToList();
        // Another run of the same length elsewhere: not the same route
        runs.Add(new ActivityInput("run-elsewhere", "running", null, null, null, Points: Run(day.AddDays(36), 3.5, 35.5)));
        // Hard efforts in the history: a max heart rate of 190
        runs.AddRange(Enumerable.Range(0, 5).Select(i => new ActivityInput($"hard-{i}", "running", null, day.AddDays(-30 - i), day.AddDays(-30 - i).AddMinutes(30),
            DistanceM: 6000, AvgHeartRate: 170, MaxHeartRate: 190)));
        await svc.IngestAsync(owner, new IngestRequest { Source = "gpx", Activities = runs });

        var ids = await db.Activities.Where(a => a.OwnerId == owner && a.ExternalId!.StartsWith("run-")).ToDictionaryAsync(a => a.ExternalId!, a => a.Id);
        // The background backfill: every run gets its efforts (not the ones without a track)
        while (await svc.AnalyzePendingEffortsAsync(3) > 0)
        {
        }

        Assert.Equal(6, await db.Efforts.CountAsync(e => e.OwnerId == owner));

        var detail = await svc.GetActivityAsync(owner, ids["run-4"]);
        var a = detail.Analysis!;
        Assert.True(a.Pace);
        Assert.Equal(6, a.Splits.Count);
        Assert.Equal(190, a.Zones!.MaxHr);
        Assert.Equal("history", a.Zones.MaxHrSource);
        Assert.Equal(1, Assert.Single(a.BestEfforts, e => e.DistanceM == 5000).AllTimeRank);
        Assert.Equal(5, a.Similar!.OfRoute);
        Assert.Equal(1, a.Similar.RankBySpeed);
        Assert.Equal(4, a.Similar.SameRoute.Count);
        Assert.Equal(ids["run-3"], a.Similar.SameRoute[0].Id);
        Assert.DoesNotContain(a.Similar.SameRoute, r => r.Id == ids["run-elsewhere"]);
        Assert.True(a.Similar.SameType.Count >= 5);
        Assert.True(a.Similar.SameType.SpeedPercentile > 50);
        // Points carry distance, derived speed and cadence
        Assert.Equal(0, detail.Points[0].D);
        Assert.InRange(detail.Points[^1].D, 5900, 6100);
        Assert.Equal(170, detail.Points[5].Cad);
        Assert.InRange(detail.Points[5].Speed!.Value, 3.5, 4.3);

        var slow = (await svc.GetActivityAsync(owner, ids["run-0"])).Analysis!;
        Assert.Equal(5, slow.Similar!.RankBySpeed);
        // Slower than the four others on the route and the one elsewhere
        Assert.Equal(6, Assert.Single(slow.BestEfforts, e => e.DistanceM == 5000).AllTimeRank);

        var insights = await svc.InsightsAsync(owner, new AtlasQuery { From = day.AddDays(-35), To = day.AddDays(40) });
        Assert.NotNull(insights.Weekly);
        Assert.InRange(insights.Weekly!.Count, 11, 13);
        Assert.Equal(insights.Totals.Hours, insights.Weekly.Sum(w => w.Hours), 0);
        Assert.True(insights.Weekly.All(w => w.Load >= 0) && insights.Weekly.Any(w => w.Load > 0));
        Assert.Equal(5, insights.ZoneSeconds!.Length);
        Assert.True(insights.ZoneSeconds[3] > 0);                 // the hard ones at 170/190 = 89 %
        var trend = Assert.Single(insights.Trends!, t => t.Type == "run");
        Assert.Equal(11, trend.Points.Count);
        Assert.NotNull(trend.SpeedChangePct);
    }
}
