/**
 * @fileoverview The Owner's Google Maps reviews (AD-39): their keys, the link of a review
 * to the place it is about, and which places are due a review. Pure.
 * @module ContactsApi.Infrastructure.Atlas
 */
using System.Globalization;
using System.Text.RegularExpressions;

namespace ContactsApi.Infrastructure.Atlas;

/// <summary>A place as linking and reminders see it</summary>
public record ReviewPlace(Guid Id, string Name, double Lat, double Lon);

/// <summary>What the reminder rules read about a place</summary>
/// <param name="Visits">Its visits (start, end), any order</param>
/// <param name="Reviewed">A review is linked to it</param>
/// <param name="Decision">The Owner's decision about the place: later, never, done (null: none)</param>
/// <param name="CategorySetAside">The Owner set its category aside</param>
public record ReviewFacts(
    string Name, string? Category, string IdentifyState, IReadOnlyList<(DateTime Start, DateTime End)> Visits, bool Reviewed,
    string? Decision = null, DateTime? Until = null, bool CategorySetAside = false);

/// <summary>The rules of AD-39</summary>
public static partial class PlaceReviews
{
    /// <summary>Candidates for a link lie within this (metres)</summary>
    public const double NameM = 75;

    /// <summary>The only place within this is the review's place (metres)</summary>
    public const double OnlyM = 30;

    /// <summary>A nearest place within this…</summary>
    public const double NearestM = 10;

    /// <summary>…with no other within this is the review's place (metres)</summary>
    public const double ClearM = 25;

    /// <summary>A word of a name has at least this many characters</summary>
    public const int MinWord = 3;

    /// <summary>A visit at least this long counts for a reminder</summary>
    public static readonly TimeSpan MinStay = TimeSpan.FromMinutes(10);

    /// <summary>A reminder needs a visit that ended within this</summary>
    public static readonly TimeSpan Recent = TimeSpan.FromDays(180);

    /// <summary>Visited on more distinct days than this: routine, never reminded</summary>
    public const int RoutineDays = 30;

    /// <summary>"Later" hides a reminder this long</summary>
    public static readonly TimeSpan Later = TimeSpan.FromDays(30);

    /// <summary>Places listed in the overview</summary>
    public const int DueShown = 30;

    /// <summary>Latest reviews listed in the overview</summary>
    public const int RecentShown = 20;

