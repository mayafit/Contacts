using System.Text.Json;
using System.Text.Json.Nodes;
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker;
using EchoRoutes.Worker.Matching;
using EchoRoutes.Worker.Valhalla;
using Microsoft.Extensions.Options;

namespace ContactsApi.Tests;

/// <summary>A /route stand-in over the synthetic straight road (no recorded data)</summary>
internal sealed class RouteOnlyEngine : IValhallaClient
{
    public readonly List<JsonObject> Routes = new();
    public Func<JsonObject, JsonDocument> Answer = r => SyntheticTrace.RouteAnswer(Locations(r), 50);

    public static List<(double Lat, double Lon)> Locations(JsonObject r) =>
        r["locations"]!.AsArray().Select(p => ((double)p!["lat"]!, (double)p["lon"]!)).ToList();

    public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<string?> VersionAsync(CancellationToken ct) => Task.FromResult<string?>("3.9.1");

    public Task<JsonDocument> TraceAttributesAsync(JsonObject request, CancellationToken ct) => throw new NotSupportedException();

    public Task<JsonDocument> RouteAsync(JsonObject request, CancellationToken ct)
    {
        Routes.Add(request);
        try
        {
            return Task.FromResult(Answer(request));
        }
        catch (Exception ex)
        {
            return Task.FromException<JsonDocument>(ex);
        }
    }
}

/// <summary>Gap filling by /route and confidence v2 (AD-35 rules 6 and 7, routes.v2 and v3, story 12.4a), on synthetic answers</summary>
public class RoutesGapFillTests
{
    private static readonly Func<int, SyntheticTrace.Label> M = v => new SyntheticTrace.Label("matched", v);

    /// <summary>Plans, answers every call, fills the stretches with <paramref name="engine"/>, assembles</summary>
    private static async Task<MatchResult> MatchAsync(List<RoutePoint> points, Func<TraceCall, JsonDocument?> answer, RouteOnlyEngine engine,
        Action<RoutesOptions>? options = null, EngineBreaker? breaker = null, string source = "google-timeline", Func<bool>? yield = null)
    {
        var o = new RoutesOptions();
        options?.Invoke(o);
        var s = MatchSettings.From(o);
        var plan = TracePlanner.Plan(points, s);
        var answers = new Dictionary<TraceCall, TraceResponse?>();
        foreach (var call in plan.SelectMany(p => p.Calls))
        {
            using var doc = answer(call);
            answers[call] = doc == null ? null : ValhallaTrace.Parse(doc.RootElement);
        }

        var profile = RouteQuality.ProfileFor("drive");
        var sk = RouteAssembler.Build(points, plan, answers, profile);
        breaker ??= new EngineBreaker(Options.Create(o), TimeProvider.System);
        var fills = await GapFiller.FillAsync(RouteAssembler.Stretches(sk, s.GapFill), "auto", s.GapFill, profile.MaxSpeedKmh, engine,
            ex => breaker.Failure(ex.Message) | ex.Unreachable, breaker.Success, default, yield);
        return RouteAssembler.Assemble(points, sk, fills, profile, "auto", s.MaxStoredVertices, s.AnchorToleranceFor(source, "auto"),
            s.GapFill.RoutedOnlyWeight);
    }

    /// <summary>Each call's shape covers only its own points' vertices</summary>
    private static Func<TraceCall, JsonDocument?> PerCall(IReadOnlyList<int> vertices, Func<int, SyntheticTrace.Label>? label = null) => call =>
    {
        var vs = Enumerable.Range(call.Start, call.Count).Select(i => vertices[i]).ToList();
        var labels = Enumerable.Range(call.Start, call.Count).Select(i => (label ?? (_ => M(vertices[i])))(i) with { Vertex = vertices[i] });
        return SyntheticTrace.Answer(vs[^1] - vs[0] + 1, labels, first: vs[0]);
    };

    /// <summary>Points at the vertices, at the given seconds from the start</summary>
    private static List<RoutePoint> At(IReadOnlyList<int> vertices, IReadOnlyList<double> seconds) =>
        vertices.Select((v, i) => new RoutePoint(SyntheticTrace.T0.AddSeconds(seconds[i]), SyntheticTrace.Vertex(v).Lat, SyntheticTrace.Vertex(v).Lon)).ToList();

    /// <summary>Five points 222 m apart, a 5.5 km jump in 240 s (over the 5 km pre-split), three more</summary>
    private static (List<int> Vertices, List<RoutePoint> Points) CutGapDrive()
    {
        var vertices = new List<int> { 0, 2, 4, 6, 8, 58, 60, 62, 64 };
        var seconds = new List<double> { 0, 20, 40, 60, 80, 320, 340, 360, 380 };
        return (vertices, At(vertices, seconds));
    }

