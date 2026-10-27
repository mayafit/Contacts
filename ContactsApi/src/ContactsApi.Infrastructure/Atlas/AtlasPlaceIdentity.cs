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
    /// </summary>
    public static IdentityDecision Decide(IEnumerable<LocationCandidate> candidates)
    {
        var relevant = candidates.Where(Relevant).OrderBy(c => c.DistanceM).ToList();
        var close = relevant.Where(c => c.DistanceM <= ClearM).ToList();
        if (close.Count == 1 && relevant.Count(c => c.DistanceM <= AloneM) == 1)
        {
            return new IdentityDecision(close[0], new List<LocationCandidate>());
        }

        return new IdentityDecision(null, relevant.Where(c => c.DistanceM <= NearbyM).Take(6).ToList());
    }

    /// <summary>
    /// Whether an area has a single identity: around the identified place, no other
    /// relevant place than the one chosen (by its OSM reference)
    /// </summary>
    public static bool SingleIdentity(IEnumerable<LocationCandidate> around, string linkedRef) =>
        around.Where(Relevant).All(c => c.Ref == linkedRef);
}

public partial class AtlasService
{
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Identifies places that wait for an identity, most visited first (the fusion
    /// service calls it in small batches while idle; each needs a lookup of about a
    /// second). A clear identity names the place ("auto"); several possible ones are
    /// kept for the user ("ambiguous").
    /// </summary>
    /// <returns>How many places it decided (0: none left, or the lookups fail — try later)</returns>
    public async Task<int> IdentifyPendingPlacesAsync(GeoLookupClient geo, int limit, CancellationToken ct = default)
    {
        var unnamed = PlaceIdentity.UnnamedNames.ToList();
        var todo = await _db.Places
            .Where(p => p.IdentifyState == "pending" && unnamed.Contains(p.Name))
            .OrderByDescending(p => _db.Visits.Count(v => v.PlaceId == p.Id))
            .ThenBy(p => p.Id)
            .Take(limit)
            .ToListAsync(ct);
        var done = 0;
        foreach (var place in todo)
        {
            List<LocationCandidate> around;
            try
            {
                around = await geo.NearbyQuickAsync(place.Lat, place.Lon, PlaceIdentity.NearbyM, ct);
            }
            catch (GeoLookupException ex)
            {
                // The service is down or busy: stop, the next run tries again
                _logger.LogWarning("Place identification paused: {Message}", ex.Message);
                break;
            }

            var decision = PlaceIdentity.Decide(around);
            if (decision.Choice is { } choice)
            {
                ApplyCandidate(place, choice);
                place.IdentifyState = "auto";
                place.IdentifiedFrom = "lookup";
                place.IdentifyCandidates = "[]";
            }
            else if (decision.Options.Count > 0)
            {
                place.IdentifyState = "ambiguous";
                place.IdentifyCandidates = JsonSerializer.Serialize(decision.Options, JsonWeb);
            }
            else
            {
                place.IdentifyState = "none";
            }

            place.UpdatedAt = DateTime.UtcNow;
            done++;
        }

        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return done;
    }

    /// <summary>The user accepts the automatic identity</summary>
    public async Task<PlaceDetail> ConfirmPlaceAsync(string owner, Guid id, CancellationToken ct = default)
    {
        var place = await _db.Places.FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == owner, ct) ?? throw new AtlasNotFoundException("Place");
        place.IdentifyState = "user";
        place.UserEdited = true;
        place.IdentifyCandidates = "[]";
        place.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return await GetPlaceAsync(owner, id, ct);
    }

    /// <summary>Gives places the identity of another (the user chose them)</summary>
    public async Task<List<NearbyPlace>> ApplyIdentityAsync(string owner, Guid fromId, IReadOnlyCollection<Guid> placeIds, CancellationToken ct = default)
    {
        var from = await _db.Places.AsNoTracking().FirstOrDefaultAsync(p => p.Id == fromId && p.OwnerId == owner, ct)
                   ?? throw new AtlasNotFoundException("Place");
        var ids = placeIds.Where(i => i != fromId).Distinct().Take(200).ToList();
        var targets = await _db.Places.Where(p => p.OwnerId == owner && ids.Contains(p.Id)).ToListAsync(ct);
        foreach (var t in targets)
        {
            CopyIdentity(from, t);
            t.IdentifyState = "user";
            t.IdentifiedFrom = "nearby";
            t.UserEdited = true;
        }

        await _db.SaveChangesAsync(ct);
        return await NearbyRowsAsync(owner, from, targets.Select(t => t.Id).ToList(), ct);
    }

