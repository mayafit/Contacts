using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker;
using EchoRoutes.Worker.Matching;
using EchoRoutes.Worker.Valhalla;
using Microsoft.Extensions.Options;

namespace ContactsApi.Tests;

/// <summary>
/// Hand-made Valhalla answers (no recorded data): a straight synthetic road along a
/// meridian, vertices 0.001° (~111 m) apart, every edge covering a few vertices
/// </summary>
internal static class SyntheticTrace
{
    public static readonly DateTime T0 = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    public const double Lon = 34.8;

    public static (double Lat, double Lon) Vertex(int i) => (31.5 + i * 0.001, Lon);

    /// <summary>Input points at the given shape vertices, <paramref name="seconds"/> apart</summary>
    public static List<RoutePoint> Points(IEnumerable<int> vertices, int seconds = 30) =>
        vertices.Select((v, k) => new RoutePoint(T0.AddSeconds(k * seconds), Vertex(v).Lat, Vertex(v).Lon)).ToList();

    /// <summary>A label as Valhalla writes it</summary>
    public record Label(string Type, int Vertex, int? Edge = null, double FromTrace = 0, bool Begin = false, bool End = false);

    /// <summary>
    /// An answer: shape vertices [first, first + n) (shape indexes from 0), edges of
    /// <paramref name="perEdge"/> vertices, the labels (at absolute vertices)
    /// </summary>
    public static JsonDocument Answer(int vertices, IEnumerable<Label> labels, int perEdge = 4, IEnumerable<(int Begin, int End)>? edges = null,
        int first = 0)
    {
        labels = labels.Select(l => l with { Vertex = l.Vertex - first }).ToList();
        var edgeList = edges?.ToList() ?? Enumerable.Range(0, (vertices - 1 + perEdge - 1) / perEdge)
            .Select(e => (e * perEdge, Math.Min((e + 1) * perEdge, vertices - 1))).ToList();
        var matched = new JsonArray();
        foreach (var l in labels)
        {
            var o = new JsonObject { ["type"] = l.Type };
            if (l.Type != "unmatched")
            {
                var edge = l.Edge ?? edgeList.FindIndex(e => l.Vertex >= e.Item1 && l.Vertex <= e.Item2);
                var (b, en) = edgeList[edge];
                o["edge_index"] = edge;
                o["distance_along_edge"] = en > b ? (double)(l.Vertex - b) / (en - b) : 0;
                o["distance_from_trace_point"] = l.FromTrace;
                o["lat"] = Vertex(l.Vertex + first).Lat;
                o["lon"] = Vertex(l.Vertex + first).Lon;
            }

            if (l.Begin)
            {
                o["begin_route_discontinuity"] = true;
            }

            if (l.End)
            {
                o["end_route_discontinuity"] = true;
            }

            matched.Add(o);
        }

        var body = new JsonObject
        {
            ["units"] = "kilometers",
            ["shape"] = Encode(Enumerable.Range(first, vertices).Select(Vertex), 6),
            ["edges"] = new JsonArray(edgeList.Select(e => (JsonNode)new JsonObject
            {
                ["id"] = 1000 + e.Item1, ["begin_shape_index"] = e.Item1, ["end_shape_index"] = e.Item2, ["length"] = 0.1
            }).ToArray()),
            ["matched_points"] = matched
        };
        return JsonDocument.Parse(body.ToJsonString());
    }

