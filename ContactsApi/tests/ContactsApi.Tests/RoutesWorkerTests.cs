using System.Text.Json.Nodes;
using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker;
using EchoRoutes.Worker.Assess;
using EchoRoutes.Worker.Backend;
using EchoRoutes.Worker.Scheduling;
using EchoRoutes.Worker.Valhalla;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContactsApi.Tests;

/// <summary>An in-memory backend: per-Owner records, a feed with cursors, call logs</summary>
internal sealed class FakeRoutesBackend : IRoutesBackend
{
    private static readonly DateTime T0 = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    public readonly Dictionary<string, List<RouteTrack>> Records = new();
    public readonly List<(string Call, string Owner, int Count, bool Interactive)> Calls = new();
    public readonly Dictionary<string, TimeSpan> BusyFor = new();
    /// <summary>When set, tracks calls fail with it</summary>
    public Exception? TracksError;

    /// <summary>Adds records to an Owner: sparse drives, or trackless ones</summary>
    public List<RouteTrack> Add(string owner, int count, int points = 13)
    {
        if (!Records.TryGetValue(owner, out var list))
        {
            list = new List<RouteTrack>();
            Records[owner] = list;
        }

        var added = Enumerable.Range(0, count).Select(i =>
        {
            var start = T0.AddHours(list.Count + i);
            var pts = Enumerable.Range(0, points).Select(k => new RoutePoint(start.AddSeconds(60 * k), 32.0 + k * 0.01, 34.85)).ToList();
            return new RouteTrack(Guid.NewGuid(), "google-timeline", $"x{list.Count + i}", "drive", "IN_PASSENGER_VEHICLE",
                start, start.AddMinutes(points), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks((list.Count + i) * 10),
                points, pts, null, null);
        }).ToList();
        list.AddRange(added);
        return added;
    }

    /// <summary>Gives a record a new UpdatedAt (and so a later place in the feed)</summary>
    public void Touch(string owner, Guid id)
    {
        var list = Records[owner];
        var i = list.FindIndex(r => r.Id == id);
        var r = list[i];
        list.RemoveAt(i);
        list.Add(r with { UpdatedAt = list.Max(x => x.UpdatedAt).AddSeconds(1) });
    }

    public Task<List<string>> OwnersAsync(CancellationToken ct) => Task.FromResult(Records.Keys.ToList());

    public Task<RouteChangesPage> ChangesAsync(string owner, string? after, int limit, bool interactive, CancellationToken ct)
    {
        Calls.Add(("changes", owner, limit, interactive));
        if (BusyFor.TryGetValue(owner, out var wait) && !interactive)
        {
            throw new BackendBusyException(wait);
        }

        var since = after == null ? DateTime.MinValue : new DateTime(long.Parse(after), DateTimeKind.Utc);
        var newer = Records.GetValueOrDefault(owner, new()).Where(r => r.UpdatedAt > since).OrderBy(r => r.UpdatedAt).ToList();
        var page = newer.Take(limit).ToList();
        var next = page.Count > 0 ? page[^1].UpdatedAt.Ticks.ToString() : after;
        return Task.FromResult(new RouteChangesPage(
            page.Select(r => new RouteChange(r.Id, r.UpdatedAt, r.PointCount, r.Type, r.SourceKey, r.SourceType)).ToList(),
            next, newer.Count > limit));
    }

    public Task<List<RouteTrack>> TracksAsync(string owner, IReadOnlyCollection<Guid> ids, bool interactive, CancellationToken ct)
    {
        Calls.Add(("tracks", owner, ids.Count, interactive));
        if (TracksError != null)
        {
            throw TracksError;
        }

        Assert.True(ids.Count <= RoutesFeedLimits.TracksMax);
        // Only the Owner's own records, as the backend does
        return Task.FromResult(Records.GetValueOrDefault(owner, new()).Where(r => ids.Contains(r.Id)).ToList());
    }

