using ContactsApi.Infrastructure.Atlas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContactsApi.Tests;

public class GeoTests
{
    [Fact]
    public void Measures_distance_and_climb()
    {
        // ~1.11 km per 0.01° of latitude
        Assert.InRange(Geo.Haversine(32.0, 34.8, 32.01, 34.8), 1100, 1125);
        var pts = new List<TrackPoint>
        {
            new() { Lat = 32, Lon = 34.8, Elevation = 100 },
            new() { Lat = 32.001, Lon = 34.8, Elevation = 101 }, // noise
            new() { Lat = 32.002, Lon = 34.8, Elevation = 110 },
            new() { Lat = 32.003, Lon = 34.8, Elevation = 104 },
            new() { Lat = 32.004, Lon = 34.8, Elevation = 120 },
        };
        Assert.Equal(26, Geo.ElevationGain(pts));
    }

    [Fact]
    public void Simplifies_and_encodes_polylines()
    {
        // Google's documented example
        Assert.Equal("_p~iF~ps|U_ulLnnqC_mqNvxq`@", Geo.EncodePolyline(new[] { (38.5, -120.2), (40.7, -120.95), (43.252, -126.453) }));
        var line = Enumerable.Range(0, 5000).Select(i => (32.0 + i * 0.0001, 34.8 + Math.Sin(i / 100.0) * 0.001)).ToList();
        Assert.True(Geo.Simplify(line, 400).Count <= 400);
    }

    [Fact]
    public void Normalizes_activity_types()
    {
        Assert.Equal("ride", AtlasService.NormalizeType("CYCLING"));
        Assert.Equal("transit", AtlasService.NormalizeType("IN_BUS"));
        Assert.Equal("drive", AtlasService.NormalizeType("in passenger vehicle"));
        Assert.Equal("hike", AtlasService.NormalizeType("Hiking"));
    }
}

/// <summary>Against a real PostgreSQL when ATLAS_TEST_DB is set (skipped otherwise)</summary>
public class AtlasServiceDbTests
{
    private static AtlasDbContext? Db()
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        if (string.IsNullOrEmpty(cs))
        {
            return null;
        }

        var db = new AtlasDbContext(new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations", "atlas")).Options);
        db.Database.Migrate();
        return db;
    }