    /// <summary>
    /// A /route answer: the straight synthetic road between the locations, a vertex
    /// about every 111 m, at <paramref name="kmh"/>; <paramref name="lengthFactor"/>
    /// stretches the reported length (a detour the shape does not show)
    /// </summary>
    public static JsonDocument RouteAnswer(IReadOnlyList<(double Lat, double Lon)> locations, double kmh, double lengthFactor = 1)
    {
        var (a, b) = (locations[0], locations[^1]);
        var straight = Geo.Haversine(a.Lat, a.Lon, b.Lat, b.Lon);
        var n = Math.Max(1, (int)Math.Ceiling(straight / 111));
        var shape = Enumerable.Range(0, n + 1).Select(i => (a.Lat + (b.Lat - a.Lat) * i / n, a.Lon + (b.Lon - a.Lon) * i / n)).ToList();
        var lengthM = straight * lengthFactor;
        var body = new JsonObject
        {
            ["trip"] = new JsonObject
            {
                ["legs"] = new JsonArray(new JsonObject { ["shape"] = Encode(shape, 6) }),
                ["summary"] = new JsonObject { ["length"] = lengthM / 1000, ["time"] = lengthM / (kmh / 3.6) },
                ["units"] = "kilometers"
            }
        };
        return JsonDocument.Parse(body.ToJsonString());
    }

    public static string Encode(IEnumerable<(double Lat, double Lon)> points, int precision)
    {
        var f = Math.Pow(10, precision);
        var sb = new StringBuilder();
        long pl = 0, po = 0;
        foreach (var (lat, lon) in points)
        {
            long la = (long)Math.Round(lat * f), lo = (long)Math.Round(lon * f);
            Put(la - pl, sb);
            Put(lo - po, sb);
            pl = la;
            po = lo;
        }

        return sb.ToString();
    }

    private static void Put(long v, StringBuilder sb)
    {
        v = v < 0 ? ~(v << 1) : v << 1;
        while (v >= 0x20)
        {
            sb.Append((char)((0x20 | (v & 0x1f)) + 63));
            v >>= 5;
        }

        sb.Append((char)(v + 63));
    }

    public static MatchSettings Settings(Action<RoutesOptions>? change = null)
    {
        var o = new RoutesOptions();
        change?.Invoke(o);
        return MatchSettings.From(o);
    }

    /// <summary>Plans, answers every call with <paramref name="answer"/>, assembles</summary>
    public static MatchResult Match(List<RoutePoint> points, Func<TraceCall, JsonDocument?> answer, MatchSettings? s = null, string type = "drive")
    {
        s ??= Settings();
        var plan = TracePlanner.Plan(points, s);
        var answers = new Dictionary<TraceCall, TraceResponse?>();
        foreach (var call in plan.SelectMany(p => p.Calls))
        {
            using var doc = answer(call);
            answers[call] = doc == null ? null : ValhallaTrace.Parse(doc.RootElement);
        }

        return RouteAssembler.Assemble(points, plan, answers, RouteQuality.ProfileFor(type), "auto", s.MaxStoredVertices);
    }

    /// <summary>Pieces are contiguous from 0 to the stored line's length</summary>
    public static void AssertContiguous(MatchResult r)
    {
        Assert.NotEmpty(r.Pieces);
        Assert.Equal(0, r.Pieces[0].FromM, 1);
        for (var i = 1; i < r.Pieces.Count; i++)
        {
            Assert.Equal(r.Pieces[i - 1].ToM, r.Pieces[i].FromM, 1);
            Assert.NotEqual(r.Pieces[i - 1].Kind, r.Pieces[i].Kind);
        }

        Assert.Equal(r.LengthM, r.Pieces[^1].ToM, 0);
    }
}

/// <summary>Stage B, pure: planning the calls, the request, pieces and confidence per label case (AD-35 rules 4, 6, 7)</summary>
public class RouteMatchingTests
{
    private static readonly Func<int, SyntheticTrace.Label> M = v => new SyntheticTrace.Label("matched", v);

