using System.Text.Json;
using System.Text.Json.Nodes;
using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker;
using EchoRoutes.Worker.Assess;
using EchoRoutes.Worker.Backend;
using EchoRoutes.Worker.Matching;
using EchoRoutes.Worker.Scheduling;
using EchoRoutes.Worker.Valhalla;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContactsApi.Tests;

/// <summary>
/// An engine stand-in: snaps every input point onto a "road" through the points
/// themselves (synthetic, no recorded data), or fails as told
/// </summary>
internal sealed class EchoEngine : IValhallaClient
{
    public readonly List<JsonObject> Requests = new();
    public Func<JsonObject, Exception?> Fail = _ => null;
    /// <summary>Called with the number of requests so far, after each one</summary>
    public Action<int>? OnCall;
    /// <summary>The /route requests</summary>
    public readonly List<JsonObject> Routes = new();
    /// <summary>Fails a /route request as told (null: answers a straight road at 80 km/h)</summary>
    public Func<JsonObject, Exception?> FailRoute = _ => null;

    public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<string?> VersionAsync(CancellationToken ct) => Task.FromResult<string?>("3.9.1");

    public Task<JsonDocument> TraceAttributesAsync(JsonObject request, CancellationToken ct)
    {
        Requests.Add(request);
        OnCall?.Invoke(Requests.Count);
        if (Fail(request) is { } ex)
        {
            return Task.FromException<JsonDocument>(ex);
        }

        var shape = request["shape"]!.AsArray().Select(p => ((double)p!["lat"]!, (double)p["lon"]!)).ToList();
        var n = shape.Count;
        var answer = new JsonObject
        {
            ["shape"] = SyntheticTrace.Encode(shape, 6),
            ["edges"] = new JsonArray(Enumerable.Range(0, n - 1)
                .Select(i => (JsonNode)new JsonObject { ["begin_shape_index"] = i, ["end_shape_index"] = i + 1 }).ToArray()),
            ["matched_points"] = new JsonArray(shape.Select((p, i) => (JsonNode)new JsonObject
            {
                ["type"] = "matched", ["edge_index"] = Math.Min(i, n - 2), ["distance_along_edge"] = i == n - 1 ? 1.0 : 0.0,
                ["distance_from_trace_point"] = 3.0, ["lat"] = p.Item1, ["lon"] = p.Item2
            }).ToArray())
        };
        return Task.FromResult(JsonDocument.Parse(answer.ToJsonString()));
    }

    public Task<JsonDocument> RouteAsync(JsonObject request, CancellationToken ct)
    {
        Routes.Add(request);
        if (FailRoute(request) is { } ex)
        {
            return Task.FromException<JsonDocument>(ex);
        }

        var locations = request["locations"]!.AsArray().Select(p => ((double)p!["lat"]!, (double)p["lon"]!)).ToList();
        return Task.FromResult(SyntheticTrace.RouteAnswer(locations, 80));
    }
}

/// <summary>Stage B against PostgreSQL (ATLAS_TEST_DB): rows, decisions, transit, backoff, breaker, yielding</summary>
[Collection("routes-db")]
public class RoutesMatchDbTests
{
    internal static ServiceProvider? Services(FakeRoutesBackend backend, IValhallaClient engine, EngineClientTests.Clock? clock = null,
        Action<RoutesOptions>? options = null, IPause? pause = null)
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        if (string.IsNullOrEmpty(cs))
        {
            return null;
        }