    /// <summary>Names that say nothing about what a place is</summary>
    public static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "Place", "Home", "Work", "Saved place", "Searched place"
    };

    /// <summary>Names a review may replace: a place with no name of its own (never Home or Work)</summary>
    public static readonly HashSet<string> UnnamedNames = new(StringComparer.OrdinalIgnoreCase) { "", "Place", "Saved place", "Searched place" };

    /// <summary>Categories never reminded: errands, private places, health care, and what one does not review</summary>
    public static readonly HashSet<string> ExcludedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fuel", "Building", "Company", "Office", "Works", "Industrial", "Construction", "Building materials", "Bank", "Post office",
        "Townhall", "Courthouse", "Police", "Diplomatic", "Public building", "Public", "Social facility", "Bus station", "Bus stop",
        "Transportation", "Clinic", "Doctors", "Hospital", "Pharmacy", "Veterinary", "School", "Place of worship", "Church",
        "Car repair", "Bridge", "Slipway", "Thermometer", "Amenity", "Convenience", "home", "work", "inferred_home", "inferred_work"
    };

    [GeneratedRegex(@"[?&]cid=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CidPattern();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    /// <summary>A review's key: the cid of its Google Maps link, else the link, else name|lat|lon (5 decimals)</summary>
    public static string Key(string? mapsUrl, string name, double lat, double lon)
    {
        if (!string.IsNullOrWhiteSpace(mapsUrl))
        {
            var cid = CidPattern().Match(mapsUrl);
            return cid.Success ? "cid:" + cid.Groups[1].Value : mapsUrl.Trim();
        }

        return string.Create(CultureInfo.InvariantCulture, $"{name.Trim().ToLowerInvariant()}|{lat:F5}|{lon:F5}");
    }

    /// <summary>The words of a name: lower case, split on anything but letters and digits, at least <see cref="MinWord"/> long</summary>
    public static HashSet<string> Words(string? name) =>
        NonWord().Split((name ?? string.Empty).ToLowerInvariant()).Where(w => w.Length >= MinWord).ToHashSet();

    /// <summary>
    /// The place a review is about (AD-39 rule 3): the nearest within <see cref="NameM"/>
    /// sharing a word with its name ("name"); else the only one within <see cref="OnlyM"/>
    /// ("only"); else the nearest within <see cref="NearestM"/> with no other within
    /// <see cref="ClearM"/> ("nearest"); else none
    /// </summary>
    public static (Guid? PlaceId, string? By) Link(string name, double lat, double lon, IEnumerable<ReviewPlace> places)
    {
        var near = places.Select(p => (Place: p, M: Meters(lat, lon, p.Lat, p.Lon)))
            .Where(x => x.M <= NameM).OrderBy(x => x.M).ToList();
        if (near.Count == 0)
        {
            return (null, null);
        }

        var words = Words(name);
        var named = near.FirstOrDefault(x => words.Overlaps(Words(x.Place.Name)));
        if (named.Place != null)
        {
            return (named.Place.Id, "name");
        }

        var within = near.Where(x => x.M <= OnlyM).ToList();
        if (within.Count == 1)
        {
            return (within[0].Place.Id, "only");
        }

        if (near[0].M <= NearestM && (near.Count == 1 || near[1].M > ClearM))
        {
            return (near[0].Place.Id, "nearest");
        }

        return (null, null);
    }

    /// <summary>
    /// Where a place stands (AD-39 rules 4 to 6): reviewed; set aside (later until, never,
    /// done, its category); not reviewable and why; or to_review
    /// </summary>
    public static ReviewState Assess(ReviewFacts f, DateTime now)
    {
        if (f.Reviewed)
        {
            return new ReviewState("reviewed");
        }

        if ((f.IdentifyState != "auto" && f.IdentifyState != "user") || GenericNames.Contains(f.Name.Trim()))
        {
            return new ReviewState("not_reviewable", Reason: "no_identity");
        }

        if (f.CategorySetAside)
        {
            return new ReviewState("never", Category: true);
        }

        switch (f.Decision)
        {
            case "never":
            case "done":
                return new ReviewState(f.Decision);
            case "later" when f.Until > now:
                return new ReviewState("later", f.Until);
        }

        if (f.Category != null && ExcludedCategories.Contains(f.Category.Trim()))
        {
            return new ReviewState("not_reviewable", Reason: "category");
        }

        if (f.Visits.Select(v => v.Start.Date).Distinct().Count() > RoutineDays)
        {
            return new ReviewState("not_reviewable", Reason: "routine");
        }

        return LastQualifying(f.Visits, now) == null
            ? new ReviewState("not_reviewable", Reason: "no_recent_visit")
            : new ReviewState("to_review");
    }

    /// <summary>The end of the latest visit of at least <see cref="MinStay"/> that ended within <see cref="Recent"/>; null when none</summary>
    public static DateTime? LastQualifying(IEnumerable<(DateTime Start, DateTime End)> visits, DateTime now) =>
        visits.Where(v => v.End - v.Start >= MinStay && v.End >= now - Recent && v.End <= now.AddDays(1))
            .Select(v => (DateTime?)v.End).Max();

    /// <summary>
    /// Where the Owner writes the review: the Maps URLs search link, opening the place by its
    /// Google place id (else by its name and position)
    /// </summary>
    public static string WriteUrl(string name, string? googlePlaceId, double lat, double lon)
    {
        var query = Uri.EscapeDataString(string.IsNullOrWhiteSpace(googlePlaceId)
            ? string.Create(CultureInfo.InvariantCulture, $"{name} {lat:F6},{lon:F6}")
            : name);
        return string.IsNullOrWhiteSpace(googlePlaceId)
            ? $"https://www.google.com/maps/search/?api=1&query={query}"
            : $"https://www.google.com/maps/search/?api=1&query={query}&query_place_id={Uri.EscapeDataString(googlePlaceId)}";
    }

    /// <summary>Equirectangular metres (short distances)</summary>
    public static double Meters(double lat1, double lon1, double lat2, double lon2)
    {
        var x = (lon2 - lon1) * Math.Cos((lat1 + lat2) / 2 * Math.PI / 180) * 111_320;
        var y = (lat2 - lat1) * 110_540;
        return Math.Sqrt(x * x + y * y);
    }
}
