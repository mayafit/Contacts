using System.Text.Json;
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker;
using EchoRoutes.Worker.Api;
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

/// <summary>Story 12.5: the round-robin with matching, interactive work between records, jobs, the sweep, restarts, status</summary>
[Collection("routes-db")]
public class RoutesSchedulingTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static RoutesScheduler Scheduler(IServiceProvider sp) =>
        new(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IRoutesBackend>(), sp.GetRequiredService<WakeSignal>(),
            sp.GetRequiredService<IPause>(), sp.GetRequiredService<IOptions<RoutesOptions>>(), NullLogger<RoutesScheduler>.Instance,
            sp.GetRequiredService<EngineBreaker>());

    private static RoutesDbContext Db(IServiceProvider sp) => sp.CreateScope().ServiceProvider.GetRequiredService<RoutesDbContext>();

    private static async Task<RouteJob> QueueAsync(IServiceProvider sp, string owner, JobScope scope, string priority = RouteJobPriorities.Batch,
        string kind = RouteJobKinds.Improve)
    {
        var db = Db(sp);
        var job = new RouteJob
        {
            OwnerId = owner, Kind = kind, Priority = priority, Total = scope.ActivityIds?.Count ?? 0,
            Scope = JsonSerializer.Serialize(scope, Web)
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    private static async Task<int> TurnsUntilIdleAsync(RoutesScheduler scheduler, int max = 50)
    {
        for (var i = 1; i <= max; i++)
        {
            if (!await scheduler.TurnAsync(default))
            {
                return i;
            }
        }

        throw new InvalidOperationException("never idle");
    }

    [Fact]
    public async Task Owners_take_turns_assessing_then_matching_25_per_turn_with_1_s_pauses()
    {
        var backend = new FakeRoutesBackend();
        var engine = new EchoEngine();
        var pause = new RecordingPause();
        await using var sp = RoutesMatchDbTests.Services(backend, engine, pause: pause);
        if (sp == null)
        {
            return;
        }

        string a = "rr2-a-" + Guid.NewGuid(), b = "rr2-b-" + Guid.NewGuid();
        backend.Add(a, 30);
        backend.Add(b, 3);
        var scheduler = Scheduler(sp);
        var db = Db(sp);

        // Turn 1: both Owners' new records are assessed (stage A, 400 ms pauses)
        Assert.True(await scheduler.TurnAsync(default));
        Assert.Equal(new[] { 400.0, 400 }, pause.Pauses.Select(p => p.TotalMilliseconds));
        Assert.Empty(engine.Requests);

        // Turn 2: one stage B batch per Owner: 25 of A's, all 3 of B's, 1 s pauses
        pause.Pauses.Clear();
        Assert.True(await scheduler.TurnAsync(default));
        Assert.Equal(new[] { 1000.0, 1000 }, pause.Pauses.Select(p => p.TotalMilliseconds));
        Assert.Equal(25, await db.MatchedRoutes.CountAsync(m => m.OwnerId == a));
        Assert.Equal(3, await db.MatchedRoutes.CountAsync(m => m.OwnerId == b));

        // Turn 3: A's last 5; then idle
        Assert.True(await scheduler.TurnAsync(default));
        Assert.False(await scheduler.TurnAsync(default));
        Assert.Equal(30, await db.MatchedRoutes.CountAsync(m => m.OwnerId == a && m.Status == RouteStatuses.Matched));
        Assert.Equal(33, engine.Requests.Count);
    }

    [Fact]
    public async Task Interactive_work_runs_between_records_of_a_running_backfill_batch()
    {
        var backend = new FakeRoutesBackend();
        var engine = new EchoEngine();
        await using var sp = RoutesMatchDbTests.Services(backend, engine);
        if (sp == null)
        {
            return;
        }

        string big = "int-big-" + Guid.NewGuid(), other = "int-other-" + Guid.NewGuid();
        backend.Add(big, 25);
        var fix = backend.Add(other, 1)[0];
        var scheduler = Scheduler(sp);
        await scheduler.TurnAsync(default); // assess both

        // While the backfill's batch runs, the other Owner presses "Fix this route"
        engine.OnCall = n =>
        {
            if (n == 3)
            {
                QueueAsync(sp, other, new JobScope(new List<Guid> { fix.Id }, null), RouteJobPriorities.Interactive).GetAwaiter().GetResult();
                sp.GetRequiredService<WakeSignal>().Interactive();
            }
        };
        backend.Calls.Clear();
        await scheduler.TurnAsync(default);

        // The batch stopped after its third record; the interactive job ran next
        var tracks = backend.Calls.Where(c => c.Call == "tracks").ToList();
        Assert.Equal((big, false), (tracks[0].Owner, tracks[0].Interactive));
        Assert.Equal((other, true), (tracks[1].Owner, tracks[1].Interactive));
        var db = Db(sp);
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.OwnerId == other);
        Assert.Equal(RouteJobStates.Done, job.State);
        Assert.Equal(3, await db.MatchedRoutes.CountAsync(m => m.OwnerId == big));
        Assert.Equal(4, engine.Requests.Count);

        // The rest of the backfill goes on afterwards, nothing lost or repeated
        engine.OnCall = null;
        await TurnsUntilIdleAsync(scheduler);
        Assert.Equal(25, await db.MatchedRoutes.CountAsync(m => m.OwnerId == big));
        Assert.Equal(26, engine.Requests.Count);
    }

    [Fact]
    public async Task A_backfill_resumes_after_a_restart_without_repeating_work()
    {
        var backend = new FakeRoutesBackend();
        var engine = new EchoEngine();
        await using var sp = RoutesMatchDbTests.Services(backend, engine);
        if (sp == null)
        {
            return;
        }

        var owner = "restart-" + Guid.NewGuid();
        backend.Add(owner, 40);
        var first = Scheduler(sp);
        await first.TurnAsync(default); // assess
        await first.TurnAsync(default); // 25 matched, then the pod restarts

        var second = Scheduler(sp);
        await TurnsUntilIdleAsync(second);

        var db = Db(sp);
        Assert.Equal(40, await db.MatchedRoutes.CountAsync(m => m.OwnerId == owner));
        Assert.Equal(40, engine.Requests.Count);
        // The feed went on from its durable cursor: every record was fetched for assessment once
        // (one tracks call of 25 and one of 15), and once more for its match
        Assert.Equal(80, backend.Calls.Where(c => c.Call == "tracks").Sum(c => c.Count));
    }

    [Fact]
    public async Task A_date_range_job_pages_through_the_range_and_matches_only_it()
    {
        var backend = new FakeRoutesBackend();
        var engine = new EchoEngine();
        await using var sp = RoutesMatchDbTests.Services(backend, engine, options: o => o.MatchBatch = 4);
        if (sp == null)
        {
            return;
        }

        var owner = "range-" + Guid.NewGuid();
        var records = backend.Add(owner, 20);
        var from = records[5].Start;
        var to = records[15].Start; // records 5..14
        var job = await QueueAsync(sp, owner, new JobScope(null, null, from, to));
        // Only the job: the feed has nothing for this Owner yet (fusion pending)
        var scheduler = Scheduler(sp);
        var db = Db(sp);
        for (var i = 0; i < 4; i++)
        {
            await scheduler.TurnAsync(default);
        }

        var done = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.Equal((RouteJobStates.Done, 10, 10), (done.State, done.Done, done.Total));
        Assert.Equal(3, backend.Calls.Count(c => c.Call == "range" && c.Count == 4));
        var matched = await db.MatchedRoutes.AsNoTracking().Where(m => m.OwnerId == owner).Select(m => m.ActivityId).ToListAsync();
        Assert.True(records.Skip(5).Take(10).Select(r => r.Id).All(matched.Contains));
        Assert.Equal(10, JsonDocument.Parse(done.Counts).RootElement.GetProperty("matched").GetInt32());
    }

    [Fact]
    public async Task A_failing_job_retries_after_1_10_60_minutes_and_then_fails()
    {
        var backend = new FakeRoutesBackend { TracksError = new HttpRequestException("backend unreachable") };
        await using var sp = RoutesMatchDbTests.Services(backend, new EchoEngine());
        if (sp == null)
        {
            return;
        }

        var owner = "jobretry-" + Guid.NewGuid();
        var record = backend.Add(owner, 1)[0];
        var job = await QueueAsync(sp, owner, new JobScope(new List<Guid> { record.Id }, null));
        var scheduler = Scheduler(sp);
        var db = Db(sp);
        var attempt = 0;
        foreach (var wait in new[] { 1, 10, 60 })
        {
            attempt++;
            await scheduler.TurnAsync(default);
            var j = await db.Jobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
            Assert.Equal(RouteJobStates.Running, j.State);
            Assert.InRange((j.NextAttemptAt!.Value - DateTime.UtcNow).TotalMinutes, wait - 0.2, wait);
            Assert.Contains("unreachable", j.LastError);
            Assert.Equal(attempt, j.Attempts);
            // Not due: the next turn leaves it alone
            await scheduler.TurnAsync(default);
            Assert.Equal(attempt, (await db.Jobs.AsNoTracking().SingleAsync(x => x.Id == job.Id)).Attempts);
            await db.Jobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
        }

        await scheduler.TurnAsync(default);
        var failed = await db.Jobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        Assert.Equal((RouteJobStates.Failed, 4), (failed.State, failed.Attempts));
    }

    [Fact]
    public async Task An_improve_job_waits_for_an_open_breaker_without_spending_an_attempt()
    {
        var backend = new FakeRoutesBackend();
        await using var sp = RoutesMatchDbTests.Services(backend, new EchoEngine(), options: o => o.BreakerFailures = 1);
        if (sp == null)
        {
            return;
        }

        var owner = "jobbreaker-" + Guid.NewGuid();
        var record = backend.Add(owner, 1)[0];
        var breaker = sp.GetRequiredService<EngineBreaker>();
        breaker.Failure("timeout");
        var job = await QueueAsync(sp, owner, new JobScope(new List<Guid> { record.Id }, null), RouteJobPriorities.Interactive);

        await Scheduler(sp).TurnAsync(default);

        var j = await Db(sp).Jobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
        Assert.Equal((RouteJobStates.Queued, 0), (j.State, j.Attempts));
        Assert.InRange((j.NextAttemptAt!.Value - breaker.OpenUntil!.Value).Duration().TotalMilliseconds, 0, 1);
        // The job fetched nothing (the feed's own assessment may run: it needs no engine)
        Assert.DoesNotContain(backend.Calls, c => c.Call == "tracks" && c.Interactive);
    }

    [Fact]
    public async Task The_weekly_sweep_removes_the_derived_rows_of_deleted_records()
    {
        var backend = new FakeRoutesBackend();
        await using var sp = RoutesMatchDbTests.Services(backend, new EchoEngine());
        if (sp == null)
        {
            return;
        }

        var owner = "sweep-" + Guid.NewGuid();
        var records = backend.Add(owner, 4);
        var scheduler = Scheduler(sp);
        await TurnsUntilIdleAsync(scheduler);
        var db = Db(sp);
        Assert.Equal(4, await db.MatchedRoutes.CountAsync(m => m.OwnerId == owner));
        db.Decisions.Add(new RouteDecision { OwnerId = owner, SourceKey = records[1].SourceKey, Kind = RouteDecisionKinds.Reject });
        await db.SaveChangesAsync();

        // Not a week yet: no sweep
        backend.Records[owner].RemoveAll(r => r.Id == records[1].Id);
        backend.Calls.Clear();
        Assert.False(await scheduler.TurnAsync(default));

        await db.Cursors.Where(c => c.OwnerId == owner).ExecuteUpdateAsync(s => s.SetProperty(c => c.SweptAt, DateTime.UtcNow.AddDays(-8)));
        Assert.True(await scheduler.TurnAsync(default));

        Assert.Equal(3, await db.Quality.CountAsync(q => q.OwnerId == owner));
        Assert.False(await db.MatchedRoutes.AnyAsync(m => m.OwnerId == owner && m.ActivityId == records[1].Id));
        // Decisions stay (a re-import brings the record back with its natural key)
        Assert.True(await db.Decisions.AnyAsync(d => d.OwnerId == owner && d.SourceKey == records[1].SourceKey));
        var cursor = await db.Cursors.AsNoTracking().SingleAsync(c => c.OwnerId == owner);
        Assert.True(cursor.SweptAt > DateTime.UtcNow.AddMinutes(-1));
        Assert.Null(cursor.SweepAfter);
    }

    [Fact]
    public async Task Status_reports_the_queue_waiting_records_eta_engine_and_breaker()
    {
        var backend = new FakeRoutesBackend();
        var engine = new EchoEngine();
        await using var sp = RoutesMatchDbTests.Services(backend, engine, options: o => o.BreakerFailures = 2);
        if (sp == null)
        {
            return;
        }

        var owner = "status-" + Guid.NewGuid();
        backend.Add(owner, 6);
        await Scheduler(sp).TurnAsync(default); // assessed, nothing matched yet
        await QueueAsync(sp, owner, new JobScope(new List<Guid> { Guid.NewGuid() }, null), RouteJobPriorities.Interactive);
        var breaker = sp.GetRequiredService<EngineBreaker>();
        breaker.Failure("the engine did not answer within 15 s");
        breaker.Failure("the engine did not answer within 15 s");

        using var scope = sp.CreateScope();
        var s = scope.ServiceProvider;
        var status = JsonSerializer.SerializeToElement(await InternalApi.StatusAsync(owner, s.GetRequiredService<RoutesDbContext>(),
            s.GetRequiredService<MatchRunner>(), s.GetRequiredService<GraphInfoSource>(), s.GetRequiredService<RegionCoverage>(), engine, breaker,
            new RoutesOptions(), default), Web);

        Assert.Equal(6, status.GetProperty("waitingForMatch").GetInt32());
        Assert.Equal(1, status.GetProperty("queued").GetProperty("interactive").GetInt32());
        Assert.True(status.GetProperty("etaSeconds").GetInt32() >= 6);
        var e = status.GetProperty("engine");
        Assert.True(e.GetProperty("up").GetBoolean());
        Assert.True(e.GetProperty("breaker").GetProperty("open").GetBoolean());
        Assert.Equal(2, e.GetProperty("breaker").GetProperty("consecutiveFailures").GetInt32());
        Assert.Contains("did not answer", e.GetProperty("breaker").GetProperty("lastFailure").GetString());
    }
}