    /// <summary>
    /// After the user identified a place: the places within 100 m that are not the
    /// user's get the same identity when the area has no other (by a lookup around
    /// it); otherwise they are suggested. A typed name (no link) is only suggested.
    /// </summary>
    private async Task<(List<NearbyPlace> Applied, List<NearbyPlace> Suggested)> SpreadIdentityAsync(
        string owner, AtlasPlace from, GeoLookupClient? geo, CancellationToken ct)
    {
        const double degLat = PlaceIdentity.NearbyM / 111_320;
        var degLon = PlaceIdentity.NearbyM / (111_320 * Math.Max(0.1, Math.Cos(from.Lat * Math.PI / 180)));
        var near = (await _db.Places
                .Where(p => p.OwnerId == owner && p.Id != from.Id && p.IdentifyState != "user"
                            && p.Lat >= from.Lat - degLat && p.Lat <= from.Lat + degLat
                            && p.Lon >= from.Lon - degLon && p.Lon <= from.Lon + degLon)
                .ToListAsync(ct))
            .Where(p => Geo.Haversine(from.Lat, from.Lon, p.Lat, p.Lon) <= PlaceIdentity.NearbyM)
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
                // The area around the place and its neighbours
                var around = await geo.NearbyQuickAsync(from.Lat, from.Lon, PlaceIdentity.NearbyM * 1.5, ct);
                single = PlaceIdentity.SingleIdentity(around, from.LinkedRef);
            }
            catch (GeoLookupException)
            {
                // Unknown: ask
            }
        }

        if (!single)
        {
            return (new(), await NearbyRowsAsync(owner, from, near.Select(p => p.Id).ToList(), ct));
        }

        foreach (var p in near)
        {
            CopyIdentity(from, p);
            p.IdentifyState = "auto";
            p.IdentifiedFrom = "nearby";
        }

        await _db.SaveChangesAsync(ct);
        return (await NearbyRowsAsync(owner, from, near.Select(p => p.Id).ToList(), ct), new());
    }

    private async Task<List<NearbyPlace>> NearbyRowsAsync(string owner, AtlasPlace from, List<Guid> ids, CancellationToken ct) =>
        (await _db.Places.AsNoTracking().Where(p => p.OwnerId == owner && ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Lat, p.Lon, p.IdentifyState, Visits = _db.Visits.Count(v => v.PlaceId == p.Id) })
            .ToListAsync(ct))
        .Select(p => new NearbyPlace(p.Id, p.Name, p.Lat, p.Lon, Math.Round(Geo.Haversine(from.Lat, from.Lon, p.Lat, p.Lon)), p.Visits, p.IdentifyState))
        .OrderBy(p => p.DistanceM)
        .ToList();

    private static void ApplyCandidate(AtlasPlace place, LocationCandidate c)
    {
        place.Name = c.Name.Length > 300 ? c.Name[..300] : c.Name;
        place.Category = c.Label;
        place.LinkedRef = c.Ref;
        place.Address = c.Address ?? place.Address;
        place.Website = c.Website;
        place.Phone = c.Phone;
        place.OpeningHours = c.OpeningHours;
        place.Details = JsonSerializer.Serialize(c.Details);
    }

    private static void CopyIdentity(AtlasPlace from, AtlasPlace to)
    {
        to.Name = from.Name;
        to.Address = from.Address;
        to.Category = from.Category;
        to.LinkedRef = from.LinkedRef;
        to.Website = from.Website;
        to.Phone = from.Phone;
        to.OpeningHours = from.OpeningHours;
        to.Details = from.Details;
        to.IdentifyCandidates = "[]";
        to.UpdatedAt = DateTime.UtcNow;
    }

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
