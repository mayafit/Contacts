using ContactsApi.Infrastructure.Atlas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContactsApi.Tests;

public class FusionRuleTests
{
    private static readonly DateTime T0 = new(2026, 9, 3, 6, 0, 0, DateTimeKind.Utc);

    private static FusionCandidate C(string source, string type, int startMin, int endMin, double lat = 32.8, int points = 100,
        double? distance = 21_000, int? hr = null) =>
        new(Guid.NewGuid(), source, type, null, T0.AddMinutes(startMin), T0.AddMinutes(endMin), distance, null, hr, hr,
            lat, 35.0, lat + 0.05, 35.05, points);

    [Fact]
    public void Joins_a_recording_with_the_phone_guess_and_reports_the_conflict()
    {
        var st = C("sports-tracker", "ride", 0, 60, hr: 140);
        var timeline = C("google-timeline", "drive", 2, 58, points: 20, distance: 17_000);
        var groups = Fusion.Group(new[] { st, timeline }, [], []);
        Assert.Single(groups);

        var fused = Fusion.Fuse(new[] { timeline, st });
        Assert.Equal(st.Id, fused.PrimaryId);
        Assert.Equal("ride", fused.Type);
        Assert.Equal(21_000, fused.DistanceM);
        Assert.Equal("sports-tracker", fused.FieldSources["type"]);
        Assert.Contains(fused.Conflicts, c => c.Field == "type" && c.Message.Contains("Google Timeline says drive"));
        Assert.Contains(fused.Conflicts, c => c.Field == "distanceM");
    }

    [Fact]
    public void Keeps_apart_what_is_far_away_short_overlapping_or_split_by_the_user()
    {
        var ride = C("sports-tracker", "ride", 0, 60);
        var elsewhere = C("google-timeline", "walk", 0, 60, lat: 31.0);
        var barely = C("google-fit", "run", 50, 120);
        Assert.Equal(3, Fusion.Group(new[] { ride, elsewhere, barely }, [], []).Count);

        var fit = C("google-fit", "ride", 1, 59, points: 0);
        Assert.Single(Fusion.Group(new[] { ride, fit }, [], []));
        Assert.Equal(2, Fusion.Group(new[] { ride, fit }, [], [(ride.Id, fit.Id)]).Count);
    }

    [Fact]
    public void A_user_merge_wins_and_one_source_twice_needs_matching_times()
    {
        var a = C("gpx", "hike", 0, 60);
        var b = C("google-fit", "hike", 200, 260);
        Assert.Single(Fusion.Group(new[] { a, b }, [(a.Id, b.Id)], []));

        var file = C("sports-tracker", "ride", 0, 60);
        var synced = C("sports-tracker", "ride", 1, 61);
        var next = C("sports-tracker", "ride", 30, 90);
        Assert.Equal(2, Fusion.Group(new[] { file, synced, next }, [], []).Count);
    }
}

public class FusionRegressionTests
{
    private static readonly DateTime T0 = new(2026, 9, 3, 6, 0, 0, DateTimeKind.Utc);

    private static FusionCandidate C(string source, string type, int startMin, int endMin, bool byUser = false) =>
        new(Guid.NewGuid(), source, type, null, T0.AddMinutes(startMin), T0.AddMinutes(endMin), 20_000, null, null, null,
            32.8, 35.0, 32.85, 35.05, 100, byUser);

    [Fact]
    public void A_long_timeline_segment_does_not_join_two_separate_rides()
    {
        var morning = C("sports-tracker", "ride", 0, 50);
        var afternoon = C("sports-tracker", "ride", 70, 120);
        var segment = C("google-timeline", "ride", 0, 120);
        var groups = Fusion.Group(new[] { morning, afternoon, segment }, [], []);
        Assert.Equal(2, groups.Count);
        Assert.DoesNotContain(groups, g => g.Contains(morning.Id) && g.Contains(afternoon.Id));
    }