    [Fact]
    public void Matched_points_close_together_give_one_observed_piece_and_full_confidence()
    {
        var vertices = Enumerable.Range(0, 11).Select(i => i * 2).ToList(); // ~222 m legs
        var r = SyntheticTrace.Match(SyntheticTrace.Points(vertices), _ => SyntheticTrace.Answer(21, vertices.Select(M)));

        Assert.Equal((RouteStatuses.Matched, 1.0), (r.Status, r.Confidence));
        var piece = Assert.Single(r.Pieces);
        Assert.Equal(PieceKinds.Observed, piece.Kind);
        Assert.InRange(r.LengthM, 2214, 2234);
        Assert.Equal((11, 11, 0, 1), (r.Metrics.PointsSent, r.Metrics.Anchors, r.Metrics.EngineBreaks, r.Metrics.Calls));
        Assert.Equal(1, r.Metrics.ObservedShare);
        // Stored at precision 5: Google encoding of the shape, decodable by the Atlas reader
        var decoded = ValhallaTrace.DecodePolyline(r.Polyline!, 5);
        Assert.Equal(SyntheticTrace.Vertex(0).Lat, decoded[0].Lat, 5);
        Assert.Equal(SyntheticTrace.Vertex(20).Lat, decoded[^1].Lat, 5);
        Assert.Equal(SyntheticTrace.Encode(decoded, 5), r.Polyline);
    }

    [Fact]
    public void Interpolated_anchors_count_as_matched_and_long_legs_are_inferred()
    {
        // Legs of ~666 m (over the 500 m straight jump); every other point interpolated
        var vertices = new[] { 0, 6, 12, 18, 24 };
        var labels = vertices.Select((v, i) => new SyntheticTrace.Label(i % 2 == 1 ? "interpolated" : "matched", v));
        var r = SyntheticTrace.Match(SyntheticTrace.Points(vertices, 60), _ => SyntheticTrace.Answer(25, labels));

        Assert.Equal(1.0, r.Confidence);
        Assert.Equal(PieceKinds.Inferred, Assert.Single(r.Pieces).Kind);
        Assert.Equal(1, r.Metrics.InferredShare);
    }

    [Fact]
    public void Mixed_legs_alternate_observed_and_inferred_at_the_anchor_positions()
    {
        var vertices = new[] { 0, 2, 4, 12, 14 }; // 222, 222, 889, 222 m
        var r = SyntheticTrace.Match(SyntheticTrace.Points(vertices, 60), _ => SyntheticTrace.Answer(15, vertices.Select(M)));

        SyntheticTrace.AssertContiguous(r);
        Assert.Equal(new[] { PieceKinds.Observed, PieceKinds.Inferred, PieceKinds.Observed }, r.Pieces.Select(p => p.Kind));
        // The inferred piece runs from vertex 4 to vertex 12 along the line
        Assert.Equal(Geo.Haversine(31.5, 34.8, 31.504, 34.8), r.Pieces[1].FromM, 0);
        Assert.Equal(Geo.Haversine(31.5, 34.8, 31.512, 34.8), r.Pieces[1].ToM, 0);
    }

    [Fact]
    public void An_unmatched_point_makes_a_gap_that_counts_against_coverage_until_filled()
    {
        var vertices = Enumerable.Range(0, 10).Select(i => i * 2).ToList();
        var labels = vertices.Select((v, i) => i == 5 ? new SyntheticTrace.Label("unmatched", v) : M(v)).ToList();
        // Valhalla also flags the path around it; that is the same doubt, not counted twice
        labels[4] = labels[4] with { Begin = true };
        labels[6] = labels[6] with { End = true };
        var r = SyntheticTrace.Match(SyntheticTrace.Points(vertices), _ => SyntheticTrace.Answer(19, labels));

        SyntheticTrace.AssertContiguous(r);
        Assert.Equal(new[] { PieceKinds.Observed, PieceKinds.Gap, PieceKinds.Observed }, r.Pieces.Select(p => p.Kind));
        Assert.Equal((0.9, 0.9, 1.0, 0), (r.Metrics.MatchedShare, r.Metrics.AnchorQuality, r.Metrics.Continuity, r.Metrics.EngineBreaks));
        // routes.v2: unfilled, the 444 m gap of the 2 km line is the weakest factor
        Assert.Equal(0.778, r.Metrics.Coverage);
        Assert.Equal(0.778, r.Confidence);
        Assert.Equal(RouteStatuses.Matched, r.Status);
    }

