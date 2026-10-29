using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker;
using EchoRoutes.Worker.Matching;
using EchoRoutes.Worker.Valhalla;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace ContactsApi.Tests;

/// <summary>
/// Gap filling against a real Valhalla (VALHALLA_TEST_URL, default http://localhost:8002;
/// skipped without one), with the time per drive with and without it. The traces are
/// synthetic: the engine's own route between two public city centres, sampled every
/// 1 to 5 km like a sparse Timeline drive. No recorded data is used.
/// </summary>
public class RoutesGapFillEngineTests
{
    private readonly ITestOutputHelper _out;

    public RoutesGapFillEngineTests(ITestOutputHelper output) => _out = output;

    private sealed class Factory : IHttpClientFactory
    {
        private readonly string _url;
        public Factory(string url) => _url = url;
        public HttpClient CreateClient(string name) => new() { BaseAddress = new Uri(_url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>The engine's road between two places, and its time</summary>
    private static async Task<(List<(double Lat, double Lon)> Road, double TimeS)> RoadAsync(string url, (double, double) from, (double, double) to)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var body = new JsonObject
        {
            ["locations"] = new JsonArray(new JsonObject { ["lat"] = from.Item1, ["lon"] = from.Item2 }, new JsonObject { ["lat"] = to.Item1, ["lon"] = to.Item2 }),
            ["costing"] = "auto",
            ["directions_type"] = "none"
        };
        var response = await http.PostAsJsonAsync(url.TrimEnd('/') + "/route", body);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var trip = json.GetProperty("trip");
        return (ValhallaTrace.DecodePolyline(trip.GetProperty("legs")[0].GetProperty("shape").GetString()!, 6),
            trip.GetProperty("summary").GetProperty("time").GetDouble());
    }

    /// <summary>A point every 1, 2, 3, 4, 5, 1, 2… km along the road, timed at the road's average speed</summary>
    private static List<RoutePoint> Sparse(List<(double Lat, double Lon)> road, double timeS)
    {
        double total = 0;
        for (var i = 1; i < road.Count; i++)
        {
            total += Geo.Haversine(road[i - 1].Lat, road[i - 1].Lon, road[i].Lat, road[i].Lon);
        }

        var speed = total / timeS;
        var t0 = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var points = new List<RoutePoint> { new(t0, road[0].Lat, road[0].Lon) };
        double along = 0, acc = 0;
        var step = 1;
        for (var i = 1; i < road.Count; i++)
        {
            var leg = Geo.Haversine(road[i - 1].Lat, road[i - 1].Lon, road[i].Lat, road[i].Lon);
            along += leg;
            acc += leg;
            if (acc >= step * 1000 || i == road.Count - 1)
            {
                points.Add(new RoutePoint(t0.AddSeconds(along / speed), road[i].Lat, road[i].Lon));
                acc = 0;
                step = step % 5 + 1;
            }
        }

        return points;
    }

    /// <summary>One drive as MatchRunner does it: trace calls, then (when on) one /route per stretch</summary>
    private static async Task<(MatchResult Result, double Ms, int Routes, List<FillOutcome> Fills)> MatchAsync(string url, List<RoutePoint> points, bool gapFill)
    {
        var options = new RoutesOptions { EngineTimeoutSeconds = 30, GapFill = gapFill };
        var client = new ValhallaClient(new Factory(url), Options.Create(options));
        var breaker = new EngineBreaker(Options.Create(options), TimeProvider.System);
        var settings = MatchSettings.From(options);
        var profile = RouteQuality.ProfileFor("drive");
        var watch = Stopwatch.StartNew();
        var plan = TracePlanner.Plan(points, settings);
        var answers = new Dictionary<TraceCall, TraceResponse?>();
        foreach (var call in plan.SelectMany(s => s.Calls))
        {
            try
            {
                using var doc = await client.TraceAttributesAsync(ValhallaTrace.BuildRequest(points, call, "auto", "google-timeline", settings), default);
                answers[call] = ValhallaTrace.Parse(doc.RootElement);
            }
            catch (EngineRejectedException)
            {
                answers[call] = null;
            }
        }

        var sk = RouteAssembler.Build(points, plan, answers, profile);
        var stretches = RouteAssembler.Stretches(sk, settings.GapFill);
        var fills = await GapFiller.FillAsync(stretches, "auto", settings.GapFill, profile.MaxSpeedKmh, client,
            ex => breaker.Failure(ex.Message) | ex.Unreachable, breaker.Success, default);
        var result = RouteAssembler.Assemble(points, sk, fills, profile, "auto", settings.MaxStoredVertices,
            settings.AnchorToleranceFor("google-timeline", "auto"), settings.GapFill.RoutedOnlyWeight);
        watch.Stop();
        return (result, watch.Elapsed.TotalMilliseconds, stretches.Count, fills);
    }

    [EngineFact]
    public async Task A_sparse_drive_is_rebuilt_from_a_similar_one_both_ways()
    {
        var url = TestEngine.Url!;
        var options = new RoutesOptions { EngineTimeoutSeconds = 30 };
        var client = new ValhallaClient(new Factory(url), Options.Create(options));
        var breaker = new EngineBreaker(Options.Create(options), TimeProvider.System);
        var settings = MatchSettings.From(options);
        // Ramla to Modi'in on 423 and 431 (AD-38): the similar drive is the road itself
        var (there, timeS) = await RoadAsync(url, (31.9300, 34.8200), (31.9010, 34.9600));
        var (back, backS) = await RoadAsync(url, (31.9010, 34.9600), (31.9300, 34.8200));
        var twin = new SimilarCandidate(Guid.NewGuid(), new DateTime(2026, 9, 1, 7, 50, 0, DateTimeKind.Utc),
            Geo.EncodePolyline(there.Select(p => (Math.Round(p.Lat, 5), Math.Round(p.Lon, 5))).ToList()), false);

        foreach (var (road, s, candidate) in new[] { (there, timeS, twin), (back, backS, twin with { Reversed = true }) })
        {
            // Sparse, with a ~6 km stretch not recorded
            var points = Sparse(road, s);
            var cut = points.Take(4).Concat(points.Skip(6)).ToList();
            var plain = (await StageB.MatchAsync(cut, "drive", "auto", "google-timeline", settings, client, breaker, null, default)).Result;

            var rebuilt = await Rebuilder.SimilarAsync(cut, new[] { candidate }, "drive", "auto", "google-timeline", settings, client, breaker, default);

            Assert.NotNull(rebuilt);
            Assert.Equal(candidate.Reversed, rebuilt!.Basis!.Reversed);
            // The way back leaves the way there in places (another ramp, the other carriageway)
            Assert.True(rebuilt.Basis.Fit >= 0.8, $"fit {rebuilt.Basis.Fit}");
            Assert.True(rebuilt.Added > 0);
            Assert.Equal(RouteStatuses.Matched, rebuilt.Result.Status);
            // Gap filling alone already bridges this cut, so the plain match is as good or better here
            // (the panel says when a suggestion is no better than the current match); the rebuild
            // is capped by its basis's fit
            Assert.True(rebuilt.Result.Confidence >= Math.Min(0.8, plain.Confidence), $"{plain.Confidence} → {rebuilt.Result.Confidence}");
            Assert.True(rebuilt.Result.Confidence <= rebuilt.Basis.Fit);
            Assert.Equal(PieceKinds.Inferred, rebuilt.Result.Pieces.OrderByDescending(p => p.ToM - p.FromM).First().Kind);
            Assert.InRange(rebuilt.Result.LengthM / LineM(road), 0.95, 1.05);
        }
    }

    private static double LineM(List<(double Lat, double Lon)> line) =>
        line.Zip(line.Skip(1)).Sum(p => Geo.Haversine(p.First.Lat, p.First.Lon, p.Second.Lat, p.Second.Lon));

    [EngineFact]
    public async Task A_filled_stretch_is_counted_on_the_road_it_was_routed_on()
    {
        var url = TestEngine.Url!;
        // Ramla to Modi'in on 431 (AD-37); one stretch of ~6 km has no recorded point
        var (road, timeS) = await RoadAsync(url, (31.9300, 34.8200), (31.9010, 34.9600));
        var points = Sparse(road, timeS);
        var cut = points.Take(4).Concat(points.Skip(6)).ToList();

        var without = await MatchAsync(url, cut, false);
        var with = await MatchAsync(url, cut, true);

        Assert.Contains(with.Fills, f => f.Accepted);
        double On431(MatchResult r) => r.Roads!.Single(x => x.Name == "431").M;
        // The filled stretch was the gap: it now counts for 431
        Assert.True(On431(with.Result) > On431(without.Result) + 3_000, $"{On431(without.Result)} → {On431(with.Result)} m on 431");
        Assert.True(On431(with.Result) > 10_000);
    }

    [EngineTheory]
    [InlineData("Route 4", 31.6688, 34.5743, 31.8044, 34.6553)] // Ashkelon to Ashdod
    [InlineData("Route 6", 31.6100, 34.7642, 32.0956, 34.9566)] // Kiryat Gat to Rosh HaAyin
    [InlineData("Route 1", 32.0853, 34.7818, 31.7683, 35.2137)] // Tel Aviv to Jerusalem
    public async Task Gap_filling_raises_a_sparse_drive_and_its_cost_is_measured(string name, double fromLat, double fromLon, double toLat, double toLon)
    {
        var url = TestEngine.Url!;
        var (road, timeS) = await RoadAsync(url, (fromLat, fromLon), (toLat, toLon));
        var points = Sparse(road, timeS);
        // A pre-split gap as in the live Route 4 drive: drop the points of one ~6 km stretch
        var cut = points.Take(4).Concat(points.Skip(6)).ToList();

        // Warm the tile cache (not measured)
        var without = await MatchAsync(url, cut, false);
        var with = await MatchAsync(url, cut, true);
        var off = new List<double>();
        var on = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            without = await MatchAsync(url, cut, false);
            off.Add(without.Ms);
            with = await MatchAsync(url, cut, true);
            on.Add(with.Ms);
        }

        double Median(List<double> xs) => xs.OrderBy(x => x).ElementAt(xs.Count / 2);
        _out.WriteLine($"{name}: {cut.Count} points, {without.Result.Metrics.TrackM / 1000:0.0} km, {without.Result.Metrics.Calls} calls, " +
                       $"{with.Routes} /route; off {Median(off):0} ms conf {without.Result.Confidence}; on {Median(on):0} ms conf {with.Result.Confidence} " +
                       $"(filled {with.Result.Metrics.Filled}, gap {with.Result.Metrics.GapM:0} m)");

        Assert.True(with.Result.Confidence >= without.Result.Confidence, $"{with.Result.Confidence} < {without.Result.Confidence}");
        Assert.True(with.Result.Confidence >= 0.8, $"confidence {with.Result.Confidence}: {with.Result.Reason}");
        Assert.Equal(RouteStatuses.Matched, with.Result.Status);
        Assert.True(with.Result.Metrics.Filled >= 1);
    }

