/**
 * @fileoverview The Owner's Google Maps reviews (AD-39): stored from Takeout, linked to
 * the places they are about, shown on a place, and the places still to review
 * @module ContactsApi.Infrastructure.Atlas
 */
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ContactsApi.Infrastructure.Atlas;

public partial class AtlasService
{
    private static readonly JsonSerializerOptions ReviewJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Adds or updates a batch's reviews, keyed by the cid of their Google Maps link
    /// (AD-39 rule 2); a review with neither stars nor text is skipped. Returns how many were read.
    /// </summary>
    private async Task<int> UpsertReviewsAsync(string owner, string source, ImportRun run, List<ReviewInput> reviews, CancellationToken ct)
    {
        var inputs = reviews
            .Where(r => !string.IsNullOrWhiteSpace(r.Name) && double.IsFinite(r.Lat) && double.IsFinite(r.Lon)
                        && (r.Rating is >= 1 and <= 5 || !string.IsNullOrWhiteSpace(r.Text)))
            .Select(r => (Key: Trim(r.ExternalId, 512) ?? Trim(PlaceReviews.Key(r.MapsUrl, r.Name, r.Lat, r.Lon), 512)!, Review: r))
            .DistinctBy(x => x.Key)
            .ToList();
        if (inputs.Count == 0)
        {
            return 0;
        }

        var keys = inputs.Select(x => x.Key).ToList();
        var existing = await _db.Reviews.Where(x => x.OwnerId == owner && x.Source == source && keys.Contains(x.ExternalId!))
            .ToDictionaryAsync(x => x.ExternalId!, ct);
        foreach (var (key, r) in inputs)
        {
            if (!existing.TryGetValue(key, out var row))
            {
                row = new PlaceReview { OwnerId = owner, Source = source, ExternalId = key, ImportId = run.Id };
                _db.Reviews.Add(row);
                existing[key] = row;
                run.Reviews++;
            }

            row.Name = Trim(r.Name.Trim(), 300)!;
            row.Address = Trim(r.Address?.Trim(), 500);
            row.CountryCode = Trim(r.CountryCode?.Trim(), 8);
            row.Lat = r.Lat;
            row.Lon = r.Lon;
            row.Rating = r.Rating is >= 1 and <= 5 ? r.Rating : null;
            row.Text = Trim(r.Text, 10_000);
            row.Published = r.Published;
            row.ReviewedAt = Utc(r.ReviewedAt);
            row.MapsUrl = Trim(r.MapsUrl?.Trim(), 1000);
            row.Answers = JsonSerializer.Serialize(
                (r.Answers ?? new()).Where(a => !string.IsNullOrWhiteSpace(a.Question) && !string.IsNullOrWhiteSpace(a.Answer))
                .Select(a => new ReviewAnswer(Trim(a.Question.Trim(), 300)!, Trim(a.Answer.Trim(), 1000)!)).ToList(), ReviewJson);
            row.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return inputs.Count;
    }

    /// <summary>
    /// Links the Owner's unlinked reviews to their places (AD-39 rule 3); a place takes one
    /// review. A review linked by position gives an unnamed place its name. Returns how many were linked.
    /// </summary>
    public async Task<int> LinkReviewsAsync(string owner, CancellationToken ct = default)
    {
        var reviews = await _db.Reviews.Where(r => r.OwnerId == owner && r.PlaceId == null).OrderBy(r => r.ReviewedAt).ToListAsync(ct);
        if (reviews.Count == 0)
        {
            return 0;
        }

        var taken = (await _db.Reviews.Where(r => r.OwnerId == owner && r.PlaceId != null).Select(r => r.PlaceId!.Value).ToListAsync(ct))
            .ToHashSet();
        var places = await _db.Places.AsNoTracking().Where(p => p.OwnerId == owner)
            .Select(p => new { p.Id, p.Name, p.Lat, p.Lon, p.UserEdited }).ToListAsync(ct);
        var byId = places.ToDictionary(p => p.Id);
        var naming = new Dictionary<Guid, PlaceReview>();
        var linked = 0;
        foreach (var r in reviews)
        {
            // Only the places around it (a box of about 110 m) are compared
            var dLon = 0.001 / Math.Max(0.1, Math.Cos(r.Lat * Math.PI / 180));
            var around = places.Where(p => !taken.Contains(p.Id) && Math.Abs(p.Lat - r.Lat) < 0.001 && Math.Abs(p.Lon - r.Lon) < dLon)
                .Select(p => new ReviewPlace(p.Id, p.Name, p.Lat, p.Lon));
            var (placeId, by) = PlaceReviews.Link(r.Name, r.Lat, r.Lon, around);
            if (placeId == null)
            {
                continue;
            }

            r.PlaceId = placeId;
            r.LinkedBy = by;
            taken.Add(placeId.Value);
            linked++;
            var place = byId[placeId.Value];
            if (by != "name" && !place.UserEdited && PlaceReviews.UnnamedNames.Contains(place.Name.Trim()))
            {
                naming[place.Id] = r;
            }
        }

        if (naming.Count > 0)
        {
            var ids = naming.Keys.ToList();
            foreach (var place in await _db.Places.Where(p => p.OwnerId == owner && ids.Contains(p.Id)).ToListAsync(ct))
            {
                var r = naming[place.Id];
                place.Name = r.Name;
                place.Address = string.IsNullOrWhiteSpace(place.Address) ? r.Address : place.Address;
                place.IdentifyState = "user";
                place.IdentifiedFrom = "review";
                place.UpdatedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync(ct);
        if (linked > 0)
        {
            _logger.LogInformation("Linked {Count} reviews to places ({Named} named by them)", linked, naming.Count);
        }

        return linked;
    }

    /// <summary>A place's review (the latest linked) and where it stands on reviews (AD-39 rule 4)</summary>
    private async Task<(PlaceReviewView? Review, ReviewState State)> PlaceReviewAsync(string owner, AtlasPlace p,
        IReadOnlyList<(DateTime Start, DateTime End)> visits, CancellationToken ct)
    {
        var review = await _db.Reviews.AsNoTracking().Where(r => r.OwnerId == owner && r.PlaceId == p.Id)
            .OrderByDescending(r => r.ReviewedAt).FirstOrDefaultAsync(ct);
        var category = p.Category?.Trim().ToLowerInvariant();
        var decisions = await _db.ReviewReminders.AsNoTracking()
            .Where(d => d.OwnerId == owner && (d.PlaceId == p.Id || (category != null && d.Category == category)))
            .ToListAsync(ct);
        var mine = decisions.FirstOrDefault(d => d.PlaceId == p.Id);
        var state = PlaceReviews.Assess(new ReviewFacts(p.Name, p.Category, p.IdentifyState, visits, review != null, mine?.Kind, mine?.Until,
            decisions.Any(d => d.Category != null)), DateTime.UtcNow);
        if (state.Reason != "no_identity")
        {
            state = state with { WriteUrl = PlaceReviews.WriteUrl(p.Name, p.GooglePlaceId, p.Lat, p.Lon) };
        }

        return (review == null ? null : View(review), state);
    }

    /// <summary>The Owner's reviews and the places to review, all time (AD-39 rule 7)</summary>
    public async Task<ReviewsOverview> ReviewsOverviewAsync(string owner, CancellationToken ct = default)
    {
        var reviews = await _db.Reviews.AsNoTracking().Where(r => r.OwnerId == owner).OrderByDescending(r => r.ReviewedAt).ToListAsync(ct);
        var rated = reviews.Where(r => r.Rating.HasValue).Select(r => r.Rating!.Value).ToList();
        var (due, total) = await DueAsync(owner, reviews.Where(r => r.PlaceId != null).Select(r => r.PlaceId!.Value).ToHashSet(), ct);
        return new ReviewsOverview(reviews.Count, rated.Count > 0 ? Math.Round(rated.Average(), 1) : null,
            reviews.Count(r => r.PlaceId != null), reviews.Count(r => r.PlaceId == null),
            reviews.Take(PlaceReviews.RecentShown).Select(View).ToList(), due, total);
    }

    /// <summary>The places to review (AD-39 rule 5), the most recently visited first; the first few and the total</summary>
    private async Task<(List<ReviewDue> Due, int Total)> DueAsync(string owner, HashSet<Guid> reviewed, CancellationToken ct)
    {
        var places = await _db.Places.AsNoTracking()
            .Where(p => p.OwnerId == owner && (p.IdentifyState == "auto" || p.IdentifyState == "user"))
            .Select(p => new { p.Id, p.Name, p.Category, p.IdentifyState, p.GooglePlaceId, p.Lat, p.Lon })
            .ToListAsync(ct);
        var ids = places.Select(p => p.Id).ToList();
        var visits = (await _db.Visits.AsNoTracking()
                .Where(v => v.OwnerId == owner && v.PlaceId != null && ids.Contains(v.PlaceId.Value))
                .Select(v => new { PlaceId = v.PlaceId!.Value, v.StartTime, v.EndTime })
                .ToListAsync(ct))
            .ToLookup(v => v.PlaceId, v => (Start: v.StartTime, End: v.EndTime));
        var decisions = await _db.ReviewReminders.AsNoTracking().Where(d => d.OwnerId == owner).ToListAsync(ct);
        var byPlace = decisions.Where(d => d.PlaceId != null).ToDictionary(d => d.PlaceId!.Value);
        var categories = decisions.Where(d => d.Category != null).Select(d => d.Category!).ToHashSet();
        var now = DateTime.UtcNow;
        var due = new List<ReviewDue>();
        foreach (var p in places)
        {
            var own = visits[p.Id].ToList();
            byPlace.TryGetValue(p.Id, out var d);
            var category = p.Category?.Trim().ToLowerInvariant();
            var state = PlaceReviews.Assess(new ReviewFacts(p.Name, p.Category, p.IdentifyState, own, reviewed.Contains(p.Id), d?.Kind, d?.Until,
                category != null && categories.Contains(category)), now);
            if (state.State == "to_review")
            {
                due.Add(new ReviewDue(p.Id, p.Name, p.Category, PlaceReviews.LastQualifying(own, now)!.Value, own.Count,
                    PlaceReviews.WriteUrl(p.Name, p.GooglePlaceId, p.Lat, p.Lon), p.Lat, p.Lon));
            }
        }

        return (due.OrderByDescending(x => x.LastVisit).Take(PlaceReviews.DueShown).ToList(), due.Count);
    }

    /// <summary>
    /// The Owner's choice about a place's reminder (AD-39 rule 6): later, never, done,
    /// never_kind (its category), or clear (with Kind, its category's too). Returns where the place stands.
    /// </summary>
    /// <exception cref="AtlasNotFoundException">No such place</exception>
    /// <exception cref="ArgumentException">An unknown action, or a category decision for a place without one</exception>
    public async Task<ReviewState> SetReviewReminderAsync(string owner, ReviewReminderRequest req, CancellationToken ct = default)
    {
        var place = await _db.Places.AsNoTracking().FirstOrDefaultAsync(p => p.OwnerId == owner && p.Id == req.PlaceId, ct)
                    ?? throw new AtlasNotFoundException("Place");
        var category = place.Category?.Trim().ToLowerInvariant();
        var action = req.Action?.Trim().ToLowerInvariant();
        switch (action)
        {
            case "later":
            case "never":
            case "done":
                var row = await _db.ReviewReminders.FirstOrDefaultAsync(d => d.OwnerId == owner && d.PlaceId == place.Id, ct);
                if (row == null)
                {
                    row = new ReviewReminder { OwnerId = owner, PlaceId = place.Id };
                    _db.ReviewReminders.Add(row);
                }

                row.Kind = action;
                row.Until = action == "later" ? DateTime.UtcNow + PlaceReviews.Later : null;
                row.CreatedAt = DateTime.UtcNow;
                break;
            case "never_kind":
                if (string.IsNullOrEmpty(category))
                {
                    throw new ArgumentException("The place has no category to set aside");
                }

                if (!await _db.ReviewReminders.AnyAsync(d => d.OwnerId == owner && d.Category == category, ct))
                {
                    _db.ReviewReminders.Add(new ReviewReminder { OwnerId = owner, Category = category, Kind = "never" });
                }

                break;
            case "clear":
                await _db.ReviewReminders.Where(d => d.OwnerId == owner && d.PlaceId == place.Id).ExecuteDeleteAsync(ct);
                if (req.Kind && category != null)
                {
                    await _db.ReviewReminders.Where(d => d.OwnerId == owner && d.Category == category).ExecuteDeleteAsync(ct);
                }

                break;
            default:
                throw new ArgumentException("action must be later, never, done, never_kind or clear");
        }

        await _db.SaveChangesAsync(ct);
        return (await GetPlaceAsync(owner, place.Id, ct)).ReviewState!;
    }

    private static PlaceReviewView View(PlaceReview r)
    {
        List<ReviewAnswer> answers;
        try
        {
            answers = JsonSerializer.Deserialize<List<ReviewAnswer>>(r.Answers, ReviewJson) ?? new();
        }
        catch (JsonException)
        {
            answers = new();
        }

        return new PlaceReviewView(r.Id, r.Name, r.Address, r.Rating, r.Text, r.Published, r.ReviewedAt, r.MapsUrl, answers, r.PlaceId, r.LinkedBy,
            r.Lat, r.Lon);
    }
}