    [Fact]
    public async Task A_filled_stretch_adds_its_street_names_and_an_unfilled_gap_adds_none()
    {
        // The matched calls are on route 4; the 5.5 km stretch between them is routed on 431 (AD-37)
        var (vertices, points) = CutGapDrive();
        Func<TraceCall, JsonDocument?> on4 = call =>
        {
            var vs = Enumerable.Range(call.Start, call.Count).Select(i => vertices[i]).ToList();
            return SyntheticTrace.Answer(vs[^1] - vs[0] + 1, vs.Select(v => M(v)), first: vs[0], names: _ => new[] { "4" });
        };
        var engine = new RouteOnlyEngine { Answer = r => SyntheticTrace.RouteAnswer(RouteOnlyEngine.Locations(r), 80, streetNames: new[] { "431" }) };

        var r = await MatchAsync(points, on4, engine);

        Assert.Equal(new[] { "4", "431" }, r.Roads!.Select(x => x.Name));
        Assert.All(r.Roads!, x => Assert.True(x.Ref));
        // Route 4 before and after the stretch: 8 + 6 vertices of 111 m
        Assert.InRange(r.Roads![0].M, 1500, 1600);
        Assert.Equal(0, r.Roads[0].FromM);
        Assert.InRange(r.Roads[1].M, 5500, 5600);
        Assert.InRange(r.Roads[1].FromM, 880, 900);

        // Not filled: the straight 5.5 km leg is on no road
        var none = new RouteOnlyEngine { Answer = r => SyntheticTrace.RouteAnswer(RouteOnlyEngine.Locations(r), 80, lengthFactor: 4, streetNames: new[] { "431" }) };
        var gap = await MatchAsync(points, on4, none);
        Assert.Equal(new[] { "4" }, gap.Roads!.Select(x => x.Name));
        Assert.InRange(gap.Roads![0].M, 1500, 1600);
    }

    [Fact]
    public void A_route_answer_gives_each_segment_its_maneuver_street_names()
    {
        var body = new JsonObject
        {
            ["trip"] = new JsonObject
            {
                ["legs"] = new JsonArray(new JsonObject
                {
                    ["shape"] = SyntheticTrace.Encode(Enumerable.Range(0, 5).Select(SyntheticTrace.Vertex), 6),
                    ["maneuvers"] = new JsonArray(
                        new JsonObject { ["begin_shape_index"] = 0, ["end_shape_index"] = 2, ["street_names"] = new JsonArray("הרצל", " ") },
                        new JsonObject { ["begin_shape_index"] = 2, ["end_shape_index"] = 3 },
                        new JsonObject { ["begin_shape_index"] = 3, ["end_shape_index"] = 4, ["street_names"] = new JsonArray("431", "431") })
                }),
                ["summary"] = new JsonObject { ["length"] = 0.444, ["time"] = 30 }
            }
        };
        using var doc = JsonDocument.Parse(body.ToJsonString());

        var fill = GapFiller.Parse(doc.RootElement)!;

        // The segment into vertex j: blank names dropped, repeats once
        Assert.Equal(new string?[] { null, "הרצל", "הרצל", null, "431" }, fill.Roads!.Select(n => n?.Single()));
    }

    [Fact]
    public async Task A_cut_gap_is_filled_with_a_plausible_route()
    {
        var (vertices, points) = CutGapDrive();
        var engine = new RouteOnlyEngine();
        var r = await MatchAsync(points, PerCall(vertices), engine);

        var call = Assert.Single(engine.Routes);
        var at = RouteOnlyEngine.Locations(call);
        // Between the two anchors' positions, with the drive's costing; maneuvers (for the
        // street names, AD-37), no narrative
        Assert.Equal(SyntheticTrace.Vertex(8).Lat, at[0].Lat, 6);
        Assert.Equal(SyntheticTrace.Vertex(58).Lat, at[1].Lat, 6);
        Assert.Equal(("auto", "maneuvers"), ((string)call["costing"]!, (string)call["directions_type"]!));

        Assert.DoesNotContain(r.Pieces, p => p.Kind == PieceKinds.Gap);
        Assert.Equal((1, 1, 1, 0), (r.Metrics.CutGaps, r.Metrics.Stretches, r.Metrics.Filled, r.Metrics.FillRejectedTime));
        Assert.InRange(r.Metrics.FilledM, 5500, 5600);
        Assert.Equal((1.0, 1.0, RouteStatuses.Matched), (r.Metrics.Coverage, r.Confidence, r.Status));
        Assert.Null(r.Reason);
        SyntheticTrace.AssertContiguous(r);
    }

    [Fact]
    public async Task Without_gap_filling_the_cut_gap_stays_straight_and_costs_coverage()
    {
        var (vertices, points) = CutGapDrive();
        var engine = new RouteOnlyEngine();
        var r = await MatchAsync(points, PerCall(vertices), engine, o => o.GapFill = false);

        Assert.Empty(engine.Routes);
        Assert.Equal(PieceKinds.Gap, Assert.Single(r.Pieces, p => p.ToM - p.FromM > 5000).Kind);
        Assert.True(r.Confidence < 0.7, $"confidence {r.Confidence}");
    }

    [Fact]
    public async Task An_engine_break_is_bridged_by_a_route_and_no_longer_penalised()
    {
        var vertices = Enumerable.Range(0, 10).Select(i => i * 2).ToList();
        var labels = vertices.Select(M).ToList();
        labels[3] = labels[3] with { Begin = true };
        labels[4] = labels[4] with { End = true };
        var engine = new RouteOnlyEngine();
        var r = await MatchAsync(SyntheticTrace.Points(vertices), _ => SyntheticTrace.Answer(19, labels), engine);

        Assert.Single(engine.Routes);
        Assert.Equal((1, 1, 1.0), (r.Metrics.EngineBreaks, r.Metrics.BridgedBreaks, r.Metrics.Continuity));
        Assert.DoesNotContain(r.Pieces, p => p.Kind == PieceKinds.Gap);
        Assert.Equal(1.0, r.Confidence);
    }

