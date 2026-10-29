using System.Data.Common;
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Routes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContactsApi.Tests;

/// <summary>Counts the SQL commands a context sends (for the no-round-trip-per-record tests)</summary>
internal sealed class CommandCounter : DbCommandInterceptor
{
    public int Count;

    /// <summary>The text of every query read (to check what a query selects)</summary>
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> Queries = new();

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Count);
        Queries.Enqueue(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Count);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Count);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>The backend's internal API for the route-quality service, against PostgreSQL (ATLAS_TEST_DB)</summary>
public class RoutesFeedDbTests
{
    private static readonly DateTime T0 = new(2026, 9, 20, 6, 0, 0, DateTimeKind.Utc);

    private sealed class QueueSignal : IFusionSignal
    {
        public void Notify()
        {
        }
    }

    private static async Task<AtlasDbContext?> DbAsync(CommandCounter? counter = null)
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        if (string.IsNullOrEmpty(cs))
        {
            return null;
        }

        var options = new DbContextOptionsBuilder<AtlasDbContext>().UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations", "atlas"));
        if (counter != null)
        {
            options.AddInterceptors(counter);
        }

        var db = new AtlasDbContext(options.Options);
        await db.Database.MigrateAsync();
        return db;
    }

    private static List<PointInput> Track(int n, double lat0, int secondsApart = 60, double step = 0.005) =>
        Enumerable.Range(0, n).Select(i => new PointInput(T0.AddSeconds(i * secondsApart), lat0 + i * step, 34.85)).ToList();

    [Fact]
    public async Task Ingest_keeps_the_source_type_and_a_reimport_fills_it_without_duplicates()
    {
        await using var db = await DbAsync();
        if (db == null)
        {
            return;
        }

        var owner = "srctype-" + Guid.NewGuid();
        var svc = new AtlasService(db, NullLogger<AtlasService>.Instance);
        var request = new IngestRequest
        {
            Source = "google-timeline",
            Activities = { new ActivityInput("tl-bus", "IN_BUS", null, T0, T0.AddMinutes(20), Points: Track(10, 32.0)) }
        };
        await svc.IngestAsync(owner, request);
        var bus = await db.Activities.AsNoTracking().SingleAsync(a => a.OwnerId == owner);
        Assert.Equal("transit", bus.Type);
        Assert.Equal("IN_BUS", bus.SourceType);

        // A record imported before the column existed: a re-import fills it in
        await db.Activities.Where(a => a.OwnerId == owner).ExecuteUpdateAsync(s => s.SetProperty(a => a.SourceType, (string?)null));
        db.ChangeTracker.Clear();
        await svc.IngestAsync(owner, request);
        var again = await db.Activities.AsNoTracking().SingleAsync(a => a.OwnerId == owner);
        Assert.Equal(bus.Id, again.Id);
        Assert.Equal("IN_BUS", again.SourceType);
        Assert.True(again.UpdatedAt > bus.UpdatedAt);
    }

    [Fact]
    public async Task Changes_wait_for_fusion_then_page_every_record_with_its_values()
    {
        await using var db = await DbAsync();
        if (db == null)
        {
            return;
        }

        var owner = "feed-" + Guid.NewGuid();
        var svc = new AtlasService(db, NullLogger<AtlasService>.Instance, new QueueSignal());
        var feed = new AtlasRoutesFeed(db);
        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "google-timeline",
            Activities =
            {
                new ActivityInput("d1", "IN_PASSENGER_VEHICLE", null, T0, T0.AddMinutes(30), Points: Track(13, 32.0)),
                new ActivityInput("d2", "IN_PASSENGER_VEHICLE", null, T0.AddHours(3), T0.AddHours(3.5), Points: Track(13, 32.2)),
                new ActivityInput("s1", "WALKING", null, T0.AddHours(6), T0.AddHours(7))
            },
            Visits = { new VisitInput("home", T0.AddHours(-2), T0.AddMinutes(-5), 31.99, 34.85) }
        });

        // Fusion is queued: routes wait for it
        await Assert.ThrowsAsync<DerivationNotReadyException>(() => feed.ChangesAsync(owner, null));
        await svc.ProcessFusionQueueAsync(owner);

        var all = new List<RouteChange>();
        string? cursor = null;
        var pages = 0;
        while (true)
        {
            var page = await feed.ChangesAsync(owner, cursor, 2);
            all.AddRange(page.Items);
            cursor = page.Next;
            pages++;
            if (!page.More)
            {
                break;
            }
        }

        Assert.Equal(2, pages);
        Assert.Equal(3, all.Select(c => c.Id).Distinct().Count());
        var d1 = all.Single(c => c.SourceKey == "google-timeline|d1");
        Assert.Equal("drive", d1.Type);
        Assert.Equal(13, d1.PointCount);
        Assert.Equal("IN_PASSENGER_VEHICLE", d1.SourceType);
        var stored = await db.Activities.AsNoTracking().SingleAsync(a => a.Id == d1.Id);
        Assert.Equal(stored.UpdatedAt, d1.UpdatedAt);
        Assert.Equal(0, all.Single(c => c.SourceKey == "google-timeline|s1").PointCount);

        // Nothing changed since: an empty page and the same cursor
        var empty = await feed.ChangesAsync(owner, cursor);
        Assert.Empty(empty.Items);
        Assert.Equal(cursor, empty.Next);

        // Tracks: points in order, and the Visit just before
        var tracks = await feed.TracksAsync(owner, new[] { d1.Id });
        var track = Assert.Single(tracks);
        Assert.Equal(13, track.Points.Count);
        Assert.True(track.Points.Zip(track.Points.Skip(1)).All(p => p.First.T < p.Second.T));
        Assert.Equal(T0.AddMinutes(-5), track.Before!.End);
        Assert.Equal("drive", track.Type);
        Assert.Equal("google-timeline|d1", track.SourceKey);
    }

    [Fact]
    public async Task Another_owner_reads_nothing_through_any_endpoint()
    {
        await using var db = await DbAsync();
        if (db == null)
        {
            return;
        }

        string a = "iso-a-" + Guid.NewGuid(), b = "iso-b-" + Guid.NewGuid();
        var svc = new AtlasService(db, NullLogger<AtlasService>.Instance);
        await svc.IngestAsync(a, new IngestRequest
        {
            Source = "gpx", Activities = { new ActivityInput("a.gpx", "driving", null, null, null, Points: Track(20, 32.4)) }
        });
        await svc.IngestAsync(b, new IngestRequest
        {
            Source = "gpx", Activities = { new ActivityInput("b.gpx", "driving", null, null, null, Points: Track(20, 32.6)) }
        });
        var feed = new AtlasRoutesFeed(db);
        var aIds = (await feed.ChangesAsync(a, null)).Items.Select(i => i.Id).ToList();
        var bIds = (await feed.ChangesAsync(b, null)).Items.Select(i => i.Id).ToList();

        Assert.Single(aIds);
        Assert.Single(bIds);
        Assert.Empty(aIds.Intersect(bIds));
        // b asks for a's records by id: nothing comes back
        Assert.Empty(await feed.TracksAsync(b, aIds));
        Assert.Empty(await feed.ExistsAsync(b, aIds));
        Assert.Equal(aIds, await feed.ExistsAsync(a, aIds.Append(Guid.NewGuid()).ToList()));
        var owners = await feed.OwnersAsync();
        Assert.Contains(a, owners);
        Assert.Contains(b, owners);
        Assert.Equal(owners.Count, owners.Distinct().Count());
        await Assert.ThrowsAsync<ArgumentException>(() => feed.TracksAsync(a, Enumerable.Range(0, 26).Select(_ => Guid.NewGuid()).ToList()));
    }

    [Fact]
    public async Task A_page_of_changes_and_a_batch_of_tracks_take_a_fixed_number_of_queries()
    {
        var counter = new CommandCounter();
        await using var db = await DbAsync(counter);
        if (db == null)
        {
            return;
        }

        var owner = "queries-" + Guid.NewGuid();
        var svc = new AtlasService(db, NullLogger<AtlasService>.Instance);
        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "google-timeline",
            Activities = Enumerable.Range(0, 25)
                .Select(i => new ActivityInput($"q{i}", "IN_PASSENGER_VEHICLE", null, T0.AddHours(i), T0.AddHours(i).AddMinutes(30), Points: Track(8, 32 + i * 0.01)))
                .ToList()
        });
        var feed = new AtlasRoutesFeed(db);

        counter.Count = 0;
        var page = await feed.ChangesAsync(owner, null);
        Assert.Equal(25, page.Items.Count);
        Assert.Equal(2, counter.Count); // the fusion queue check, the page

        counter.Count = 0;
        var tracks = await feed.TracksAsync(owner, page.Items.Select(i => i.Id).ToList());
        Assert.Equal(25, tracks.Count);
        Assert.Equal(3, counter.Count); // records, points, Visits
    }

    [Fact]
    public async Task Cluster_resets_move_UpdatedAt_only_when_the_cluster_changes()
    {
        await using var db = await DbAsync();
        if (db == null)
        {
            return;
        }

        var owner = "cluster-" + Guid.NewGuid();
        var svc = new AtlasService(db, NullLogger<AtlasService>.Instance);
        // A ride recorded by Sports Tracker and seen by Google Timeline: one fused activity
        var st = await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "sports-tracker",
            Activities = { new ActivityInput("st-1", "cycling", null, T0, T0.AddHours(1), Points: Track(120, 32.8, 30, 0.001)) }
        });
        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "google-timeline",
            Activities = { new ActivityInput("tl-1", "IN_PASSENGER_VEHICLE", null, T0.AddMinutes(1), T0.AddMinutes(58), DistanceM: 19_000, Points: Track(20, 32.8, 30, 0.001)) }
        });
        var timeline = await db.Activities.AsNoTracking().SingleAsync(x => x.OwnerId == owner && x.Source == "google-timeline");
        Assert.NotNull(timeline.ClusterId);

        // An unchanged re-fusion touches nobody
        await svc.FuseAsync(owner);
        var same = await db.Activities.AsNoTracking().SingleAsync(x => x.Id == timeline.Id);
        Assert.Equal(timeline.UpdatedAt, same.UpdatedAt);
        Assert.Equal(timeline.ClusterId, same.ClusterId);

        // Deleting the primary's import dissolves the cluster: the Timeline record moves
        await svc.DeleteImportAsync(owner, st.ImportId);
        var alone = await db.Activities.AsNoTracking().SingleAsync(x => x.Id == timeline.Id);
        Assert.Null(alone.ClusterId);
        Assert.True(alone.UpdatedAt > timeline.UpdatedAt);
    }

    [Fact]
    public async Task Fusion_marks_a_distance_taken_from_the_track()
    {
        await using var db = await DbAsync();
        if (db == null)
        {
            return;
        }

        var owner = "trackdist-" + Guid.NewGuid();
        var svc = new AtlasService(db, NullLogger<AtlasService>.Instance);
        // The source says 100 m; the track is about 4 km
        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "sports-tracker",
            Activities = { new ActivityInput("st-short", "cycling", null, T0, T0.AddMinutes(40), DistanceM: 100, Points: Track(80, 32.9, 30)) }
        });
        var id = await db.Activities.AsNoTracking().Where(x => x.OwnerId == owner).Select(x => x.Id).SingleAsync();
        var detail = await svc.GetActivityAsync(owner, id);

        Assert.Equal("track", detail.FieldSources["distance"]);
        Assert.True(detail.Summary.DistanceM > 1000);
    }
}