    /// <summary>A point every <paramref name="stepM"/> along the road; every <paramref name="every"/>th one pushed <paramref name="offM"/> east</summary>
    private static List<RoutePoint> Noisy(List<(double Lat, double Lon)> road, double timeS, double stepM, int every, double offM)
    {
        double total = 0;
        for (var i = 1; i < road.Count; i++)
        {
            total += Geo.Haversine(road[i - 1].Lat, road[i - 1].Lon, road[i].Lat, road[i].Lon);
        }

        var speed = total / timeS;
        var t0 = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var points = new List<RoutePoint> { new(t0, road[0].Lat, road[0].Lon) };
        double along = 0, acc = 0;
        for (var i = 1; i < road.Count; i++)
        {
            var leg = Geo.Haversine(road[i - 1].Lat, road[i - 1].Lon, road[i].Lat, road[i].Lon);
            along += leg;
            acc += leg;
            if (acc >= stepM || i == road.Count - 1)
            {
                var off = points.Count % every == 0 && i < road.Count - 1 ? offM / 94_900 : 0;
                points.Add(new RoutePoint(t0.AddSeconds(along / speed), road[i].Lat, road[i].Lon + off));
                acc = 0;
            }
        }

        return points;
    }

    [EngineFact]
    public async Task A_noisy_dense_drive_makes_at_most_the_capped_number_of_route_calls()
    {
        var url = TestEngine.Url!;
        var (road, timeS) = await RoadAsync(url, (32.0853, 34.7818), (31.7683, 35.2137)); // Tel Aviv to Jerusalem
        var points = Noisy(road, timeS, 300, 3, 400);

        await MatchAsync(url, points, true);
        var off = new List<double>();
        var on = new List<double>();
        (MatchResult Result, double Ms, int Routes, List<FillOutcome> Fills) without = default, with = default;
        for (var i = 0; i < 3; i++)
        {
            without = await MatchAsync(url, points, false);
            off.Add(without.Ms);
            with = await MatchAsync(url, points, true);
            on.Add(with.Ms);
        }

        double Median(List<double> xs) => xs.OrderBy(x => x).ElementAt(xs.Count / 2);
        var m = with.Result.Metrics;
        var routed = m.Stretches - m.FillSkipped - m.FillDeferred - m.FillRejectedTime;
        _out.WriteLine($"Noisy Route 1: {points.Count} points, {m.TrackM / 1000:0.0} km, {m.Calls} calls, {m.EngineBreaks} breaks, " +
                       $"{m.Stretches} stretches ({m.FillSkipped} over the cap); off {Median(off):0} ms conf {without.Result.Confidence}; " +
                       $"on {Median(on):0} ms conf {with.Result.Confidence} (filled {m.Filled}, rejected {m.FillRejectedLength + m.FillRejectedTime}, " +
                       $"no route {m.FillNoRoute}, gap {m.GapM:0} m; {with.Result.Reason})");

        Assert.InRange(routed, 0, 20);
        Assert.True(with.Result.Confidence >= without.Result.Confidence, $"{with.Result.Confidence} < {without.Result.Confidence}");
    }

