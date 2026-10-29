using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Routes;

namespace ContactsApi.Tests;

/// <summary>Rule routes.v1, stage A (AD-35 rules 1 to 5)</summary>
public class RouteQualityTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 7, 0, 0, DateTimeKind.Utc);
    private const double MetresPerDegreeLat = 111_195.08;

    private static bool Covered(IReadOnlyList<RoutePoint> _) => true;

    /// <summary>A track northwards from Tel Aviv, legs of the given lengths, 60 s apart</summary>
    private static List<RoutePoint> Track(IEnumerable<double> legsM, double lat0 = 32.0, double lon = 34.85, double secondsPerLeg = 60)
    {
        var points = new List<RoutePoint> { new(T0, lat0, lon) };
        var lat = lat0;
        var t = T0;
        foreach (var m in legsM)
        {
            lat += m / MetresPerDegreeLat;
            t = t.AddSeconds(secondsPerLeg);
            points.Add(new RoutePoint(t, lat, lon));
        }

        return points;
    }

    [Fact]
    public void Worked_example_timeline_drive_scores_about_64_fair_and_needs_improvement()
    {
        // 0.94 points per km, half the length in legs over 500 m (AD-35 rule 3, first example)
        var m = new RouteMetrics(13, 13_830, 0.94, 340, 2800, 300, 2400, 0.5, 4, 0, null, null);
        var score = RouteQuality.Score(m, RouteQuality.ProfileFor("drive"));

        Assert.Equal(64, score);
        Assert.Equal("fair", RouteQuality.Grade(score));
    }

    [Fact]
    public void A_sparse_timeline_drive_is_fair_and_needs_improvement()
    {
        // 19 points over 19.7 km (0.97 per km): 17 legs of 480 m and one of 11.5 km, at driving speeds
        var legs = Enumerable.Repeat(480.0, 17).Append(11_500).ToList();
        var a = RouteQuality.Assess("drive", "IN_PASSENGER_VEHICLE", Track(legs, secondsPerLeg: 400), null, null, Covered);

        Assert.InRange(a.Metrics.PointsPerKm!.Value, 0.94, 0.98);
        Assert.Equal("fair", a.Grade);
        Assert.InRange(a.Score!.Value, 60, 79);
        Assert.True(a.NeedsImprovement);
        Assert.Equal(Matchability.Yes, a.Matchable);
        Assert.Contains("sparse", a.Flags);
        Assert.Contains("straight", a.Flags);
        Assert.Equal(1, a.Metrics.StraightCount);
    }

    [Fact]
    public void Worked_example_sports_tracker_ride_at_91_per_km_is_good_and_not_matched()
    {
        // 11 m legs, 4 s apart: 91 points per km, every leg short and plausible
        var points = Track(Enumerable.Repeat(11.0, 909), secondsPerLeg: 4);
        var a = RouteQuality.Assess("ride", "cycling", points, null, null, Covered);

        Assert.InRange(a.Metrics.PointsPerKm!.Value, 90, 92);
        Assert.Equal(100, a.Score);
        Assert.Equal("good", a.Grade);
        Assert.False(a.NeedsImprovement);
        Assert.Empty(a.Flags);
    }

    [Fact]
    public void Fewer_than_two_points_is_no_track_with_no_score()
    {
        var a = RouteQuality.Assess("walk", null, new List<RoutePoint> { new(T0, 32, 34.8) }, null, null, Covered);

        Assert.Equal(Matchability.NoTrack, a.Matchable);
        Assert.Null(a.Score);
        Assert.Null(a.Grade);
        Assert.False(a.NeedsImprovement);
        Assert.Equal(new[] { "no_track" }, a.Flags);
        Assert.Equal(Matchability.NoTrack, RouteQuality.Assess("drive", null, new List<RoutePoint>(), null, null, Covered).Matchable);
    }

    [Theory]
    [InlineData("flight")]
    [InlineData("ski")]
    [InlineData("swim")]
    [InlineData("other")]
    public void Flight_ski_swim_and_other_are_never_matched(string type)
    {
        var a = RouteQuality.Assess(type, null, Track(new[] { 2000.0, 2000.0 }), null, null, Covered);

        Assert.Equal(Matchability.TypeNever, a.Matchable);
        Assert.False(a.NeedsImprovement);
        Assert.NotNull(a.Score);
    }

    [Theory]
    [InlineData(null, Matchability.Unknown, false)]
    [InlineData("IN_TRAIN", Matchability.Rail, false)]
    [InlineData("in_subway", Matchability.Rail, false)]
    [InlineData("IN_BUS", Matchability.Yes, true)]
    [InlineData("in_taxi", Matchability.Yes, true)]
    public void Transit_is_matched_only_as_bus_or_taxi(string? sourceType, string matchable, bool needs)
    {
        var a = RouteQuality.Assess("transit", sourceType, Track(new[] { 2000.0, 1500.0, 2500.0 }), null, null, Covered);

        Assert.Equal(matchable, a.Matchable);
        Assert.Equal(needs, a.NeedsImprovement);
    }

    [Fact]
    public void Foot_and_bike_types_wait_for_their_phase()
    {
        // A sparse walk needs improvement in principle, but R2 is not enabled
        var a = RouteQuality.Assess("walk", "WALKING", Track(new[] { 800.0, 900.0 }, secondsPerLeg: 600), null, null, Covered);

        Assert.Equal(Matchability.Yes, a.Matchable);
        Assert.Equal("poor", a.Grade);
        Assert.False(a.NeedsImprovement);
    }

    [Fact]
    public void A_drive_outside_the_outlines_is_outside_coverage()
    {
        var coverage = new RegionCoverage(new[] { RegionCoverage.Parse(File.ReadAllText(BundledPoly())) });
        // Around Amman
        var a = RouteQuality.Assess("drive", null, Track(new[] { 2000.0, 2000.0 }, 31.95, 35.93), null, null, coverage.Covers);

        Assert.Equal(Matchability.OutsideCoverage, a.Matchable);
        Assert.False(a.NeedsImprovement);
        Assert.Contains("outside_coverage", a.Flags);
    }

    [Fact]
    public void Implausible_speed_and_far_endpoints_lower_the_score()
    {
        // 10 km in 60 s, and the start 2 km from the Visit that ended 5 min before
        var points = Track(new[] { 400.0, 10_000.0, 400.0 });
        var before = new RouteVisit(T0.AddHours(-1), T0.AddMinutes(-5), 32.0 - 2000 / MetresPerDegreeLat, 34.85);
        var a = RouteQuality.Assess("drive", null, points, before, null, Covered);

        Assert.InRange(a.Metrics.ImplausibleSpeedShare, 0.9, 1);
        Assert.InRange(a.Metrics.StartEndpointM!.Value, 1990, 2010);
        Assert.Null(a.Metrics.EndEndpointM);
        Assert.Contains("implausible_speed", a.Flags);
        Assert.Contains("endpoint_far", a.Flags);

        // A Visit that ended hours earlier does not count
        var old = before with { End = T0.AddHours(-3) };
        Assert.Null(RouteQuality.Assess("drive", null, points, old, null, Covered).Metrics.StartEndpointM);
    }

    [Fact]
    public void Gaps_are_measured_with_the_90th_percentile()
    {
        var legs = Enumerable.Range(1, 10).Select(i => i * 100.0).ToList();
        var m = RouteQuality.Measure(RouteQuality.ProfileFor("drive"), Track(legs), null, null);

        Assert.Equal(11, m.PointCount);
        Assert.InRange(m.MaxGapM!.Value, 999, 1001);
        Assert.InRange(m.P90GapM!.Value, 899, 901);
        Assert.Equal(60, m.P90GapS);
        Assert.InRange(m.TrackM, 5499, 5501);
    }

    [Fact]
    public void Input_hash_follows_times_and_rounded_coordinates()
    {
        var points = Track(new[] { 500.0, 500.0 });
        var same = points.Select(p => p with { Lat = p.Lat + 1e-9 }).ToList();
        var moved = points.Select(p => p with { Lat = p.Lat + 1e-5 }).ToList();

        Assert.Equal(64, RouteQuality.InputHash(points).Length);
        Assert.Equal(RouteQuality.InputHash(points), RouteQuality.InputHash(same));
        Assert.NotEqual(RouteQuality.InputHash(points), RouteQuality.InputHash(moved));
    }

    internal static string BundledPoly() =>
        Path.Combine(AppContext.BaseDirectory, "Regions", "israel-and-palestine.poly");
}

