using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker;
using EchoRoutes.Worker.Matching;
using EchoRoutes.Worker.Valhalla;
using Microsoft.Extensions.Options;

namespace ContactsApi.Tests;

/// <summary>The engine of the integration tests (VALHALLA_TEST_URL, default http://localhost:8002), probed once</summary>
internal static class TestEngine
{
    private static readonly Lazy<string?> Probe = new(() =>
    {
        var url = Environment.GetEnvironmentVariable("VALHALLA_TEST_URL") ?? "http://localhost:8002";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var status = http.GetFromJsonAsync<JsonElement>(url.TrimEnd('/') + "/status").GetAwaiter().GetResult();
            return status.TryGetProperty("version", out _) ? url : null;
        }
        catch (Exception)
        {
            return null;
        }
    });

    /// <summary>The engine's URL; null when none answers</summary>
    public static string? Url => Probe.Value;

    public const string SkipReason = "No Valhalla answers at VALHALLA_TEST_URL (default http://localhost:8002)";
}

/// <summary>A fact that needs the routing engine: reported as skipped (not passed) without one</summary>
public sealed class EngineFactAttribute : FactAttribute
{
    public EngineFactAttribute()
    {
        if (TestEngine.Url == null)
        {
            Skip = TestEngine.SkipReason;
        }
    }
}

/// <summary>A theory that needs the routing engine: reported as skipped (not passed) without one</summary>
public sealed class EngineTheoryAttribute : TheoryAttribute
{
    public EngineTheoryAttribute()
    {
        if (TestEngine.Url == null)
        {
            Skip = TestEngine.SkipReason;
        }
    }
}

/// <summary>
/// Against a real Valhalla with the Israel tiles (VALHALLA_TEST_URL, default
/// http://localhost:8002); reported as skipped when none answers. The traces are synthetic: the
/// engine routes between two public city centres, and the route is sampled sparsely
/// like a Timeline drive. No recorded data is used.
/// </summary>
public class RoutesEngineIntegrationTests
{
    private sealed class Factory : IHttpClientFactory
    {
        private readonly string _url;
        public Factory(string url) => _url = url;
        public HttpClient CreateClient(string name) => new() { BaseAddress = new Uri(_url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(60) };
    }


    /// <summary>The engine's own road route between two places, as (lat, lon)</summary>
    private static async Task<List<(double Lat, double Lon)>> RoadAsync(string url, (double, double) from, (double, double) to)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var body = new JsonObject
        {
            ["locations"] = new JsonArray(new JsonObject { ["lat"] = from.Item1, ["lon"] = from.Item2 }, new JsonObject { ["lat"] = to.Item1, ["lon"] = to.Item2 }),
            ["costing"] = "auto"
        };
        var response = await http.PostAsJsonAsync(url.TrimEnd('/') + "/route", body);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return ValhallaTrace.DecodePolyline(json.GetProperty("trip").GetProperty("legs")[0].GetProperty("shape").GetString()!, 6);
    }

    /// <summary>A Timeline-like trace: a point every ~<paramref name="everyM"/> along the road, 40 s apart</summary>
    private static List<RoutePoint> Sample(List<(double Lat, double Lon)> road, double everyM)
    {
        var t0 = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var points = new List<RoutePoint> { new(t0, road[0].Lat, road[0].Lon) };
        double acc = 0;
        for (var i = 1; i < road.Count; i++)
        {
            acc += Geo.Haversine(road[i - 1].Lat, road[i - 1].Lon, road[i].Lat, road[i].Lon);
            if (acc >= everyM || i == road.Count - 1)
            {
                points.Add(new RoutePoint(t0.AddSeconds(40 * points.Count), road[i].Lat, road[i].Lon));
                acc = 0;
            }
        }

        return points;
    }

    private static async Task<MatchResult> MatchAsync(string url, List<RoutePoint> points)
    {
        var options = new RoutesOptions { EngineTimeoutSeconds = 30 };
        var client = new ValhallaClient(new Factory(url), Options.Create(options));
        var settings = MatchSettings.From(options);
        var plan = TracePlanner.Plan(points, settings);
        var answers = new Dictionary<TraceCall, TraceResponse?>();
        foreach (var call in plan.SelectMany(s => s.Calls))
        {
            using var doc = await client.TraceAttributesAsync(ValhallaTrace.BuildRequest(points, call, "auto", "google-timeline", settings), default);
            answers[call] = ValhallaTrace.Parse(doc.RootElement);
        }

        return RouteAssembler.Assemble(points, plan, answers, RouteQuality.ProfileFor("drive"), "auto", settings.MaxStoredVertices);
    }

