using System.Text.Json;
using ContactsApi.Infrastructure.Atlas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContactsApi.Tests;

public class OneDriveParseTests
{
    [Fact]
    public void Delta_keeps_photos_and_videos_and_reports_the_rest_as_removed()
    {
        var json = """
        {
          "@odata.nextLink": "https://graph.microsoft.com/v1.0/me/drive/root/delta?token=2",
          "value": [
            { "id": "folder1", "name": "Camera Roll", "folder": { "childCount": 3 } },
            { "id": "p1", "name": "IMG_1.jpg", "size": 3000000, "webUrl": "https://1drv.ms/i/p1",
              "file": { "mimeType": "image/jpeg" }, "image": { "width": 4000, "height": 3000 },
              "photo": { "takenDateTime": "2026-09-13T08:15:00Z", "cameraMake": "samsung", "cameraModel": "SM-S918B" },
              "location": { "latitude": 31.81, "longitude": 34.86, "altitude": 120.5 },
              "parentReference": { "path": "/drive/root:/Pictures/Camera Roll" } },
            { "id": "p2", "name": "IMG_2.jpg", "file": { "mimeType": "image/jpeg" },
              "photo": { "takenDateTime": "2026-09-13T08:20:00Z" }, "location": { "latitude": 0, "longitude": 0 } },
            { "id": "v1", "name": "VID_1.mp4", "file": { "mimeType": "video/mp4" }, "video": { "width": 1920, "height": 1080, "duration": 5000 },
              "fileSystemInfo": { "createdDateTime": "2026-09-13T08:30:00Z" } },
            { "id": "doc", "name": "notes.pdf", "file": { "mimeType": "application/pdf" } },
            { "id": "gone", "deleted": { "state": "deleted" } }
          ]
        }
        """;
        var page = OneDriveClient.ParseDelta(JsonDocument.Parse(json).RootElement);

        Assert.Equal(6, page.Items);
        Assert.Equal(new[] { "p1", "p2", "v1" }, page.Photos.Select(p => p.Id));
        Assert.Equal(new[] { "doc", "gone" }, page.Removed);
        Assert.NotNull(page.NextLink);
        Assert.Null(page.DeltaLink);

        var p1 = page.Photos[0];
        Assert.Equal(new DateTime(2026, 9, 13, 8, 15, 0, DateTimeKind.Utc), p1.TakenAt);
        Assert.Equal(DateTimeKind.Utc, p1.TakenAt.Kind);
        Assert.Equal((31.81, 34.86), (p1.Lat!.Value, p1.Lon!.Value));
        Assert.Equal((4000, 3000), (p1.Width!.Value, p1.Height!.Value));
        Assert.Equal("/Pictures/Camera Roll", p1.Folder);
        Assert.Equal("SM-S918B", p1.CameraModel);

        // 0,0 is no position
        Assert.Null(page.Photos[1].Lat);
        // A video's time is the file's when it has no photo details
        Assert.True(page.Photos[2].IsVideo);
        Assert.Equal(new DateTime(2026, 9, 13, 8, 30, 0, DateTimeKind.Utc), page.Photos[2].TakenAt);
    }

    [Fact]
    public void Sign_in_url_asks_for_reading_files_and_staying_signed_in()
    {
        var url = OneDriveClient.AuthorizeUrl("app-id", "https://contacts.example.com/api/atlas/connections/onedrive/callback", "st");
        Assert.StartsWith("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?", url);
        Assert.Contains("scope=offline_access%20Files.Read%20User.Read", url);
        Assert.Contains("redirect_uri=https%3A%2F%2Fcontacts.example.com%2Fapi%2Fatlas%2Fconnections%2Fonedrive%2Fcallback", url);
        Assert.Contains("state=st", url);
    }
}