    public Task<RouteIdsPage> RangeAsync(string owner, DateTime from, DateTime to, string? after, int limit, bool interactive, CancellationToken ct)
    {
        Calls.Add(("range", owner, limit, interactive));
        var all = Records.GetValueOrDefault(owner, new()).Where(r => r.Start >= from && r.Start < to)
            .OrderBy(r => r.Start).ThenBy(r => r.Id).ToList();
        var skip = after == null ? 0 : int.Parse(after);
        var page = all.Skip(skip).Take(limit).Select(r => r.Id).ToList();
        return Task.FromResult(new RouteIdsPage(page, (skip + page.Count).ToString(), skip + page.Count < all.Count));
    }

    public Task<List<Guid>> ExistsAsync(string owner, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        Task.FromResult(Records.GetValueOrDefault(owner, new()).Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToList());
}

/// <summary>Valhalla stand-in: answers, and records nothing (stage B comes with story 12.4)</summary>
internal sealed class FakeValhalla : IValhallaClient
{
    public bool Up = true;

    public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(Up);

    public Task<string?> VersionAsync(CancellationToken ct) => Task.FromResult<string?>("3.9.1");

    public Task<System.Text.Json.JsonDocument> TraceAttributesAsync(JsonObject request, CancellationToken ct) =>
        Task.FromResult(System.Text.Json.JsonDocument.Parse("{}"));
}

/// <summary>Records the pauses instead of waiting</summary>
internal sealed class RecordingPause : IPause
{
    public readonly List<TimeSpan> Pauses = new();

    public Task DelayAsync(TimeSpan time, CancellationToken ct)
    {
        Pauses.Add(time);
        return Task.CompletedTask;
    }
}

/// <summary>Readiness and graph.json</summary>
public class RoutesReadinessTests
{
    [Fact]
    public async Task Ready_only_once_graph_json_is_read_and_the_engine_answers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "routes-ready-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "graph.json");
        var valhalla = new FakeValhalla { Up = false };
        var check = new RoutesReadyCheck(new GraphInfoSource(Options.Create(new RoutesOptions { GraphJsonPath = path })), valhalla);
        var context = new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext();

        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, (await check.CheckHealthAsync(context)).Status);
        await File.WriteAllTextAsync(path, """{"regions":["israel-and-palestine"],"extractDate":"2026-10-02","valhallaVersion":"3.9.1","tarBytes":312000000}""");
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, (await check.CheckHealthAsync(context)).Status);
        valhalla.Up = true;
        var ready = await check.CheckHealthAsync(context);
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy, ready.Status);
        Assert.Equal("israel-and-palestine@2026-10-02+valhalla-3.9.1", ready.Description);
        Directory.Delete(dir, true);
    }
}

/// <summary>Not run in parallel with itself: the scheduler prunes cursors of Owners it does not serve</summary>
[CollectionDefinition("routes-db", DisableParallelization = true)]
public class RoutesDbCollection
{
}

/// <summary>The assess pipeline and the scheduler, against PostgreSQL (ATLAS_TEST_DB, schema routes)</summary>
[Collection("routes-db")]
public class RoutesWorkerDbTests
{
    private static ServiceProvider? Services(FakeRoutesBackend backend, RecordingPause pause, CommandCounter? counter = null)
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        if (string.IsNullOrEmpty(cs))
        {
            return null;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<RoutesDbContext>(o =>
        {
            o.UseNpgsql(cs, n => n.MigrationsHistoryTable(RoutesDbContext.HistoryTable, RoutesDbContext.Schema));
            if (counter != null)
            {
                o.AddInterceptors(counter);
            }
        });
        services.AddSingleton<IRoutesBackend>(backend);
        services.AddSingleton(RegionCoverage.Load(Path.GetDirectoryName(RouteQualityTests.BundledPoly())!));
        services.AddScoped<AssessRunner>();
        services.AddSingleton<WakeSignal>();
        services.AddSingleton<IPause>(pause);
        services.AddSingleton(Options.Create(new RoutesOptions()));
        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<RoutesDbContext>().Database.Migrate();
        return provider;
    }