    [Fact]
    public void A_short_walk_inside_a_long_drive_stays_separate()
    {
        var drive = C("google-timeline", "drive", 0, 40);
        var walk = C("google-fit", "walk", 10, 15);
        Assert.Equal(2, Fusion.Group(new[] { drive, walk }, [], []).Count);
        var ride = C("sports-tracker", "ride", 0, 60);
        var guess = C("google-timeline", "drive", 5, 55);
        Assert.Single(Fusion.Group(new[] { ride, guess }, [], []));
    }

    [Fact]
    public void A_moment_joins_what_it_falls_within_and_the_user_type_wins()
    {
        var ride = C("sports-tracker", "ride", 0, 60);
        var moment = C("fitbit", "other", 30, 30);
        Assert.Single(Fusion.Group(new[] { ride, moment }, [], []));

        var fused = Fusion.Fuse(new[] { C("sports-tracker", "ride", 0, 60), C("google-timeline", "walk", 0, 60, byUser: true) });
        Assert.Equal("walk", fused.Type);
        Assert.Equal("user", fused.FieldSources["type"]);
    }
}

public class SportsTrackerParsingTests
{
    [Fact]
    public void Reads_the_workout_list_and_a_gpx_track()
    {
        var workouts = SportsTrackerClient.ParseWorkouts("""
            {"error":null,"payload":[
              {"workoutKey":"abc123","activityId":2,"workoutName":"Carmel","startTime":1788415200000,"stopTime":1788418800000,
               "totalTime":3600.5,"totalDistance":21300.0,"totalAscent":420,"hrdata":{"avg":141,"hrmax":168}},
              {"key":"def456","activityId":11,"startTime":1788501600000,"totalTime":7200},
              {"activityId":1}
            ]}
            """);
        Assert.Equal(2, workouts.Count);
        Assert.Equal("abc123", workouts[0].Key);
        Assert.Equal(141, workouts[0].AvgHeartRate);
        Assert.Equal("hike", SportsTrackerClient.TypeOf(workouts[1].ActivityId));

        var gpx = """
            <?xml version="1.0"?>
            <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1" xmlns:gpxtpx="http://www.garmin.com/xmlschemas/TrackPointExtension/v1">
              <trk><trkseg>
                <trkpt lat="32.75" lon="35.02"><ele>120</ele><time>2026-09-03T06:00:00Z</time>
                  <extensions><gpxtpx:TrackPointExtension><gpxtpx:hr>132</gpxtpx:hr><gpxtpx:cad>80</gpxtpx:cad></gpxtpx:TrackPointExtension></extensions></trkpt>
                <trkpt lat="32.76" lon="35.03"><time>2026-09-03T06:10:00Z</time></trkpt>
                <trkpt lat="bad" lon="35.03"><time>2026-09-03T06:11:00Z</time></trkpt>
              </trkseg></trk>
            </gpx>
            """;
        var activity = SportsTrackerClient.ToActivity(workouts[0], gpx);
        Assert.Equal("ride", activity.Type);
        Assert.Equal("abc123", activity.ExternalId);
        Assert.Equal(2, activity.Points!.Count);
        Assert.Equal(132, activity.Points[0].Hr);
        Assert.Equal(80, activity.Points[0].Cad);
        Assert.Equal(new DateTime(2026, 9, 3, 6, 0, 0, DateTimeKind.Utc), activity.Points[0].T);
        Assert.Null(SportsTrackerClient.ToActivity(workouts[1], null).Points);
    }
}

/// <summary>Against a real PostgreSQL when ATLAS_TEST_DB is set (skipped otherwise)</summary>
public class AtlasFusionDbTests
{
    [Fact]
    public async Task Fuses_on_import_splits_merges_and_deletes_together()
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        if (string.IsNullOrEmpty(cs))
        {
            return;
        }