    [Fact]
    public async Task A_leg_skipping_an_unmatched_point_on_a_continuous_shape_keeps_the_shape_and_asks_no_route()
    {
        var vertices = Enumerable.Range(0, 10).Select(i => i * 2).ToList();
        var labels = vertices.Select((v, i) => i == 5 ? new SyntheticTrace.Label("unmatched", v) : M(v)).ToList();
        var engine = new RouteOnlyEngine();
        var r = await MatchAsync(SyntheticTrace.Points(vertices), _ => SyntheticTrace.Answer(19, labels), engine);

        Assert.Empty(engine.Routes);
        Assert.Equal((0, PieceKinds.Observed), (r.Metrics.Stretches, Assert.Single(r.Pieces).Kind));
        Assert.Equal((1.0, 0.9), (r.Metrics.Coverage, r.Metrics.AnchorQuality));
        Assert.Equal((0.9, RouteStatuses.Matched), (r.Confidence, r.Status));
    }

    [Fact]
    public async Task A_leg_skipping_an_unmatched_point_across_a_break_is_routed_between_the_anchors_around_it()
    {
        var vertices = Enumerable.Range(0, 10).Select(i => i * 2).ToList();
        var labels = vertices.Select((v, i) => i == 5 ? new SyntheticTrace.Label("unmatched", v) { End = true } : M(v)).ToList();
        var engine = new RouteOnlyEngine();
        var r = await MatchAsync(SyntheticTrace.Points(vertices), _ => SyntheticTrace.Answer(19, labels), engine);

        var at = RouteOnlyEngine.Locations(Assert.Single(engine.Routes));
        Assert.Equal((SyntheticTrace.Vertex(8).Lat, SyntheticTrace.Vertex(12).Lat), (Math.Round(at[0].Lat, 6), Math.Round(at[1].Lat, 6)));
        Assert.Equal((1, 1.0), (r.Metrics.Filled, r.Metrics.Coverage));
    }

    /// <summary>A drive of <paramref name="n"/> + 1 anchors on one call, the engine breaking every leg; leg k is k + 1 vertices long</summary>
    private static (List<RoutePoint> Points, Func<TraceCall, JsonDocument?> Answer) BrokenEveryLeg(int n)
    {
        var vertices = new List<int> { 0 };
        for (var k = 0; k < n; k++)
        {
            vertices.Add(vertices[^1] + k + 1);
        }

        // 60 s a leg; each anchor ends one route and begins the next
        var points = At(vertices, vertices.Select((_, i) => i * 60.0).ToList());
        var labels = vertices.Select((v, i) => M(v) with { Begin = i > 0 && i < n, End = i > 0 && i < n }).ToList();
        return (points, call => SyntheticTrace.Answer(vertices[^1] + 1, labels.Skip(call.Start).Take(call.Count), perEdge: 1));
    }

    [Fact]
    public async Task An_anchored_drive_routes_at_most_the_cap_longest_stretches_first_and_marks_the_rest_skipped()
    {
        var (points, answer) = BrokenEveryLeg(25);
        var engine = new RouteOnlyEngine();
        var r = await MatchAsync(points, answer, engine);

        Assert.Equal(25, r.Metrics.Stretches);
        Assert.Equal(20, engine.Routes.Count);
        Assert.Equal((20, 5, 0), (r.Metrics.Filled, r.Metrics.FillSkipped, r.Metrics.FillTimeouts));
        // The longest first: the first five legs (1 to 5 vertices) are the ones left
        var routed = engine.Routes.Select(q => Math.Round(RouteOnlyEngine.Locations(q)[0].Lat, 6)).ToList();
        Assert.DoesNotContain(Math.Round(SyntheticTrace.Vertex(0).Lat, 6), routed);
        Assert.Equal(Math.Round(SyntheticTrace.Vertex(300).Lat, 6), routed[0]);
        Assert.Contains(r.Pieces, p => p.Kind == PieceKinds.Gap);

        var three = await MatchAsync(points, answer, new RouteOnlyEngine(), o => o.GapFillMaxCallsPerDrive = 3);
        Assert.Equal((3, 22), (three.Metrics.Filled, three.Metrics.FillSkipped));
    }

    [Fact]
    public async Task Interactive_work_stops_a_drive_s_filling_between_calls_and_defers_the_rest()
    {
        var (points, answer) = BrokenEveryLeg(6);
        var engine = new RouteOnlyEngine();
        var waiting = false;
        engine.Answer = q =>
        {
            waiting = engine.Routes.Count >= 2;
            return SyntheticTrace.RouteAnswer(RouteOnlyEngine.Locations(q), 50);
        };

        var r = await MatchAsync(points, answer, engine, yield: () => waiting);

        Assert.Equal(2, engine.Routes.Count);
        Assert.Equal((2, 4, 0), (r.Metrics.Filled, r.Metrics.FillDeferred, r.Metrics.FillSkipped));
        Assert.NotNull(r.Polyline);
    }

    [Fact]
    public async Task A_route_much_slower_than_the_elapsed_time_is_rejected_and_the_gap_stays()
    {
        var (vertices, points) = CutGapDrive();
        // 5.5 km at 15 km/h: 22 min for the 4 min that passed
        var engine = new RouteOnlyEngine { Answer = q => SyntheticTrace.RouteAnswer(RouteOnlyEngine.Locations(q), 15) };
        var r = await MatchAsync(points, PerCall(vertices), engine);

        Assert.Equal((1, 0), (r.Metrics.FillRejectedTime, r.Metrics.Filled));
        Assert.Contains(r.Pieces, p => p.Kind == PieceKinds.Gap);
        Assert.True(r.Confidence < 0.7, $"confidence {r.Confidence}");
        Assert.NotEqual(RouteStatuses.Matched, r.Status);
        Assert.Matches(@"^low confidence 0\.\d+: a 5\.6 km stretch with no road that fits the 4 min elapsed$", r.Reason);
    }

