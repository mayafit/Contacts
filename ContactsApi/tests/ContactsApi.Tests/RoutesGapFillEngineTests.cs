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
    private static async Task<(MatchResult Result, double Ms, int Routes)> MatchAsync(string url, List<RoutePoint> points, bool gapFill)
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
        return (result, watch.Elapsed.TotalMilliseconds, stretches.Count);
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
        (MatchResult Result, double Ms, int Routes) without = default, with = default;
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
}