        await using var db = new AtlasDbContext(new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations", "atlas")).Options);
        await db.Database.MigrateAsync();
        var owner = "fusion-" + Guid.NewGuid();
        var svc = new AtlasService(db, NullLogger<AtlasService>.Instance);
        var t0 = new DateTime(2026, 9, 3, 6, 0, 0, DateTimeKind.Utc);
        List<PointInput> Track(int n, double lat0, int? hr) =>
            Enumerable.Range(0, n).Select(i => new PointInput(t0.AddSeconds(i * 30), lat0 + i * 0.001, 35.0, 100, hr)).ToList();

        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "sports-tracker",
            Activities = { new ActivityInput("st-1", "cycling", "Carmel", t0, t0.AddHours(1), Points: Track(120, 32.80, 140)) },
            Visits = { new VisitInput("cafe", t0.AddMinutes(-20), t0.AddMinutes(-2), 32.80, 35.0, PlaceName: "Start café") }
        });
        // Google Timeline saw the same hour as driving, along the same road
        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "google-timeline",
            Activities = { new ActivityInput("tl-1", "IN_PASSENGER_VEHICLE", null, t0.AddMinutes(1), t0.AddMinutes(58), DistanceM: 19_000, Points: Track(20, 32.80, null)) }
        });

        var found = await svc.SearchAsync(owner, new AtlasQuery());
        var ride = Assert.Single(found.Activities);
        Assert.Equal("ride", ride.Type);
        Assert.Equal(2, ride.MemberCount);
        Assert.Equal(new[] { "sports-tracker", "google-timeline" }, ride.Sources);
        Assert.True(ride.ConflictCount > 0);
        Assert.Equal(1, (await svc.SummaryAsync(owner)).Activities);

        var detail = await svc.GetActivityAsync(owner, ride.Id);
        Assert.Equal(2, detail.Members.Count);
        Assert.True(detail.Members[0].Primary);
        Assert.Equal("sports-tracker", detail.FieldSources["heartRate"]);
        Assert.Contains(detail.Conflicts, c => c.Field == "type");
        Assert.Contains(detail.Places, p => p.Role == "start" && p.PlaceName == "Start café");

        // A member's id leads to the fused activity
        var timelineId = detail.Members.Single(m => !m.Primary).Id;
        Assert.Equal(ride.Id, (await svc.GetActivityAsync(owner, timelineId)).Summary.Id);

        // Split: two activities, and re-fusing keeps them apart
        await svc.SplitActivityAsync(owner, ride.Id, timelineId);
        Assert.Equal(2, (await svc.SearchAsync(owner, new AtlasQuery())).Activities.Count);
        await svc.FuseAsync(owner);
        Assert.Equal(2, (await svc.SearchAsync(owner, new AtlasQuery())).Activities.Count);

        // Merge again: one activity
        var merged = await svc.MergeActivitiesAsync(owner, ride.Id, timelineId);
        Assert.Equal(2, merged.Members.Count);
        Assert.Single((await svc.SearchAsync(owner, new AtlasQuery())).Activities);

        // Re-importing the timeline keeps the fusion
        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "google-timeline",
            Activities = { new ActivityInput("tl-1", "IN_PASSENGER_VEHICLE", null, t0.AddMinutes(1), t0.AddMinutes(58), DistanceM: 19_000, Points: Track(20, 32.80, null)) }
        });
        Assert.Single((await svc.SearchAsync(owner, new AtlasQuery())).Activities);

        // A type set by hand wins over the sources and survives re-fusion
        var edited = await svc.UpdateActivityAsync(owner, merged.Summary.Id, new ActivityPatch(Type: "hike"));
        Assert.Equal("hike", edited.Type);
        await svc.FuseAsync(owner);
        Assert.Equal("hike", (await svc.SearchAsync(owner, new AtlasQuery())).Activities.Single().Type);

        // Deleting the fused activity deletes both records
        await svc.DeleteActivityAsync(owner, merged.Summary.Id);
        Assert.Empty((await svc.SearchAsync(owner, new AtlasQuery { Visits = false })).Activities);
        Assert.Equal(0, await db.Activities.CountAsync(a => a.OwnerId == owner));
    }
}
