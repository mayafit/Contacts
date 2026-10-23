/**
 * @fileoverview Sports Tracker: its web API (sign-in, workout list, GPX export)
 * and a GPX reader that turns a workout into an Atlas activity
 * @module ContactsApi.Infrastructure.Atlas
 */
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;

namespace ContactsApi.Infrastructure.Atlas;

/// <summary>Why talking to Sports Tracker failed (the code goes to the app)</summary>
public class SportsTrackerException : Exception
{
    /// <summary>SPORTS_TRACKER_LOGIN_REFUSED, SPORTS_TRACKER_KEY_INVALID, SPORTS_TRACKER_KEY_EXPIRED, SPORTS_TRACKER_UNAVAILABLE</summary>
    public string Code { get; }

    /// <summary>Creates the exception</summary>
    public SportsTrackerException(string code, string message) : base(message) => Code = code;
}

/// <summary>A workout as listed by Sports Tracker</summary>
public record SportsTrackerWorkout(
    string Key, int ActivityId, string? Name, string? Description, DateTime Start, DateTime? Stop,
    double? TotalTimeS, double? DistanceM, double? AscentM, int? AvgHeartRate, int? MaxHeartRate);

/// <summary>
/// Sports Tracker's web API (the one its own web app uses; not an official,
/// documented API, so it may change). Uses a session key; a password is only
/// passed through to sign in and is never kept.
/// </summary>
public class SportsTrackerClient
{
    /// <summary>Named HttpClient</summary>
    public const string HttpClientName = "sports-tracker";

    private const string Api = "https://api.sports-tracker.com/apiserver/v1";
    private readonly HttpClient _http;

    /// <summary>Creates the client</summary>
    public SportsTrackerClient(HttpClient http) => _http = http;

