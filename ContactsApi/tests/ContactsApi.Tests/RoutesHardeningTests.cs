using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker.Assess;
using System.Text.Json;
using EchoRoutes.Worker;
using EchoRoutes.Worker.Matching;
using EchoRoutes.Worker.Scheduling;
using EchoRoutes.Worker.Valhalla;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContactsApi.Tests;

/// <summary>The review fixes of R1 in the service, against PostgreSQL (ATLAS_TEST_DB)</summary>
[Collection("routes-db")]
public class RoutesHardeningDbTests
{
    private static RoutesDbContext Db(IServiceProvider sp) => sp.CreateScope().ServiceProvider.GetRequiredService<RoutesDbContext>();

    private static async Task AssessAsync(IServiceProvider sp, string owner)
    {
        using var scope = sp.CreateScope();
        while ((await scope.ServiceProvider.GetRequiredService<AssessRunner>().RunFeedBatchAsync(owner, 500, false, default)).More)
        {
        }
    }

    private static async Task<MatchBatchResult> MatchAsync(IServiceProvider sp, string owner)
    {
        using var scope = sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MatchRunner>().RunPendingBatchAsync(owner, 25, () => false, default);
    }

    [Fact]
    public async Task An_unreachable_engine_uses_no_attempt_and_the_records_match_when_it_is_back()
    {
        var backend = new FakeRoutesBackend();
        var engine = new EchoEngine { Fail = _ => new EngineUnavailableException("the engine cannot be reached: refused", false, unreachable: true) };
        var clock = new EngineClientTests.Clock();
        await using var sp = RoutesMatchDbTests.Services(backend, engine, clock, o => o.BreakerFailures = 100);
        if (sp == null)
        {
            return;
        }

        var owner = "down-" + Guid.NewGuid();
        backend.Add(owner, 1);
        await AssessAsync(sp, owner);
        var db = Db(sp);

        // Far more outage than the backoff steps: the record never gives up
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(1, (await MatchAsync(sp, owner)).Tally.Retrying);
            var row = await db.MatchedRoutes.AsNoTracking().SingleAsync(m => m.OwnerId == owner);
            Assert.Equal((0, clock.Now.UtcDateTime.AddMinutes(1)), (row.Attempts, row.NextAttemptAt));
            Assert.Contains("not counted", row.Reason);
            clock.Now = clock.Now.AddMinutes(1);
        }

