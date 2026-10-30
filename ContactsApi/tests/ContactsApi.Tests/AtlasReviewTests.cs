using ContactsApi.Infrastructure.Atlas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContactsApi.Tests;

/// <summary>The Owner's Google Maps reviews (AD-39): keys, linking, reminders — pure</summary>
public class PlaceReviewRuleTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A point <paramref name="northM"/> north and <paramref name="eastM"/> east of (32, 34.8)</summary>
    private static (double Lat, double Lon) At(double northM, double eastM = 0) => (32 + northM / 110_540, 34.8 + eastM / 94_410);

    private static ReviewPlace Place(string name, double northM, double eastM = 0)
    {
        var (lat, lon) = At(northM, eastM);
        return new ReviewPlace(Guid.NewGuid(), name, lat, lon);
    }

    private static (DateTime, DateTime) Visit(int daysAgo, int minutes = 60) =>
        (Now.AddDays(-daysAgo).AddMinutes(-minutes), Now.AddDays(-daysAgo));

    [Fact]
    public void A_review_is_keyed_by_the_cid_of_its_link_else_the_link_else_its_name_and_position()
    {
        Assert.Equal("cid:1234567890", PlaceReviews.Key("http://maps.google.com/?cid=1234567890", "Cafe", 32, 34.8));
        Assert.Equal("cid:42", PlaceReviews.Key("https://www.google.com/maps?hl=en&cid=42&x=1", "Cafe", 32, 34.8));
        Assert.Equal("https://maps.app.goo.gl/abc", PlaceReviews.Key(" https://maps.app.goo.gl/abc ", "Cafe", 32, 34.8));
        Assert.Equal("cafe x|32.00000|34.80000", PlaceReviews.Key(null, " Cafe X ", 32, 34.8));
    }

    [Fact]
    public void Words_are_letters_and_digits_of_three_or_more_in_any_script()
    {
        Assert.Equal(new[] { "cafe", "landwer", "ראשון" }.OrderBy(x => x),
            PlaceReviews.Words("Cafe-Landwer (ראשון) of 1").OrderBy(x => x));
        Assert.Empty(PlaceReviews.Words(null));
    }

    [Fact]
    public void A_shared_word_links_within_75_m_even_past_a_nearer_place()
    {
        var (lat, lon) = At(0);
        var shop = Place("Paz", 8);
        var cafe = Place("Landwer Cafe", 60);

        Assert.Equal((cafe.Id, "name"), PlaceReviews.Link("קפה לנדוור - Landwer", lat, lon, new[] { shop, cafe }));
        // Beyond 75 m a shared word does not reach
        Assert.Equal((null, null), PlaceReviews.Link("Landwer", lat, lon, new[] { Place("Landwer", 80) }));
    }

    [Fact]
    public void Without_a_shared_word_only_a_clear_place_links()
    {
        var (lat, lon) = At(0);
        // The only place within 30 m
        var only = Place("Place", 25);
        Assert.Equal((only.Id, "only"), PlaceReviews.Link("Hummus Abu", lat, lon, new[] { only, Place("Place", 50) }));

        // A mall: two places within 30 m, neither named like the review
        var a = Place("Place", 12);
        var b = Place("Place", -20);
        Assert.Equal((null, null), PlaceReviews.Link("Shoe shop", lat, lon, new[] { a, b }));

        // A clear nearest: within 10 m, the next over 25 m away
        var near = Place("Place", 5);
        Assert.Equal((near.Id, "nearest"), PlaceReviews.Link("Shoe shop", lat, lon, new[] { near, Place("Place", 0, 28) }));
        Assert.Equal((null, null), PlaceReviews.Link("Shoe shop", lat, lon, new[] { near, Place("Place", 0, 20) }));
        Assert.Equal((null, null), PlaceReviews.Link("Shoe shop", lat, lon, Array.Empty<ReviewPlace>()));
    }

    [Fact]
    public void A_place_is_to_review_after_a_recent_real_visit_unless_a_rule_says_otherwise()
    {
        ReviewFacts F(string name = "Falafel Hakosem", string? category = "Restaurant", string state = "auto",
            List<(DateTime, DateTime)>? visits = null, bool reviewed = false, string? decision = null, DateTime? until = null,
            bool kind = false) =>
            new(name, category, state, visits ?? new List<(DateTime, DateTime)> { Visit(3) }, reviewed, decision, until, kind);
        string? Reason(ReviewFacts f) => PlaceReviews.Assess(f, Now).Reason;

        Assert.Equal("to_review", PlaceReviews.Assess(F(), Now).State);
        Assert.Equal("to_review", PlaceReviews.Assess(F(category: null, state: "user"), Now).State);
        Assert.Equal("reviewed", PlaceReviews.Assess(F(reviewed: true), Now).State);
        Assert.Equal("no_identity", Reason(F(state: "ambiguous")));
        Assert.Equal("no_identity", Reason(F(name: "Home", state: "user")));
        Assert.Equal("category", Reason(F(category: "fuel")));
        Assert.Equal("category", Reason(F(category: "inferred_work")));
        // Routine: visits on 31 days
        Assert.Equal("routine", Reason(F(visits: Enumerable.Range(1, 31).Select(d => Visit(d)).ToList())));
        Assert.Equal("to_review", PlaceReviews.Assess(F(visits: Enumerable.Range(1, 30).Select(d => Visit(d)).ToList()), Now).State);
        // Too short, or too long ago
        Assert.Equal("no_recent_visit", Reason(F(visits: new() { Visit(3, 9) })));
        Assert.Equal("no_recent_visit", Reason(F(visits: new() { Visit(181) })));
        Assert.Equal("to_review", PlaceReviews.Assess(F(visits: new() { Visit(179, 10) }), Now).State);
        // The Owner's choices
        Assert.Equal(new ReviewState("later", Now.AddDays(5)), PlaceReviews.Assess(F(decision: "later", until: Now.AddDays(5)), Now));
        Assert.Equal("to_review", PlaceReviews.Assess(F(decision: "later", until: Now.AddDays(-1)), Now).State);
        Assert.Equal("never", PlaceReviews.Assess(F(decision: "never"), Now).State);
        Assert.Equal("done", PlaceReviews.Assess(F(decision: "done"), Now).State);
        Assert.Equal(new ReviewState("never", Category: true), PlaceReviews.Assess(F(kind: true), Now));
        Assert.Equal(Now.AddDays(-3), PlaceReviews.LastQualifying(new[] { Visit(3), Visit(9), Visit(1, 5) }, Now));
    }

    [Fact]
    public void The_write_link_opens_the_place_by_its_google_place_id()
    {
        Assert.Equal("https://www.google.com/maps/search/?api=1&query=Caf%C3%A9%20Noir&query_place_id=ChIJabc",
            PlaceReviews.WriteUrl("Café Noir", "ChIJabc", 32, 34.8));
        Assert.Equal("https://www.google.com/maps/search/?api=1&query=Caf%C3%A9%20Noir%2032.000000%2C34.800000",
            PlaceReviews.WriteUrl("Café Noir", null, 32, 34.8));
    }
}

