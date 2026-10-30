/**
 * @fileoverview Public flight data (AD-42 rules 5, 6 and 8): The OpenSky Network for
 * flights and tracks, adsbdb for airlines, call signs and aircraft
 * @module ContactsApi.Infrastructure.Atlas.Flights
 */
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContactsApi.Infrastructure.Atlas.Flights;

/// <summary>A flight as OpenSky saw it</summary>
public record OpenSkyFlight(string Icao24, string Callsign, DateTime FirstSeen, DateTime LastSeen, string? Departure, string? Arrival);

/// <summary>A public source was busy or unreachable: try again later</summary>
public class FlightSourceException : Exception
{
    /// <summary>Creates the exception</summary>
    public FlightSourceException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>OpenSky's daily allowance is (nearly) used: stop asking until it is back</summary>
public class OpenSkyLimitException : Exception
{
    /// <summary>Creates the exception</summary>
    public OpenSkyLimitException(string message) : base(message) { }
}

/// <summary>Flights and tracks (OpenSky)</summary>
public interface IOpenSky
{
    /// <summary>Whether the operator set credentials</summary>
    bool Configured { get; }

    /// <summary>The flights that left an airport within a window (at most 2 days)</summary>
    Task<List<OpenSkyFlight>> DeparturesAsync(string airportIcao, DateTime begin, DateTime end, CancellationToken ct);

    /// <summary>The path an aircraft flew around a time ([unix, lat, lon, altitude m]); null when none</summary>
    Task<List<double[]>?> TrackAsync(string icao24, DateTime time, CancellationToken ct);
}

/// <summary>Airlines, call signs and aircraft (adsbdb)</summary>
public interface IAdsbdb
{
    /// <summary>The airlines with an IATA or ICAO code</summary>
    Task<List<FlightAirline>> AirlinesAsync(string code, CancellationToken ct);

    /// <summary>A call sign's IATA flight number and airline; null when unknown</summary>
    Task<(string? FlightNumber, FlightAirline? Airline)?> CallsignAsync(string callsign, CancellationToken ct);

    /// <summary>An aircraft by its 24-bit address; null when unknown</summary>
    Task<FlightAircraft?> AircraftAsync(string icao24, CancellationToken ct);
}

/// <summary>OpenSky credentials (Vault: OpenSky__ClientId, OpenSky__ClientSecret)</summary>
public class OpenSkyOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

/// <summary>The OpenSky Network's REST API with client credentials, keeping its allowance</summary>
public class OpenSkyClient : IOpenSky
{
    /// <summary>The named HTTP client</summary>
    public const string HttpClientName = "opensky";

    /// <summary>Credits kept in reserve: below this, no more queries until the allowance returns</summary>
    public const int Reserve = 300;

    private const string TokenUrl = "https://auth.opensky-network.org/auth/realms/opensky-network/protocol/openid-connect/token";
    private const string Api = "https://opensky-network.org/api";
    private readonly Func<HttpClient> _http;
    private readonly OpenSkyOptions _options;
    private readonly ILogger<OpenSkyClient> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTime _tokenUntil;
    private DateTime _pausedUntil;