    private static RoutesScheduler Scheduler(IServiceProvider sp) =>
        new(sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IRoutesBackend>(), sp.GetRequiredService<WakeSignal>(),
            sp.GetRequiredService<IPause>(), sp.GetRequiredService<IOptions<RoutesOptions>>(), NullLogger<RoutesScheduler>.Instance);

    [Fact]
    public async Task Unchanged_records_are_skipped_with_no_track_fetched()
    {
        var backend = new FakeRoutesBackend();
        var counter = new CommandCounter();
        await using var sp = Services(backend, new RecordingPause(), counter);
        if (sp == null)
        {
            return;
        }

        var owner = "skip-" + Guid.NewGuid();
        var records = backend.Add(owner, 60);
        backend.Add(owner, 3, points: 0); // trackless (Samsung Health): no track call for these
        using (var scope = sp.CreateScope())
        {
            counter.Count = 0;
            var first = await scope.ServiceProvider.GetRequiredService<AssessRunner>().RunFeedBatchAsync(owner, 500, false, default);
            Assert.Equal((63, 63, 0, false), (first.Seen, first.Assessed, first.Skipped, first.More));
            // ceil(60 / 25) track calls, never one per record
            Assert.Equal(3, backend.Calls.Count(c => c.Call == "tracks"));
            // cursor, stored rows, matched rows, one save (and its transaction)
            Assert.InRange(counter.Count, 4, 6);
        }

        var db = sp.CreateScope().ServiceProvider.GetRequiredService<RoutesDbContext>();
        var rows = await db.Quality.AsNoTracking().Where(q => q.OwnerId == owner).ToListAsync();
        Assert.Equal(63, rows.Count);
        Assert.Equal(3, rows.Count(r => r.Matchable == Matchability.NoTrack));
        var drive = rows.Single(r => r.ActivityId == records[0].Id);
        Assert.Equal(("drive", "google-timeline|x0", RouteQuality.RuleVersion), (drive.Type, drive.SourceKey, drive.RuleVersion));
        Assert.True(drive.NeedsImprovement);
        Assert.Equal(64, drive.InputHash.Length);

        // A re-fusion returns the whole window again: everything is skipped, nothing fetched
        backend.Calls.Clear();
        using (var scope = sp.CreateScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<AssessRunner>();
            var again = await runner.AssessChangesAsync(owner,
                backend.Records[owner].Select(r => new RouteChange(r.Id, r.UpdatedAt, r.PointCount, r.Type, r.SourceKey, r.SourceType)).ToList(),
                false, default);
            Assert.Equal((0, 63), again);
            Assert.DoesNotContain(backend.Calls, c => c.Call == "tracks");
        }

        // One record really changed: one track call, for one record
        backend.Touch(owner, records[5].Id);
        using (var scope = sp.CreateScope())
        {
            var next = await scope.ServiceProvider.GetRequiredService<AssessRunner>().RunFeedBatchAsync(owner, 500, false, default);
            Assert.Equal((1, 1, 0), (next.Seen, next.Assessed, next.Skipped));
            Assert.Equal(1, Assert.Single(backend.Calls, c => c.Call == "tracks").Count);
        }
    }

