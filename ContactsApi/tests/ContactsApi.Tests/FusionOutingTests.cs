using ContactsApi.Infrastructure.Atlas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContactsApi.Tests;

/// <summary>An outing recorded in pieces (the ride of 13 Sept 2026, as the sources gave it)</summary>
public class FusionOutingTests
{
    private static readonly DateTime T0 = new(2026, 9, 13, 4, 0, 0, DateTimeKind.Utc);
    private static readonly (double Lat, double Lon) Home = (31.8106, 34.7742);
    private static readonly (double Lat, double Lon) Turn = (31.8535, 34.8649);

    private static FusionCandidate Rec(string source, string type, double startMin, double endMin, double? distance, int points,
        (double Lat, double Lon)? first = null, (double Lat, double Lon)? last = null, double? trackM = null, int? hr = null,
        double minLat = 31.797, double minLon = 34.774, double maxLat = 31.856, double maxLon = 34.891) =>
        new(Guid.NewGuid(), source, type, null, T0.AddMinutes(startMin), T0.AddMinutes(endMin), distance, null, hr, hr,
            minLat, minLon, maxLat, maxLon, points, false, first, last, trackM,
            first.HasValue && last.HasValue ? Geo.EncodePolyline(new[] { first.Value, last.Value }) : null);

    // Sports Tracker: out (its summary lost most of it: 973 m) and back, restarted 83 s later 4 m away
    private static readonly FusionCandidate Out = Rec("sports-tracker", "ride", 0.4, 134.7, 973, 2410, Home, Turn, trackM: 21_500);
    private static readonly FusionCandidate Back = Rec("sports-tracker", "ride", 136.1, 215.9, 14_188, 1397, (31.85351, 34.86491), Home, trackM: 14_000);
    // The watch: the whole outing
    private static readonly FusionCandidate Watch = Rec("samsung-health", "ride", 0.6, 226.8, 39_980, 0, hr: 128);
    // The phone: three segments around two stops
    private static readonly FusionCandidate Tl1 = Rec("google-timeline", "ride", 4.5, 42.6, 11_102, 18, maxLat: 31.813, maxLon: 34.86);
    private static readonly FusionCandidate Tl2 = Rec("google-timeline", "ride", 55.2, 71.3, 3_748, 9, minLat: 31.81, minLon: 34.859, maxLat: 31.823);
    private static readonly FusionCandidate Tl3 = Rec("google-timeline", "ride", 79.7, 215.5, 8_191, 46);

    private static readonly FusionCandidate[] All = { Out, Back, Watch, Tl1, Tl2, Tl3 };

    [Fact]
    public void A_recording_stopped_and_started_again_is_one_outing_with_everything_seen_of_it()
    {
        Assert.True(Fusion.Continues(Out, Back, FusionRules.Default));
        var group = Assert.Single(Fusion.Group(All, [], []));
        Assert.Equal(6, group.Count);

        var fused = Fusion.Fuse(All);
        Assert.Equal(Out.Id, fused.PrimaryId);
        Assert.Equal(new[] { Out.Id, Back.Id }, fused.Segments.Select(s => s.Id));
        Assert.Equal(Out.Start, fused.Start);
        Assert.Equal(Back.End, fused.End);
        // The track's 21.5 km instead of the lost summary, plus the way back
        Assert.Equal(21_500 + 14_188, fused.DistanceM);
        Assert.Equal((Out.End - Out.Start + (Back.End - Back.Start)).TotalSeconds, fused.DurationS!.Value, 0);
        Assert.Equal(128, fused.AvgHeartRate);
        Assert.NotNull(fused.Polyline);
        Assert.Equal(4, AtlasService.DecodePolyline(fused.Polyline!).Count);

        Assert.True(Assert.Single(fused.Evidence, e => e.MemberId == Back.Id).Segment);
        Assert.True(Assert.Single(fused.Evidence, e => e.MemberId == Tl2.Id).Contained);
        Assert.True(fused.MatchConfidence >= 0.85);
        // No time conflicts for the pieces; the lost distance is reported
        Assert.DoesNotContain(fused.Conflicts, c => c.Message.Contains("Sports Tracker starts") || c.Message.StartsWith("Google Timeline"));
        Assert.Contains(fused.Conflicts, c => c.Field == "distanceM" && c.Message.Contains("its own track is 21.5 km"));
        // The phone's segments cover parts of the outing: not a distance disagreement
        Assert.DoesNotContain(fused.Conflicts, c => c.Field == "distanceM" && c.Message.Contains("Google Timeline"));
    }

    [Fact]
    public void A_moment_record_has_no_time_conflicts()
    {
        var moment = Rec("samsung-health", "other", 2.3, 2.3, null, 0);
        var fused = Fusion.Fuse(new[] { Out, Back, Watch, moment });
        Assert.Contains(fused.Members, m => m.Id == moment.Id);
        Assert.DoesNotContain(fused.Conflicts, c => c.Values.Any(v => v.Value == moment.End.ToString("u")));
    }

