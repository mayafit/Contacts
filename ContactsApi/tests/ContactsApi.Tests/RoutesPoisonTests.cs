using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker.Assess;
using EchoRoutes.Worker.Matching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ContactsApi.Tests;

/// <summary>Natural keys that would not fit their column</summary>
public class RouteKeysTests
{
    [Fact]
    public void A_short_key_is_unchanged()
    {
        var id = Guid.NewGuid();
        Assert.Equal("gpx|ride.gpx#0|0", RouteKeys.SourceKey("gpx", "ride.gpx#0|0", id));
        Assert.Equal($"activity|{id}", RouteKeys.SourceKey("manual", null, id));
    }

    [Fact]
    public void A_long_key_keeps_its_readable_head_and_a_stable_hash_within_the_limit()
    {
        var name = new string('a', 400);
        var key = RouteKeys.SourceKey("gpx", $"{name}.gpx#0|1", Guid.NewGuid());
        var again = RouteKeys.SourceKey("gpx", $"{name}.gpx#0|1", Guid.NewGuid());
        var other = RouteKeys.SourceKey("gpx", $"{name}.gpx#0|2", Guid.NewGuid());

        Assert.Equal(RouteKeys.MaxLength, key.Length);
        Assert.StartsWith("gpx|aaaa", key);
        Assert.Contains("#~", key);
        Assert.Equal(key, again);
        Assert.NotEqual(key, other);
    }

    [Fact]
    public void A_long_key_never_cuts_a_surrogate_pair()
    {
        // Emoji are two UTF-16 units: whatever the cut, no lone half remains
        foreach (var pad in new[] { 0, 1 })
        {
            var key = RouteKeys.SourceKey("gpx", new string('x', pad) + string.Concat(Enumerable.Repeat("\U0001F6B2", 150)), Guid.NewGuid());
            Assert.True(key.Length <= RouteKeys.MaxLength);
            for (var i = 0; i < key.Length; i++)
            {
                Assert.False(char.IsHighSurrogate(key[i]) && (i + 1 == key.Length || !char.IsLowSurrogate(key[i + 1])), $"lone high surrogate at {i}");
            }
        }
    }
}

/// <summary>One bad record never stalls an Owner (stage A's feed, stage B's batches), against PostgreSQL</summary>
[Collection("routes-db")]
public class RoutesPoisonDbTests
{
    private static RoutesDbContext Db(IServiceProvider sp) => sp.CreateScope().ServiceProvider.GetRequiredService<RoutesDbContext>();

    [Fact]
    public async Task A_record_with_a_very_long_external_id_is_assessed_under_its_shortened_key()
    {
        var backend = new FakeRoutesBackend();
        await using var sp = RoutesMatchDbTests.Services(backend, new EchoEngine());
        if (sp == null)
        {
            return;
        }

        var owner = "longkey-" + Guid.NewGuid();
        backend.Add(owner, 2);
        var list = backend.Records[owner];
        list[0] = list[0] with { Source = "gpx", ExternalId = new string('f', 600) + ".gpx#0|0" };

        using var scope = sp.CreateScope();
        var r = await scope.ServiceProvider.GetRequiredService<AssessRunner>().RunFeedBatchAsync(owner, 500, false, default);

        Assert.Equal((2, 0), (r.Assessed, r.Failed));
        var row = await Db(sp).Quality.AsNoTracking().SingleAsync(q => q.OwnerId == owner && q.ActivityId == list[0].Id);
        Assert.Equal(RouteKeys.SourceKey("gpx", list[0].ExternalId, list[0].Id), row.SourceKey);
        Assert.True(row.SourceKey.Length <= RouteKeys.MaxLength);
    }