    [Fact]
    public async Task Owners_take_turns_in_batches_of_500_with_400_ms_pauses()
    {
        var backend = new FakeRoutesBackend();
        var pause = new RecordingPause();
        await using var sp = Services(backend, pause);
        if (sp == null)
        {
            return;
        }

        string big = "rr-big-" + Guid.NewGuid(), small = "rr-small-" + Guid.NewGuid();
        backend.Add(big, 1200, points: 0);
        backend.Add(small, 10, points: 0);
        var scheduler = Scheduler(sp);

        Assert.True(await scheduler.TurnAsync(default));
        // One batch per Owner per turn: the new Owner is served in the first turn
        var changes = backend.Calls.Where(c => c.Call == "changes").ToList();
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal(500, c.Count));
        Assert.Equal(new[] { big, small }.OrderBy(o => o), changes.Select(c => c.Owner).OrderBy(o => o));
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(400) }, pause.Pauses);

        // The big backfill goes on; the small Owner is not polled again before its interval
        Assert.True(await scheduler.TurnAsync(default));
        Assert.True(await scheduler.TurnAsync(default));
        Assert.False(await scheduler.TurnAsync(default));
        Assert.Equal(new[] { big, big }, backend.Calls.Where(c => c.Call == "changes").Skip(2).Select(c => c.Owner));
        var db = sp.CreateScope().ServiceProvider.GetRequiredService<RoutesDbContext>();
        Assert.Equal(1200, await db.Quality.CountAsync(q => q.OwnerId == big));
        Assert.Equal(10, await db.Quality.CountAsync(q => q.OwnerId == small));

        // A wake makes the Owner due at once
        backend.Add(small, 2, points: 0);
        sp.GetRequiredService<WakeSignal>().Wake(small);
        Assert.True(await scheduler.TurnAsync(default));
        Assert.Equal(12, await db.Quality.CountAsync(q => q.OwnerId == small));
    }

    [Fact]
    public async Task A_429_pauses_the_turn_for_its_retry_after()
    {
        var backend = new FakeRoutesBackend();
        var pause = new RecordingPause();
        await using var sp = Services(backend, pause);
        if (sp == null)
        {
            return;
        }

        var owner = "busy-" + Guid.NewGuid();
        backend.Add(owner, 5, points: 0);
        backend.BusyFor[owner] = TimeSpan.FromSeconds(3);
        var scheduler = Scheduler(sp);

        Assert.True(await scheduler.TurnAsync(default));
        Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, pause.Pauses);
        var db = sp.CreateScope().ServiceProvider.GetRequiredService<RoutesDbContext>();
        Assert.Equal(0, await db.Quality.CountAsync(q => q.OwnerId == owner));

        backend.BusyFor.Remove(owner);
        Assert.True(await scheduler.TurnAsync(default));
        Assert.Equal(5, await db.Quality.CountAsync(q => q.OwnerId == owner));
    }

    [Fact]
    public async Task One_owner_cannot_read_or_queue_into_another_owners_routes()
    {
        var backend = new FakeRoutesBackend();
        await using var sp = Services(backend, new RecordingPause());
        if (sp == null)
        {
            return;
        }

        string a = "two-a-" + Guid.NewGuid(), b = "two-b-" + Guid.NewGuid();
        var aRecords = backend.Add(a, 3);
        backend.Add(b, 2);
        var scheduler = Scheduler(sp);
        await scheduler.TurnAsync(default);

        var db = sp.CreateScope().ServiceProvider.GetRequiredService<RoutesDbContext>();
        // Every row and cursor carries its own Owner, and only that Owner's records
        Assert.Equal(aRecords.Select(r => r.Id).OrderBy(x => x),
            (await db.Quality.Where(q => q.OwnerId == a).Select(q => q.ActivityId).ToListAsync()).OrderBy(x => x));
        Assert.Equal(2, await db.Quality.CountAsync(q => q.OwnerId == b));
        Assert.Equal(2, await db.Cursors.CountAsync(c => c.OwnerId == a || c.OwnerId == b));

        // B queues a job naming A's records: the backend returns none of them, nothing is written for B
        db.Jobs.Add(new RouteJob
        {
            OwnerId = b, Priority = RouteJobPriorities.Interactive, Total = 3,
            Scope = System.Text.Json.JsonSerializer.Serialize(new JobScope(aRecords.Select(r => r.Id).ToList(), null),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
        });
        await db.SaveChangesAsync();
        backend.Calls.Clear();
        await scheduler.TurnAsync(default);

        var tracks = Assert.Single(backend.Calls, c => c.Call == "tracks");
        Assert.Equal(b, tracks.Owner);
        Assert.True(tracks.Interactive);
        Assert.Equal(2, await db.Quality.CountAsync(q => q.OwnerId == b));
        Assert.False(await db.Quality.AnyAsync(q => q.OwnerId == b && aRecords.Select(r => r.Id).Contains(q.ActivityId)));
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.OwnerId == b);
        Assert.Equal(RouteJobStates.Done, job.State);
        // A asks for B's job: not found
        Assert.False(await db.Jobs.AnyAsync(j => j.OwnerId == a && j.Id == job.Id));

        // An Owner who drops out of the owners list loses its cursor
        backend.Records.Remove(b);
        var fresh = Scheduler(sp);
        await fresh.TurnAsync(default);
        Assert.False(await db.Cursors.AnyAsync(c => c.OwnerId == b));
        Assert.True(await db.Cursors.AnyAsync(c => c.OwnerId == a));
    }

    [Fact]
    public void Every_routes_table_carries_OwnerId_first_in_every_index()
    {
        using var db = new RoutesDbContextFactory().CreateDbContext(Array.Empty<string>());
        foreach (var entity in db.Model.GetEntityTypes())
        {
            Assert.NotNull(entity.FindProperty("OwnerId"));
            Assert.Equal("routes", entity.GetSchema());
            foreach (var index in entity.GetIndexes())
            {
                Assert.Equal("OwnerId", index.Properties[0].Name);
            }

            var key = entity.FindPrimaryKey()!;
            Assert.True(key.Properties[0].Name is "OwnerId" or "Id", $"{entity.GetTableName()} key");
        }

        Assert.Equal(6, db.Model.GetEntityTypes().Count());
    }
}