    [Fact]
    public void Pieces_far_apart_later_of_another_kind_or_split_by_the_user_stay_apart()
    {
        var rules = FusionRules.Default;
        // Started again 20 minutes later
        Assert.False(Fusion.Continues(Out, Back with { Start = Out.End.AddMinutes(20), End = Out.End.AddMinutes(100) }, rules));
        // Started again 2 km away
        Assert.False(Fusion.Continues(Out, Back with { First = (31.87, 34.87) }, rules));
        // A run after the ride
        Assert.False(Fusion.Continues(Out, Back with { Type = "run" }, rules));
        // Phone guesses never continue each other (consecutive Timeline segments are separate activities)
        var a = Tl1 with { First = Home, Last = Turn, PointCount = 18 };
        var b = Tl2 with { First = Turn, Last = Home };
        Assert.False(Fusion.Continues(a, b, rules));

        Assert.Equal(2, Fusion.Group(new[] { Out, Back }, [], [(Out.Id, Back.Id)]).Count);
    }

    [Fact]
    public void A_short_segment_of_another_type_inside_a_recording_is_not_part_of_it()
    {
        var walk = Tl2 with { Type = "walk" };
        Assert.False(Fusion.Within(Out, walk, FusionRules.Default));
        Assert.Equal(2, Fusion.Group(new[] { Out, walk }, [], []).Count);
        // The same type elsewhere is not either
        var away = Tl2 with { MinLat = 32.5, MaxLat = 32.6 };
        Assert.Equal(2, Fusion.Group(new[] { Out, away }, [], []).Count);
    }

    [Fact]
    public void One_recording_with_a_lost_distance_uses_its_track()
    {
        var fused = Fusion.Fuse(new[] { Out, Tl1 });
        Assert.Equal(21_500, fused.DistanceM);
        Assert.Single(fused.Segments);
        Assert.Null(fused.DurationS);
    }

    /// <summary>Against a real PostgreSQL when ATLAS_TEST_DB is set (skipped otherwise)</summary>
    [Fact]
    public async Task The_outing_shows_its_whole_track_distance_and_time()
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        if (string.IsNullOrEmpty(cs))
        {
            return;
        }

        await using var db = new AtlasDbContext(new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations", "atlas")).Options);
        await db.Database.MigrateAsync();
        var owner = "outing-" + Guid.NewGuid();
        var svc = new AtlasService(db, NullLogger<AtlasService>.Instance);
        var t0 = DateTime.UtcNow.Date.AddDays(-30).AddHours(4);

        // Out 10 km north in 30 min, a pause of 90 s, back 10 km
        List<PointInput> Leg(DateTime start, double lat0, double dir) =>
            Enumerable.Range(0, 301).Select(i => new PointInput(start.AddSeconds(i * 6), lat0 + dir * i * 33.3 / 111_195.0, 34.77, 50, 130)).ToList();
        var outLeg = Leg(t0, 31.80, 1);
        var backLeg = Leg(t0.AddMinutes(31.5), outLeg[^1].Lat, -1);
        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "sports-tracker",
            Activities =
            {
                new ActivityInput("st-out", "cycling", "Morning ride", null, null, DistanceM: 973, DurationS: 264, Points: outLeg),
                new ActivityInput("st-back", "cycling", null, null, null, Points: backLeg)
            }
        });
        await svc.FuseAsync(owner);

        var ids = await db.Activities.Where(a => a.OwnerId == owner).ToDictionaryAsync(a => a.ExternalId!, a => a.Id);
        var found = await svc.SearchAsync(owner, new AtlasQuery());
        var ride = Assert.Single(found.Activities);
        Assert.Equal(ids["st-out"], ride.Id);
        Assert.InRange(ride.DistanceM!.Value, 19_000, 21_000);
        Assert.InRange(ride.DurationS!.Value, 3550, 3650);
        Assert.Equal(t0, ride.StartTime);
        Assert.Equal(backLeg[^1].T, ride.EndTime, TimeSpan.FromSeconds(1));
        Assert.Equal("Morning ride", ride.Name);

        // Opening either piece shows the whole outing: both tracks, analysed together
        var detail = await svc.GetActivityAsync(owner, ids["st-back"]);
        Assert.Equal(ids["st-out"], detail.Summary.Id);
        Assert.Equal(outLeg[0].T, detail.Points[0].T);
        Assert.Equal(backLeg[^1].T, detail.Points[^1].T);
        Assert.InRange(detail.Analysis!.DistanceM, 19_000, 21_000);
        Assert.InRange(detail.Analysis.StoppedS, 60, 120);
        Assert.Equal(4, detail.Analysis.Splits.Count(s => s.DistanceM >= 4900));
        Assert.Contains(detail.Evidence, e => e.MemberId == ids["st-back"] && e.Segment);
    }
}
