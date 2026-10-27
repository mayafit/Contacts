/**
 * @fileoverview What is at a point: the street address and named places around it,
 * from OpenStreetMap (Nominatim, Photon and Overpass), cached and within their usage policy
 * @module ContactsApi.Infrastructure.Atlas
 */
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace ContactsApi.Infrastructure.Atlas;

/// <summary>What is at a point (see the contract in docs/planning/epic-11-fusion.md)</summary>
public record LocationLookup(double Lat, double Lon, LookupAddress? Address, List<LocationCandidate> Candidates, List<string> Errors,
    string Attribution);

/// <summary>The street address at a point</summary>
public record LookupAddress(string Display, string? HouseNumber, string? Road, string? Suburb, string? City, string? State,
    string? Postcode, string? Country, string? CountryCode);

/// <summary>A named place near the point</summary>
/// <param name="Ref">"osm:node/123", "osm:way/456"</param>
/// <param name="Name">In English when known, else the local name</param>
/// <param name="LocalName">The local-language name when it differs</param>
/// <param name="Kind">The OSM tag that makes it what it is, e.g. "amenity=cafe"</param>
/// <param name="Label">A human label, e.g. "Cafe"</param>
public record LocationCandidate(string Ref, string Name, string? LocalName, string Kind, string Label, double Lat, double Lon,
    double DistanceM, string? Address, string? Website, string? Phone, string? OpeningHours, Dictionary<string, string> Details);

/// <summary>A lookup service failed or answered nonsense</summary>
public class GeoLookupException : Exception
{
    /// <summary>The service said it is overloaded (429/502/503/504)</summary>
    public bool Busy { get; init; }

    /// <summary>How long it asked to be left alone</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>Creates the exception</summary>
    public GeoLookupException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// Asks OpenStreetMap what is at a point. Nominatim allows one request per second
/// with an identifying User-Agent; answers are cached for a day (places rarely move).
/// </summary>
public class GeoLookupClient
{
    /// <summary>Named HttpClient</summary>
    public const string HttpClientName = "geo-lookup";

    /// <summary>Shown with the results, as the data's licence requires</summary>
    public const string Attribution = "© OpenStreetMap contributors";

    private const string UserAgent = "Atlas/1.0 (personal data app; https://contacts.mayaffit.com)";
    private const string Nominatim = "https://nominatim.openstreetmap.org/reverse";
    // Public instances: often busy (502 "at capacity") or queueing for half a minute
    private static readonly string[] Overpass =
    {
        "https://overpass-api.de/api/interpreter", "https://overpass.private.coffee/api/interpreter",
        "https://overpass.kumi.systems/api/interpreter"
    };

    /// <summary>How long nearby places may take in all (the app's proxy allows 100 s)</summary>
    // Only for extra details (phone, website, hours): Photon lists the places quickly
    private static readonly TimeSpan OverpassBudget = TimeSpan.FromSeconds(20);
    private const string Photon = "https://photon.komoot.io/reverse";
    private static readonly SemaphoreSlim PhotonGate = new(2, 2);
    private static readonly TimeSpan CacheFor = TimeSpan.FromDays(1);
    private static readonly ConcurrentDictionary<string, (DateTime At, LocationLookup Value)> Cache = new();
    private static readonly ConcurrentDictionary<string, (DateTime At, (LookupAddress?, LocationCandidate?) Value)> ReverseCache = new();
    private static readonly SemaphoreSlim NominatimGate = new(1, 1);
    // Overpass is shared too: at most two requests at a time from this app
    private static readonly SemaphoreSlim OverpassGate = new(2, 2);
    // Instances that said they are full, and until when to leave them alone
    private static readonly ConcurrentDictionary<string, DateTime> BusyUntil = new();
    private static DateTime _lastNominatim = DateTime.MinValue;

    /// <summary>The keys that say what a place is, most telling first</summary>
    private static readonly string[] KindKeys =
    {
        "amenity", "shop", "tourism", "leisure", "historic", "healthcare", "office", "craft", "sport", "public_transport",
        "railway", "aeroway", "man_made", "natural", "club", "place", "landuse", "building"
    };

