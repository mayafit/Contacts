using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker.Matching;

namespace ContactsApi.Tests;

/// <summary>Rebuilding a route on demand (AD-38): synthetic points, similar drives, marked points — pure, on synthetic geometry</summary>
public class RouteRebuildTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 7, 30, 0, DateTimeKind.Utc);
    private static readonly Func<int, SyntheticTrace.Label> M = v => new SyntheticTrace.Label("matched", v);

    /// <summary>A straight road north along a meridian: a vertex every ~111 m</summary>
    private static List<(double Lat, double Lon)> Road(int vertices, double lon = 34.8) =>
        Enumerable.Range(0, vertices).Select(i => (31.5 + i * 0.001, lon)).ToList();

    private static string Line(IEnumerable<(double Lat, double Lon)> points) => Geo.EncodePolyline(points.ToList());

    /// <summary>Sparse real points along the road: every <paramref name="every"/> vertices, a minute apart</summary>
    private static List<RoutePoint> Sparse(int vertices, int every, double lonOffset = 0) =>
        Enumerable.Range(0, vertices).Where(i => i % every == 0 || i == vertices - 1)
            .Select((v, k) => new RoutePoint(T0.AddMinutes(k), 31.5 + v * 0.001, 34.8 + lonOffset)).ToList();

    [Fact]
    public void Legs_to_synthetic_points_are_inferred_and_the_confidence_counts_real_points_only()
    {
        // 9 points 222 m apart on the road; the middle 5 are synthetic
        var vertices = Enumerable.Range(0, 9).Select(i => i * 2).ToList();
        var points = SyntheticTrace.Points(vertices).Select((p, i) => p with { Synthetic = i is >= 2 and <= 6 }).ToList();
        var r = SyntheticTrace.Match(points, _ => SyntheticTrace.Answer(17, vertices.Select(v => M(v))));

        Assert.Equal(4, r.Metrics.PointsSent);
        Assert.Equal(4, r.Metrics.Anchors);
        // Observed only between two real points: the first and the last leg
        Assert.Equal(new[] { PieceKinds.Observed, PieceKinds.Inferred, PieceKinds.Observed }, r.Pieces.Select(p => p.Kind));
        Assert.InRange(r.Pieces[0].ToM, 220, 224);
        Assert.InRange(r.Pieces[1].ToM - r.Pieces[1].FromM, 1330, 1336);
    }

    [Fact]
    public void A_drive_between_the_same_places_fits_and_one_elsewhere_does_not()
    {
        var points = Sparse(60, 8);
        var same = new SimilarCandidate(Guid.NewGuid(), T0.AddDays(-3), Line(Road(60)));
        // A parallel road 1 km east: the ends are near enough, the way is not
        var elsewhere = new SimilarCandidate(Guid.NewGuid(), T0.AddDays(-1), Line(Road(60, 34.81)));

        var ranked = SimilarRoutes.Rank(points, new[] { elsewhere, same });

        var best = Assert.Single(ranked);
        Assert.Equal(same.ActivityId, best.Basis.ActivityId);
        Assert.Equal((1.0, 2, 1, false), (best.Basis.Fit, best.Basis.Candidates, best.Basis.Agreeing, best.Basis.Reversed));
    }

    [Fact]
    public void The_way_back_fits_a_drive_there_reversed()
    {
        // This drive goes south; the similar one was recorded going north
        var points = Sparse(60, 8).Select((p, i) => p).Reverse()
            .Select((p, k) => p with { T = T0.AddMinutes(k) }).ToList();
        var north = Line(Road(60));

        var back = Assert.Single(SimilarRoutes.Rank(points, new[] { new SimilarCandidate(Guid.NewGuid(), T0, north) }));
        Assert.True(back.Basis.Reversed);
        Assert.Equal(1.0, back.Basis.Fit);
        // The basis line runs the drive's way: it starts where the drive starts
        Assert.Equal(points[0].Lat, back.Line.Line[0].Lat, 3);
    }

    [Fact]
    public void A_round_trip_is_read_in_travel_order()
    {
        // Out 3 km and back on the same road: every place is passed twice
        var outAndBack = Road(28).Concat(Road(28).AsEnumerable().Reverse().Skip(1)).ToList();
        var points = new[] { 0, 9, 18, 27, 36, 45, 54 }
            .Select((v, k) => new RoutePoint(T0.AddMinutes(k * 2), outAndBack[v].Lat, outAndBack[v].Lon)).ToList();

        var ranked = SimilarRoutes.Rank(points, new[] { new SimilarCandidate(Guid.NewGuid(), T0, Line(outAndBack)) });

        var basis = Assert.Single(ranked);
        Assert.Equal(1.0, basis.Basis.Fit);
        var cum = basis.Line.Cum;
        var at = SimilarRoutes.ProjectAlong(basis.Line.Line, cum, points);
        // Positions only go forward, past the turn
        Assert.Equal(at.Select(a => a.Pos).OrderBy(x => x), at.Select(a => a.Pos));
        Assert.True(at[^1].Pos > cum[^1] - 50);
    }

    [Fact]
    public void A_round_trip_is_scored_both_ways_round_and_its_right_direction_wins()
    {
        // Home → out on one road → back on another road 1 km east → home
        var outward = Road(28);
        var back = Road(28, 34.81).AsEnumerable().Reverse().ToList();
        var loop = outward.Concat(new (double Lat, double Lon)[] { (outward[^1].Lat, 34.805) }).Concat(back)
            .Concat(new (double Lat, double Lon)[] { (31.5, 34.805), outward[0] }).ToList();
        // This drive went out the eastern road and back the western one: the loop reversed
        var reversed = Enumerable.Reverse(loop).ToList();
        var points = Enumerable.Range(0, reversed.Count).Where(i => i % 6 == 0 || i == reversed.Count - 1)
            .Select((i, k) => new RoutePoint(T0.AddMinutes(k), reversed[i].Lat, reversed[i].Lon)).ToList();
        var id = Guid.NewGuid();
        var line = Line(loop);

        var ranked = SimilarRoutes.Rank(points, new[] { new SimilarCandidate(id, T0, line) });

        Assert.True(ranked[0].Basis.Reversed);
        Assert.Equal(1.0, ranked[0].Basis.Fit);
        Assert.Equal((1, 1), (ranked[0].Basis.Candidates, ranked[0].Basis.Agreeing));
        Assert.Single(ranked[0].Basis.Examples);
    }

    [Fact]
    public void The_corridor_cells_hold_every_point_within_150_m_of_the_line()
    {
        // A diagonal line of 5 km with vertices 1 km apart
        var line = Enumerable.Range(0, 6).Select(i => (31.5 + i * 0.0064, 34.8 + i * 0.0075)).ToList();
        var cells = RouteCells.Line(line);

        Assert.Equal(cells.OrderBy(c => c), cells);
        var cum = SimilarRoutes.Cumulative(line);
        var rnd = new Random(7);
        for (var k = 0; k < 500; k++)
        {
            // A point at a random spot, up to 150 m off the line to either side
            var (lat, lon) = SimilarRoutes.PointAt(line, cum, rnd.NextDouble() * cum[^1]);
            var off = (rnd.NextDouble() * 2 - 1) * 150;
            var p = (Lat: lat + off * 0.7 / 110_540, Lon: lon - off * 0.7 / 94_800);
            Assert.True(SimilarRoutes.Project(line, cum, p.Lat, p.Lon).Dist <= 150.5);
            Assert.Contains(RouteCells.Of(p.Lat, p.Lon), cells);
        }

        // 2 km away: in none of them; a line of one point has none
        Assert.DoesNotContain(RouteCells.Of(31.5 + 0.0064 * 2.5 + 0.018, 34.8 + 0.0075 * 2.5 - 0.021), cells);
        Assert.Empty(RouteCells.Line(new List<(double Lat, double Lon)> { (31.5, 34.8) }));
        Assert.Equal(cells, RouteCells.Line(Line(line)));
    }

    [Fact]
    public void A_stray_point_is_one_reached_and_left_too_fast_even_with_a_minute_of_slack()
    {
        var points = new List<RoutePoint>
        {
            new(T0, 31.5, 34.8),
            // 1.1 km in "0 s": the minute-rounded times of the Timeline, not stray
            new(T0, 31.51, 34.8),
            // 30 km off and back within two minutes: stray
            new(T0.AddMinutes(1), 31.78, 34.8),
            new(T0.AddMinutes(2), 31.52, 34.8),
            new(T0.AddMinutes(4), 31.54, 34.8),
            // The last point 20 km on, 10 s later: stray on its one leg
            new(T0.AddMinutes(4).AddSeconds(10), 31.72, 34.8)
        };

        Assert.Equal(new[] { false, false, true, false, false, true }, SimilarRoutes.Stray(points, 160));
        Assert.All(SimilarRoutes.Stray(points, null), Assert.False);
        Assert.Equal(4, SimilarRoutes.Clean(points, 160).Count);

        // Stray points take no part: the drive still fits the road fully, and keeps them all
        var ranked = SimilarRoutes.Rank(points, new[] { new SimilarCandidate(Guid.NewGuid(), T0, Line(Road(60))) }, maxSpeedKmh: 160);
        var basis = Assert.Single(ranked);
        Assert.Equal(1.0, basis.Basis.Fit);
        var enriched = SimilarRoutes.Borrow(points, basis.Line, 160, 200);
        Assert.Equal(6, enriched.Count(p => !p.Synthetic));
        Assert.Equal(enriched.Select(p => p.T).OrderBy(t => t), enriched.Select(p => p.T));
    }

    [Fact]
    public void A_recording_that_starts_late_and_stops_early_borrows_only_its_own_part()
    {
        // The commute is 6.5 km; this recording covers 2.2 km to 4.4 km of it
        var points = new List<RoutePoint>
        {
            new(T0, 31.52, 34.8), new(T0.AddMinutes(1), 31.531, 34.8), new(T0.AddMinutes(2), 31.542, 34.8)
        };

        var basis = Assert.Single(SimilarRoutes.Rank(points, new[] { new SimilarCandidate(Guid.NewGuid(), T0, Line(Road(60))) }, maxSpeedKmh: 160));
        var added = SimilarRoutes.Borrow(points, basis.Line, 160, 200).Where(p => p.Synthetic).ToList();

        Assert.Equal(1.0, basis.Basis.Fit);
        Assert.NotEmpty(added);
        Assert.All(added, p => Assert.InRange(p.Lat, 31.52, 31.542));
    }

    [Fact]
    public void A_way_too_long_for_the_drive_s_time_is_dropped_and_a_detour_ranks_lower()
    {
        // Three points 2.2 km apart, 2 minutes apart (66 km/h)
        var points = new List<RoutePoint> { new(T0, 31.5, 34.8), new(T0.AddMinutes(2), 31.52, 34.8), new(T0.AddMinutes(4), 31.54, 34.8) };
        var straight = Road(41);
        // Out 20 km east and back between the second and third points: 44 km in 4 minutes
        (double Lat, double Lon)[] Out(double km) => new[] { (31.53, 34.8), (31.53, 34.8 + km / 94.8), (31.531, 34.8 + km / 94.8), (31.531, 34.8) };
        List<(double Lat, double Lon)> Via(double km) => straight.Take(31).Concat(Out(km)).Concat(straight.Skip(31)).ToList();
        var far = new SimilarCandidate(Guid.NewGuid(), T0, Line(Via(20)));
        // Out 3 km and back at the same time of day; the straight way two hours off
        var detour = new SimilarCandidate(Guid.NewGuid(), T0.AddDays(-1), Line(Via(3)));
        var direct = new SimilarCandidate(Guid.NewGuid(), T0.AddDays(-2).AddHours(2), Line(straight));

        var ranked = SimilarRoutes.Rank(points, new[] { far, detour, direct }, maxSpeedKmh: 160);

        Assert.Equal(new[] { direct.ActivityId, detour.ActivityId }, ranked.Select(r => r.Basis.ActivityId));
        Assert.Equal((3, 2), (ranked[0].Basis.Candidates, ranked[0].Basis.Agreeing));
    }

    [Fact]
    public void The_way_most_drives_take_is_tried_first_then_the_best_of_another_way()
    {
        // Only the two ends were recorded: any way between them fits
        var points = new List<RoutePoint> { new(T0, 31.5, 34.8), new(T0.AddMinutes(10), 31.559, 34.8) };
        var west = Line(Road(60));
        // A parallel way 1 km east, joined at both ends
        var east = Line(new[] { (31.5, 34.8) }.Concat(Road(60, 34.81)).Concat(new[] { (31.559, 34.8) }));
        // Three drives the west way an hour off; one the east way at the very same time
        var common = Enumerable.Range(1, 3).Select(d => new SimilarCandidate(Guid.NewGuid(), T0.AddDays(-7 * d).AddHours(1), west)).ToList();
        var rare = new SimilarCandidate(Guid.NewGuid(), T0.AddDays(-7), east);

        var ranked = SimilarRoutes.Rank(points, common.Append(rare).ToList(), maxSpeedKmh: 160);

        Assert.Equal(3, ranked.Count);
        Assert.Equal(common[0].ActivityId, ranked[0].Basis.ActivityId);
        Assert.Equal(3, ranked[0].Basis.SameWay);
        // The other way gets its try second, ahead of the common way's second best
        Assert.Equal((rare.ActivityId, 1), (ranked[1].Basis.ActivityId, ranked[1].Basis.SameWay));
        Assert.Equal(common[1].ActivityId, ranked[2].Basis.ActivityId);
    }

    [Fact]
    public void The_kind_of_day_is_friday_saturday_or_a_working_day()
    {
        // 2026-09-04 is a Friday
        var friday = new DateTime(2026, 9, 4, 7, 30, 0, DateTimeKind.Utc);
        Assert.Equal(new[] { 1, 2, 0, 0 }, new[] { friday, friday.AddDays(1), friday.AddDays(2), friday.AddDays(6) }.Select(SimilarRoutes.DayKind));
        var points = Sparse(60, 8);
        var saturday = new SimilarCandidate(Guid.NewGuid(), friday.AddDays(1), Line(Road(60)));
        var weekday = new SimilarCandidate(Guid.NewGuid(), friday.AddDays(-7 * 4 + 4), Line(Road(60)));

        Assert.Equal(weekday.ActivityId, SimilarRoutes.Rank(points, new[] { saturday, weekday })[0].Basis.ActivityId);
    }

    [Fact]
    public void Ranking_prefers_the_fit_then_the_time_of_day()
    {
        var points = Sparse(60, 8);
        var evening = new SimilarCandidate(Guid.NewGuid(), T0.AddDays(-2).AddHours(10), Line(Road(60)));
        var morning = new SimilarCandidate(Guid.NewGuid(), T0.AddDays(-9).AddMinutes(20), Line(Road(60)));

        var ranked = SimilarRoutes.Rank(points, new[] { evening, morning });

        Assert.Equal(new[] { morning.ActivityId, evening.ActivityId }, ranked.Select(r => r.Basis.ActivityId));
        Assert.Equal(new[] { morning.ActivityId, evening.ActivityId }, ranked[0].Basis.Examples.Select(e => e.ActivityId));
        // One way, taken by both
        Assert.Equal(2, ranked[0].Basis.SameWay);
        Assert.Equal(10, SimilarRoutes.HourDiff(T0, T0.AddHours(14)), 6);
    }

    [Fact]
    public void Points_are_borrowed_on_each_leg_timed_by_distance()
    {
        // Two real points 2.2 km apart along the road, 2 minutes apart
        var points = new List<RoutePoint> { new(T0, 31.5, 34.8), new(T0.AddMinutes(2), 31.52, 34.8) };
        var basis = Assert.Single(SimilarRoutes.Rank(points, new[] { new SimilarCandidate(Guid.NewGuid(), T0, Line(Road(21))) })).Line;

        var dense = SimilarRoutes.Borrow(points, basis, 200, 200);
        var sparse = SimilarRoutes.Borrow(points, basis, 200, 600);

        Assert.Equal(10, dense.Count(p => p.Synthetic));
        Assert.Equal(3, sparse.Count(p => p.Synthetic));
        // In time order, at constant speed: each 200 m takes 1/11 of the 120 s
        Assert.Equal(dense.Select(p => p.T).OrderBy(t => t), dense.Select(p => p.T));
        var first = dense[1];
        Assert.InRange((first.T - T0).TotalSeconds, 10.5, 11.3);
        Assert.InRange(Geo.Haversine(31.5, 34.8, first.Lat, first.Lon), 195, 205);
    }

    [Fact]
    public void Nothing_is_borrowed_too_fast_on_a_short_leg_off_the_line_or_on_the_basis_s_own_gaps()
    {
        var line = Road(41);
        var points = new List<RoutePoint>
        {
            new(T0, 31.5, 34.8),
            // 2.2 km in 20 s: 396 km/h
            new(T0.AddSeconds(20), 31.52, 34.8),
            // 200 m on
            new(T0.AddSeconds(80), 31.522, 34.8),
            // 1.1 km on, but 2 km east of the line
            new(T0.AddSeconds(200), 31.533, 34.82),
            new(T0.AddSeconds(400), 31.54, 34.8)
        };
        var gapPieces = new List<RoutePiece> { new(0, 2000, PieceKinds.Observed), new(2000, 4500, PieceKinds.Gap) };
        var basis = SimilarRoutes.Rank(points.Take(3).ToList(),
            new[] { new SimilarCandidate(Guid.NewGuid(), T0, Line(line), gapPieces) }).Single().Line;

        var enriched = SimilarRoutes.Borrow(points, basis, 200, 200);

        Assert.DoesNotContain(enriched, p => p.Synthetic);
        Assert.True(basis.InGap(2500));

        // The same 2.2 km at a plausible speed borrows only up to the basis's gap at 2 km
        var slow = new List<RoutePoint> { new(T0, 31.5, 34.8), new(T0.AddMinutes(3), 31.522, 34.8) };
        var added = SimilarRoutes.Borrow(slow, basis, 200, 200).Where(p => p.Synthetic).ToList();
        Assert.NotEmpty(added);
        Assert.All(added, p => Assert.True(Geo.Haversine(31.5, 34.8, p.Lat, p.Lon) <= 2010));
    }

    [Fact]
    public void Marked_points_are_placed_on_their_leg_in_the_order_marked_and_timed_by_distance()
    {
        var points = new List<RoutePoint> { new(T0, 31.5, 34.8), new(T0.AddMinutes(10), 31.6, 34.8), new(T0.AddMinutes(20), 31.7, 34.8) };
        // Two on the first leg (a detour east), one on the second
        var marks = new List<(double Lat, double Lon)> { (31.53, 34.82), (31.57, 34.82), (31.65, 34.801) };

        Assert.Null(Waypoints.Check(points, marks));
        var placed = Waypoints.Place(points, marks);

        Assert.Equal(6, placed.Count);
        Assert.Equal(new[] { false, true, true, false, true, false }, placed.Select(p => p.Synthetic));
        Assert.Equal(placed.Select(p => p.T).OrderBy(t => t), placed.Select(p => p.T));
        Assert.Equal(31.53, placed[1].Lat, 6);
        // Timed by the distance along real → marks → real, not by the straight leg
        var d1 = Geo.Haversine(31.5, 34.8, 31.53, 34.82);
        var total = d1 + Geo.Haversine(31.53, 34.82, 31.57, 34.82) + Geo.Haversine(31.57, 34.82, 31.6, 34.8);
        Assert.InRange((placed[1].T - T0).TotalSeconds, 600 * d1 / total - 1, 600 * d1 / total + 1);
    }

    [Fact]
    public void Marked_points_must_be_few_and_near_the_track()
    {
        var points = new List<RoutePoint> { new(T0, 31.5, 34.8), new(T0.AddMinutes(10), 31.6, 34.8) };

        Assert.NotNull(Waypoints.Check(points, new List<(double, double)>()));
        Assert.NotNull(Waypoints.Check(points, Enumerable.Repeat((31.55, 34.8), Waypoints.Max + 1).ToList()));
        Assert.Contains("5 km", Waypoints.Check(points, new List<(double, double)> { (31.55, 34.9) }));
        Assert.Null(Waypoints.Check(points, new List<(double, double)> { (31.55, 34.84) }));
    }

    [Fact]
    public void Stored_marks_round_trip()
    {
        var json = RouteMarks.Serialize(new[] { (31.1234567, 34.7654321) });
        Assert.Equal("[[31.123457,34.765432]]", json);
        Assert.Equal((31.123457, 34.765432), RouteMarks.Parse(json).Single());
        Assert.Empty(RouteMarks.Parse("{"));
    }
}
