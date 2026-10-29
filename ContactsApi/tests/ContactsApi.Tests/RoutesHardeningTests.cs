using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker.Assess;
using EchoRoutes.Worker.Matching;
using EchoRoutes.Worker.Valhalla;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