/// <summary>Region outlines (.poly) and the coverage test</summary>
public class RegionCoverageTests
{
    [Fact]
    public void The_bundled_outline_covers_israel_and_not_its_neighbours()
    {
        var coverage = RegionCoverage.Load(Path.GetDirectoryName(RouteQualityTests.BundledPoly())!);

        Assert.True(coverage.Any);
        Assert.True(coverage.Contains(32.08, 34.78)); // Tel Aviv
        Assert.True(coverage.Contains(31.78, 35.21)); // Jerusalem
        Assert.True(coverage.Contains(32.81, 34.99)); // Haifa
        Assert.True(coverage.Contains(29.56, 34.95)); // Eilat
        Assert.True(coverage.Contains(31.50, 34.45)); // Gaza
        Assert.False(coverage.Contains(31.95, 35.93)); // Amman
        Assert.False(coverage.Contains(33.89, 35.50)); // Beirut
        Assert.False(coverage.Contains(30.04, 31.24)); // Cairo
        Assert.False(coverage.Contains(35.17, 33.36)); // Nicosia
    }

    [Fact]
    public void Holes_are_left_out_and_a_track_is_covered_when_any_point_is_inside()
    {
        var region = RegionCoverage.Parse("""
            square
            1
               0 0
               10 0
               10 10
               0 10
            END
            !2
               4 4
               6 4
               6 6
               4 6
            END
            END
            """);
        var coverage = new RegionCoverage(new[] { region });

        Assert.Equal("square", Assert.Single(coverage.Names));
        Assert.True(coverage.Contains(1, 1));
        Assert.False(coverage.Contains(5, 5));
        Assert.False(coverage.Contains(11, 5));

        var t = DateTime.UtcNow;
        Assert.True(coverage.Covers(new List<RoutePoint> { new(t, 20, 20), new(t, 2, 2) }));
        Assert.False(coverage.Covers(new List<RoutePoint> { new(t, 20, 20), new(t, 5, 5) }));
    }
}