    /// <summary>Creates the client</summary>
    /// <param name="http">Gives the named HTTP client</param>
    /// <param name="options">Credentials</param>
    /// <param name="logger">Logger</param>
    public OpenSkyClient(Func<HttpClient> http, IOptions<OpenSkyOptions> options, ILogger<OpenSkyClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool Configured => !string.IsNullOrWhiteSpace(_options.ClientId) && !string.IsNullOrWhiteSpace(_options.ClientSecret);

    /// <inheritdoc />
    public async Task<List<OpenSkyFlight>> DeparturesAsync(string airportIcao, DateTime begin, DateTime end, CancellationToken ct)
    {
        using var doc = await GetAsync($"{Api}/flights/departure?airport={Uri.EscapeDataString(airportIcao)}&begin={Unix(begin)}&end={Unix(end)}", ct);
        var list = new List<OpenSkyFlight>();
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var f in doc.RootElement.EnumerateArray())
        {
            var icao24 = Str(f, "icao24");
            if (icao24 == null || !f.TryGetProperty("firstSeen", out var fs) || !f.TryGetProperty("lastSeen", out var ls)
                || fs.ValueKind != JsonValueKind.Number || ls.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            list.Add(new OpenSkyFlight(icao24, (Str(f, "callsign") ?? string.Empty).Trim(),
                FromUnix(fs.GetInt64()), FromUnix(ls.GetInt64()), Str(f, "estDepartureAirport"), Str(f, "estArrivalAirport")));
        }

        return list;
    }

    /// <inheritdoc />
    public async Task<List<double[]>?> TrackAsync(string icao24, DateTime time, CancellationToken ct)
    {
        using var doc = await GetAsync($"{Api}/tracks/all?icao24={Uri.EscapeDataString(icao24)}&time={Unix(time)}", ct);
        if (doc == null || !doc.RootElement.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var points = new List<double[]>();
        foreach (var p in path.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 4
                || p[0].ValueKind != JsonValueKind.Number || p[1].ValueKind != JsonValueKind.Number || p[2].ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            points.Add(new[] { p[0].GetDouble(), p[1].GetDouble(), p[2].GetDouble(), p[3].ValueKind == JsonValueKind.Number ? p[3].GetDouble() : 0 });
        }

        return points.Count >= 2 ? points : null;
    }

    /// <summary>A GET with the token; null on 404 (no flights); the allowance kept</summary>
    private async Task<JsonDocument?> GetAsync(string url, CancellationToken ct)
    {
        if (!Configured)
        {
            throw new InvalidOperationException("OpenSky is not configured");
        }

        if (DateTime.UtcNow < _pausedUntil)
        {
            throw new OpenSkyLimitException($"OpenSky's allowance is used until {_pausedUntil:u}");
        }

        var client = _http();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await TokenAsync(client, ct));
        HttpResponseMessage res;
        try
        {
            res = await client.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new FlightSourceException("OpenSky could not be reached", ex);
        }

        using (res)
        {
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = Header(res, "X-Rate-Limit-Retry-After-Seconds") ?? 3600;
                _pausedUntil = DateTime.UtcNow.AddSeconds(Math.Clamp(wait, 60, 86400));
                throw new OpenSkyLimitException("OpenSky's allowance is used");
            }

            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                _token = null;
                throw new FlightSourceException("OpenSky refused the token");
            }

            if (res.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!res.IsSuccessStatusCode)
            {
                throw new FlightSourceException($"OpenSky answered {(int)res.StatusCode}");
            }

            if (Header(res, "X-Rate-Limit-Remaining") is { } left && left < Reserve)
            {
                _logger.LogInformation("OpenSky: {Left} credits left, pausing for an hour", left);
                _pausedUntil = DateTime.UtcNow.AddHours(1);
            }

            var body = await res.Content.ReadAsStringAsync(ct);
            try
            {
                return JsonDocument.Parse(body.Length == 0 ? "null" : body);
            }
            catch (JsonException ex)
            {
                throw new FlightSourceException("OpenSky's answer is not JSON", ex);
            }
        }
    }