    /// <summary>Extra facts worth keeping about a place</summary>
    private static readonly string[] DetailKeys =
    {
        "cuisine", "brand", "operator", "wikipedia", "wikidata", "description", "religion", "denomination", "heritage",
        "email", "start_date", "level", "wheelchair", "outdoor_seating", "takeaway", "diet:vegetarian", "diet:vegan", "diet:kosher"
    };

    private readonly HttpClient _http;

    /// <summary>Creates the client</summary>
    public GeoLookupClient(HttpClient http) => _http = http;

    /// <summary>The address at a point and the named places within <paramref name="radiusM"/>, nearest first</summary>
    public async Task<LocationLookup> LookupAsync(double lat, double lon, double radiusM = 80, CancellationToken ct = default)
    {
        if (!double.IsFinite(lat) || !double.IsFinite(lon) || lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            throw new ArgumentException("Coordinates out of range");
        }

        radiusM = double.IsFinite(radiusM) ? Math.Clamp(radiusM, 20, 300) : 80;
        var key = string.Create(CultureInfo.InvariantCulture, $"{lat:F5},{lon:F5},{radiusM:F0}");
        if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheFor)
        {
            return hit.Value;
        }

        var errors = new List<string>();
        LookupAddress? address = null;
        LocationCandidate? atPoint = null;
        var nearby = new List<LocationCandidate>();
        var reverse = ReverseCachedAsync(lat, lon, ct);
        var photon = PhotonAsync(lat, lon, radiusM, ct);
        var around = AroundAsync(lat, lon, radiusM, ct);
        // All finish before any is looked at (no unobserved failure)
        try
        {
            await Task.WhenAll(reverse, photon, around);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // each one below
        }

        ct.ThrowIfCancellationRequested();
        if (reverse.IsCompletedSuccessfully)
        {
            (address, atPoint) = reverse.Result;
        }
        else
        {
            errors.Add("The address lookup is unavailable right now.");
        }

        // Photon lists the places; Overpass, when it answers, adds phone, website and hours
        var byRef = new Dictionary<string, LocationCandidate>();
        if (photon.IsCompletedSuccessfully)
        {
            foreach (var c in photon.Result)
            {
                byRef[c.Ref] = c;
            }
        }

        if (around.IsCompletedSuccessfully)
        {
            foreach (var c in around.Result)
            {
                byRef[c.Ref] = c;
            }
        }

        if (!photon.IsCompletedSuccessfully && !around.IsCompletedSuccessfully)
        {
            errors.Add("Nearby places are unavailable right now.");
        }

        nearby = byRef.Values.ToList();

        if (atPoint != null && nearby.All(c => c.Ref != atPoint.Ref))
        {
            nearby.Add(atPoint);
        }

        var result = new LocationLookup(lat, lon, address, nearby.OrderBy(c => c.DistanceM).Take(25).ToList(), errors, Attribution);
        if (errors.Count == 0)
        {
            if (Cache.Count > 2000)
            {
                Cache.Clear();
            }

            Cache[key] = (DateTime.UtcNow, result);
        }

        return result;
    }

    /// <summary>The reverse lookup does not depend on the radius: cached by the point alone</summary>
    private async Task<(LookupAddress?, LocationCandidate?)> ReverseCachedAsync(double lat, double lon, CancellationToken ct)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{lat:F5},{lon:F5}");
        if (ReverseCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheFor)
        {
            return hit.Value;
        }

        var value = await ReverseAsync(lat, lon, ct);
        if (ReverseCache.Count > 2000)
        {
            ReverseCache.Clear();
        }