/// <summary>The feed's helpers: cursors, thinning, cluster changes</summary>
public class RoutesFeedHelperTests
{
    [Fact]
    public void Cursor_round_trips_and_rejects_anything_else()
    {
        var t = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc).AddTicks(10);
        var id = Guid.NewGuid();

        Assert.Equal((t, id, (DateTime?)null), AtlasRoutesFeed.ParseCursor(AtlasRoutesFeed.Cursor(t, id)));
        var read = t.AddMinutes(3);
        Assert.Equal((t, id, (DateTime?)read), AtlasRoutesFeed.ParseCursor(AtlasRoutesFeed.Cursor(t, id, read)));
        Assert.Equal(Guid.Empty, AtlasRoutesFeed.ParseCursor(null).Id);
        Assert.Throws<ArgumentException>(() => AtlasRoutesFeed.ParseCursor("2026-10-28"));
        Assert.Throws<ArgumentException>(() => AtlasRoutesFeed.ParseCursor("-1_" + id.ToString("N")));
        Assert.Throws<ArgumentException>(() => AtlasRoutesFeed.ParseCursor(AtlasRoutesFeed.Cursor(t, id) + "_x"));
        Assert.Throws<ArgumentException>(() => AtlasRoutesFeed.ParseCursor(AtlasRoutesFeed.Cursor(t, id, read) + "_1"));
    }

    [Fact]
    public void Long_tracks_are_thinned_keeping_both_ends()
    {
        var t = DateTime.UtcNow;
        var points = Enumerable.Range(0, 12_000).Select(i => new RoutePoint(t.AddSeconds(i), i, 0)).ToList();
        var thin = AtlasRoutesFeed.Thin(points);

        Assert.Equal(RoutesFeedLimits.PointsMax, thin.Count);
        Assert.Equal(points[0], thin[0]);
        Assert.Equal(points[^1], thin[^1]);
        var small = points.Take(10).ToList();
        Assert.Same(small, AtlasRoutesFeed.Thin(small));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(2000)]
    [InlineData(2001)]
    [InlineData(5000)]
    [InlineData(12_345)]
    [InlineData(100_000)]
    public void The_query_side_step_keeps_at_most_PointsMax_points_with_both_ends(int n)
    {
        // As the tracks query keeps them: every step-th point, and the last
        var step = AtlasRoutesFeed.ThinStep(n);
        var kept = Enumerable.Range(1, n).Count(rn => step == 1 || (rn - 1) % step == 0 || rn == n);

        Assert.InRange(kept, Math.Min(n, 2), RoutesFeedLimits.PointsMax);
        Assert.Equal(n <= RoutesFeedLimits.PointsMax ? 1 : step, step);
    }

    [Fact]
    public void Only_records_whose_cluster_changed_are_reported()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid();
        var before = new Dictionary<Guid, Guid> { [a] = a, [b] = a, [c] = c };
        // a and b stay together; c's cluster dissolves; d joins a
        var changed = AtlasService.ClusterChanges(before, new[] { a, b, d }, new[] { a, a, a });

        Assert.Equal(new[] { c, d }.OrderBy(x => x), changed.OrderBy(x => x));
    }
}
