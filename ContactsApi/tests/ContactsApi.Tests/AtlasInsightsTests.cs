using ContactsApi.Infrastructure.Atlas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContactsApi.Tests;

public class InsightFormatTests
{
    [Fact]
    public void Formats_durations_and_paces_without_sixty_minute_glitches()
    {
        Assert.Equal("1:00 h", AtlasService.Duration(59.9 / 60));
        Assert.Equal("2:05 h", AtlasService.Duration(2 + 5.0 / 60));
        Assert.Equal("45 min", AtlasService.Duration(0.75));
        Assert.Equal("5:00", AtlasService.Pace(12));
        Assert.Equal("4:30", AtlasService.Pace(60 / 4.5));
    }
}

/// <summary>Against a real PostgreSQL when ATLAS_TEST_DB is set (skipped otherwise)</summary>
public class AtlasInsightsDbTests
{
    [Fact]
    public async Task Computes_statistics_records_places_and_proposals()
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        if (string.IsNullOrEmpty(cs))
        {
            return;
        }

        await using var db = new AtlasDbContext(new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations", "atlas")).Options);
        await db.Database.MigrateAsync();
        var owner = "insights-" + Guid.NewGuid();
        var svc = new AtlasService(db, NullLogger<AtlasService>.Instance);
        var today = DateTime.UtcNow.Date.AddHours(7);
        List<PointInput> Track(DateTime t0, int n, double lat0, int? hr) =>
            Enumerable.Range(0, n).Select(i => new PointInput(t0.AddSeconds(i * 30), lat0 + i * 0.001, 35.0, 100 + i, hr)).ToList();

        // Rides every 4 days until 20 days ago (rhythm), the last one the longest ever
        var rides = Enumerable.Range(0, 10).Select(i =>
        {
            var t0 = today.AddDays(-56 + i * 4);
            return new ActivityInput($"ride-{i}", "cycling", $"Ride {i}", null, null, Points: Track(t0, 60 + i * 10, 32.7, i % 2 == 0 ? 130 : null));
        }).ToList();
        await svc.IngestAsync(owner, new IngestRequest { Source = "sports-tracker", Activities = rides });
        // Runs: a quiet month, then a big last week (load)
        var runs = Enumerable.Range(0, 4).Select(i => new ActivityInput($"run-{i}", "running", null, null, null,
                Points: Track(today.AddDays(-30 + i * 6), 30, 31.9, 150)))
            .Concat(Enumerable.Range(0, 3).Select(i => new ActivityInput($"run-big-{i}", "running", null, null, null,
                Points: Track(today.AddDays(-5 + i), 90, 31.9, 155))))
            .ToList();
        await svc.IngestAsync(owner, new IngestRequest { Source = "gpx", Activities = runs });
        // The phone saw one ride as a drive: a fused activity with a conflict
        var ride9 = today.AddDays(-20);
        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "google-timeline",
            Activities = { new ActivityInput("tl-drive", "IN_PASSENGER_VEHICLE", null, ride9.AddMinutes(1), ride9.AddMinutes(70), Points: Track(ride9, 10, 32.7, null)) },
            Places = { new PlaceInput("home", "Home", 32.08, 34.78), new PlaceInput("pl-x", "Place", 32.5, 34.9), new PlaceInput("rome", "Hotel Roma", 41.9, 12.5) },
            Visits = Enumerable.Range(0, 6).Select(i => new VisitInput($"x{i}", today.AddDays(-1 - i * 3).AddHours(5), today.AddDays(-1 - i * 3).AddHours(7), 32.5, 34.9, "pl-x"))
                .Concat(Enumerable.Range(0, 3).Select(i => new VisitInput($"rome{i}", today.AddDays(-40 + i).AddHours(3), today.AddDays(-40 + i).AddHours(10), 41.9, 12.5, "rome")))
                .Append(new VisitInput("home1", today.AddDays(-2), today.AddDays(-2).AddHours(10), 32.08, 34.78, "home"))
                .ToList()
        });

        var q = new AtlasQuery { From = today.AddDays(-60), To = DateTime.UtcNow };
        var insights = await svc.InsightsAsync(owner, q, "Asia/Jerusalem");

        Assert.Equal(17, insights.Totals.Activities);
        Assert.NotNull(insights.Previous);
        Assert.Equal(0, insights.Previous!.Activities);
        var rideStats = insights.ByType.Single(t => t.Type == "ride");
        Assert.Equal(10, rideStats.Count);
        Assert.NotNull(rideStats.AvgSpeedKmh);
        Assert.Equal("week", insights.Series.Bucket);
        Assert.Equal(17, insights.ByWeekday.Sum());
        Assert.Equal(17, insights.ByHour.Sum());
        Assert.Contains(insights.Records, r => r.Kind == "longest" && r.Type == "ride" && r.Label == "Longest ride");
        Assert.Contains(insights.Records, r => r.Kind == "fastest" && r.Type == "run" && r.Value.EndsWith("/km"));
        Assert.True(insights.Streaks.LongestActiveDays >= 3);
        Assert.Equal(1, insights.Coverage.WithConflicts);
        Assert.Equal(1, insights.Coverage.Fused);
        Assert.Equal(3, insights.Travel.DaysAway);
        var unnamed = insights.TopPlaces.Single(p => p.Unnamed);
        Assert.Equal("Unnamed place", unnamed.Name);
        Assert.Equal(6, unnamed.Visits);

        var kinds = insights.Proposals.Select(p => p.Kind).ToList();
        Assert.Contains("rhythm", kinds);
        Assert.Contains("load-high", kinds);
        Assert.Contains("conflicts", kinds);
        Assert.Contains("unnamed-place", kinds);
        Assert.Contains("away", kinds);
        Assert.Contains("record", kinds);
        Assert.Equal("warning", insights.Proposals[0].Severity);

        // Only what the sources disagree on
        var conflicts = await svc.SearchAsync(owner, new AtlasQuery { ConflictsOnly = true });
        Assert.Single(conflicts.Activities);
        Assert.Empty(conflicts.Visits);
    }
}