public class PhotoClockTests
{
    private static readonly DateTime Day = new(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A day out: home until 06:00 UTC, an hour at each of eight places 5 km apart, home again</summary>
    private static PhotoClock.History Trip()
    {
        var fixes = new List<PhotoClock.Fix> { new(Day, Day.AddHours(6), 32.0, 34.8, 32.0, 34.8) };
        for (var i = 0; i < 8; i++)
        {
            var lat = 32.05 + i * 0.05;
            fixes.Add(new PhotoClock.Fix(Day.AddHours(6 + i), Day.AddHours(7 + i), lat, 34.8, lat, 34.8));
        }

        fixes.Add(new PhotoClock.Fix(Day.AddHours(14), Day.AddHours(23), 32.0, 34.8, 32.0, 34.8));
        return new PhotoClock.History(fixes);
    }

    /// <summary>A photo at each of the eight places, half way through the hour, its time shifted by the camera</summary>
    private static List<PhotoClock.Sample> Photos(int cameraAheadMin) =>
        Enumerable.Range(0, 8).Select(i => new PhotoClock.Sample(Day.AddHours(6.5 + i).AddMinutes(cameraAheadMin), 32.05 + i * 0.05, 34.8, false)).ToList();

    [Fact]
    public void Times_that_match_the_history_as_they_are_are_UTC()
    {
        var r = PhotoClock.Decide(Photos(0), Trip());
        Assert.False(r.LocalClock);
        Assert.True(r.UtcVotes >= 5);
        Assert.All(r.Offsets, o => Assert.Equal(0, o));
    }

    [Fact]
    public void Times_three_hours_ahead_are_the_cameras_local_clock()
    {
        var photos = Photos(180);
        // Without a position: takes the shift of the nearest photo in time
        photos.Add(new PhotoClock.Sample(Day.AddHours(9.75).AddMinutes(180), null, null, false));
        var r = PhotoClock.Decide(photos, Trip());
        Assert.True(r.LocalClock);
        Assert.All(r.Offsets, o => Assert.Equal(180, o));
    }

    [Fact]
    public void The_local_offset_follows_the_place_and_daylight_saving()
    {
        Assert.Equal(180, PhotoClock.LocalOffset(31.8, 34.8, new DateTime(2026, 9, 13, 12, 0, 0)));
        Assert.Equal(120, PhotoClock.LocalOffset(31.8, 34.8, new DateTime(2026, 12, 13, 12, 0, 0)));
        Assert.Equal(60, PhotoClock.LocalOffset(51.5, -0.12, new DateTime(2026, 7, 1, 12, 0, 0)));
        Assert.Equal(0, PhotoClock.LocalOffset(51.5, -0.12, new DateTime(2026, 1, 1, 12, 0, 0)));
    }

    [Fact]
    public void A_photo_whose_UTC_reading_is_merely_unknown_does_not_vote()
    {
        // 40-minute visits every 75 minutes: each photo fits its visit at the local reading,
        // and its time as it is falls between visits (unknown, not contradicted)
        var at = (int i) => Day.AddMinutes(360 + 75 * i);
        var history = new PhotoClock.History(Enumerable.Range(0, 8)
            .Select(i => new PhotoClock.Fix(at(i), at(i).AddMinutes(40), 32.05 + i * 0.05, 34.8, 32.05 + i * 0.05, 34.8)));
        var photos = Enumerable.Range(0, 8)
            .Select(i => new PhotoClock.Sample(at(i).AddMinutes(20).AddHours(3), 32.05 + i * 0.05, 34.8, false)).ToList();
        var r = PhotoClock.Decide(photos, history);
        Assert.Equal(0, r.LocalVotes + r.UtcVotes);
        Assert.False(r.LocalClock);
    }

    [Fact]
    public void Staying_put_says_nothing()
    {
        var history = new PhotoClock.History(new[] { new PhotoClock.Fix(Day, Day.AddHours(23), 32.0, 34.8, 32.0, 34.8) });
        var photos = Enumerable.Range(0, 10).Select(i => new PhotoClock.Sample(Day.AddHours(8 + i), 32.0, 34.8, false)).ToList();
        var r = PhotoClock.Decide(photos, history);
        Assert.Equal(0, r.LocalVotes + r.UtcVotes);
        Assert.False(r.LocalClock);
    }
}

/// <summary>Against a real PostgreSQL when ATLAS_TEST_DB is set (skipped otherwise)</summary>
public class PhotoDbTests
{
    private static OneDrivePhoto P(string id, DateTime t, double? lat = null, double? lon = null, bool video = false) =>
        new(id, id + ".jpg", "image/jpeg", video, 1000, 4000, 3000, "samsung", "SM-S918B", t, lat, lon, null, "https://1drv.ms/" + id, "/Pictures");