        var o = new RoutesOptions { GraphJsonPath = Path.Combine(Path.GetTempPath(), "no-graph-" + Guid.NewGuid()) };
        options?.Invoke(o);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<RoutesDbContext>(x => x.UseNpgsql(cs, n => n.MigrationsHistoryTable(RoutesDbContext.HistoryTable, RoutesDbContext.Schema)));
        services.AddSingleton<IRoutesBackend>(backend);
        services.AddSingleton(engine);
        services.AddSingleton(RegionCoverage.Load(Path.GetDirectoryName(RouteQualityTests.BundledPoly())!));
        services.AddSingleton<TimeProvider>(clock ?? (TimeProvider)TimeProvider.System);
        services.AddSingleton(Options.Create(o));
        services.AddSingleton<GraphInfoSource>();
        services.AddSingleton<EngineBreaker>();
        services.AddScoped<AssessRunner>();
        services.AddScoped<MatchRunner>();
        services.AddSingleton<WakeSignal>();
        services.AddSingleton(pause ?? new RecordingPause());
        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<RoutesDbContext>().Database.Migrate();
        return provider;
    }

    internal static RoutesScheduler Scheduler(IServiceProvider sp) =>
        new(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IRoutesBackend>(), sp.GetRequiredService<WakeSignal>(),
            sp.GetRequiredService<IPause>(), sp.GetRequiredService<IOptions<RoutesOptions>>(), NullLogger<RoutesScheduler>.Instance);

    private static async Task AssessAllAsync(IServiceProvider sp, string owner)
    {
        using var scope = sp.CreateScope();
        while ((await scope.ServiceProvider.GetRequiredService<AssessRunner>().RunFeedBatchAsync(owner, 500, false, default)).More)
        {
        }
    }

    private static async Task<MatchBatchResult> PendingBatchAsync(IServiceProvider sp, string owner, int limit = 25, Func<bool>? yield = null)
    {
        using var scope = sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MatchRunner>().RunPendingBatchAsync(owner, limit, yield ?? (() => false), default);
    }

    private static RoutesDbContext Db(IServiceProvider sp) => sp.CreateScope().ServiceProvider.GetRequiredService<RoutesDbContext>();

    [Fact]
    public async Task Drives_needing_improvement_are_matched_and_stored_with_the_read_time_values()
    {
        var backend = new FakeRoutesBackend();
        var engine = new EchoEngine();
        await using var sp = Services(backend, engine);
        if (sp == null)
        {
            return;
        }

        var owner = "match-" + Guid.NewGuid();
        var drives = backend.Add(owner, 3);
        await AssessAllAsync(sp, owner);
        using (var scope = sp.CreateScope())
        {
            Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<MatchRunner>().PendingCountAsync(owner, default));
        }

        var result = await PendingBatchAsync(sp, owner);
        Assert.Equal((3, 3, 3), (result.Seen, result.Processed, result.Tally.Matched));
        Assert.Equal(3, engine.Requests.Count);
        Assert.All(engine.Requests, r => Assert.Equal("auto", (string)r["costing"]!));
        Assert.All(engine.Requests, r => Assert.Equal(75.0, (double)r["trace_options"]!["gps_accuracy"]!));

        var row = await Db(sp).MatchedRoutes.AsNoTracking().SingleAsync(m => m.OwnerId == owner && m.ActivityId == drives[0].Id);
        Assert.Equal((RouteStatuses.Matched, "auto", "valhalla", "3.9.1", RouteQuality.RuleVersion),
            (row.Status, row.Costing, row.Engine, row.EngineVersion, row.RuleVersion));
        Assert.Equal((drives[0].UpdatedAt, drives[0].PointCount, "drive", "google-timeline|x0"),
            (row.SourceUpdatedAt, row.SourcePointCount, row.FusedType, row.SourceKey));
        Assert.Equal(RouteQuality.InputHash(drives[0].Points), row.InputHash);
        Assert.True(row.Confidence >= 0.7);
        Assert.NotNull(row.Polyline);
        var pieces = RouteChoice.Pieces(row);
        Assert.Equal(row.MatchedDistanceM!.Value, pieces[^1].ToM, 0);
        Assert.Contains(pieces, p => p.Kind == PieceKinds.Inferred); // 1.1 km legs
        Assert.True(RouteChoice.Drawable(row, owner, drives[0].UpdatedAt, drives[0].PointCount, "drive", null, new RouteSettingsDoc()));
        Assert.False(RouteChoice.Drawable(row, owner, drives[0].UpdatedAt.AddSeconds(1), drives[0].PointCount, "drive", null, new RouteSettingsDoc()));
        Assert.False(RouteChoice.Drawable(row, owner, drives[0].UpdatedAt, drives[0].PointCount, "drive", RouteDecisionKinds.Reject, new RouteSettingsDoc()));

        // Nothing waits any more, and nothing is matched twice
        engine.Requests.Clear();
        Assert.Equal(0, (await PendingBatchAsync(sp, owner)).Seen);
        Assert.Empty(engine.Requests);
    }

    [Fact]
    public async Task A_reject_decision_is_never_rematched_and_transit_needs_a_bus_or_taxi_source_type()
    {
        var backend = new FakeRoutesBackend();
        var engine = new EchoEngine();
        await using var sp = Services(backend, engine);
        if (sp == null)
        {
            return;
        }

        var owner = "rules-" + Guid.NewGuid();
        var records = backend.Add(owner, 5);
        var list = backend.Records[owner];
        list[1] = list[1] with { Type = "transit", SourceType = "IN_BUS" };
        list[2] = list[2] with { Type = "transit", SourceType = "in_taxi" };
        list[3] = list[3] with { Type = "transit", SourceType = null };
        list[4] = list[4] with { Type = "transit", SourceType = "IN_TRAIN" };
        var db = Db(sp);
        db.Decisions.Add(new RouteDecision { OwnerId = owner, SourceKey = records[0].SourceKey, Kind = RouteDecisionKinds.Reject });
        await db.SaveChangesAsync();
        await AssessAllAsync(sp, owner);

        var result = await PendingBatchAsync(sp, owner);

        Assert.Equal(2, result.Tally.Matched);
        Assert.Equal(new[] { "auto", "bus" }, engine.Requests.Select(r => (string)r["costing"]!).OrderBy(c => c));
        var matched = await db.MatchedRoutes.AsNoTracking().Where(m => m.OwnerId == owner).Select(m => m.ActivityId).ToListAsync();
        Assert.Equal(new[] { records[1].Id, records[2].Id }.OrderBy(x => x), matched.OrderBy(x => x));

        // Forced, by id: the reject still holds, unknown transit and rail are still not sent
        engine.Requests.Clear();
        using var scope = sp.CreateScope();
        var forced = await scope.ServiceProvider.GetRequiredService<MatchRunner>()
            .ImproveAsync(owner, new[] { list[0], list[3], list[4] }.ToList(), true, () => false, default);
        Assert.Equal((1, 2), (forced.Tally.KeptOriginal, forced.Tally.NotMatchable));
        Assert.Empty(engine.Requests);
    }

    [Fact]
    public async Task Engine_timeouts_back_off_1_10_60_minutes_and_then_fail_with_the_reason()
    {
        var backend = new FakeRoutesBackend();
        var engine = new EchoEngine { Fail = _ => new EngineUnavailableException("the engine did not answer within 15 s", true) };
        var clock = new EngineClientTests.Clock();
        await using var sp = Services(backend, engine, clock, o => o.BreakerFailures = 100);
        if (sp == null)
        {
            return;
        }

        var owner = "backoff-" + Guid.NewGuid();
        var drive = backend.Add(owner, 1)[0];
        await AssessAllAsync(sp, owner);
        var db = Db(sp);

        foreach (var (attempt, wait) in new[] { (1, 1), (2, 10), (3, 60) })
        {
            var r = await PendingBatchAsync(sp, owner);
            Assert.Equal(1, r.Tally.Retrying);
            var row = await db.MatchedRoutes.AsNoTracking().SingleAsync(m => m.OwnerId == owner);
            Assert.Equal((RouteStatuses.Failed, attempt, clock.Now.UtcDateTime.AddMinutes(wait)), (row.Status, row.Attempts, row.NextAttemptAt));
            Assert.Null(row.Polyline);
            // Not due yet: nothing waits
            Assert.Equal(0, (await PendingBatchAsync(sp, owner)).Seen);
            clock.Now = clock.Now.AddMinutes(wait);
        }

        var last = await PendingBatchAsync(sp, owner);
        Assert.Equal(1, last.Tally.Failed);
        var failed = await db.MatchedRoutes.AsNoTracking().SingleAsync(m => m.OwnerId == owner);
        Assert.Equal((4, (DateTime?)null), (failed.Attempts, failed.NextAttemptAt));
        Assert.Contains("did not answer", failed.Reason);
        Assert.Contains("gave up after 4 attempts", failed.Reason);
        clock.Now = clock.Now.AddDays(1);
        Assert.Equal(0, (await PendingBatchAsync(sp, owner)).Seen);

        // The track changes: a new attempt from scratch, and it works
        engine.Fail = _ => null;
        var list = backend.Records[owner];
        list[0] = list[0] with
        {
            UpdatedAt = list[0].UpdatedAt.AddMinutes(1),
            Points = list[0].Points.Select(p => p with { Lon = p.Lon + 0.0001 }).ToList()
        };
        await AssessAllAsync(sp, owner);
        Assert.Equal(1, (await PendingBatchAsync(sp, owner)).Tally.Matched);
        var ok = await db.MatchedRoutes.AsNoTracking().SingleAsync(m => m.OwnerId == owner);
        Assert.Equal((RouteStatuses.Matched, 0, (DateTime?)null), (ok.Status, ok.Attempts, ok.NextAttemptAt));
    }

    [Fact]
    public async Task The_breaker_stops_a_batch_so_a_dying_engine_does_not_burn_the_queue()
    {
        var backend = new FakeRoutesBackend();
        var engine = new EchoEngine { Fail = _ => new EngineUnavailableException("connection refused", false) };
        var clock = new EngineClientTests.Clock();
        await using var sp = Services(backend, engine, clock, o => { o.BreakerFailures = 3; o.BreakerPauseMinutes = 5; });
        if (sp == null)
        {
            return;
        }

        var owner = "breaker-" + Guid.NewGuid();
        backend.Add(owner, 10);
        await AssessAllAsync(sp, owner);

        var r = await PendingBatchAsync(sp, owner);
        Assert.True(r.Stopped);
        Assert.Equal(3, engine.Requests.Count);
        Assert.True(sp.GetRequiredService<EngineBreaker>().IsOpen);
        // While open, nothing is sent
        engine.Requests.Clear();
        var paused = await PendingBatchAsync(sp, owner);
        Assert.Equal((0, true), (paused.Processed, paused.Stopped));
        Assert.Empty(engine.Requests);

        clock.Now = clock.Now.AddMinutes(6);
        engine.Fail = _ => null;
        // The three that failed are due again too (1 min backoff): all ten are matched
        Assert.Equal(10, (await PendingBatchAsync(sp, owner)).Tally.Matched);
    }

    [Fact]
    public async Task A_map_snap_refusal_is_final_for_the_record_and_never_trips_the_breaker()
    {
        // Spike 12.1: about 2.7% of drives answer 400 E444 "map_snap failed"
        var backend = new FakeRoutesBackend();
        var engine = new EchoEngine
        {
            Fail = _ => new EngineRejectedException(444, "engine 400 (444): map_snap algorithm failed to snap the shape points to the correct shape"),
            // No road between the points either, so no routed-only line (AD-35 rule 7)
            FailRoute = _ => new EngineRejectedException(442, "engine 400 (442): No path could be found for input")
        };
        await using var sp = Services(backend, engine, null, o => o.BreakerFailures = 3);
        if (sp == null)
        {
            return;
        }

        var owner = "e444-" + Guid.NewGuid();
        backend.Add(owner, 6);
        await AssessAllAsync(sp, owner);

        var r = await PendingBatchAsync(sp, owner);
        Assert.False(r.Stopped);
        Assert.Equal(6, engine.Requests.Count);
        var breaker = sp.GetRequiredService<EngineBreaker>();
        Assert.False(breaker.IsOpen);
        Assert.Equal(0, breaker.ConsecutiveFailures);
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RoutesDbContext>();
        var rows = await db.MatchedRoutes.AsNoTracking().Where(m => m.OwnerId == owner).ToListAsync();
        Assert.Equal(6, rows.Count);
        // Final: failed with the engine's reason, no retry scheduled
        Assert.All(rows, m => Assert.Equal((RouteStatuses.Failed, 0, (DateTime?)null), (m.Status, m.Attempts, m.NextAttemptAt)));
        Assert.All(rows, m => Assert.Contains("444", m.Reason));

        engine.Requests.Clear();
        await PendingBatchAsync(sp, owner);
        Assert.Empty(engine.Requests);
    }

    [Fact]
    public async Task A_batch_stops_after_the_current_record_when_interactive_work_arrives()
    {
        var backend = new FakeRoutesBackend();
        var engine = new EchoEngine();
        await using var sp = Services(backend, engine);
        if (sp == null)
        {
            return;
        }

        var owner = "yield-" + Guid.NewGuid();
        backend.Add(owner, 6);
        await AssessAllAsync(sp, owner);
        var asked = 0;
        var r = await PendingBatchAsync(sp, owner, yield: () => ++asked >= 2);

        Assert.Equal((6, 2, true), (r.Seen, r.Processed, r.Stopped));
        Assert.Equal(2, engine.Requests.Count);
        // The rest still waits, durably
        using var scope = sp.CreateScope();
        Assert.Equal(4, await scope.ServiceProvider.GetRequiredService<MatchRunner>().PendingCountAsync(owner, default));
    }

    [Fact]
    public async Task An_interactive_improve_job_assesses_and_matches_with_interactive_backend_calls()
    {
        var backend = new FakeRoutesBackend();
        var engine = new EchoEngine();
        await using var sp = Services(backend, engine);
        if (sp == null)
        {
            return;
        }

        var owner = "fix-" + Guid.NewGuid();
        var drives = backend.Add(owner, 2);
        var db = Db(sp);
        db.Jobs.Add(new RouteJob
        {
            OwnerId = owner, Kind = RouteJobKinds.Improve, Priority = RouteJobPriorities.Interactive, Total = 1,
            Scope = JsonSerializer.Serialize(new JobScope(new List<Guid> { drives[1].Id }, null), new JsonSerializerOptions(JsonSerializerDefaults.Web))
        });
        await db.SaveChangesAsync();
        backend.BusyFor[owner] = TimeSpan.FromSeconds(5); // the Owner is using the map

        await Scheduler(sp).TurnAsync(default);

        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.OwnerId == owner);
        Assert.Equal((RouteJobStates.Done, 1), (job.State, job.Done));
        Assert.Equal(1, JsonDocument.Parse(job.Counts).RootElement.GetProperty("matched").GetInt32());
        Assert.True(Assert.Single(backend.Calls, c => c.Call == "tracks").Interactive);
        Assert.True(await db.MatchedRoutes.AnyAsync(m => m.OwnerId == owner && m.ActivityId == drives[1].Id && m.Status == RouteStatuses.Matched));
    }

    [Fact]
    public async Task Rows_of_an_older_rule_are_rematched_worst_first_keep_their_line_on_failure_and_rejects_win()
    {
        var backend = new FakeRoutesBackend();
        var engine = new EchoEngine();
        await using var sp = Services(backend, engine);
        if (sp == null)
        {
            return;
        }

        var owner = "rule-" + Guid.NewGuid();
        var drives = backend.Add(owner, 4);
        await AssessAllAsync(sp, owner);
        await PendingBatchAsync(sp, owner);
        using (var db = Db(sp))
        {
            // As routes.v1 left them: matched (newest), low_confidence, failed, and one the Owner rejected
            var rows = await db.MatchedRoutes.Where(m => m.OwnerId == owner).ToListAsync();
            string[] statuses = { RouteStatuses.Matched, RouteStatuses.LowConfidence, RouteStatuses.Failed, RouteStatuses.Matched };
            for (var i = 0; i < 4; i++)
            {
                var row = rows.Single(r => r.ActivityId == drives[i].Id);
                row.RuleVersion = "routes.v1";
                row.Status = statuses[i];
                row.Attempts = i == 2 ? 4 : 0;
                if (i == 2)
                {
                    row.Polyline = null;
                }
            }

            db.Decisions.Add(new RouteDecision { OwnerId = owner, SourceKey = drives[3].SourceKey, Kind = RouteDecisionKinds.Reject });
            await db.SaveChangesAsync();
        }

        using (var scope = sp.CreateScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<MatchRunner>();
            Assert.Equal(3, await runner.RematchCountAsync(owner, default));
            Assert.Equal(3, await runner.PendingCountAsync(owner, default));
        }

        // low_confidence and failed go before matched, although matched is the newest record
        engine.Requests.Clear();
        var first = await PendingBatchAsync(sp, owner, 2);
        Assert.Equal(2, first.Processed);
        using (var db = Db(sp))
        {
            var done = await db.MatchedRoutes.AsNoTracking().Where(m => m.OwnerId == owner && m.RuleVersion == RouteQuality.RuleVersion)
                .Select(m => m.ActivityId).ToListAsync();
            Assert.Equal(new[] { drives[1].Id, drives[2].Id }.OrderBy(x => x), done.OrderBy(x => x));
        }

        // The matched one fails its re-match (a timeout): its old line stays, and it backs off
        engine.Fail = _ => new EngineUnavailableException("the engine did not answer within 15 s", true);
        var second = await PendingBatchAsync(sp, owner);
        Assert.Equal(1, second.Tally.Retrying);
        using (var db = Db(sp))
        {
            var kept = await db.MatchedRoutes.AsNoTracking().SingleAsync(m => m.OwnerId == owner && m.ActivityId == drives[0].Id);
            Assert.Equal(("routes.v1", RouteStatuses.Matched, 1), (kept.RuleVersion, kept.Status, kept.Attempts));
            Assert.NotNull(kept.Polyline);
            Assert.NotNull(kept.NextAttemptAt);
            // The rejected one was never touched
            Assert.Equal("routes.v1", (await db.MatchedRoutes.AsNoTracking().SingleAsync(m => m.OwnerId == owner && m.ActivityId == drives[3].Id)).RuleVersion);
        }

        using (var scope = sp.CreateScope())
        {
            // Backing off: not waiting now, so no busy loop
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<MatchRunner>().PendingCountAsync(owner, default));
        }
    }

    [Fact]
    public async Task A_route_timeout_while_filling_a_gap_stores_the_record_with_its_gap()
    {
        var backend = new FakeRoutesBackend();
        var engine = new EchoEngine { FailRoute = _ => new EngineUnavailableException("the engine did not answer within 15 s", true) };
        await using var sp = Services(backend, engine);
        if (sp == null)
        {
            return;
        }

        var owner = "fill-" + Guid.NewGuid();
        var drive = backend.Add(owner, 1)[0];
        // A 5.6 km jump in 5 min after the 7th point: cut before the call (synthetic)
        for (var k = 7; k < drive.Points.Count; k++)
        {
            drive.Points[k] = drive.Points[k] with { Lat = drive.Points[k].Lat + 0.05, T = drive.Points[k].T.AddSeconds(240) };
        }

        await AssessAllAsync(sp, owner);
        var r = await PendingBatchAsync(sp, owner);

        Assert.Equal((2, 1), (engine.Requests.Count, engine.Routes.Count));
        Assert.Equal(0, r.Tally.Retrying);
        var row = await Db(sp).MatchedRoutes.AsNoTracking().SingleAsync(m => m.OwnerId == owner);
        Assert.Equal((0, (DateTime?)null), (row.Attempts, row.NextAttemptAt));
        Assert.NotNull(row.Polyline);
        Assert.Matches(@"""kind"":\s*""gap""", row.Pieces);
        Assert.Matches(@"""fillTimeouts"":\s*1\b", row.MetricsAfter);
        Assert.Equal(1, sp.GetRequiredService<EngineBreaker>().ConsecutiveFailures);

        // With the engine answering, the same jump is routed and the record is matched
        engine.FailRoute = _ => null;
        using (var scope = sp.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MatchRunner>().ImproveAsync(owner, new List<RouteTrack> { drive }, true,
                () => false, default);
        }

        row = await Db(sp).MatchedRoutes.AsNoTracking().SingleAsync(m => m.OwnerId == owner);
        Assert.DoesNotMatch(@"""kind"":\s*""gap""", row.Pieces);
        Assert.Equal(RouteStatuses.Matched, row.Status);
    }
}