    /// <summary>The cumulative signed turn (degrees, clockwise positive) of a shape over 20 m steps from <paramref name="fromM"/> to <paramref name="toM"/></summary>
    private static double Turning(IReadOnlyList<(double Lat, double Lon)> shape, double fromM, double toM)
    {
        var cum = new double[shape.Count];
        for (var i = 1; i < shape.Count; i++)
        {
            cum[i] = cum[i - 1] + Geo.Haversine(shape[i - 1].Lat, shape[i - 1].Lon, shape[i].Lat, shape[i].Lon);
        }

        (double Lat, double Lon) At(double m)
        {
            var k = Math.Clamp(Array.FindIndex(cum, c => c >= m), 1, shape.Count - 1);
            var span = cum[k] - cum[k - 1];
            var t = span > 0 ? Math.Clamp((m - cum[k - 1]) / span, 0, 1) : 0;
            return (shape[k - 1].Lat + t * (shape[k].Lat - shape[k - 1].Lat), shape[k - 1].Lon + t * (shape[k].Lon - shape[k - 1].Lon));
        }

        double total = 0;
        double? last = null;
        for (var m = fromM; m + 20 <= Math.Min(toM, cum[^1]); m += 20)
        {
            var b = GapFiller.Bearing(At(m), At(m + 20));
            if (last.HasValue)
            {
                total += ((b - last.Value + 540) % 360) - 180;
            }

            last = b;
        }

        return total;
    }