/// <summary>The service's client of the backend's internal API: envelope, 409, 429, priority header</summary>
public class RoutesBackendClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Answer = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Answer(request));
        }
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public Factory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, false) { BaseAddress = new Uri("http://backend:5000/") };
    }

    private static HttpResponseMessage Json(object body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), System.Text.Encoding.UTF8, "application/json")
        };

    [Fact]
    public async Task Reads_the_envelope_and_marks_interactive_calls()
    {
        var id = Guid.NewGuid();
        var t = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var handler = new StubHandler
        {
            Answer = _ => Json(new
            {
                success = true,
                data = new RouteChangesPage(new List<RouteChange> { new(id, t, 13, "drive", "google-timeline|x", "IN_BUS") }, "c2", true)
            })
        };
        var client = new RoutesBackendClient(new Factory(handler));

        var page = await client.ChangesAsync("owner 1", "c1", 500, true, default);

        Assert.Equal((id, t, 13, "drive", "IN_BUS"), (page.Items[0].Id, page.Items[0].UpdatedAt, page.Items[0].PointCount, page.Items[0].Type, page.Items[0].SourceType));
        Assert.Equal(("c2", true), (page.Next, page.More));
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/internal/routes/changes?ownerId=owner%201&limit=500&after=c1", request.RequestUri!.PathAndQuery);
        Assert.Equal(EchoPriority.Interactive, request.Headers.GetValues(EchoPriority.Header).Single());

        handler.Answer = _ => Json(new { success = true, data = Array.Empty<object>() });
        Assert.Empty(await client.TracksAsync("o", new[] { id }, false, default));
        Assert.Equal($"/internal/routes/tracks?ownerId=o&ids={id}", handler.Requests[^1].RequestUri!.PathAndQuery);
        Assert.False(handler.Requests[^1].Headers.Contains(EchoPriority.Header));
    }

    [Fact]
    public async Task A_429_carries_its_retry_after_and_a_409_means_fusion_is_running()
    {
        var handler = new StubHandler();
        var client = new RoutesBackendClient(new Factory(handler));

        handler.Answer = _ =>
        {
            var r = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return r;
        };
        var busy = await Assert.ThrowsAsync<BackendBusyException>(() => client.OwnersAsync(default));
        Assert.Equal(TimeSpan.FromSeconds(2), busy.RetryAfter);

        handler.Answer = _ => Json(new { success = false, error = new { code = "DERIVATION_NOT_READY", message = "wait" } }, System.Net.HttpStatusCode.Conflict);
        await Assert.ThrowsAsync<DerivationPendingException>(() => client.ChangesAsync("o", null, 500, false, default));
    }
}