    [Fact]
    public async Task A_jump_too_fast_for_any_road_is_not_even_routed()
    {
        // 10 km in 20 s: even at 200 km/h no road does it within 20 × 1.6 + 120 s
        var vertices = new List<int> { 0, 2, 4, 94, 96 };
        var points = At(vertices, new double[] { 0, 20, 40, 60, 80 });
        var engine = new RouteOnlyEngine();
        var r = await MatchAsync(points, PerCall(vertices), engine);

        Assert.Empty(engine.Routes);
        Assert.Equal(1, r.Metrics.FillRejectedTime);
        Assert.True(r.Confidence < 0.7, $"confidence {r.Confidence}");
    }

    [Fact]
    public async Task A_route_much_longer_than_the_straight_line_is_rejected()
    {
        var (vertices, points) = CutGapDrive();
        // A 3x detour (the shape is drawn straight; the reported length is what counts)
        var engine = new RouteOnlyEngine { Answer = q => SyntheticTrace.RouteAnswer(RouteOnlyEngine.Locations(q), 200, 3) };
        var r = await MatchAsync(points, PerCall(vertices), engine);

        Assert.Equal((1, 0), (r.Metrics.FillRejectedLength, r.Metrics.Filled));
        Assert.True(r.Confidence < 0.7, $"confidence {r.Confidence}");
        Assert.EndsWith("a 5.6 km straight jump that no road explains", r.Reason);
    }

    [Fact]
    public async Task A_route_over_the_distance_cap_is_not_asked_for()
    {
        var (vertices, points) = CutGapDrive();
        var engine = new RouteOnlyEngine();
        var r = await MatchAsync(points, PerCall(vertices), engine, o => o.GapFillMaxKm = 5);

        Assert.Empty(engine.Routes);
        Assert.Equal((1, 0), (r.Metrics.FillRejectedLength, r.Metrics.Filled));
    }

    [Fact]
    public async Task A_drive_with_no_matched_point_is_built_from_routes_at_a_lower_confidence()
    {
        var vertices = new List<int> { 0, 10, 20, 30, 40 };
        var points = SyntheticTrace.Points(vertices, 60);
        var engine = new RouteOnlyEngine();
        var r = await MatchAsync(points, _ => null, engine);

        Assert.Equal(4, engine.Routes.Count);
        Assert.True(r.Metrics.RoutedOnly);
        Assert.Equal((0, 4), (r.Metrics.Anchors, r.Metrics.Filled));
        Assert.Equal(PieceKinds.Inferred, Assert.Single(r.Pieces).Kind);
        Assert.Equal((0.6, RouteStatuses.LowConfidence), (r.Confidence, r.Status));
        Assert.NotNull(r.Polyline);
        Assert.EndsWith("no recorded point matched a road; the line is routed between them", r.Reason);
    }

    [Fact]
    public async Task A_drive_whose_every_leg_exceeds_the_split_is_routed_point_to_point()
    {
        // 6.1 km every 5 min: no call at all (no_call), every leg routed
        var vertices = new List<int> { 0, 55, 110, 165 };
        var points = SyntheticTrace.Points(vertices, 300);
        var engine = new RouteOnlyEngine { Answer = q => SyntheticTrace.RouteAnswer(RouteOnlyEngine.Locations(q), 90) };
        var r = await MatchAsync(points, _ => throw new InvalidOperationException("no call is planned"), engine, o => o.GapFillRoutedOnlyWeight = 0.75);

        Assert.Equal((0, 3, 3), (r.Metrics.Calls, r.Metrics.CutGaps, r.Metrics.Filled));
        Assert.Equal((0.75, RouteStatuses.Matched), (r.Confidence, r.Status));

        // Off: failed as before
        var off = await MatchAsync(points, _ => null, new RouteOnlyEngine(), o => o.GapFill = false);
        Assert.Equal(RouteStatuses.Failed, off.Status);
        Assert.StartsWith("no_call", off.Reason);
    }

    [Fact]
    public async Task A_lone_route_timeout_leaves_a_gap_and_filling_goes_on_without_the_breaker()
    {
        // Two stretches: the cut gap and an engine break after it; the long one times out
        var (vertices, points) = CutGapDrive();
        Func<int, SyntheticTrace.Label> label = i => i switch
        {
            6 => M(vertices[i]) with { Begin = true },
            7 => M(vertices[i]) with { End = true },
            _ => M(vertices[i])
        };
        var engine = new RouteOnlyEngine();
        engine.Answer = q => engine.Routes.Count == 1
            ? throw new EngineUnavailableException("the engine did not answer within 5 s", true)
            : SyntheticTrace.RouteAnswer(RouteOnlyEngine.Locations(q), 50);
        var o = new RoutesOptions { BreakerFailures = 1 };
        var breaker = new EngineBreaker(Options.Create(o), TimeProvider.System);

        var r = await MatchAsync(points, PerCall(vertices, label), engine, x => x.BreakerFailures = 1, breaker);

        Assert.Equal(2, engine.Routes.Count);
        Assert.Equal((2, 1, 1, 0), (r.Metrics.Stretches, r.Metrics.Filled, r.Metrics.FillTimeouts, r.Metrics.FillDeferred));
        Assert.NotNull(r.Polyline); // the record is stored, not failed for the engine
        Assert.Contains(r.Pieces, p => p.Kind == PieceKinds.Gap);
        Assert.Equal((0, false), (breaker.ConsecutiveFailures, breaker.IsOpen));
    }