    [Fact]
    public void An_engine_discontinuity_is_a_gap_and_lowers_continuity()
    {
        var vertices = Enumerable.Range(0, 10).Select(i => i * 2).ToList();
        var labels = vertices.Select(M).ToList();
        labels[3] = labels[3] with { Begin = true };
        labels[4] = labels[4] with { End = true };
        var r = SyntheticTrace.Match(SyntheticTrace.Points(vertices), _ => SyntheticTrace.Answer(19, labels));

        Assert.Equal(new[] { PieceKinds.Observed, PieceKinds.Gap, PieceKinds.Observed }, r.Pieces.Select(p => p.Kind));
        Assert.Equal((1, 0.85), (r.Metrics.EngineBreaks, r.Metrics.Continuity));
        Assert.Equal(0.85, r.Confidence);
    }

    [Fact]
    public void A_shape_break_between_edges_is_an_engine_break_too()
    {
        // Edge 1 ends at vertex 8, edge 2 starts at vertex 9: Valhalla restarted the path
        var vertices = new[] { 0, 2, 4, 6, 8, 10, 12, 14 };
        var edges = new[] { (0, 4), (4, 8), (9, 12), (12, 14) };
        var labels = vertices.Select(v => M(v) with { Edge = v <= 4 ? (v == 4 ? 0 : 0) : v <= 8 ? 1 : v <= 12 ? 2 : 3 }).ToList();
        var r = SyntheticTrace.Match(SyntheticTrace.Points(vertices), _ => SyntheticTrace.Answer(15, labels, edges: edges));

        Assert.Equal(new[] { PieceKinds.Observed, PieceKinds.Gap, PieceKinds.Observed }, r.Pieces.Select(p => p.Kind));
        Assert.Equal(1, r.Metrics.EngineBreaks);
        Assert.Equal(0.85, r.Confidence);
    }

    [Fact]
    public void A_long_gap_is_cut_before_the_call_becomes_a_gap_and_is_not_counted()
    {
        // 20 minutes between point 4 and 5 (over the 15 min threshold): two calls
        var points = SyntheticTrace.Points(Enumerable.Range(0, 10).Select(i => i * 2));
        for (var i = 5; i < points.Count; i++)
        {
            points[i] = points[i] with { T = points[i].T.AddMinutes(20) };
        }

        var calls = new List<TraceCall>();
        var r = SyntheticTrace.Match(points, call =>
        {
            calls.Add(call);
            var vs = Enumerable.Range(call.Start, call.Count).Select(i => i * 2).ToList();
            // Each call's shape covers only its own stretch
            return SyntheticTrace.Answer(vs[^1] - vs[0] + 1, vs.Select(M), first: vs[0]);
        });

        Assert.Equal(new[] { new TraceCall(0, 5), new TraceCall(5, 5) }, calls);
        Assert.Contains(r.Pieces, p => p.Kind == PieceKinds.Gap);
        Assert.Equal((1, 0, 2, 1.0), (r.Metrics.CutGaps, r.Metrics.EngineBreaks, r.Metrics.Calls, r.Metrics.Continuity));
    }

    [Fact]
    public void A_trace_over_the_point_limit_is_split_into_calls_with_no_gap_and_no_break()
    {
        var vertices = Enumerable.Range(0, 12).Select(i => i * 2).ToList();
        var s = SyntheticTrace.Settings(o => o.MaxPointsPerCall = 5);
        var calls = new List<TraceCall>();
        var r = SyntheticTrace.Match(SyntheticTrace.Points(vertices), call =>
        {
            calls.Add(call);
            var vs = Enumerable.Range(call.Start, call.Count).Select(i => i * 2).ToList();
            return SyntheticTrace.Answer(vs[^1] - vs[0] + 1, vs.Select(M), first: vs[0]);
        }, s);

        Assert.All(calls, c => Assert.InRange(c.Count, 2, 5));
        Assert.Equal(12, calls.Sum(c => c.Count));
        Assert.Equal(PieceKinds.Observed, Assert.Single(r.Pieces).Kind);
        Assert.Equal((1.0, 0, 0), (r.Confidence, r.Metrics.EngineBreaks, r.Metrics.CutGaps));
    }

