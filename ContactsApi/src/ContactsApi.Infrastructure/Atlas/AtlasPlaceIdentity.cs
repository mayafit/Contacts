/**
 * @fileoverview Place identity: places identified automatically in the background
 * (one clear establishment around them), ambiguous ones kept for the user, and an
 * identity the user gives spread to the places around it
 * @module ContactsApi.Infrastructure.Atlas
 */
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ContactsApi.Infrastructure.Atlas;

/// <summary>A place near another one (to give it the same identity)</summary>
public record NearbyPlace(Guid PlaceId, string Name, double Lat, double Lon, double DistanceM, int Visits, string IdentifyState);

/// <summary>What the automatic identification decided for a place</summary>
/// <param name="Choice">The one clear identity, or null</param>
/// <param name="Options">The possible identities when it is not clear (nearest first)</param>
public record IdentityDecision(LocationCandidate? Choice, List<LocationCandidate> Options);

/// <summary>The rules (pure: no database or network)</summary>
public static class PlaceIdentity
{
    /// <summary>The one identity must be this close…</summary>
    public const double ClearM = 40;

    /// <summary>…with no other one this close</summary>
    public const double AloneM = 80;

    /// <summary>Places this close to an identified one may get its identity</summary>
    public const double NearbyM = 100;

    /// <summary>Visits this long (typically) are to a home or a workplace, not to the shop next door</summary>
    public const double LongStayHours = 6;

    /// <summary>Not what a visit is to: transport stops, parking, street furniture</summary>
    private static readonly HashSet<string> Incidental = new(StringComparer.OrdinalIgnoreCase)
    {
        "highway=bus_stop", "amenity=parking", "amenity=parking_entrance", "amenity=parking_space", "amenity=bicycle_parking",
        "amenity=motorcycle_parking", "amenity=bench", "amenity=waste_basket", "amenity=recycling", "amenity=toilets",
        "amenity=drinking_water", "amenity=telephone", "amenity=post_box", "amenity=vending_machine", "amenity=atm",
        "amenity=shelter", "amenity=charging_station", "amenity=taxi", "amenity=fountain", "amenity=clock", "leisure=pitch",
        "leisure=playground", "man_made=water_tower", "man_made=mast", "natural=tree"
    };

    /// <summary>Generic names that say nothing about the place (it waits for an identity)</summary>
    public static readonly HashSet<string> UnnamedNames = new(StringComparer.OrdinalIgnoreCase) { "", "Place", "Saved place", "Searched place" };

    /// <summary>Whether a candidate is something a visit is to (a shop, a site, an office…)</summary>
    public static bool Relevant(LocationCandidate c) =>
        !Incidental.Contains(c.Kind) && !c.Kind.StartsWith("public_transport=", StringComparison.Ordinal)
                                     && !c.Kind.StartsWith("railway=", StringComparison.Ordinal);

    /// <summary>
    /// One clear identity: exactly one relevant place within 40 m and no other within
    /// 80 m. Otherwise the relevant places within 100 m are the options (none: nothing).
    /// A place of long stays (a home, a workplace) is never named after its neighbour
    /// automatically: the clear one becomes an option for the user.
    /// </summary>
    /// <param name="longStay">The place's visits typically last <see cref="LongStayHours"/> or more</param>
    public static IdentityDecision Decide(IEnumerable<LocationCandidate> candidates, bool longStay = false)
    {
        var relevant = candidates.Where(Relevant).OrderBy(c => c.DistanceM).ToList();
        var close = relevant.Where(c => c.DistanceM <= ClearM).ToList();
        var options = relevant.Where(c => c.DistanceM <= NearbyM).Take(6).ToList();
        if (close.Count == 1 && relevant.Count(c => c.DistanceM <= AloneM) == 1 && !longStay)
        {
            return new IdentityDecision(close[0], new List<LocationCandidate>());
        }

        return new IdentityDecision(null, options);
    }

    /// <summary>
    /// Whether an area has a single identity: the answer contains the chosen place (by
    /// its OSM reference) and no other relevant one. An empty answer (an outage, a gap
    /// in the data) is not proof of anything.
    /// </summary>
    public static bool SingleIdentity(IReadOnlyCollection<LocationCandidate> around, string linkedRef) =>
        around.Any(c => c.Ref == linkedRef) && around.Where(Relevant).All(c => c.Ref == linkedRef);
}