    private async Task<string> TokenAsync(HttpClient client, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token != null && DateTime.UtcNow < _tokenUntil)
            {
                return _token;
            }

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.ClientId!,
                ["client_secret"] = _options.ClientSecret!
            });
            HttpResponseMessage res;
            try
            {
                res = await client.PostAsync(TokenUrl, form, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                throw new FlightSourceException("OpenSky's sign-in could not be reached", ex);
            }

            using (res)
            {
                if (!res.IsSuccessStatusCode)
                {
                    throw new FlightSourceException($"OpenSky's sign-in answered {(int)res.StatusCode}");
                }

                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                _token = Str(doc.RootElement, "access_token") ?? throw new FlightSourceException("OpenSky gave no token");
                var seconds = doc.RootElement.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 1800;
                _tokenUntil = DateTime.UtcNow.AddSeconds(seconds - 60);
                return _token;
            }
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static int? Header(HttpResponseMessage res, string name) =>
        res.Headers.TryGetValues(name, out var v) && int.TryParse(v.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    internal static long Unix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    internal static DateTime FromUnix(long s) => DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>adsbdb's open API: airlines (cached for a day), call signs, aircraft</summary>
public class AdsbdbClient : IAdsbdb
{
    /// <summary>The named HTTP client</summary>
    public const string HttpClientName = "adsbdb";

    private const string Api = "https://api.adsbdb.com/v0";
    private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(250);
    private readonly Func<HttpClient> _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTime at, List<FlightAirline> airlines)> _airlines = new(StringComparer.Ordinal);
    private DateTime _last;

    /// <summary>Creates the client</summary>
    /// <param name="http">Gives the named HTTP client</param>
    public AdsbdbClient(Func<HttpClient> http) => _http = http;

    /// <inheritdoc />
    public async Task<List<FlightAirline>> AirlinesAsync(string code, CancellationToken ct)
    {
        code = code.Trim().ToUpperInvariant();
        lock (_airlines)
        {
            if (_airlines.TryGetValue(code, out var hit) && DateTime.UtcNow - hit.at < TimeSpan.FromDays(1))
            {
                return hit.airlines;
            }
        }

        using var doc = await GetAsync($"{Api}/airline/{Uri.EscapeDataString(code)}", ct);
        var list = new List<FlightAirline>();
        if (doc != null && doc.RootElement.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.Array)
        {
            list.AddRange(r.EnumerateArray().Select(Airline).OfType<FlightAirline>());
        }

        lock (_airlines)
        {
            _airlines[code] = (DateTime.UtcNow, list);
        }

        return list;
    }

    /// <inheritdoc />
    public async Task<(string? FlightNumber, FlightAirline? Airline)?> CallsignAsync(string callsign, CancellationToken ct)
    {
        using var doc = await GetAsync($"{Api}/callsign/{Uri.EscapeDataString(callsign.Trim().ToUpperInvariant())}", ct);
        if (doc == null || !doc.RootElement.TryGetProperty("response", out var r) || !r.TryGetProperty("flightroute", out var route))
        {
            return null;
        }

        return (OpenSkyClient.Str(route, "callsign_iata"), route.TryGetProperty("airline", out var a) ? Airline(a) : null);
    }

    /// <inheritdoc />
    public async Task<FlightAircraft?> AircraftAsync(string icao24, CancellationToken ct)
    {
        using var doc = await GetAsync($"{Api}/aircraft/{Uri.EscapeDataString(icao24.Trim().ToUpperInvariant())}", ct);
        if (doc == null || !doc.RootElement.TryGetProperty("response", out var r) || !r.TryGetProperty("aircraft", out var a))
        {
            return null;
        }

        return new FlightAircraft(OpenSkyClient.Str(a, "type"), OpenSkyClient.Str(a, "icao_type"), OpenSkyClient.Str(a, "manufacturer"),
            OpenSkyClient.Str(a, "registration"), OpenSkyClient.Str(a, "registered_owner"));
    }

    private static FlightAirline? Airline(JsonElement a)
    {
        var name = OpenSkyClient.Str(a, "name");
        return name == null ? null : new FlightAirline(name, OpenSkyClient.Str(a, "iata"), OpenSkyClient.Str(a, "icao"));
    }

    /// <summary>A GET spaced 250 ms from the last; null when unknown (404)</summary>
    private async Task<JsonDocument?> GetAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _last + Spacing - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct);
            }

            _last = DateTime.UtcNow;
            HttpResponseMessage res;
            try
            {
                res = await _http().GetAsync(url, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                throw new FlightSourceException("adsbdb could not be reached", ex);
            }

            using (res)
            {
                if (res.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (!res.IsSuccessStatusCode)
                {
                    throw new FlightSourceException($"adsbdb answered {(int)res.StatusCode}");
                }

                try
                {
                    return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                }
                catch (JsonException ex)
                {
                    throw new FlightSourceException("adsbdb's answer is not JSON", ex);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