/// <summary>Reviews against a real PostgreSQL when ATLAS_TEST_DB is set (skipped otherwise)</summary>
public class PlaceReviewDbTests
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

    private static (double Lat, double Lon) At(double northM, double eastM = 0) => (32.1 + northM / 110_540, 34.8 + eastM / 94_300);

    private static ReviewInput Review(string name, (double Lat, double Lon) at, string cid, int? stars = 5, string? text = "Great",
        bool published = true) =>
        new(null, name, "Somewhere 1, Tel Aviv", "IL", at.Lat, at.Lon, stars, text, published, new DateTime(2025, 9, 3, 10, 0, 0, DateTimeKind.Utc),
            $"http://maps.google.com/?cid={cid}", new List<ReviewAnswer> { new("Price per person", "₪40–60") });

    [Fact]
    public async Task Reviews_are_stored_linked_shown_and_their_reminders_set_aside()
    {
        await using var db = Db();
        if (db == null)
        {
            return;
        }

        var owner = "reviews-" + Guid.NewGuid();
        var svc = new AtlasService(db, NullLogger<AtlasService>.Instance);
        var now = DateTime.UtcNow;
        PlaceInput P(string id, string name, (double Lat, double Lon) at, string? category) =>
            new(id, name, at.Lat, at.Lon, null, category, "ChIJ" + id);
        VisitInput V(string place, (double Lat, double Lon) at, int daysAgo, int minutes = 45) =>
            new($"{place}-{daysAgo}-{minutes}", now.AddDays(-daysAgo).AddMinutes(-minutes), now.AddDays(-daysAgo), at.Lat, at.Lon, place);
        var cafe = At(0);
        var unnamed = At(0, 2000);
        var fuel = At(0, 4000);
        var gym = At(0, 6000);
        var falafel = At(0, 8000);
        var beach = At(0, 10000);
        var museum = At(0, 12000);
        await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "google-timeline",
            Places =
            {
                P("cafe", "Landwer Cafe", cafe, "Cafe"), P("unnamed", "Place", unnamed, null), P("fuel", "Paz", fuel, "Fuel"),
                P("gym", "Holmes Place", gym, "Fitness centre"), P("falafel", "Falafel Hakosem", falafel, "Fast food"),
                P("beach", "Gordon Beach", beach, "Beach"), P("museum", "Eretz Israel Museum", museum, "Museum")
            },
            Visits = Enumerable.Range(1, 35).Select(d => V("gym", gym, d)).Concat(new[]
            {
                V("cafe", cafe, 2), V("unnamed", unnamed, 3), V("fuel", fuel, 1, 15), V("falafel", falafel, 5), V("beach", beach, 9),
                V("museum", museum, 300)
            }).ToList()
        });
        var places = await db.Places.AsNoTracking().Where(p => p.OwnerId == owner).ToDictionaryAsync(p => p.ExternalId!);
        Assert.Equal("pending", places["unnamed"].IdentifyState);

        // Reviews: one shares a word 40 m off, one is right by an unnamed place, one is far from any place
        var far = At(5000, 5000);
        var result = await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "google-maps", FileName = "Reviews.json",
            Reviews =
            {
                Review("קפה לנדוור Landwer", At(40), "1"), Review("Hummus Abu Hassan", At(4, 2000), "2", 4),
                Review("Far away bar", far, "3", null, "Draft words", false), Review("Nothing", At(1, 1), "4", null, " ")
            }
        });
        Assert.Equal(3, result.Reviews);
        var reviews = await db.Reviews.AsNoTracking().Where(r => r.OwnerId == owner).ToDictionaryAsync(r => r.ExternalId!);
        Assert.Equal((places["cafe"].Id, "name"), (reviews["cid:1"].PlaceId, reviews["cid:1"].LinkedBy));
        Assert.Equal((places["unnamed"].Id, "only"), (reviews["cid:2"].PlaceId, reviews["cid:2"].LinkedBy));
        Assert.Null(reviews["cid:3"].PlaceId);
        // The review names the unnamed place
        var named = await db.Places.AsNoTracking().SingleAsync(p => p.Id == places["unnamed"].Id);
        Assert.Equal(("Hummus Abu Hassan", "Somewhere 1, Tel Aviv", "user", "review"),
            (named.Name, named.Address, named.IdentifyState, named.IdentifiedFrom));

        // The same export again, with a changed text: updated, not added
        var again = await svc.IngestAsync(owner, new IngestRequest
        {
            Source = "google-maps", Reviews = { Review("קפה לנדוור Landwer", At(40), "1", 3, "Changed") }
        });
        Assert.Equal(1, again.Reviews);
        Assert.Equal(3, await db.Reviews.CountAsync(r => r.OwnerId == owner));

        // The place panel
        var cafeDetail = await svc.GetPlaceAsync(owner, places["cafe"].Id);
        Assert.Equal((3, "Changed", "reviewed"), (cafeDetail.Review!.Rating, cafeDetail.Review.Text, cafeDetail.ReviewState!.State));
        Assert.Equal(new ReviewAnswer("Price per person", "₪40–60"), cafeDetail.Review.Answers.Single());
        var falafelDetail = await svc.GetPlaceAsync(owner, places["falafel"].Id);
        Assert.Equal("to_review", falafelDetail.ReviewState!.State);
        Assert.Contains("query_place_id=ChIJfalafel", falafelDetail.ReviewState.WriteUrl);
        Assert.Equal("category", (await svc.GetPlaceAsync(owner, places["fuel"].Id)).ReviewState!.Reason);
        Assert.Equal("routine", (await svc.GetPlaceAsync(owner, places["gym"].Id)).ReviewState!.Reason);
        Assert.Equal("no_recent_visit", (await svc.GetPlaceAsync(owner, places["museum"].Id)).ReviewState!.Reason);

        // The overview: the falafel (5 days ago) before the beach (9 days ago)
        var o = await svc.ReviewsOverviewAsync(owner);
        Assert.Equal((3, (double?)3.5, 2, 1), (o.Written, o.Average, o.Linked, o.Unlinked));
        Assert.Equal(new[] { places["falafel"].Id, places["beach"].Id }, o.Due.Select(d => d.PlaceId));
        Assert.Equal(2, o.DueTotal);

        // Later, and not this kind
        var later = await svc.SetReviewReminderAsync(owner, new ReviewReminderRequest(places["falafel"].Id, "later"));
        Assert.Equal("later", later.State);
        Assert.InRange(later.Until!.Value, now.AddDays(29), now.AddDays(31));
        var kind = await svc.SetReviewReminderAsync(owner, new ReviewReminderRequest(places["beach"].Id, "never_kind"));
        Assert.Equal(("never", true), (kind.State, kind.Category));
        Assert.Empty((await svc.ReviewsOverviewAsync(owner)).Due);
        // Remind me again (the place and its kind)
        await svc.SetReviewReminderAsync(owner, new ReviewReminderRequest(places["falafel"].Id, "clear"));
        await svc.SetReviewReminderAsync(owner, new ReviewReminderRequest(places["beach"].Id, "clear", Kind: true));
        Assert.Equal(2, (await svc.ReviewsOverviewAsync(owner)).DueTotal);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SetReviewReminderAsync(owner, new ReviewReminderRequest(places["unnamed"].Id, "never_kind")));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SetReviewReminderAsync(owner, new ReviewReminderRequest(places["cafe"].Id, "soon")));

        // Another Owner sees none of it and cannot touch it
        var other = "reviews-other-" + Guid.NewGuid();
        Assert.Equal(0, (await svc.ReviewsOverviewAsync(other)).Written);
        await Assert.ThrowsAsync<AtlasNotFoundException>(() => svc.SetReviewReminderAsync(other, new ReviewReminderRequest(places["falafel"].Id, "never")));

        // A place that comes later takes the waiting review
        await svc.IngestAsync(owner, new IngestRequest { Source = "google-timeline", Places = { P("bar", "Far away bar", far, "Bar") } });
        Assert.NotNull((await db.Reviews.AsNoTracking().SingleAsync(r => r.OwnerId == owner && r.ExternalId == "cid:3")).PlaceId);

        // A deleted place lets its review go; deleting the import deletes the reviews
        await svc.DeletePlaceAsync(owner, places["cafe"].Id);
        Assert.Null((await db.Reviews.AsNoTracking().SingleAsync(r => r.OwnerId == owner && r.ExternalId == "cid:1")).PlaceId);
        await svc.DeleteImportAsync(owner, result.ImportId);
        Assert.False(await db.Reviews.AnyAsync(r => r.OwnerId == owner));
    }
}