    [Fact]
    public async Task A_record_that_cannot_be_stored_is_skipped_and_the_cursor_moves_past_it()
    {
        var backend = new FakeRoutesBackend();
        await using var sp = RoutesMatchDbTests.Services(backend, new EchoEngine());
        if (sp == null)
        {
            return;
        }

        var owner = "poison-a-" + Guid.NewGuid();
        backend.Add(owner, 3);
        var list = backend.Records[owner];
        // Longer than its column (varchar(64)): this row's insert fails, and with it the batch's save
        list[1] = list[1] with { SourceType = new string('t', 100) };

        using (var scope = sp.CreateScope())
        {
            var r = await scope.ServiceProvider.GetRequiredService<AssessRunner>().RunFeedBatchAsync(owner, 500, false, default);
            Assert.Equal((3, 2, 1), (r.Seen, r.Assessed, r.Failed));
        }

        var db = Db(sp);
        var stored = await db.Quality.AsNoTracking().Where(q => q.OwnerId == owner).Select(q => q.ActivityId).ToListAsync();
        Assert.Equal(new[] { list[0].Id, list[2].Id }.OrderBy(x => x), stored.OrderBy(x => x));
        var cursor = await db.Cursors.AsNoTracking().SingleAsync(c => c.OwnerId == owner);
        Assert.Equal(list.Max(x => x.UpdatedAt).Ticks.ToString(), cursor.Cursor);
        Assert.Contains(list[1].Id.ToString(), cursor.LastError);

        // The next page starts after it: nothing is read again, the feed is not stuck
        using (var scope = sp.CreateScope())
        {
            var next = await scope.ServiceProvider.GetRequiredService<AssessRunner>().RunFeedBatchAsync(owner, 500, false, default);
            Assert.Equal((0, false), (next.Seen, next.More));
        }

        Assert.Null((await db.Cursors.AsNoTracking().SingleAsync(c => c.OwnerId == owner)).LastError);
    }

    [Fact]
    public async Task A_record_whose_match_throws_backs_off_with_the_reason_and_the_batch_goes_on()
    {
        var backend = new FakeRoutesBackend();
        var engine = new EchoEngine();
        // Not an engine failure: a bug in handling the first record's answer
        engine.Fail = _ => engine.Requests.Count == 1 ? new InvalidOperationException("unexpected answer shape") : null;
        var clock = new EngineClientTests.Clock();
        await using var sp = RoutesMatchDbTests.Services(backend, engine, clock);
        if (sp == null)
        {
            return;
        }

        var owner = "poison-b-" + Guid.NewGuid();
        backend.Add(owner, 3);
        using (var scope = sp.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AssessRunner>().RunFeedBatchAsync(owner, 500, false, default);
        }

        MatchBatchResult r;
        using (var scope = sp.CreateScope())
        {
            r = await scope.ServiceProvider.GetRequiredService<MatchRunner>().RunPendingBatchAsync(owner, 25, () => false, default);
        }

        Assert.Equal((3, 3, 2, 1), (r.Seen, r.Processed, r.Tally.Matched, r.Tally.Retrying));
        var failed = await Db(sp).MatchedRoutes.AsNoTracking().SingleAsync(m => m.OwnerId == owner && m.Status != RouteStatuses.Matched);
        Assert.Equal((RouteStatuses.Failed, 1, clock.Now.UtcDateTime.AddMinutes(1)), (failed.Status, failed.Attempts, failed.NextAttemptAt));
        Assert.Contains("unexpected answer shape", failed.Reason);

        // It no longer heads every batch: nothing waits until its backoff is due
        using (var scope = sp.CreateScope())
        {
            Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<MatchRunner>().RunPendingBatchAsync(owner, 25, () => false, default)).Seen);
        }

        clock.Now = clock.Now.AddMinutes(1);
        using (var scope = sp.CreateScope())
        {
            var retry = await scope.ServiceProvider.GetRequiredService<MatchRunner>().RunPendingBatchAsync(owner, 25, () => false, default);
            Assert.Equal((1, 1), (retry.Seen, retry.Tally.Matched));
        }
    }
}