    [Fact]
    public void Splits_respect_the_kilometre_limit_and_never_leave_a_call_of_one_point()
    {
        var s = SyntheticTrace.Settings(o => o.MaxKmPerCall = 0.5); // ~2 legs of 222 m
        var plan = TracePlanner.Plan(SyntheticTrace.Points(Enumerable.Range(0, 7).Select(i => i * 2)), s);
        var calls = Assert.Single(plan).Calls;
        Assert.All(calls, c => Assert.True(c.Count >= 2));
        Assert.Equal(7, calls.Sum(c => c.Count));
        Assert.Equal(Enumerable.Range(0, 7), calls.SelectMany(c => Enumerable.Range(c.Start, c.Count)));
    }

    [Fact]
    public void Points_far_from_their_road_lower_proximity()
    {
        var vertices = Enumerable.Range(0, 10).Select(i => i * 2).ToList();
        var labels = vertices.Select((v, i) => M(v) with { FromTrace = i < 3 ? 80 : 10 });
        var r = SyntheticTrace.Match(SyntheticTrace.Points(vertices), _ => SyntheticTrace.Answer(19, labels));

        Assert.Equal((0.7, 3), (r.Metrics.Proximity, r.Metrics.OffRoad));
        Assert.Equal(RouteStatuses.Matched, r.Status);
    }

    [Fact]
    public void A_detour_much_longer_than_the_track_is_implausible()
    {
        // Two points 222 m apart, matched on a line ~3x longer than the straight leg
        var points = SyntheticTrace.Points(new[] { 0, 2 }, 120);
        var r = SyntheticTrace.Match(points, _ => SyntheticTrace.Answer(7, new[] { M(0), M(6) }));

        Assert.Equal(0, r.Metrics.Plausibility);
        Assert.Equal(RouteStatuses.Failed, r.Status);
        Assert.StartsWith("low confidence", r.Reason);
    }

    [Fact]
    public void A_line_faster_than_the_type_allows_is_implausible()
    {
        // 2.2 km in 10 s
        var points = SyntheticTrace.Points(new[] { 0, 20 }, 10);
        var r = SyntheticTrace.Match(points, _ => SyntheticTrace.Answer(21, new[] { M(0), M(20) }));
        Assert.Equal(0, r.Metrics.Plausibility);
    }

    [Fact]
    public void No_match_from_the_engine_fails_with_its_reason()
    {
        var r = SyntheticTrace.Match(SyntheticTrace.Points(new[] { 0, 2, 4 }), _ => null);
        Assert.Equal((RouteStatuses.Failed, 0.0), (r.Status, r.Confidence));
        Assert.Null(r.Polyline);
        Assert.StartsWith("no_match", r.Reason);
    }

    [Theory]
    [InlineData(0.7, RouteStatuses.Matched)]
    [InlineData(0.69, RouteStatuses.LowConfidence)]
    [InlineData(0.3, RouteStatuses.LowConfidence)]
    [InlineData(0.29, RouteStatuses.Failed)]
    public void Status_thresholds(double confidence, string status) => Assert.Equal(status, RouteStatuses.Of(confidence));

    [Fact]
    public void Pieces_survive_simplification_to_the_vertex_limit()
    {
        var vertices = Enumerable.Range(0, 200).ToList();
        var labels = vertices.Select(M).ToList();
        labels[100] = labels[100] with { Begin = true };
        labels[101] = labels[101] with { End = true };
        var s = SyntheticTrace.Settings(o => o.MaxStoredVertices = 10);
        var r = SyntheticTrace.Match(SyntheticTrace.Points(vertices, 10), _ => SyntheticTrace.Answer(200, labels), s);

        Assert.True(ValhallaTrace.DecodePolyline(r.Polyline!, 5).Count <= 10);
        SyntheticTrace.AssertContiguous(r);
        var gap = Assert.Single(r.Pieces, p => p.Kind == PieceKinds.Gap);
        Assert.InRange(gap.FromM, Geo.Haversine(31.5, 34.8, 31.6, 34.8) - 20, Geo.Haversine(31.5, 34.8, 31.6, 34.8) + 20);
    }