    [Fact]
    public async Task Route_timeouts_in_a_row_count_once_for_the_breaker_and_stop_the_record_s_filling()
    {
        var (points, answer) = BrokenEveryLeg(6);
        var engine = new RouteOnlyEngine { Answer = _ => throw new EngineUnavailableException("the engine did not answer within 5 s", true) };
        var o = new RoutesOptions { BreakerFailures = 3 };
        var breaker = new EngineBreaker(Options.Create(o), TimeProvider.System);

        var r = await MatchAsync(points, answer, engine, x => x.BreakerFailures = 3, breaker);

        Assert.Equal(3, engine.Routes.Count);
        Assert.Equal((0, 3, 3), (r.Metrics.Filled, r.Metrics.FillTimeouts, r.Metrics.FillDeferred));
        Assert.Equal((1, false), (breaker.ConsecutiveFailures, breaker.IsOpen));
        Assert.NotNull(r.Polyline);
    }

    [Fact]
    public async Task A_route_that_turns_back_near_an_end_is_rejected_and_a_straight_one_accepted()
    {
        var (vertices, points) = CutGapDrive();
        // Leaves the anchor the wrong way for 200 m, turns back, then drives on
        var engine = new RouteOnlyEngine
        {
            Answer = q =>
            {
                var at = RouteOnlyEngine.Locations(q);
                var back = (at[0].Lat - 0.0018, at[0].Lon);
                var straight = SyntheticTrace.RouteAnswer(at, 50);
                var fill = GapFiller.Parse(straight.RootElement)!;
                var shape = new List<(double Lat, double Lon)> { at[0], back }.Concat(fill.Shape).ToList();
                return JsonDocument.Parse(new JsonObject
                {
                    ["trip"] = new JsonObject
                    {
                        ["legs"] = new JsonArray(new JsonObject { ["shape"] = SyntheticTrace.Encode(shape, 6) }),
                        ["summary"] = new JsonObject { ["length"] = (fill.LengthM + 400) / 1000, ["time"] = fill.TimeS + 30 }
                    }
                }.ToJsonString());
            }
        };
        var r = await MatchAsync(points, PerCall(vertices), engine);

        Assert.Equal((0, 0, 1), (r.Metrics.Filled, r.Metrics.FillRejectedLength, r.Metrics.FillRejectedReversal));
        // Asked once more without headings: the same U-turn, still rejected
        Assert.Equal((2, 1), (engine.Routes.Count, r.Metrics.FillRetried));
        Assert.Contains(r.Pieces, p => p.Kind == PieceKinds.Gap);

        var ok = await MatchAsync(points, PerCall(vertices), new RouteOnlyEngine());
        Assert.Equal(1, ok.Metrics.Filled);
    }

    [Fact]
    public void A_reversal_is_found_near_either_end_but_not_in_the_middle_or_through_a_roundabout()
    {
        List<(double Lat, double Lon)> Line(params double[] lats) => lats.Select(l => (31.5 + l, 34.8)).ToList();
        Assert.False(GapFiller.Reverses(Line(0, 0.01, 0.02)));
        Assert.True(GapFiller.Reverses(Line(0, -0.002, 0.02))); // 220 m back at the start
        Assert.True(GapFiller.Reverses(Line(0, 0.02, 0.018))); // 220 m back at the end
        Assert.False(GapFiller.Reverses(Line(0, 0.01, 0.005, 0.02))); // a long way round in the middle
        Assert.False(GapFiller.Reverses(Line(0, -0.005, 0.02))); // 550 m back is not near the end
        // A 30 m roundabout passed straight through, 100 m after the start
        var round = new List<(double Lat, double Lon)> { (31.5, 34.8), (31.5009, 34.8) };
        for (var a = -90; a <= 90; a += 15)
        {
            var rad = a * Math.PI / 180;
            round.Add((31.50105 + 0.000135 * Math.Sin(rad), 34.8 + 0.000158 * Math.Cos(rad)));
        }

        round.Add((31.51, 34.8));
        Assert.False(GapFiller.Reverses(round));
    }

    /// <summary>A shape from moves in metres (east, north) from 31.5 N 34.8 E</summary>
    private static List<(double Lat, double Lon)> Path(IEnumerable<(double E, double N)> metres) =>
        metres.Select(m => (31.5 + m.N / 111_320.0, 34.8 + m.E / 94_900.0)).ToList();

