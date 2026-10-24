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

public class FusionToleranceTests
{
    private static readonly DateTime T0 = new(2026, 9, 3, 6, 0, 0, DateTimeKind.Utc);

    private static FusionCandidate C(string source, int startMin, int endMin) =>
        new(Guid.NewGuid(), source, "ride", null, T0.AddMinutes(startMin), T0.AddMinutes(endMin), 20_000, null, null, null,
            32.8, 35.0, 32.9, 35.05, 120);

    private static List<TrackSample> Track(double latOffset) =>
        Enumerable.Range(0, 60).Select(i => new TrackSample(T0.AddMinutes(i), 32.8 + i * 0.001 + latOffset, 35.0)).ToList();

    [Fact]
    public void Compares_tracks_at_the_same_moments()
    {
        Assert.InRange(Fusion.Deviation(Track(0), Track(0.001))!.Value, 105, 117);
        Assert.Null(Fusion.Deviation(Track(0), new List<TrackSample>()));
    }

    [Fact]
    public void Two_recordings_at_the_same_time_but_far_apart_are_not_one_activity()
    {
        var mine = C("sports-tracker", 0, 59);
        var file = C("gpx", 0, 59);
        var near = new Dictionary<Guid, List<TrackSample>> { [mine.Id] = Track(0), [file.Id] = Track(0.0002) };
        var e = Fusion.Evaluate(mine, file, FusionRules.Default, near)!;
        Assert.Equal(1, e.Confidence);
        Assert.Equal(55, e.LocationToleranceM + 5);
        Assert.Single(Fusion.Group(new[] { mine, file }, [], [], FusionRules.Default, near));

        // Same minutes, but 2 km apart: a friend's ride, not mine
        var far = new Dictionary<Guid, List<TrackSample>> { [mine.Id] = Track(0), [file.Id] = Track(0.018) };
        Assert.Equal(0, Fusion.Evaluate(mine, file, FusionRules.Default, far)!.Confidence);
        Assert.Equal(2, Fusion.Group(new[] { mine, file }, [], [], FusionRules.Default, far).Count);

        // Joined by the user anyway: the track difference is a location conflict
        var fused = Fusion.Fuse(new[] { mine, file }, FusionRules.Default, far, new HashSet<Guid> { mine.Id, file.Id });
        Assert.Contains(fused.Conflicts, c => c.Field == "location");
        Assert.True(fused.Evidence.Single().UserDecision);
    }

    [Fact]
    public void A_ride_inside_a_long_segment_matches_but_a_moment_does_not_chain_two_activities()
    {
        var ride = C("sports-tracker", 60, 120);
        var segment = C("google-timeline", 0, 180);
        Assert.Single(Fusion.Group(new[] { ride, segment }, [], []));

        // A moment inside both a run and an overlapping drive does not make them one
        var run = C("sports-tracker", 0, 60);
        var drive = new FusionCandidate(Guid.NewGuid(), "google-timeline", "drive", null, T0.AddMinutes(50), T0.AddMinutes(90),
            30_000, null, null, null, 32.8, 35.0, 32.9, 35.05, 40);
        var moment = new FusionCandidate(Guid.NewGuid(), "fitbit", "other", null, T0.AddMinutes(55), T0.AddMinutes(55),
            null, null, null, null, null, null, null, null, 0);
        var groups = Fusion.Group(new[] { run, drive, moment }, [], []);
        Assert.DoesNotContain(groups, g => g.Contains(run.Id) && g.Contains(drive.Id));
    }

    [Fact]
    public void Wider_tolerances_accept_looser_matches()
    {
        var ride = C("sports-tracker", 0, 60);
        var guess = C("google-timeline", 35, 105);
        Assert.Equal(2, Fusion.Group(new[] { ride, guess }, [], []).Count);
        var loose = new FusionRules(FusionRules.Default.Sources.ToDictionary(kv => kv.Key,
            kv => kv.Key == "google-timeline" ? new Tolerance(3600, 300) : kv.Value), 0.5);
        Assert.Single(Fusion.Group(new[] { ride, guess }, [], [], loose));
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

        // Fusion settings: saved clamped, shown, reset
        var saved = await svc.SaveFusionSettingsAsync(owner, new FusionSettingsDto(2, new List<SourceToleranceDto>
            { new("google-timeline", 999_999, 50), new("nonsense", 1, 1) }));
        Assert.Equal(0.99, saved.MatchThreshold);
        var tl = saved.Sources.Single(x => x.Source == "google-timeline");
        Assert.Equal(21_600, tl.TimeToleranceS);
        Assert.False(tl.IsDefault);
        Assert.True((await svc.ResetFusionSettingsAsync(owner)).Sources.All(x => x.IsDefault));

        // Deleting the fused activity deletes both records
        await svc.DeleteActivityAsync(owner, merged.Summary.Id);
        Assert.Empty((await svc.SearchAsync(owner, new AtlasQuery { Visits = false })).Activities);
        Assert.Equal(0, await db.Activities.CountAsync(a => a.OwnerId == owner));
    }
}

/// <summary>The fusion service path: imports queue, the service fuses (against PostgreSQL when ATLAS_TEST_DB is set)</summary>
public class FusionQueueDbTests
{
    private sealed class CountingSignal : IFusionSignal
    {
        public int Count;

        public void Notify() => Count++;
    }

    [Fact]
    public async Task Imports_queue_the_service_fuses_and_health_enhances()
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        if (string.IsNullOrEmpty(cs))
        {
            return;
        }

        await using var db = new AtlasDbContext(new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations", "atlas")).Options);
        await db.Database.MigrateAsync();
        var owner = "queue-" + Guid.NewGuid();
        var signal = new CountingSignal();
        var svc = new AtlasService(db, NullLogger<AtlasService>.Instance, signal);
        var t0 = new DateTime(2026, 9, 5, 7, 0, 0, DateTimeKind.Utc);
        List<PointInput> Track(int n) => Enumerable.Range(0, n).Select(i => new PointInput(t0.AddSeconds(i * 30), 32.70 + i * 0.001, 35.1)).ToList();

        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "gpx", Activities = { new ActivityInput("walk.gpx", "walking", "Evening walk", null, null, Points: Track(80)) }
        });
        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "google-timeline", Activities = { new ActivityInput("tl-w", "WALKING", null, t0.AddMinutes(1), t0.AddMinutes(39), Points: Track(10)) }
        });
        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "fitbit", Health = Enumerable.Range(0, 40).Select(i => new HealthInput("heart_rate", t0.AddMinutes(i), 100 + i)).ToList()
        });
        Assert.Equal(3, signal.Count);
        Assert.True(await svc.PendingFusionAsync(owner) >= 3);
        Assert.Equal(2, (await svc.SearchAsync(owner, new AtlasQuery())).Activities.Count); // not fused yet

        await svc.ProcessFusionQueueAsync(owner);
        Assert.Equal(0, await svc.PendingFusionAsync(owner));
        var walk = Assert.Single((await svc.SearchAsync(owner, new AtlasQuery())).Activities);
        Assert.Equal(2, walk.MemberCount);
        Assert.Contains("fitbit", walk.Sources);
        Assert.InRange(walk.AvgHeartRate!.Value, 115, 125);

        var detail = await svc.GetActivityAsync(owner, walk.Id);
        var evidence = Assert.Single(detail.Evidence);
        Assert.Equal("google-timeline", evidence.Source);
        Assert.Equal(0, evidence.TrackDeviationM);
        Assert.Equal(1, detail.MatchConfidence);
    }
}