    [Theory]
    [InlineData("drive", null, "auto")]
    [InlineData("transit", "IN_BUS", "bus")]
    [InlineData("transit", "in_taxi", "auto")]
    [InlineData("transit", "IN_TRAIN", null)]
    [InlineData("transit", null, null)]
    [InlineData("walk", null, null)]
    [InlineData("flight", null, null)]
    public void Costing_follows_the_type_and_the_sources_own_transit_type(string type, string? sourceType, string? costing) =>
        Assert.Equal(costing, ValhallaTrace.CostingFor(type, sourceType));

    [Fact]
    public void The_request_carries_the_costing_options_timestamps_and_filters()
    {
        var points = SyntheticTrace.Points(new[] { 0, 2, 4 });
        var s = SyntheticTrace.Settings(o => o.SearchRadiusM = 50);
        var timeline = ValhallaTrace.BuildRequest(points, new TraceCall(0, 3), "bus", "google-timeline", s);
        var dense = ValhallaTrace.BuildRequest(points, new TraceCall(1, 2), "auto", "sports-tracker", s);

        Assert.Equal("bus", (string)timeline["costing"]!);
        Assert.Equal("map_snap", (string)timeline["shape_match"]!);
        Assert.True((bool)timeline["use_timestamps"]!);
        // gps_accuracy per source; search_radius never below it
        Assert.Equal((75.0, 75.0, 5000.0), ((double)timeline["trace_options"]!["gps_accuracy"]!,
            (double)timeline["trace_options"]!["search_radius"]!, (double)timeline["trace_options"]!["breakage_distance"]!));
        Assert.Equal((15.0, 50.0), ((double)dense["trace_options"]!["gps_accuracy"]!, (double)dense["trace_options"]!["search_radius"]!));
        var shape = dense["shape"]!.AsArray();
        Assert.Equal(2, shape.Count);
        Assert.Equal(new DateTimeOffset(points[1].T).ToUnixTimeSeconds(), (long)shape[0]!["time"]!);
        var attributes = timeline["filters"]!["attributes"]!.AsArray().Select(a => (string)a!).ToList();
        Assert.Contains("matched.begin_route_discontinuity", attributes);
        Assert.Contains("matched.end_route_discontinuity", attributes);
        Assert.Contains("matched.distance_along_edge", attributes);
        Assert.Contains("edge.begin_shape_index", attributes);
        Assert.Equal("include", (string)timeline["filters"]!["action"]!);
    }

    [Fact]
    public void Valhallas_out_of_range_edge_index_means_no_edge()
    {
        using var doc = JsonDocument.Parse("""
            {"shape":"_c`|@_c`|@","edges":[{"begin_shape_index":0,"end_shape_index":0}],
             "matched_points":[{"type":"matched","edge_index":4294967295,"lat":1,"lon":1}]}
            """);
        var label = Assert.Single(ValhallaTrace.Parse(doc.RootElement).Labels);
        Assert.False(label.IsAnchor);
    }
}