    /// <summary>Points on a circle round (cx, cy) of radius r, from angle a0 to a1 (degrees, counterclockwise from east)</summary>
    private static IEnumerable<(double E, double N)> Arc(double cx, double cy, double r, double a0, double a1)
    {
        var steps = (int)Math.Ceiling(Math.Abs(a1 - a0) / 10);
        for (var k = 0; k <= steps; k++)
        {
            var a = (a0 + (a1 - a0) * k / steps) * Math.PI / 180;
            yield return (cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }
    }

    [Fact]
    public void A_motorway_loop_ramp_is_not_a_reversal_but_a_back_track_is()
    {
        // A cloverleaf loop: 100 m south, a 270 degree loop (radius 60 m) to the right,
        // then east across the way in at a right angle (under the bridge) for 2 km
        var clover = Path(new[] { (0.0, 0.0) }.Concat(Arc(-60, -100, 60, 0, -270)).Append((2000.0, -40.0)));
        Assert.True(GapFiller.Turn(GapFiller.Bearing(clover[0], clover[1]), GapFiller.Bearing(clover[^3], clover[^2])) > 80);
        Assert.False(GapFiller.Reverses(clover));
        Assert.False(GapFiller.Reverses(Enumerable.Reverse(clover).ToList())); // the same loop at the end

        // A loop ramp turning 180 degrees (radius 90 m) onto the road the other way, 180 m
        // from the way in (the live Route 4 drive: routes.v2 rejected it)
        var ramp = Path(new[] { (0.0, 0.0) }.Concat(Arc(-90, -50, 90, 0, -180)).Append((-180.0, 5000.0)));
        Assert.False(GapFiller.Reverses(ramp));
        Assert.False(GapFiller.Reverses(Enumerable.Reverse(ramp).ToList()));

        // A U-turn across a 30 m median, 200 m out: back the other way beside the way out
        var median = Path(new[] { (0.0, 0.0), (0.0, -200.0) }.Concat(Arc(-15, -200, 15, 0, -180)).Append((-30.0, 3000.0)));
        Assert.True(GapFiller.Reverses(median));
        Assert.True(GapFiller.Reverses(Enumerable.Reverse(median).ToList()));
    }

    /// <summary>A route answer along <paramref name="shape"/> at 50 km/h</summary>
    private static JsonDocument ShapeAnswer(List<(double Lat, double Lon)> shape)
    {
        var lengthM = Enumerable.Range(1, shape.Count - 1).Sum(i => Geo.Haversine(shape[i - 1].Lat, shape[i - 1].Lon, shape[i].Lat, shape[i].Lon));
        return JsonDocument.Parse(new JsonObject
        {
            ["trip"] = new JsonObject
            {
                ["legs"] = new JsonArray(new JsonObject { ["shape"] = SyntheticTrace.Encode(shape, 6) }),
                ["summary"] = new JsonObject { ["length"] = lengthM / 1000, ["time"] = lengthM / (50 / 3.6) }
            }
        }.ToJsonString());
    }

    [Fact]
    public async Task A_headed_route_rejected_for_its_length_is_retried_once_without_headings()
    {
        var (vertices, points) = CutGapDrive();
        // With headings the engine leaves the wrong way round a 10 km loop; without, the road
        var engine = new RouteOnlyEngine
        {
            Answer = q =>
            {
                var at = RouteOnlyEngine.Locations(q);
                if (q["locations"]![0]!["heading"] == null)
                {
                    return SyntheticTrace.RouteAnswer(at, 50);
                }

                const double east = 5000 / 94900.0;
                return ShapeAnswer(new List<(double Lat, double Lon)> { at[0], (at[0].Lat, at[0].Lon + east), (at[1].Lat, at[1].Lon + east), at[1] });
            }
        };
        var r = await MatchAsync(points, PerCall(vertices), engine);

        Assert.Equal(2, engine.Routes.Count);
        Assert.NotNull(engine.Routes[0]["locations"]![0]!["heading"]);
        Assert.Null(engine.Routes[1]["locations"]![0]!["heading"]);
        Assert.Null(engine.Routes[1]["locations"]![1]!["heading"]);
        Assert.Equal((1, 1, 0), (r.Metrics.Filled, r.Metrics.FillRetried, r.Metrics.FillRejectedLength));
        Assert.Equal(RouteStatuses.Matched, r.Status);

        // The retry counts against the per-drive cap: with one call, no retry
        var capped = await MatchAsync(points, PerCall(vertices), engine = new RouteOnlyEngine { Answer = engine.Answer }, o => o.GapFillMaxCallsPerDrive = 1);
        Assert.Single(engine.Routes);
        Assert.Equal((0, 0, 1), (capped.Metrics.Filled, capped.Metrics.FillRetried, capped.Metrics.FillRejectedLength));

        // Without headings there is nothing to retry
        var plain = await MatchAsync(points, PerCall(vertices), engine = new RouteOnlyEngine(), o => o.GapFillHeadingTolerance = 0);
        Assert.Single(engine.Routes);
        Assert.Equal((1, 0), (plain.Metrics.Filled, plain.Metrics.FillRetried));
    }

    [Fact]
    public async Task A_route_through_a_loop_ramp_fills_the_stretch()
    {
        var (vertices, points) = CutGapDrive();
        // Leaves the anchor by a 180 degree loop ramp (radius 90 m) and drives on to the next one
        var engine = new RouteOnlyEngine
        {
            Answer = q =>
            {
                var at = RouteOnlyEngine.Locations(q);
                var loop = Arc(-90, 0, 90, 0, -180).Select(m => (at[0].Lat + m.N / 111_320.0, at[0].Lon + m.E / 94_900.0)).ToList();
                return ShapeAnswer(loop.Append((at[1].Lat, at[0].Lon - 180 / 94_900.0)).Append(at[1]).ToList());
            }
        };
        var r = await MatchAsync(points, PerCall(vertices), engine);

        Assert.Equal((1, 0, 0), (r.Metrics.Filled, r.Metrics.FillRejectedReversal, r.Metrics.FillRetried));
        Assert.Single(engine.Routes);
    }

    [Fact]
    public async Task Each_anchor_carries_the_matched_shape_s_heading_to_the_route()
    {
        var (vertices, points) = CutGapDrive();
        var engine = new RouteOnlyEngine();
        await MatchAsync(points, PerCall(vertices), engine);

        var locations = Assert.Single(engine.Routes)["locations"]!.AsArray();
        // The synthetic road runs due north
        Assert.Equal((0.0, 45.0), ((double)locations[0]!["heading"]!, (double)locations[0]!["heading_tolerance"]!));
        Assert.Equal(0.0, (double)locations[1]!["heading"]!);

        var off = await MatchAsync(points, PerCall(vertices), engine = new RouteOnlyEngine(), o => o.GapFillHeadingTolerance = 0);
        Assert.Null(Assert.Single(engine.Routes)["locations"]!.AsArray()[0]!["heading"]);
        Assert.Equal(1, off.Metrics.Filled);
    }

    [Fact]
    public async Task A_filled_detour_does_not_count_against_the_observed_part_s_plausibility()
    {
        var (vertices, points) = CutGapDrive();
        // The road round the jump goes 3 km east, north, and 3 km back west (within 2.5 × + slack)
        var engine = new RouteOnlyEngine
        {
            Answer = q =>
            {
                var at = RouteOnlyEngine.Locations(q);
                const double east = 3000 / 94900.0;
                var shape = new List<(double Lat, double Lon)> { at[0], (at[0].Lat, at[0].Lon + east), (at[1].Lat, at[1].Lon + east), at[1] };
                var lengthM = Enumerable.Range(1, 3).Sum(i => Geo.Haversine(shape[i - 1].Lat, shape[i - 1].Lon, shape[i].Lat, shape[i].Lon));
                return JsonDocument.Parse(new JsonObject
                {
                    ["trip"] = new JsonObject
                    {
                        ["legs"] = new JsonArray(new JsonObject { ["shape"] = SyntheticTrace.Encode(shape, 6) }),
                        ["summary"] = new JsonObject { ["length"] = lengthM / 1000, ["time"] = lengthM / (120 / 3.6) }
                    }
                }.ToJsonString());
            }
        };
        var r = await MatchAsync(points, PerCall(vertices), engine);

        Assert.Equal(1, r.Metrics.Filled);
        Assert.True(r.LengthM / r.Metrics.TrackM > 1.7, $"{r.LengthM} / {r.Metrics.TrackM}");
        Assert.Equal((1.0, 1.0), (r.Metrics.LengthRatio, r.Metrics.Plausibility));
        Assert.Equal(RouteStatuses.Matched, r.Status);
    }

    [Fact]
    public async Task A_single_anchor_scores_no_lower_than_none()
    {
        // Cut gaps all round; the 5-point middle part matches only its first point
        var vertices = new List<int> { 0, 55, 110, 112, 114, 116, 118, 173 };
        var points = At(vertices, new double[] { 0, 300, 600, 660, 720, 780, 840, 1140 });
        Func<TraceCall, JsonDocument?> one = PerCall(vertices, i => i == 2 ? M(vertices[i]) : new SyntheticTrace.Label("unmatched", vertices[i]));
        var single = await MatchAsync(points, one, new RouteOnlyEngine { Answer = q => SyntheticTrace.RouteAnswer(RouteOnlyEngine.Locations(q), 90) });
        var none = await MatchAsync(points, _ => null, new RouteOnlyEngine { Answer = q => SyntheticTrace.RouteAnswer(RouteOnlyEngine.Locations(q), 90) });

        Assert.Equal((1, 5), (single.Metrics.Anchors, single.Metrics.PointsSent));
        Assert.False(single.Metrics.RoutedOnly);
        Assert.True(none.Metrics.RoutedOnly);
        // Not 1/5: the accepted routes carry the routed-only weight
        Assert.Equal(0.6, single.Metrics.AnchorQuality);
        Assert.Equal(0.6, none.Metrics.AnchorQuality);
    }

    [Fact]
    public async Task A_routed_only_drive_joins_a_point_far_off_the_road_by_a_gap()
    {
        var vertices = new List<int> { 0, 10, 20, 30, 40 };
        var points = SyntheticTrace.Points(vertices, 60);
        // The road runs 50 m east of the recorded points
        var engine = new RouteOnlyEngine
        {
            Answer = q => SyntheticTrace.RouteAnswer(RouteOnlyEngine.Locations(q).Select(p => (p.Lat, p.Lon + 0.00053)).ToList(), 50)
        };
        var r = await MatchAsync(points, _ => null, engine);

        Assert.True(r.Metrics.RoutedOnly);
        Assert.Equal(4, r.Metrics.Filled);
        Assert.Contains(r.Pieces, p => p.Kind == PieceKinds.Gap);
        Assert.InRange(r.Metrics.GapM, 300, 500); // 8 connectors of ~50 m
        Assert.Contains(r.Pieces, p => p.Kind == PieceKinds.Inferred);
    }

    [Fact]
    public async Task An_engine_that_cannot_be_reached_during_filling_makes_the_record_wait()
    {
        var (vertices, points) = CutGapDrive();
        var engine = new RouteOnlyEngine { Answer = _ => throw new EngineUnavailableException("connection refused", false, unreachable: true) };
        await Assert.ThrowsAsync<EngineUnavailableException>(() => MatchAsync(points, PerCall(vertices), engine));
    }

    /// <summary>
    /// A synthetic replica of a live Timeline drive (no real coordinates): 17 points,
    /// about 16 km in 29 min, one pre-split cut gap of 5.1 km in 240 s, one engine break,
    /// the first and last points unmatched, two points 70 m off the road
    /// </summary>
    private static (List<int> Vertices, List<RoutePoint> Points, Func<TraceCall, JsonDocument?> Answer) Replica()
    {
        var vertices = new List<int> { 0, 4, 8, 12, 17, 22, 68, 75, 82, 89, 96, 103, 110, 118, 126, 134, 142 };
        var seconds = new List<double>();
        for (var i = 0; i < vertices.Count; i++)
        {
            seconds.Add(i <= 5 ? i * 100 : 740 + (i - 6) * 100);
        }

        var answer = PerCall(vertices, i => i switch
        {
            0 or 16 => new SyntheticTrace.Label("unmatched", vertices[i]),
            4 or 14 => M(vertices[i]) with { FromTrace = 70 },
            8 => M(vertices[i]) with { Begin = true },
            9 => M(vertices[i]) with { End = true },
            _ => M(vertices[i])
        });
        return (vertices, At(vertices, seconds), answer);
    }

    [Fact]
    public async Task The_replica_drive_scores_matched_once_its_gaps_are_filled()
    {
        var (_, points, answer) = Replica();
        Assert.Equal(1740, (points[^1].T - points[0].T).TotalSeconds);
        Assert.InRange(Geo.Haversine(points[5].Lat, points[5].Lon, points[6].Lat, points[6].Lon), 5050, 5150);

        // routes.v1 behaviour: the straight 5 km jump and the break stay; below 0.7
        var before = await MatchAsync(points, answer, new RouteOnlyEngine(), o => o.GapFill = false);
        Assert.Equal((1, 1, 15, 17), (before.Metrics.CutGaps, before.Metrics.EngineBreaks, before.Metrics.Anchors, before.Metrics.PointsSent));
        Assert.True(before.Confidence < 0.7, $"confidence {before.Confidence}");
        Assert.Equal(RouteStatuses.LowConfidence, before.Status);

        // routes.v2: both stretches routed and accepted
        var engine = new RouteOnlyEngine();
        var after = await MatchAsync(points, answer, engine);
        Assert.Equal(2, engine.Routes.Count);
        Assert.Equal((2, 1, 1.0, 1.0), (after.Metrics.Filled, after.Metrics.BridgedBreaks, after.Metrics.Coverage, after.Metrics.Continuity));
        // Timeline points 70 m off are within the 100 m tolerance; 15 of 17 points matched
        Assert.Equal((0, 100.0), (after.Metrics.OffRoad, after.Metrics.AnchorToleranceM));
        Assert.Equal(0.882, after.Confidence);
        Assert.Equal(RouteStatuses.Matched, after.Status);
    }

    [Fact]
    public async Task The_replica_drive_stays_below_matched_when_no_road_fits_its_jump()
    {
        var (_, points, answer) = Replica();
        // The only road between the jump's ends is a long way round
        var engine = new RouteOnlyEngine
        {
            Answer = q =>
            {
                var at = RouteOnlyEngine.Locations(q);
                var far = Geo.Haversine(at[0].Lat, at[0].Lon, at[1].Lat, at[1].Lon) > 3000;
                return SyntheticTrace.RouteAnswer(at, 50, far ? 4 : 1);
            }
        };
        var r = await MatchAsync(points, answer, engine);

        Assert.Equal((1, 1), (r.Metrics.Filled, r.Metrics.FillRejectedLength));
        Assert.True(r.Confidence < 0.7, $"confidence {r.Confidence}");
    }

    [Fact]
    public void A_route_answer_is_read_in_metres_and_seconds()
    {
        using var doc = SyntheticTrace.RouteAnswer(new[] { SyntheticTrace.Vertex(0), SyntheticTrace.Vertex(10) }, 36);
        var fill = GapFiller.Parse(doc.RootElement)!;
        Assert.True(fill.Shape.Count >= 11);
        Assert.InRange(fill.LengthM, 1100, 1120);
        Assert.InRange(fill.TimeS, 110, 112);
        using var empty = JsonDocument.Parse("""{"trip":{"legs":[]}}""");
        Assert.Null(GapFiller.Parse(empty.RootElement));
    }

    [Theory]
    [InlineData(240, 5000, 200, 5100, GapFiller.Filled)]
    [InlineData(240, 5000, 505, 5100, GapFiller.RejectedTime)] // over 240 × 1.6 + 120
    [InlineData(1200, 5000, 290, 5100, GapFiller.Filled)] // a jam or a stop: over 1200 × 0.1
    [InlineData(1200, 5000, 110, 5100, GapFiller.RejectedTime)] // under 1200 × 0.1: the trip went elsewhere
    [InlineData(120, 300, 20, 400, GapFiller.Filled)] // short elapsed: no lower limit
    [InlineData(240, 1000, 100, 3100, GapFiller.RejectedLength)] // over 2.5 × 1000 + 500
    [InlineData(60, 100, 30, 440, GapFiller.Filled)] // a short stretch: 2.5 × 100 + max(100, 200)
    [InlineData(60, 100, 30, 460, GapFiller.RejectedLength)]
    [InlineData(60, 30, 30, 170, GapFiller.Filled)] // 2.5 × 30 + 100
    [InlineData(60, 30, 30, 180, GapFiller.RejectedLength)]
    [InlineData(4000, 59000, 2400, 61000, GapFiller.TooLong)] // over the 60 km cap
    public void The_acceptance_rule(double elapsedS, double straightM, double routeS, double routeM, string result)
    {
        var g = GapFillSettings.From(new RoutesOptions());
        var s = new GapStretch(0, 1, (0, 0), (0, 0), elapsedS, straightM, straightM, 0, false);
        Assert.Equal(result, GapFiller.Judge(s, new RouteFill(new() { (0, 0), (0, 0) }, routeM, routeS), g));
    }
}