    /// <summary>Signs in; returns the session key</summary>
    /// <exception cref="SportsTrackerException">SPORTS_TRACKER_LOGIN_REFUSED when Sports Tracker does not accept it</exception>
    public async Task<string> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        using var body = new FormUrlEncodedContent(new Dictionary<string, string> { ["l"] = username, ["p"] = password });
        using var res = await SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{Api}/login?source=javascript") { Content = body }, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        string? key = null;
        if (res.IsSuccessStatusCode)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                key = doc.RootElement.TryGetProperty("sessionkey", out var k) ? k.GetString() : null;
            }
            catch (JsonException)
            {
                // not JSON: refused below
            }
        }

        return !string.IsNullOrEmpty(key)
            ? key
            : throw new SportsTrackerException("SPORTS_TRACKER_LOGIN_REFUSED",
                res.StatusCode is HttpStatusCode.Unauthorized
                    ? "Sports Tracker did not accept this user name and password."
                    : "Sports Tracker refused a sign-in from the server. Connect with a session key instead.");
    }

    /// <summary>All of the user's workouts (newest first)</summary>
    /// <exception cref="SportsTrackerException">SPORTS_TRACKER_KEY_INVALID when the key is not (or no longer) valid</exception>
    public async Task<List<SportsTrackerWorkout>> ListWorkoutsAsync(string sessionKey, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{Api}/workouts?limited=true&limit=1000000");
        req.Headers.TryAddWithoutValidation("STTAuthorization", sessionKey);
        using var res = await SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new SportsTrackerException("SPORTS_TRACKER_KEY_INVALID", "Sports Tracker did not accept the session key (it may have expired).");
        }

        if (!res.IsSuccessStatusCode)
        {
            throw new SportsTrackerException("SPORTS_TRACKER_UNAVAILABLE", $"Sports Tracker answered {(int)res.StatusCode} to the workout list.");
        }

        return ParseWorkouts(text);
    }

    /// <summary>One workout's track as GPX</summary>
    public async Task<string> ExportGpxAsync(string sessionKey, string workoutKey, CancellationToken ct = default)
    {
        // The key goes in a header; only when that is refused, in the URL (as
        // Sports Tracker's own export links do)
        var url = $"{Api}/workout/exportGpx/{Uri.EscapeDataString(workoutKey)}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("STTAuthorization", sessionKey);
        var res = await SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            res.Dispose();
            res = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{url}?token={Uri.EscapeDataString(sessionKey)}"), ct);
        }

        using (res)
        {
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new SportsTrackerException("SPORTS_TRACKER_KEY_INVALID", "Sports Tracker did not accept the session key (it may have expired).");
            }

            if (!res.IsSuccessStatusCode)
            {
                throw new SportsTrackerException("SPORTS_TRACKER_UNAVAILABLE", $"Sports Tracker answered {(int)res.StatusCode} to a track download.");
            }

            return await res.Content.ReadAsStringAsync(ct);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Personal Atlas)");
        try
        {
            return await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SportsTrackerException("SPORTS_TRACKER_UNAVAILABLE", $"Sports Tracker could not be reached: {ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SportsTrackerException("SPORTS_TRACKER_UNAVAILABLE", "Sports Tracker did not answer in time.");
        }
    }

    /// <summary>The workout list response: { error, payload: [ ... ] }</summary>
    public static List<SportsTrackerWorkout> ParseWorkouts(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Array)
        {
            return new List<SportsTrackerWorkout>();
        }

        var list = new List<SportsTrackerWorkout>();
        foreach (var w in payload.EnumerateArray())
        {
            var key = Str(w, "workoutKey") ?? Str(w, "key");
            var start = Num(w, "startTime");
            if (key == null || start == null)
            {
                continue;
            }

            var stop = Num(w, "stopTime");
            var hr = w.TryGetProperty("hrdata", out var h) && h.ValueKind == JsonValueKind.Object ? h : default;
            list.Add(new SportsTrackerWorkout(
                key,
                (int)(Num(w, "activityId") ?? 4),
                Str(w, "workoutName"),
                Str(w, "description"),
                DateTimeOffset.FromUnixTimeMilliseconds((long)start.Value).UtcDateTime,
                stop.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds((long)stop.Value).UtcDateTime : null,
                Num(w, "totalTime"),
                Num(w, "totalDistance"),
                Num(w, "totalAscent"),
                ToInt(hr.ValueKind == JsonValueKind.Object ? Num(hr, "avg") ?? Num(hr, "workoutAvgHR") : Num(w, "avgHr")),
                ToInt(hr.ValueKind == JsonValueKind.Object ? Num(hr, "hrmax") ?? Num(hr, "max") ?? Num(hr, "workoutMaxHR") : Num(w, "maxHr"))));
        }

        return list;
    }

    /// <summary>Sports Tracker activity ids → Atlas types (unknown ones are "other")</summary>
    public static string TypeOf(int activityId) => activityId switch
    {
        0 or 24 => "walk",
        1 or 22 => "run",
        2 or 10 => "ride",
        3 or 13 or 30 or 31 => "ski",
        11 => "hike",
        21 => "swim",
        _ => "other"
    };

    /// <summary>The workout as an Atlas activity, with the track from its GPX</summary>
    public static ActivityInput ToActivity(SportsTrackerWorkout w, string? gpx)
    {
        var points = gpx != null ? ReadGpx(gpx) : new List<PointInput>();
        var end = w.Stop ?? (w.TotalTimeS.HasValue ? w.Start.AddSeconds(w.TotalTimeS.Value) : (DateTime?)null);
        return new ActivityInput(w.Key, TypeOf(w.ActivityId), string.IsNullOrWhiteSpace(w.Name) ? null : w.Name.Trim(),
            w.Start, end, w.DistanceM, w.TotalTimeS, w.AscentM, string.IsNullOrWhiteSpace(w.Description) ? null : w.Description.Trim(),
            points.Count > 0 ? points : null, w.AvgHeartRate, w.MaxHeartRate);
    }

    /// <summary>Track points of a GPX file (heart rate and cadence from any extension)</summary>
    public static List<PointInput> ReadGpx(string gpx)
    {
        var doc = XDocument.Parse(gpx);
        var points = new List<PointInput>();
        foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "trkpt"))
        {
            if (!double.TryParse((string?)p.Attribute("lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                || !double.TryParse((string?)p.Attribute("lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            {
                continue;
            }

            var time = p.Elements().FirstOrDefault(e => e.Name.LocalName == "time")?.Value;
            if (time == null || !DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
            {
                continue;
            }

            double? Child(string name) =>
                double.TryParse(p.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value,
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

            points.Add(new PointInput(t, lat, lon, Child("ele"), ToInt(Child("hr") ?? Child("heartrate")), null, ToInt(Child("cad") ?? Child("cadence"))));
        }

        return points;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int? ToInt(double? v) => v is > 0 and < 1000 ? (int)Math.Round(v.Value) : null;
}