/// <summary>The engine client's hard timeout and error split, and the circuit breaker</summary>
public class EngineClientTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Answer = (_, _) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Answer(request, ct);
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public Factory(HttpMessageHandler h) => _h = h;
        public HttpClient CreateClient(string name) => new(_h, false) { BaseAddress = new Uri("http://127.0.0.1:8002/") };
    }

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task A_hung_engine_times_out_at_the_configured_limit()
    {
        var handler = new Handler { Answer = async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new HttpResponseMessage(); } };
        var client = new ValhallaClient(new Factory(handler), Options.Create(new RoutesOptions { EngineTimeoutSeconds = 1 }));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<EngineUnavailableException>(() => client.TraceAttributesAsync(new JsonObject(), default));

        Assert.True(ex.Timeout);
        Assert.InRange(watch.Elapsed.TotalSeconds, 0.9, 5);
    }

    [Fact]
    public async Task A_route_call_has_its_own_shorter_timeout()
    {
        var handler = new Handler { Answer = async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return new HttpResponseMessage(); } };
        var client = new ValhallaClient(new Factory(handler), Options.Create(new RoutesOptions { EngineTimeoutSeconds = 20, GapFillTimeoutSeconds = 1 }));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<EngineUnavailableException>(() => client.RouteAsync(new JsonObject(), default));

        Assert.True(ex.Timeout);
        Assert.InRange(watch.Elapsed.TotalSeconds, 0.9, 5);
        Assert.Contains("within 1 s", ex.Message);
    }

    [Fact]
    public async Task A_refusal_is_final_for_the_trace_and_a_5xx_is_the_engine_failing()
    {
        var handler = new Handler();
        var client = new ValhallaClient(new Factory(handler), Options.Create(new RoutesOptions()));

        handler.Answer = (_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error_code":443,"error":"Map Match algorithm failed to find path"}""")
        });
        var refused = await Assert.ThrowsAsync<EngineRejectedException>(() => client.TraceAttributesAsync(new JsonObject(), default));
        Assert.Equal(443, refused.Code);

        handler.Answer = (_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        Assert.False((await Assert.ThrowsAsync<EngineUnavailableException>(() => client.TraceAttributesAsync(new JsonObject(), default))).Timeout);

        handler.Answer = (_, _) => throw new HttpRequestException("connection refused");
        await Assert.ThrowsAsync<EngineUnavailableException>(() => client.TraceAttributesAsync(new JsonObject(), default));
    }

    [Fact]
    public async Task Only_the_configured_number_of_calls_run_at_once()
    {
        var inFlight = 0;
        var peak = 0;
        var handler = new Handler
        {
            Answer = async (_, ct) =>
            {
                peak = Math.Max(peak, Interlocked.Increment(ref inFlight));
                await Task.Delay(50, ct);
                Interlocked.Decrement(ref inFlight);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
        };
        var client = new ValhallaClient(new Factory(handler), Options.Create(new RoutesOptions { EngineConcurrency = 1 }));
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.TraceAttributesAsync(new JsonObject(), default)));
        Assert.Equal(1, peak);
    }

    [Fact]
    public void The_breaker_opens_after_N_failures_pauses_M_minutes_and_closes_on_success()
    {
        var clock = new Clock();
        var breaker = new EngineBreaker(Options.Create(new RoutesOptions { BreakerFailures = 3, BreakerPauseMinutes = 10 }), clock);

        breaker.Failure("timeout");
        breaker.Failure("timeout");
        Assert.False(breaker.IsOpen);
        breaker.Success();
        breaker.Failure("timeout");
        breaker.Failure("timeout");
        Assert.False(breaker.IsOpen);
        breaker.Failure("timeout");
        Assert.True(breaker.IsOpen);
        Assert.Equal(clock.Now.UtcDateTime.AddMinutes(10), breaker.OpenUntil);

        clock.Now = clock.Now.AddMinutes(11);
        Assert.False(breaker.IsOpen); // one call is let through
        breaker.Failure("still down");
        Assert.True(breaker.IsOpen);
        clock.Now = clock.Now.AddMinutes(11);
        breaker.Success();
        Assert.Equal((false, 0, (DateTime?)null), (breaker.IsOpen, breaker.ConsecutiveFailures, breaker.OpenUntil));
        Assert.Equal(2, breaker.Trips);
    }

    [Fact]
    public void Backoff_steps_come_from_configuration()
    {
        Assert.Equal(new[] { 1.0, 10, 60 }, new RoutesOptions().Backoff().Select(t => t.TotalMinutes));
        Assert.Equal(new[] { 2.0, 5 }, new RoutesOptions { BackoffMinutes = "2, 5" }.Backoff().Select(t => t.TotalMinutes));
    }
}