public partial class AtlasService
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Identifies places that wait for an identity, most visited first (the fusion
    /// service calls it in small batches while idle; each needs a lookup of about a
    /// second). Places where nothing was found are looked at again after 30 days. Each
    /// place is written only if it still waits (the user may have named it meanwhile).
    /// </summary>
    /// <returns>How many places it looked at (0: none left, or the lookups fail — try later)</returns>
    public async Task<int> IdentifyPendingPlacesAsync(GeoLookupClient geo, int limit, CancellationToken ct = default)
    {
        var unnamed = PlaceIdentity.UnnamedNames.ToList();
        var recheck = DateTime.UtcNow.AddDays(-30);
        var todo = await _db.Places.AsNoTracking()
            .Where(p => unnamed.Contains(p.Name)
                        && (p.IdentifyState == "pending"
                            || (p.IdentifyState == "none" && (p.IdentifyCheckedAt == null || p.IdentifyCheckedAt < recheck))))
            .OrderByDescending(p => _db.Visits.Count(v => v.PlaceId == p.Id))
            .ThenBy(p => p.Id)
            .Take(limit)
            .Select(p => new { p.Id, p.Lat, p.Lon, p.Address })
            .ToListAsync(ct);
        if (todo.Count == 0)
        {
            return 0;
        }

        // How long visits there typically last (the median)
        var ids = todo.Select(t => t.Id).ToList();
        var stays = (await _db.Visits.AsNoTracking().Where(v => v.PlaceId != null && ids.Contains(v.PlaceId.Value))
                .Select(v => new { PlaceId = v.PlaceId!.Value, v.StartTime, v.EndTime }).ToListAsync(ct))
            .GroupBy(v => v.PlaceId)
            .ToDictionary(g => g.Key, g =>
            {
                var hours = g.Select(v => (v.EndTime - v.StartTime).TotalHours).OrderBy(h => h).ToList();
                return hours[hours.Count / 2];
            });

        var answers = new List<(Guid Id, string? Address, List<LocationCandidate> Around)>();
        foreach (var place in todo)
        {
            try
            {
                answers.Add((place.Id, place.Address, await geo.NearbyQuickAsync(place.Lat, place.Lon, PlaceIdentity.NearbyM, ct)));
            }
            catch (GeoLookupException ex)
            {
                // The service is down or busy: stop, the next run tries again
                _logger.LogWarning("Place identification paused: {Message}", ex.Message);
                break;
            }
        }

        // Nothing anywhere in a whole batch is an outage, not a fact about these places
        if (answers.Count >= 5 && answers.All(a => a.Around.Count == 0))
        {
            _logger.LogWarning("Place identification paused: {Count} lookups in a row found nothing", answers.Count);
            return 0;
        }

        var now = DateTime.UtcNow;
        foreach (var (id, address, around) in answers)
        {
            var decision = PlaceIdentity.Decide(around, stays.GetValueOrDefault(id) >= PlaceIdentity.LongStayHours);
            var waiting = _db.Places.Where(p => p.Id == id && unnamed.Contains(p.Name)
                                                && (p.IdentifyState == "pending" || p.IdentifyState == "none"));
            if (decision.Choice is { } c)
            {
                var details = JsonSerializer.Serialize(c.Details);
                var name = c.Name.Length > 300 ? c.Name[..300] : c.Name;
                await waiting.ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Name, name)
                    .SetProperty(p => p.Category, c.Label)
                    .SetProperty(p => p.LinkedRef, c.Ref)
                    .SetProperty(p => p.Address, c.Address ?? address)
                    .SetProperty(p => p.Website, c.Website)
                    .SetProperty(p => p.Phone, c.Phone)
                    .SetProperty(p => p.OpeningHours, c.OpeningHours)
                    .SetProperty(p => p.Details, details)
                    .SetProperty(p => p.IdentifyState, "auto")
                    .SetProperty(p => p.IdentifiedFrom, "lookup")
                    .SetProperty(p => p.IdentifyCandidates, "[]")
                    .SetProperty(p => p.IdentifyCheckedAt, now)
                    .SetProperty(p => p.UpdatedAt, now), ct);
            }
            else
            {
                var state = decision.Options.Count > 0 ? "ambiguous" : "none";
                var candidates = JsonSerializer.Serialize(decision.Options, JsonWeb);
                await waiting.ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.IdentifyState, state)
                    .SetProperty(p => p.IdentifyCandidates, candidates)
                    .SetProperty(p => p.IdentifyCheckedAt, now)
                    .SetProperty(p => p.UpdatedAt, now), ct);
            }
        }

        return answers.Count;
    }

    /// <summary>The user accepts the automatic identity (only an automatic one)</summary>
    public async Task<PlaceDetail> ConfirmPlaceAsync(string owner, Guid id, CancellationToken ct = default)
    {
        var place = await _db.Places.FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == owner, ct) ?? throw new AtlasNotFoundException("Place");
        if (place.IdentifyState != "auto")
        {
            throw new ArgumentException("Only an automatic identity can be confirmed");
        }

        place.IdentifyState = "user";
        place.UserEdited = true;
        place.IdentifyCandidates = "[]";
        place.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return await GetPlaceAsync(owner, id, ct);
    }

    /// <summary>
    /// Gives places the identity of one the user identified (they chose them); places
    /// the user named themselves keep their names
    /// </summary>
    public async Task<List<NearbyPlace>> ApplyIdentityAsync(string owner, Guid fromId, IReadOnlyCollection<Guid> placeIds, CancellationToken ct = default)
    {
        var from = await _db.Places.AsNoTracking().FirstOrDefaultAsync(p => p.Id == fromId && p.OwnerId == owner, ct)
                   ?? throw new AtlasNotFoundException("Place");
        if (from.IdentifyState != "user" || PlaceIdentity.UnnamedNames.Contains(from.Name))
        {
            throw new ArgumentException("Name this place first");
        }

        var changed = new List<Guid>();
        foreach (var id in placeIds.Where(i => i != fromId).Distinct().Take(200))
        {
            if (await CopyIdentityAsync(owner, from, id, "user", ct) > 0)
            {
                changed.Add(id);
            }
        }

        return await NearbyRowsAsync(owner, from, changed, ct);
    }

    /// <summary>
    /// After the user identified a place: the places within 100 m that are not the
    /// user's get the same identity when the area has no other (by a lookup around
    /// it, which may take a few seconds at most); otherwise they are suggested. A typed
    /// name (no link) is only suggested.
    /// </summary>
    private async Task<(List<NearbyPlace> Applied, List<NearbyPlace> Suggested)> SpreadIdentityAsync(
        string owner, AtlasPlace from, GeoLookupClient? geo, CancellationToken ct)
    {
        const double degLat = PlaceIdentity.NearbyM / 111_320;
        var degLon = PlaceIdentity.NearbyM / (111_320 * Math.Max(0.1, Math.Cos(from.Lat * Math.PI / 180)));
        var near = (await _db.Places.AsNoTracking()
                .Where(p => p.OwnerId == owner && p.Id != from.Id && p.IdentifyState != "user"
                            && p.Lat >= from.Lat - degLat && p.Lat <= from.Lat + degLat
                            && p.Lon >= from.Lon - degLon && p.Lon <= from.Lon + degLon)
                .Select(p => new { p.Id, p.Lat, p.Lon })
                .ToListAsync(ct))
            .Where(p => Geo.Haversine(from.Lat, from.Lon, p.Lat, p.Lon) <= PlaceIdentity.NearbyM)
            .Select(p => p.Id)
            .ToList();
        if (near.Count == 0)
        {
            return (new(), new());
        }

        var single = false;
        if (from.LinkedRef != null && geo != null)
        {
            try
            {
                // The area around the place and its neighbours; the user is waiting
                var around = await geo.NearbyQuickAsync(from.Lat, from.Lon, PlaceIdentity.NearbyM * 1.5, ct, TimeSpan.FromSeconds(4));
                single = PlaceIdentity.SingleIdentity(around, from.LinkedRef);
            }
            catch (GeoLookupException)
            {
                // Unknown: ask
            }
        }

        if (!single)
        {
            return (new(), await NearbyRowsAsync(owner, from, near, ct));
        }

        var applied = new List<Guid>();
        foreach (var id in near)
        {
            if (await CopyIdentityAsync(owner, from, id, "auto", ct) > 0)
            {
                applied.Add(id);
            }
        }

        return (await NearbyRowsAsync(owner, from, applied, ct), new());
    }

    /// <summary>Copies a place's identity to another, unless the user named that one</summary>
    /// <returns>1 when copied</returns>
    private Task<int> CopyIdentityAsync(string owner, AtlasPlace from, Guid toId, string state, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return _db.Places.Where(p => p.Id == toId && p.OwnerId == owner && p.IdentifyState != "user")
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Name, from.Name)
                .SetProperty(p => p.Address, from.Address)
                .SetProperty(p => p.Category, from.Category)
                .SetProperty(p => p.LinkedRef, from.LinkedRef)
                .SetProperty(p => p.Website, from.Website)
                .SetProperty(p => p.Phone, from.Phone)
                .SetProperty(p => p.OpeningHours, from.OpeningHours)
                .SetProperty(p => p.Details, from.Details)
                .SetProperty(p => p.IdentifyState, state)
                .SetProperty(p => p.IdentifiedFrom, "nearby")
                .SetProperty(p => p.IdentifyCandidates, "[]")
                .SetProperty(p => p.UserEdited, state == "user")
                .SetProperty(p => p.UpdatedAt, now), ct);
    }

    private async Task<List<NearbyPlace>> NearbyRowsAsync(string owner, AtlasPlace from, List<Guid> ids, CancellationToken ct) =>
        (await _db.Places.AsNoTracking().Where(p => p.OwnerId == owner && ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Lat, p.Lon, p.IdentifyState, Visits = _db.Visits.Count(v => v.PlaceId == p.Id) })
            .ToListAsync(ct))
        .Select(p => new NearbyPlace(p.Id, p.Name, p.Lat, p.Lon, Math.Round(Geo.Haversine(from.Lat, from.Lon, p.Lat, p.Lon)), p.Visits, p.IdentifyState))
        .OrderBy(p => p.DistanceM)
        .ToList();

    private static List<LocationCandidate> CandidatesOf(AtlasPlace p)
    {
        try
        {
            return JsonSerializer.Deserialize<List<LocationCandidate>>(p.IdentifyCandidates, JsonWeb) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}