    [Fact]
    public async Task Ingests_searches_and_deletes_an_import()
    {
        await using var db = Db();
        if (db == null)
        {
            return;
        }

        var owner = "test-" + Guid.NewGuid();
        var svc = new AtlasService(db, NullLogger<AtlasService>.Instance);
        var t0 = new DateTime(2026, 9, 3, 6, 0, 0, DateTimeKind.Utc);
        var points = Enumerable.Range(0, 120).Select(i => new PointInput(t0.AddSeconds(i * 30), 32.80 + i * 0.001, 35.00, 100 + i, 120 + i % 30)).ToList();
        var req = new IngestRequest
        {
            Source = "gpx",
            FileName = "ride.gpx",
            Activities = { new ActivityInput("ride-1", "cycling", "Morning ride", null, null, Points: points) },
            Places = { new PlaceInput("p1", "Haifa café", 32.82, 35.0, "Haifa") },
            Visits = { new VisitInput("v1", t0.AddHours(2), t0.AddHours(3), 32.82, 35.0, "p1") },
            Health = Enumerable.Range(0, 3000).Select(i => new HealthInput("heart_rate", t0.AddMinutes(-60 + i * 0.1), 70 + i % 50)).ToList()
        };
        var first = await svc.IngestAsync(owner, req);
        Assert.Equal(1, first.Activities);
        Assert.Equal(3000, first.HealthSamples);

        // The user renames the ride; importing the same file again keeps the name
        var rideId = (await svc.SearchAsync(owner, new AtlasQuery { Text = "Morning" })).Activities.Single().Id;
        await svc.UpdateActivityAsync(owner, rideId, new ActivityPatch(Name: "Carmel with Dana"));

        // Same file again: updated, not duplicated
        var again = await svc.IngestAsync(owner, req);
        Assert.Equal("Carmel with Dana", (await svc.GetActivityAsync(owner, rideId)).Summary.Name);
        Assert.Equal(0, again.HealthSamples);
        var summary = await svc.SummaryAsync(owner);
        Assert.Equal(1, summary.Activities);
        Assert.Equal(1, summary.Visits);
        Assert.Equal(3000, summary.HealthSamples);
        Assert.Equal("ride", summary.ByType.Keys.Single());

        var found = await svc.SearchAsync(owner, new AtlasQuery { From = t0.AddDays(-1), To = t0.AddDays(1), MinLat = 32.85, MaxLat = 32.9, MinLon = 34.9, MaxLon = 35.1, Text = "Carmel" });
        Assert.Single(found.Activities);
        Assert.InRange(found.Activities[0].DistanceM!.Value, 13000, 13400);
        Assert.Equal(149, found.Activities[0].MaxHeartRate);

        var visits = await svc.SearchAsync(owner, new AtlasQuery { Text = "café" });
        Assert.Equal("Haifa café", visits.Visits.Single().PlaceName);

        var detail = await svc.GetActivityAsync(owner, found.Activities[0].Id, 50);
        Assert.True(detail.Points.Count <= 51);
        Assert.Equal("track", detail.HeartRateSource);

        var hr = await svc.HealthAsync(owner, "heart_rate", t0.AddHours(-1), t0.AddHours(5), 100);
        Assert.InRange(hr.Count, 50, 101);
        Assert.All(hr, p => Assert.NotNull(p.Max));

        var histogram = await svc.HistogramAsync(owner, t0.AddDays(-2), t0.AddDays(2), "day");
        Assert.Contains(histogram, r => r.Kind == "ride" && r.Count == 1);
        Assert.Contains(histogram, r => r.Kind == "health");

        var near = await svc.NearAsync(owner, 32.85, 35.0, 300, null, null);
        Assert.Single(near.Activities);
        Assert.Empty(near.Visits);

        // An indoor workout: start time, no track, no end
        await svc.IngestAsync(owner, new IngestRequest { Source = "google-fit", Activities = { new ActivityInput("fit-1", "running", "Treadmill", t0.AddDays(1), null, DurationS: 1800) } });
        var indoor = (await svc.SearchAsync(owner, new AtlasQuery { From = t0.AddDays(1).AddMinutes(10), To = t0.AddDays(1).AddMinutes(20) })).Activities.Single();
        Assert.Equal(t0.AddDays(1).AddMinutes(30), indoor.EndTime);

        // Local-day buckets: 23:30 UTC is the next day in Israel
        await svc.IngestAsync(owner, new IngestRequest { Source = "manual", Visits = { new VisitInput("late", new DateTime(2026, 9, 10, 23, 30, 0, DateTimeKind.Utc), new DateTime(2026, 9, 11, 0, 30, 0, DateTimeKind.Utc), 32, 35) } });
        var local = await svc.HistogramAsync(owner, new DateTime(2026, 9, 10), new DateTime(2026, 9, 12), "day", default, "Asia/Jerusalem");
        Assert.Contains(local, r => r.Kind == "visit" && r.Bucket == new DateTime(2026, 9, 10, 21, 0, 0, DateTimeKind.Utc));

        // The timeline follows the map's filters: an area away from the ride, or another type
        var elsewhere = await svc.HistogramAsync(owner, t0.AddDays(-2), t0.AddDays(2), "day", default, null,
            new AtlasQuery { MinLat = 31, MaxLat = 31.5, MinLon = 34, MaxLon = 34.5 });
        Assert.DoesNotContain(elsewhere, r => r.Kind == "ride");
        Assert.Contains(elsewhere, r => r.Kind == "health"); // health has no place
        var hikes = await svc.HistogramAsync(owner, t0.AddDays(-2), t0.AddDays(2), "day", default, null, new AtlasQuery { Types = ["hike"] });
        Assert.DoesNotContain(hikes, r => r.Kind is "ride" or "visit");
        var carmel = await svc.HistogramAsync(owner, t0.AddDays(-2), t0.AddDays(2), "day", default, null, new AtlasQuery { Text = "Carmel" });
        Assert.Contains(carmel, r => r.Kind == "ride" && r.Count == 1);

        // Someone else sees nothing
        Assert.Empty((await svc.SearchAsync("other", new AtlasQuery())).Activities);

        Assert.True(await svc.DeleteImportAsync(owner, first.ImportId) > 0);
        Assert.Equal(1, (await svc.SummaryAsync(owner)).Activities); // the treadmill run is another import
    }
}