    [EngineFact]
    public async Task A_cut_gap_that_leaves_a_motorway_by_a_loop_ramp_is_filled()
    {
        var url = TestEngine.Url!;
        // Rishon LeZion to Kiryat Malakhi: south on Route 4, off it by a loop ramp at an interchange
        var (road, timeS) = await RoadAsync(url, (31.9730, 34.7925), (31.7298, 34.7464));
        var cum = new double[road.Count];
        for (var i = 1; i < road.Count; i++)
        {
            cum[i] = cum[i - 1] + Geo.Haversine(road[i - 1].Lat, road[i - 1].Lon, road[i].Lat, road[i].Lon);
        }

        // The loop: more than 200 degrees of turning within 500 m
        var loopM = Enumerable.Range(0, (int)(cum[^1] / 20) - 25).Select(k => k * 20.0)
            .First(m => Math.Abs(Turning(road, m, m + 500)) > 200);

        // A point every 1 km, the last one before the gap where the road ahead turns
        // round by more than 150 degrees within 300 m (what routes.v2 called a U-turn), the
        // next one more than 5.2 km (the pre-split) beyond it in a straight line
        var speed = cum[^1] / timeS;
        var t0 = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        RoutePoint P(double m)
        {
            var k = Math.Clamp(Array.FindIndex(cum, c => c >= m), 1, road.Count - 1);
            return new RoutePoint(t0.AddSeconds(cum[k] / speed), road[k].Lat, road[k].Lon);
        }

        (double Lat, double Lon) Pos(double m)
        {
            var k = Math.Clamp(Array.FindIndex(cum, c => c >= m), 1, road.Count - 1);
            var t = cum[k] > cum[k - 1] ? Math.Clamp((m - cum[k - 1]) / (cum[k] - cum[k - 1]), 0, 1) : 0;
            return (road[k - 1].Lat + t * (road[k].Lat - road[k - 1].Lat), road[k - 1].Lon + t * (road[k].Lon - road[k - 1].Lon));
        }

        double Dir(double m) => GapFiller.Bearing(Pos(m), Pos(m + 50));
        var startM = Enumerable.Range(0, 70).Select(k => loopM - 300 + k * 10.0)
            .First(m => Enumerable.Range(1, 10).Any(x => GapFiller.Turn(Dir(m), Dir(m + x * 25)) > 160));
        var before = Enumerable.Range(0, (int)(startM / 1000)).Select(k => startM - k * 1000).Reverse().ToList();
        var gapStart = P(startM);
        var resume = startM + 100;
        while (resume < cum[^1] && Geo.Haversine(gapStart.Lat, gapStart.Lon, P(resume).Lat, P(resume).Lon) < 5200)
        {
            resume += 100;
        }

        var after = Enumerable.Range(0, 6).Select(k => resume + k * 1000).Where(m => m < cum[^1]).ToList();
        var points = before.Concat(after).Select(P).ToList();

        var (result, _, _, fills) = await MatchAsync(url, points, true);
        var jump = fills.Single(f => Geo.Haversine(f.Stretch.Start.Lat, f.Stretch.Start.Lon, gapStart.Lat, gapStart.Lon) < 200);
        _out.WriteLine($"loop at {loopM:0} m, gap from {startM:0} m; jump {jump.Stretch.StraightM:0} m -> {jump.Result}, route {jump.Route?.LengthM:0} m; " +
                       $"confidence {result.Confidence} {result.Status} ({result.Reason})");

        // The route really leaves by the loop (routes.v2 rejected it as a U-turn)
        Assert.NotNull(jump.Route);
        Assert.True(Math.Abs(Turning(jump.Route!.Shape, 0, 700)) > 180, "the route does not take the loop ramp");
        Assert.Equal(GapFiller.Filled, jump.Result);
        Assert.Equal(0, result.Metrics.FillRejectedReversal);
        Assert.True(result.Confidence >= 0.8, $"confidence {result.Confidence}: {result.Reason}");
        Assert.Equal(RouteStatuses.Matched, result.Status);
    }
}