        engine.Fail = _ => null;
        Assert.Equal(1, (await MatchAsync(sp, owner)).Tally.Matched);
    }

    [Fact]
    public async Task The_failure_that_opens_the_breaker_and_a_failed_probe_use_no_attempt()
    {
        var backend = new FakeRoutesBackend();
        var engine = new EchoEngine { Fail = _ => new EngineUnavailableException("the engine did not answer within 15 s", true) };
        var clock = new EngineClientTests.Clock();
        await using var sp = RoutesMatchDbTests.Services(backend, engine, clock, o => { o.BreakerFailures = 2; o.BreakerPauseMinutes = 5; });
        if (sp == null)
        {
            return;
        }

        var owner = "trip-" + Guid.NewGuid();
        backend.Add(owner, 3);
        await AssessAsync(sp, owner);
        var db = Db(sp);

        var r = await MatchAsync(sp, owner);
        Assert.True(r.Stopped);
        var rows = await db.MatchedRoutes.AsNoTracking().Where(m => m.OwnerId == owner).OrderBy(m => m.ComputedAt).ToListAsync();
        Assert.Equal(2, rows.Count);
        // The first timeout is the record's own; the second opened the breaker: the engine's
        Assert.Equal(new[] { 1, 0 }, rows.Select(x => x.Attempts).OrderByDescending(x => x));
        var tripped = rows.Single(x => x.Attempts == 0);
        Assert.Equal(clock.Now.UtcDateTime.AddMinutes(5), tripped.NextAttemptAt);

        // After the pause one call goes through and times out again: still not counted
        clock.Now = clock.Now.AddMinutes(5);
        engine.Requests.Clear();
        var probe = await MatchAsync(sp, owner);
        Assert.Single(engine.Requests);
        Assert.True(probe.Stopped);
        var after = await db.MatchedRoutes.AsNoTracking().Where(m => m.OwnerId == owner).ToListAsync();
        var before = rows.ToDictionary(x => x.ActivityId, x => x.Attempts);
        Assert.All(after, x => Assert.Equal(before.GetValueOrDefault(x.ActivityId), x.Attempts));
        Assert.Contains(after, x => x.ComputedAt == clock.Now.UtcDateTime);
        Assert.True(sp.GetRequiredService<EngineBreaker>().IsOpen);
    }

    private static RoutesScheduler Scheduler(IServiceProvider sp) =>
        new(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<EchoRoutes.Worker.Backend.IRoutesBackend>(), sp.GetRequiredService<WakeSignal>(),
            sp.GetRequiredService<IPause>(), sp.GetRequiredService<IOptions<RoutesOptions>>(), NullLogger<RoutesScheduler>.Instance,
            sp.GetRequiredService<EngineBreaker>());

    private static async Task<int> RowsAsync(RoutesDbContext db, string owner) =>
        await db.Quality.CountAsync(x => x.OwnerId == owner) + await db.MatchedRoutes.CountAsync(x => x.OwnerId == owner)
        + await db.Decisions.CountAsync(x => x.OwnerId == owner) + await db.Settings.CountAsync(x => x.OwnerId == owner)
        + await db.Jobs.CountAsync(x => x.OwnerId == owner) + await db.Cursors.CountAsync(x => x.OwnerId == owner);

    [Fact]
    public async Task An_empty_or_failed_owner_list_deletes_nothing_and_a_gone_owner_loses_all_routes_data()
    {
        var backend = new FakeRoutesBackend();
        await using var sp = RoutesMatchDbTests.Services(backend, new EchoEngine(), options: o => o.PollSeconds = 0);
        if (sp == null)
        {
            return;
        }

        var stays = "stays-" + Guid.NewGuid();
        var leaves = "leaves-" + Guid.NewGuid();
        backend.Add(stays, 2);
        backend.Add(leaves, 2);
        var scheduler = Scheduler(sp);
        for (var i = 0; i < 6; i++)
        {
            await scheduler.TurnAsync(default);
        }

        var db = Db(sp);
        db.Decisions.Add(new RouteDecision { OwnerId = leaves, SourceKey = "google-timeline|x0" });
        db.Settings.Add(new RouteSetting { OwnerId = leaves, Json = "{}" });
        db.Jobs.Add(new RouteJob { OwnerId = leaves, State = RouteJobStates.Done, FinishedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var before = await RowsAsync(db, leaves);
        Assert.True(before >= 6, $"{before} rows");

        // The backend answers nothing, then fails: transient, nothing is deleted and work goes on
        backend.OwnersAnswer = new List<string>();
        await scheduler.TurnAsync(default);
        backend.OwnersAnswer = null;
        backend.OwnersError = new HttpRequestException("backend restarting");
        await scheduler.TurnAsync(default);
        Assert.Equal(before, await RowsAsync(db, leaves));
        Assert.True(await db.Cursors.AnyAsync(c => c.OwnerId == stays));

        // The Owner is no longer listed (no Atlas data left): all of their routes data goes
        backend.OwnersError = null;
        backend.OwnersAnswer = new List<string> { stays };
        await scheduler.TurnAsync(default);
        Assert.Equal(0, await RowsAsync(db, leaves));
        Assert.True(await RowsAsync(db, stays) > 0);
    }

    [Fact]
    public async Task Finished_jobs_older_than_a_week_are_removed()
    {
        var backend = new FakeRoutesBackend();
        await using var sp = RoutesMatchDbTests.Services(backend, new EchoEngine());
        if (sp == null)
        {
            return;
        }

        var owner = "prune-" + Guid.NewGuid();
        backend.Add(owner, 1);
        var db = Db(sp);
        var old = new RouteJob { OwnerId = owner, State = RouteJobStates.Done, FinishedAt = DateTime.UtcNow.AddDays(-8) };
        var failed = new RouteJob { OwnerId = owner, State = RouteJobStates.Failed, FinishedAt = DateTime.UtcNow.AddDays(-30) };
        var recent = new RouteJob { OwnerId = owner, State = RouteJobStates.Done, FinishedAt = DateTime.UtcNow.AddDays(-6) };
        var queued = new RouteJob { OwnerId = owner, State = RouteJobStates.Queued, NextAttemptAt = DateTime.UtcNow.AddDays(1) };
        db.Jobs.AddRange(old, failed, recent, queued);
        await db.SaveChangesAsync();

        await Scheduler(sp).TurnAsync(default);

        var left = await db.Jobs.AsNoTracking().Where(j => j.OwnerId == owner).Select(j => j.Id).ToListAsync();
        Assert.Equal(new[] { recent.Id, queued.Id }.OrderBy(x => x), left.OrderBy(x => x));
    }

    [Fact]
    public async Task A_range_job_stopped_mid_page_counts_each_record_once()
    {
        var backend = new FakeRoutesBackend();
        var engine = new EchoEngine();
        await using var sp = RoutesMatchDbTests.Services(backend, engine, options: o => o.MatchBatch = 4);
        if (sp == null)
        {
            return;
        }

        var owner = "rangecount-" + Guid.NewGuid();
        var records = backend.Add(owner, 12);
        var db = Db(sp);
        var job = new RouteJob
        {
            OwnerId = owner, Kind = RouteJobKinds.Improve, Priority = RouteJobPriorities.Batch,
            Scope = JsonSerializer.Serialize(new JobScope(null, null, records[1].Start, records[11].Start), new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        // Interactive work arrives after the 2nd and the 7th record: the batch stops mid-page twice
        var wake = sp.GetRequiredService<WakeSignal>();
        engine.OnCall = n =>
        {
            if (n is 2 or 7)
            {
                wake.Interactive();
            }
        };

        var scheduler = Scheduler(sp);
        for (var i = 0; i < 20 && (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id)).State != RouteJobStates.Done; i++)
        {
            await scheduler.TurnAsync(default);
        }

        var done = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.Equal((RouteJobStates.Done, 10, 10), (done.State, done.Done, done.Total));
        Assert.Equal(10, JsonDocument.Parse(done.Counts).RootElement.GetProperty("matched").GetInt32());
        // Each record was matched once: nothing finished was redone after a stop
        Assert.Equal(10, engine.Requests.Count);
    }

    [Fact]
    public void A_range_job_cursor_keeps_the_page_cursor_and_the_records_done()
    {
        Assert.Equal(((string?)null, 0), RoutesScheduler.SplitJobCursor(null));
        Assert.Equal(("123_abc", 0), RoutesScheduler.SplitJobCursor("123_abc"));
        Assert.Equal(("123_abc", 3), RoutesScheduler.SplitJobCursor(RoutesScheduler.JoinJobCursor("123_abc", 3)));
        Assert.Equal(((string?)null, 2), RoutesScheduler.SplitJobCursor(RoutesScheduler.JoinJobCursor(null, 2)));
    }

    [Fact]
    public async Task A_match_is_stored_with_its_list_and_density_lines_and_old_rows_are_filled()
    {
        var backend = new FakeRoutesBackend();
        await using var sp = RoutesMatchDbTests.Services(backend, new EchoEngine());
        if (sp == null)
        {
            return;
        }

        var owner = "lines-" + Guid.NewGuid();
        backend.Add(owner, 2, points: 60);
        await AssessAsync(sp, owner);
        Assert.Equal(2, (await MatchAsync(sp, owner)).Tally.Matched);

        var db = Db(sp);
        var rows = await db.MatchedRoutes.AsNoTracking().Where(m => m.OwnerId == owner).ToListAsync();
        Assert.All(rows, r => Assert.Equal(RouteLines.Derive(r.Polyline!), (r.ListPolyline!, r.DensityPolyline!)));

        // Rows stored before the columns existed are filled by the service, a page at a time
        await db.MatchedRoutes.Where(m => m.OwnerId == owner)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ListPolyline, (string?)null).SetProperty(m => m.DensityPolyline, (string?)null));
        using (var scope = sp.CreateScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<MatchRunner>();
            while (await runner.FillLinesAsync(1, default) > 0)
            {
            }
        }

        var filled = await db.MatchedRoutes.AsNoTracking().Where(m => m.OwnerId == owner).ToListAsync();
        Assert.All(filled, r => Assert.Equal(RouteLines.Derive(r.Polyline!), (r.ListPolyline!, r.DensityPolyline!)));
    }
}