        ReverseCache[key] = (DateTime.UtcNow, value);
        return value;
    }

    private async Task<(LookupAddress?, LocationCandidate?)> ReverseAsync(double lat, double lon, CancellationToken ct)
    {
        var url = string.Create(CultureInfo.InvariantCulture,
            $"{Nominatim}?format=jsonv2&lat={lat:F6}&lon={lon:F6}&zoom=18&addressdetails=1&extratags=1&namedetails=1&accept-language=en");
        // At most 25 s, waiting for the gate included (the HTTP client allows 90 s for Overpass)
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(25));
        await NominatimGate.WaitAsync(limit.Token);
        try
        {
            // One request per second, as Nominatim's policy asks
            var wait = _lastNominatim.AddSeconds(1.1) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, limit.Token);
            }

            var json = await GetAsync(new HttpRequestMessage(HttpMethod.Get, url), limit.Token);
            return ParseNominatim(json, lat, lon);
        }
        finally
        {
            _lastNominatim = DateTime.UtcNow;
            NominatimGate.Release();
        }
    }

    /// <summary>The named places nearest to the point (Photon: fast, names and kinds only)</summary>
    private async Task<List<LocationCandidate>> PhotonAsync(double lat, double lon, double radiusM, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        var url = string.Create(CultureInfo.InvariantCulture,
            $"{Photon}?lat={lat:F6}&lon={lon:F6}&limit=25&radius={radiusM / 1000:F3}&lang=en");
        await PhotonGate.WaitAsync(limit.Token);
        try
        {
            return ParsePhoton(await GetAsync(new HttpRequestMessage(HttpMethod.Get, url), limit.Token), lat, lon, radiusM);
        }
        finally
        {
            PhotonGate.Release();
        }
    }

    /// <summary>Photon's answer: named places of a telling kind within the radius, nearest first</summary>
    internal static List<LocationCandidate> ParsePhoton(string json, double lat, double lon, double radiusM)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
            {
                return new List<LocationCandidate>();
            }

            var found = new List<LocationCandidate>();
            foreach (var f in features.EnumerateArray())
            {
                if (!f.TryGetProperty("properties", out var p) || p.ValueKind != JsonValueKind.Object
                    || !f.TryGetProperty("geometry", out var g) || !g.TryGetProperty("coordinates", out var xy)
                    || xy.ValueKind != JsonValueKind.Array || xy.GetArrayLength() < 2
                    || !xy[0].TryGetDouble(out var plon) || !xy[1].TryGetDouble(out var plat))
                {
                    continue;
                }

                var type = Str(p, "osm_type") switch { "N" => "node", "W" => "way", "R" => "relation", _ => null };
                var key = Str(p, "osm_key");
                var value = Str(p, "osm_value");
                var name = Str(p, "name");
                // Places of a telling kind (and bus stops); not streets, junctions or areas
                if (type == null || !p.TryGetProperty("osm_id", out var idEl) || !idEl.TryGetInt64(out var id)
                    || key == null || value == null || string.IsNullOrWhiteSpace(name)
                    || !(KindKeys.Contains(key) || (key == "highway" && value == "bus_stop")) || key is "place" or "landuse")
                {
                    continue;
                }

                var tags = new Dictionary<string, string> { [key] = value, ["name"] = name };
                var street = Str(p, "street");
                if (street != null)
                {
                    tags["addr:street"] = street;
                    if (Str(p, "housenumber") is { } number)
                    {
                        tags["addr:housenumber"] = number;
                    }

                    if (Str(p, "city") is { } city)
                    {
                        tags["addr:city"] = city;
                    }
                }

                if (Candidate(type, id, tags, plat, plon, lat, lon) is { } c && c.DistanceM <= radiusM * 1.5)
                {
                    found.Add(c);
                }
            }

            return found.OrderBy(c => c.DistanceM).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new GeoLookupException("The nearby-places lookup answered nonsense", ex);
        }
    }

    private async Task<List<LocationCandidate>> AroundAsync(double lat, double lon, double radiusM, CancellationToken ct)
    {
        var keys = string.Join("|", KindKeys);
        // A small declared timeout: busy instances admit small queries more readily
        var query = string.Create(CultureInfo.InvariantCulture,
            $"[out:json][timeout:25];nwr(around:{radiusM:F0},{lat:F6},{lon:F6})[name][~\"^({keys})$\"~\".\"];out center tags 80;");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan Left() => OverpassBudget - clock.Elapsed;

        // Waiting behind other lookups counts against the budget
        if (!await OverpassGate.WaitAsync(OverpassBudget - TimeSpan.FromSeconds(10), ct))
        {
            throw new GeoLookupException("Nearby-places lookups are busy");
        }

        try
        {
            // A busy instance answers 502/504/429 or keeps the request queued: wait for it,
            // else the next one; at most four attempts, and an instance that said it is
            // full is left alone for a minute (retrying would only make it worse)
            Exception? last = null;
            for (var attempt = 0; attempt < 2 && Left() > TimeSpan.FromSeconds(5); attempt++)
            {
                var url = Overpass.Select((u, i) => Overpass[(attempt + i) % Overpass.Length])
                    .FirstOrDefault(u => !BusyUntil.TryGetValue(u, out var until) || until < DateTime.UtcNow);
                if (url == null)
                {
                    break;
                }

                using var attemptCt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCt.CancelAfter(Left() < TimeSpan.FromSeconds(50) ? Left() : TimeSpan.FromSeconds(50));
                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["data"] = query })
                };
                try
                {
                    return ParseOverpass(await GetAsync(request, attemptCt.Token), lat, lon);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested
                                           && ex is HttpRequestException or GeoLookupException or OperationCanceledException)
                {
                    last = ex;
                    if (ex is GeoLookupException { Busy: true } busy)
                    {
                        BusyUntil[url] = DateTime.UtcNow + (busy.RetryAfter ?? TimeSpan.FromSeconds(60));
                    }

                    var pause = TimeSpan.FromSeconds(Math.Min(4, 1 << attempt));
                    if (pause < Left() - TimeSpan.FromSeconds(5))
                    {
                        await Task.Delay(pause, ct);
                    }
                }
            }

            throw new GeoLookupException("No Overpass instance answered in time", last);
        }
        finally
        {
            OverpassGate.Release();
        }
    }

    private async Task<string> GetAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.ParseAdd("application/json");
            using var res = await _http.SendAsync(request, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                var status = (int)res.StatusCode;
                var retryAfter = res.Headers.RetryAfter?.Delta;
                throw new GeoLookupException($"{request.RequestUri?.Host} answered {status}")
                {
                    Busy = status is 429 or 502 or 503 or 504,
                    RetryAfter = retryAfter is { } r && r > TimeSpan.Zero && r < TimeSpan.FromMinutes(10) ? r : null
                };
            }

            return text;
        }
    }

    /// <summary>Nominatim's reverse answer: the address, and the object at the point when it has a name</summary>
    internal static (LookupAddress?, LocationCandidate?) ParseNominatim(string json, double lat, double lon)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
            {
                return (null, null);
            }

            LookupAddress? address = null;
            if (root.TryGetProperty("address", out var a) && a.ValueKind == JsonValueKind.Object)
            {
                string? A(params string[] names) => names.Select(n => Str(a, n)).FirstOrDefault(v => v != null);
                var road = A("road", "pedestrian", "footway", "path", "square");
                var number = A("house_number");
                var city = A("city", "town", "village", "hamlet", "municipality");
                var line = string.Join(", ", new[]
                {
                    road != null ? (number != null ? $"{number} {road}" : road) : null,
                    A("suburb", "neighbourhood", "quarter"), city, A("country")
                }.Where(x => !string.IsNullOrWhiteSpace(x)));
                address = new LookupAddress(line.Length > 0 ? line : Str(root, "display_name") ?? "", number, road,
                    A("suburb", "neighbourhood", "quarter"), city, A("state"), A("postcode"), A("country"), A("country_code"));
            }

            LocationCandidate? at = null;
            var type = Str(root, "osm_type");
            var id = root.TryGetProperty("osm_id", out var idEl) && idEl.TryGetInt64(out var i) ? i : (long?)null;
            var tags = new Dictionary<string, string>();
            if (root.TryGetProperty("extratags", out var extra) && extra.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in extra.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
                {
                    tags[p.Name] = p.Value.GetString()!;
                }
            }

            if (root.TryGetProperty("namedetails", out var names) && names.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in names.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
                {
                    tags[p.Name] = p.Value.GetString()!;
                }
            }

            var category = Str(root, "category");
            var kindValue = Str(root, "type");
            if (type != null && id != null && category != null && kindValue != null && tags.ContainsKey("name")
                && category is not ("highway" or "boundary" or "place" or "landuse"))
            {
                tags[category] = kindValue;
                var plat = double.TryParse(Str(root, "lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var la) ? la : lat;
                var plon = double.TryParse(Str(root, "lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) ? lo : lon;
                at = Candidate(type, id.Value, tags, plat, plon, lat, lon);
            }

            return (address, at);
        }
        catch (JsonException ex)
        {
            throw new GeoLookupException("The address lookup answered nonsense", ex);
        }
    }

    /// <summary>Overpass's answer: named places with their tags, nearest first</summary>
    internal static List<LocationCandidate> ParseOverpass(string json, double lat, double lon)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            // A query that ran out of time answers 200 with a remark and no elements
            if (doc.RootElement.TryGetProperty("remark", out var remark) && remark.GetString() is { } note
                && note.Contains("runtime error", StringComparison.OrdinalIgnoreCase))
            {
                throw new GeoLookupException("Overpass could not finish the query");
            }

            if (!doc.RootElement.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
            {
                return new List<LocationCandidate>();
            }

            var found = new List<LocationCandidate>();
            foreach (var e in elements.EnumerateArray())
            {
                var type = Str(e, "type");
                if (type == null || !e.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)
                    || !e.TryGetProperty("tags", out var t) || t.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var point = e.TryGetProperty("center", out var center) ? center : e;
                if (!point.TryGetProperty("lat", out var la) || !point.TryGetProperty("lon", out var lo)
                    || !la.TryGetDouble(out var plat) || !lo.TryGetDouble(out var plon))
                {
                    continue;
                }

                var tags = new Dictionary<string, string>();
                foreach (var p in t.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
                {
                    tags.TryAdd(p.Name, p.Value.GetString()!);
                }

                if (Candidate(type, id, tags, plat, plon, lat, lon) is { } c)
                {
                    found.Add(c);
                }
            }

            return found.OrderBy(c => c.DistanceM).ToList();
        }
        catch (JsonException ex)
        {
            throw new GeoLookupException("The nearby-places lookup answered nonsense", ex);
        }
    }

    private static LocationCandidate? Candidate(string type, long id, Dictionary<string, string> tags, double lat, double lon,
        double fromLat, double fromLon)
    {
        var local = tags.GetValueOrDefault("name");
        var english = tags.GetValueOrDefault("name:en");
        var name = english ?? local;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // A bus stop is tagged public_transport=platform too: say what it is
        var key = tags.GetValueOrDefault("highway") == "bus_stop" ? "highway" : KindKeys.FirstOrDefault(k => tags.ContainsKey(k));
        var value = key != null ? tags[key] : null;
        var kind = key != null ? $"{key}={value}" : "";
        var label = key == null ? "Place" : value is "yes" or null ? Humanize(key) : Humanize(value);
        string? Tag(params string[] keys) => keys.Select(k => tags.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var street = Tag("addr:street");
        var number = Tag("addr:housenumber");
        var city = Tag("addr:city");
        var address = street != null
            ? string.Join(", ", new[] { number != null ? $"{number} {street}" : street, city }.Where(x => x != null))
            : Tag("addr:full");
        var details = DetailKeys.Where(tags.ContainsKey).ToDictionary(k => k, k => tags[k]);
        if (tags.TryGetValue("brand:en", out var brand))
        {
            details["brand"] = brand;
        }
        return new LocationCandidate($"osm:{type}/{id}", name, english != null && local != null && local != english ? local : null,
            kind, label, Math.Round(lat, 6), Math.Round(lon, 6), Math.Round(Geo.Haversine(fromLat, fromLon, lat, lon)), address,
            Tag("website", "contact:website", "url"), Tag("phone", "contact:phone", "contact:mobile"), Tag("opening_hours"), details);
    }

    /// <summary>"fast_food" → "Fast food"</summary>
    internal static string Humanize(string value)
    {
        var text = value.Replace('_', ' ').Replace(';', ',').Trim();
        return text.Length == 0 ? value : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