/// <summary>
/// AA-17 end to end: the real Atlas feed (PostgreSQL) behind the service's stage A
/// and stage B. A re-fusion returns the whole window and is skipped with no engine
/// call and no hidden match; a dissolved cluster moves UpdatedAt and is re-matched.
/// </summary>
[Collection("routes-db")]
public class RoutesChangeFeedEndToEndTests
{
    private static readonly DateTime T0 = new(2026, 9, 21, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>The backend's internal API, in-process over the real feed</summary>
    private sealed class FeedBackend : IRoutesBackend
    {
        private readonly AtlasRoutesFeed _feed;
        public int Tracks;

        public FeedBackend(AtlasRoutesFeed feed) => _feed = feed;

        public Task<List<string>> OwnersAsync(CancellationToken ct) => _feed.OwnersAsync(ct);

        public async Task<RouteChangesPage> ChangesAsync(string owner, string? after, int limit, bool interactive, CancellationToken ct)
        {
            try
            {
                return await _feed.ChangesAsync(owner, after, limit, ct);
            }
            catch (DerivationNotReadyException)
            {
                throw new DerivationPendingException();
            }
        }

        public Task<List<RouteTrack>> TracksAsync(string owner, IReadOnlyCollection<Guid> ids, bool interactive, CancellationToken ct)
        {
            Tracks += ids.Count;
            return _feed.TracksAsync(owner, ids, ct);
        }

        public Task<RouteIdsPage> RangeAsync(string owner, DateTime from, DateTime to, string? after, int limit, bool interactive, CancellationToken ct) =>
            _feed.RangeAsync(owner, from, to, after, limit, ct);

        public Task<List<Guid>> ExistsAsync(string owner, IReadOnlyCollection<Guid> ids, CancellationToken ct) => _feed.ExistsAsync(owner, ids, ct);
    }

    private static List<PointInput> Track(int n, double lat0, double step = 0.005) =>
        Enumerable.Range(0, n).Select(i => new PointInput(T0.AddSeconds(i * 60), lat0 + i * step, 34.85)).ToList();

    [Fact]
    public async Task Refusion_is_skipped_and_a_dissolved_cluster_is_rematched()
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        if (string.IsNullOrEmpty(cs))
        {
            return;
        }

        await using var atlas = new AtlasDbContext(new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations", "atlas")).Options);
        await atlas.Database.MigrateAsync();
        var svc = new AtlasService(atlas, NullLogger<AtlasService>.Instance);
        var owner = "aa17-" + Guid.NewGuid();
        // Two Timeline drives; a Sports Tracker recording of the second joins its cluster
        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "google-timeline",
            Activities =
            {
                new ActivityInput("d1", "IN_PASSENGER_VEHICLE", null, T0, T0.AddMinutes(13), Points: Track(13, 31.90)),
                new ActivityInput("d2", "IN_PASSENGER_VEHICLE", null, T0.AddHours(3), T0.AddHours(3).AddMinutes(13),
                    Points: Track(13, 32.10).Select(p => p with { T = p.T.AddHours(3) }).ToList())
            }
        });
        var st = await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "sports-tracker",
            Activities =
            {
                new ActivityInput("st-2", "driving", null, T0.AddHours(3), T0.AddHours(3).AddMinutes(13),
                    Points: Enumerable.Range(0, 150).Select(i => new PointInput(T0.AddHours(3).AddSeconds(i * 5), 32.10 + i * 0.0004, 34.85)).ToList())
            }
        });
        await svc.FuseAsync(owner);
        await atlas.FusionQueue.Where(q => q.OwnerId == owner).ExecuteDeleteAsync();
        var d2 = await atlas.Activities.AsNoTracking().SingleAsync(a => a.OwnerId == owner && a.ExternalId == "d2");
        var clustered = d2.ClusterId != null;

        var backend = new FeedBackend(new AtlasRoutesFeed(atlas));
        var engine = new EchoEngine();
        await using var sp = RoutesMatchDbTests.Services(new FakeRoutesBackend(), engine);
        Assert.NotNull(sp);
        using var scope = sp!.CreateScope();
        var routes = scope.ServiceProvider.GetRequiredService<RoutesDbContext>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<RoutesOptions>>();
        var assess = new AssessRunner(routes, backend, scope.ServiceProvider.GetRequiredService<RegionCoverage>());
        var match = new MatchRunner(routes, backend, engine, scope.ServiceProvider.GetRequiredService<EngineBreaker>(),
            scope.ServiceProvider.GetRequiredService<GraphInfoSource>(), assess, TimeProvider.System, options);

        async Task SettleAsync()
        {
            while ((await assess.RunFeedBatchAsync(owner, 500, false, default)).More)
            {
            }

            while ((await match.RunPendingBatchAsync(owner, 25, () => false, default)).Seen > 0)
            {
            }
        }

        await SettleAsync();
        var timelineIds = await atlas.Activities.AsNoTracking().Where(a => a.OwnerId == owner && a.Source == "google-timeline")
            .Select(a => a.Id).ToListAsync();
        Assert.All(timelineIds, id => Assert.True(routes.MatchedRoutes.Any(m => m.OwnerId == owner && m.ActivityId == id && m.Status == RouteStatuses.Matched)));
        var calls = engine.Requests.Count;

        // A re-fusion (as after a fusion settings save) returns the whole window again...
        await svc.QueueFusionAsync(owner, null, null);
        await svc.ProcessFusionQueueAsync(owner);
        backend.Tracks = 0;
        await SettleAsync();
        // ...and every record is skipped: no track fetched, no engine call, no match hidden
        Assert.Equal((calls, 0), (engine.Requests.Count, backend.Tracks));
        foreach (var a in await atlas.Activities.AsNoTracking().Where(a => a.OwnerId == owner && a.Source == "google-timeline").ToListAsync())
        {
            var row = await routes.MatchedRoutes.AsNoTracking().SingleAsync(m => m.OwnerId == owner && m.ActivityId == a.Id);
            var fused = await atlas.Fusions.AsNoTracking().Where(f => f.ActivityId == (a.ClusterId ?? a.Id)).Select(f => f.Type).SingleOrDefaultAsync() ?? a.Type;
            Assert.True(RouteChoice.Drawable(row, owner, DateTime.SpecifyKind(a.UpdatedAt, DateTimeKind.Utc), a.PointCount, fused, null, new RouteSettingsDoc()));
        }

        // Deleting the Sports Tracker import dissolves d2's cluster: d2 moves, is seen and re-checked
        if (clustered)
        {
            await svc.DeleteImportAsync(owner, st.ImportId);
            await atlas.FusionQueue.Where(q => q.OwnerId == owner).ExecuteDeleteAsync();
            backend.Tracks = 0;
            await SettleAsync();
            var moved = await atlas.Activities.AsNoTracking().SingleAsync(a => a.Id == d2.Id);
            Assert.True(moved.UpdatedAt > d2.UpdatedAt);
            Assert.True(backend.Tracks >= 1);
            var row = await routes.MatchedRoutes.AsNoTracking().SingleAsync(m => m.OwnerId == owner && m.ActivityId == d2.Id);
            Assert.Equal(DateTime.SpecifyKind(moved.UpdatedAt, DateTimeKind.Utc), row.SourceUpdatedAt);
        }
    }

    [Fact]
    public async Task The_range_endpoint_pages_records_by_start_time_for_the_owner_only()
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        if (string.IsNullOrEmpty(cs))
        {
            return;
        }

        await using var atlas = new AtlasDbContext(new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations", "atlas")).Options);
        await atlas.Database.MigrateAsync();
        var svc = new AtlasService(atlas, NullLogger<AtlasService>.Instance);
        string owner = "rng-" + Guid.NewGuid(), other = "rng-x-" + Guid.NewGuid();
        var inputs = Enumerable.Range(0, 7).Select(i => new ActivityInput($"r{i}", "WALKING", null, T0.AddHours(i), T0.AddHours(i).AddMinutes(10))).ToList();
        var request = new IngestRequest { Source = "google-timeline" };
        request.Activities.AddRange(inputs);
        await svc.IngestAsync(owner, request);
        await svc.IngestAsync(other, request);
        var feed = new AtlasRoutesFeed(atlas);

        var ids = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await feed.RangeAsync(owner, T0.AddHours(1), T0.AddHours(6), cursor, 2);
            ids.AddRange(page.Ids);
            cursor = page.Next;
            pages++;
            if (!page.More)
            {
                break;
            }
        }
        while (true);

        Assert.Equal(3, pages);
        var expected = await atlas.Activities.AsNoTracking()
            .Where(a => a.OwnerId == owner && a.StartTime >= T0.AddHours(1) && a.StartTime < T0.AddHours(6))
            .OrderBy(a => a.StartTime).Select(a => a.Id).ToListAsync();
        Assert.Equal(expected, ids);
        Assert.Equal(5, ids.Count);
    }
}