    /// <summary>Distance from a point to a polyline, in metres (equirectangular, fine at these scales)</summary>
    private static double DistanceTo((double Lat, double Lon) p, List<(double Lat, double Lon)> line)
    {
        var best = double.MaxValue;
        var kx = Math.Cos(p.Lat * Math.PI / 180) * 111_320;
        const double ky = 110_540;
        for (var i = 1; i < line.Count; i++)
        {
            double ax = (line[i - 1].Lon - p.Lon) * kx, ay = (line[i - 1].Lat - p.Lat) * ky;
            double bx = (line[i].Lon - p.Lon) * kx, by = (line[i].Lat - p.Lat) * ky;
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
            var t = len2 > 0 ? Math.Clamp(-(ax * dx + ay * dy) / len2, 0, 1) : 0;
            best = Math.Min(best, Math.Sqrt(Math.Pow(ax + t * dx, 2) + Math.Pow(ay + t * dy, 2)));
        }

        return best;
    }

    [EngineTheory]
    [InlineData(32.0853, 34.7818, 31.7683, 35.2137)] // Tel Aviv to Jerusalem (Route 1)
    [InlineData(32.7940, 34.9896, 32.0853, 34.7818)] // Haifa to Tel Aviv (Routes 2 / 4)
    [InlineData(31.2518, 34.7913, 31.7683, 35.2137)] // Be'er Sheva to Jerusalem (Route 6 area)
    public async Task A_sparse_drive_is_matched_onto_the_road_it_followed(double fromLat, double fromLon, double toLat, double toLon)
    {
        var url = TestEngine.Url!;
        var road = await RoadAsync(url, (fromLat, fromLon), (toLat, toLon));
        var points = Sample(road, 800);

        var r = await MatchAsync(url, points);

        Assert.Equal(RouteStatuses.Matched, r.Status);
        Assert.True(r.Confidence >= 0.7, $"confidence {r.Confidence}");
        // At least 90% of the matched line lies within 30 m of the road driven
        var line = ValhallaTrace.DecodePolyline(r.Polyline!, 5);
        var near = line.Count(p => DistanceTo(p, road) <= 30) / (double)line.Count;
        Assert.True(near >= 0.9, $"{near:P0} of the line within 30 m");
        double roadM = 0;
        for (var i = 1; i < road.Count; i++)
        {
            roadM += Geo.Haversine(road[i - 1].Lat, road[i - 1].Lon, road[i].Lat, road[i].Lon);
        }

        Assert.InRange(r.LengthM / roadM, 0.95, 1.05);
        Assert.Contains(r.Pieces, p => p.Kind == PieceKinds.Inferred); // 800 m legs: routed between points
    }

    [EngineFact]
    public async Task A_matched_drive_lists_the_roads_it_followed_in_travel_order()
    {
        var url = TestEngine.Url!;
        // Ramla to Modi'in: 423, then most of the way on 431 (AD-37)
        var road = await RoadAsync(url, (31.9300, 34.8200), (31.9010, 34.9600));
        var points = Sample(road, 800);

        var r = await MatchAsync(url, points);

        Assert.Equal(RouteStatuses.Matched, r.Status);
        var on431 = Assert.Single(r.Roads!, x => x.Name == "431");
        Assert.True(on431.Ref);
        Assert.True(on431.M > 10_000, $"{on431.M} m on 431");
        Assert.True(r.Roads!.Single(x => x.Name == "423").FromM < on431.FromM);
        Assert.All(r.Roads!, x => Assert.True(x.M >= RouteRoads.MinM));

        // A stretch with nothing recorded and not filled (no gap filling here) is on no road
        var cut = points.Take(4).Concat(points.Skip(11).Select(p => p with { T = p.T.AddMinutes(2) })).ToList();
        var gapped = await MatchAsync(url, cut);
        var gap = gapped.Pieces.Where(p => p.Kind == PieceKinds.Gap).Sum(p => p.ToM - p.FromM);
        Assert.True(gap > 4_000, $"{gap} m of gap");
        Assert.InRange(gapped.Roads!.Single(x => x.Name == "431").M, on431.M - gap - 1_500, on431.M - gap + 1_500);
    }

    [EngineFact]
    public async Task A_long_gap_in_the_recording_stays_a_gap_and_the_rest_is_matched()
    {
        var url = TestEngine.Url!;

        var road = await RoadAsync(url, (32.0853, 34.7818), (31.7683, 35.2137));
        var points = Sample(road, 800);
        // Nothing recorded for ~12 km and 25 minutes in the middle
        var cut = points.Take(20).Concat(points.Skip(35).Select(p => p with { T = p.T.AddMinutes(25) })).ToList();

        var r = await MatchAsync(url, cut);

        Assert.Equal(RouteStatuses.Matched, r.Status);
        Assert.Equal((1, 0), (r.Metrics.CutGaps, r.Metrics.EngineBreaks));
        var gap = Assert.Single(r.Pieces, p => p.Kind == PieceKinds.Gap);
        Assert.InRange(gap.ToM - gap.FromM, 5_000, 20_000);
    }
}