    [Fact]
    public async Task Places_photos_on_tracks_and_visits_and_finds_them_by_activity_place_and_map()
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        if (string.IsNullOrEmpty(cs))
        {
            return;
        }

        await using var db = new AtlasDbContext(new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations", "atlas")).Options);
        await db.Database.MigrateAsync();
        // Other tests' photos would use up the batches
        await db.Photos.Where(p => !p.Placed).ExecuteUpdateAsync(s => s.SetProperty(p => p.Placed, true));
        var owner = "photos-" + Guid.NewGuid();
        var svc = new AtlasService(db, NullLogger<AtlasService>.Instance);
        var t0 = DateTime.UtcNow.Date.AddDays(-3).AddHours(6);

        // A ride north from 31.80 (0.001° a minute, an hour), then a cafe visit
        var ride = Enumerable.Range(0, 61).Select(i => new PointInput(t0.AddMinutes(i), 31.80 + i * 0.001, 34.86, null, null)).ToList();
        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "sports-tracker",
            Activities = { new ActivityInput("ride", "cycling", "Morning ride", null, null, Points: ride) }
        });
        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "google-timeline",
            Places = { new PlaceInput("cafe", "Cafe Nimrod", 31.87, 34.87) },
            Visits = { new VisitInput("v", t0.AddMinutes(70), t0.AddMinutes(130), 31.87, 34.87, "cafe") }
        });
        var rideId = (await db.Activities.SingleAsync(a => a.OwnerId == owner)).Id;
        var placeId = (await db.Places.SingleAsync(p => p.OwnerId == owner)).Id;

        var photos = new List<OneDrivePhoto>
        {
            P("gps", t0.AddMinutes(10), 31.811, 34.861),       // on the ride, with a position
            P("nogps", t0.AddMinutes(30).AddSeconds(30)),        // on the ride, placed by the track
            P("cafe", t0.AddMinutes(90)),                         // at the cafe, placed by the visit
            P("home", t0.AddDays(-1))                             // nowhere known
        };
        Assert.Equal(4, await svc.UpsertPhotosAsync(owner, "onedrive", photos));
        // The same again changes nothing
        Assert.Equal(0, await svc.UpsertPhotosAsync(owner, "onedrive", photos));

        // Not placed yet: the ride finds its photos by time
        Assert.Equal(new[] { "gps.jpg", "nogps.jpg" }, (await svc.ActivityPhotosAsync(owner, rideId)).Select(p => p.Name));

        while (await svc.PlacePendingPhotosAsync(500) > 0)
        {
        }

        var rows = await db.Photos.AsNoTracking().Where(p => p.OwnerId == owner).ToDictionaryAsync(p => p.ExternalId);
        Assert.Equal(("gps", rideId), (rows["gps"].PlacedBy, rows["gps"].ActivityId!.Value));
        Assert.Equal("track", rows["nogps"].PlacedBy);
        Assert.Equal(31.8305, rows["nogps"].MapLat!.Value, 4);    // half way between the 30th and 31st minute
        Assert.Equal(("visit", 31.87), (rows["cafe"].PlacedBy, rows["cafe"].MapLat!.Value));
        Assert.NotNull(rows["cafe"].VisitId);
        Assert.Null(rows["home"].PlacedBy);
        Assert.True(rows["home"].Placed);

        Assert.Equal(new[] { "gps.jpg", "nogps.jpg" }, (await svc.ActivityPhotosAsync(owner, rideId)).Select(p => p.Name));
        Assert.Equal(new[] { "cafe.jpg" }, (await svc.PlacePhotosAsync(owner, placeId)).Select(p => p.Name));
        var map = await svc.MapPhotosAsync(owner, new AtlasQuery());
        Assert.Equal(3, map.Photos.Count);
        Assert.Equal("samsung SM-S918B", map.Photos[0].Camera);
        var rides = await svc.MapPhotosAsync(owner, new AtlasQuery { Types = new List<string> { "ride" } });
        Assert.Equal(new[] { "nogps.jpg", "gps.jpg" }, rides.Photos.Select(p => p.Name));
        // The results list says how many photos each item has
        var found = await svc.SearchAsync(owner, new AtlasQuery());
        Assert.Equal(2, found.Activities.Single().PhotoCount);
        Assert.Equal(1, found.Visits.Single().PhotoCount);
        Assert.Equal(new[] { "cafe.jpg" }, (await svc.VisitPhotosAsync(owner, found.Visits.Single().Id)).Select(p => p.Name));

        // New data at a photo's time places it again
        // (an hour either side: the cafe photo and the one at 30 min, not the one at 10)
        Assert.Equal(2, await svc.RequeuePhotosAsync(owner, t0.AddMinutes(85), t0.AddMinutes(95)));

        // Removed from the library
        Assert.Equal(1, await svc.RemovePhotosAsync(owner, "onedrive", new[] { "home" }));
        Assert.Equal((3, 3), await svc.PhotoCountsAsync(owner));

        // Times one hour ahead of the history (a camera's local clock): too few votes to say
        var clock = await svc.CalibratePhotoClocksAsync(owner);
        Assert.False(clock.LocalClock);
        Assert.Equal(3, await svc.DeleteAllPhotosAsync(owner, "onedrive"));
    }

    [Fact]
    public async Task A_camera_clock_shifts_the_photos_onto_the_ride()
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        if (string.IsNullOrEmpty(cs))
        {
            return;
        }

        await using var db = new AtlasDbContext(new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations", "atlas")).Options);
        await db.Database.MigrateAsync();
        var owner = "clock-" + Guid.NewGuid();
        var svc = new AtlasService(db, NullLogger<AtlasService>.Instance);
        // Israel summer time: the camera's clock is three hours ahead of UTC
        var t0 = new DateTime(2026, 9, 10, 6, 0, 0, DateTimeKind.Utc);

        // Home before and after; visits 5 km apart every hour in between
        var visits = new List<VisitInput> { new("home1", t0.AddHours(-6), t0, 31.80, 34.80) };
        for (var i = 0; i < 8; i++)
        {
            visits.Add(new VisitInput("v" + i, t0.AddHours(i), t0.AddHours(i + 1), 31.85 + i * 0.05, 34.80));
        }

        visits.Add(new VisitInput("home2", t0.AddHours(8), t0.AddHours(16), 31.80, 34.80));
        await svc.IngestAsync(owner, new IngestRequest { Source = "google-timeline", Visits = visits });

        // The camera's clock is three hours ahead
        var photos = Enumerable.Range(0, 8).Select(i => P("p" + i, t0.AddHours(i + 0.5).AddHours(3), 31.85 + i * 0.05, 34.80)).ToList();
        await svc.UpsertPhotosAsync(owner, "onedrive", photos);
        var clock = await svc.CalibratePhotoClocksAsync(owner);
        Assert.True(clock.LocalClock);
        var rows = await db.Photos.AsNoTracking().Where(p => p.OwnerId == owner).OrderBy(p => p.TakenAt).ToListAsync();
        Assert.All(rows, r => Assert.Equal(180, r.ClockOffsetMin));
        Assert.Equal(t0.AddHours(0.5), rows[0].TakenAt);
        Assert.All(rows, r => Assert.False(r.Placed));

        // A sync bringing the same photos keeps the shift
        await svc.UpsertPhotosAsync(owner, "onedrive", photos.Select(p => p with { Name = "renamed.jpg" }).ToList());
        Assert.Equal(t0.AddHours(0.5), (await db.Photos.AsNoTracking().Where(p => p.OwnerId == owner).OrderBy(p => p.TakenAt).FirstAsync()).TakenAt);
        await svc.DeleteAllPhotosAsync(owner, "onedrive");
    }
}
