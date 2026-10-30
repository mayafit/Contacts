/**
 * @fileoverview Takeout exports in Google Drive (AD-40): the Drive calls that list and
 * download them, and the reviews file read out of a Takeout zip (the browser
 * importer's rules, AD-39 rule 1). The parser is pure.
 * @module ContactsApi.Infrastructure.Atlas
 */
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ContactsApi.Infrastructure.Atlas;

/// <summary>A Takeout export in Drive</summary>
public record TakeoutFile(string Id, string Name, long? Size, DateTime Modified);

/// <summary>
/// A Drive call failed. Unauthorized: the token no longer allows Drive (sign in again).
/// Transient: try again later (Google busy or unreachable). Otherwise the file itself cannot be read.
/// </summary>
public class GoogleDriveException : Exception
{
    /// <summary>The Owner must sign in again and allow Drive</summary>
    public bool Unauthorized { get; }

    /// <summary>Worth trying again later (5xx, 429, a timeout)</summary>
    public bool Transient { get; }

    /// <summary>Creates the exception</summary>
    public GoogleDriveException(string message, bool unauthorized = false, bool transient = false) : base(message)
    {
        Unauthorized = unauthorized;
        Transient = transient;
    }
}

/// <summary>Google Drive API v3, read only: the Owner's own Takeout zips, and their content</summary>
public class GoogleDriveTakeout
{
    /// <summary>Name of the HttpClient</summary>
    public const string HttpClientName = "google-drive";

    private const string Files = "https://www.googleapis.com/drive/v3/files";

    /// <summary>
    /// The Owner's own files named like Takeout exports (takeout-&lt;time&gt;-&lt;n&gt;.zip), never
    /// files others shared with them; the .zip ending is checked on the names returned
    /// </summary>
    public const string Query = "'me' in owners and name contains 'takeout-' and trashed = false";

    /// <summary>A download that takes longer than this is given up (a stalled body)</summary>
    public static readonly TimeSpan DownloadLimit = TimeSpan.FromMinutes(20);

    /// <summary>The message of a file over the size limit</summary>
    public const string TooLarge = "The export is larger than allowed.";

    private readonly HttpClient _http;

    /// <summary>Creates the client</summary>
    public GoogleDriveTakeout(HttpClient http) => _http = http;

    /// <summary>The Owner's newest Takeout zips (at most 50)</summary>
    /// <exception cref="GoogleDriveException">The call failed</exception>
    public async Task<List<TakeoutFile>> ListAsync(string accessToken, CancellationToken ct)
    {
        var url = $"{Files}?q={Uri.EscapeDataString(Query)}&orderBy=modifiedTime%20desc&pageSize=50" +
                  "&fields=files(id%2Cname%2Csize%2CmodifiedTime)&spaces=drive";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        await EnsureAsync(res, true, ct);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        }
        catch (JsonException)
        {
            throw new GoogleDriveException("Google Drive gave an unreadable list.", transient: true);
        }

        using (doc)
        {
            var list = new List<TakeoutFile>();
            if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var f in files.EnumerateArray())
            {
                var id = f.TryGetProperty("id", out var i) ? i.GetString() : null;
                var name = f.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (id == null || name == null || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                long? size = f.TryGetProperty("size", out var s) && long.TryParse(s.GetString(), out var bytes) ? bytes : null;
                var modified = f.TryGetProperty("modifiedTime", out var m)
                               && DateTime.TryParse(m.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                    ? t
                    : DateTime.MinValue;
                list.Add(new TakeoutFile(id, name, size, DateTime.SpecifyKind(modified, DateTimeKind.Utc)));
            }

            return list;
        }
    }

    /// <summary>Streams a file's content to a local path, stopping past <paramref name="maxBytes"/> or <see cref="DownloadLimit"/></summary>
    /// <exception cref="GoogleDriveException">The call failed, or the file is larger than allowed</exception>
    public async Task DownloadAsync(string accessToken, string id, string path, long maxBytes, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(DownloadLimit);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Files}/{Uri.EscapeDataString(id)}?alt=media");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        try
        {
            using var res = await SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            await EnsureAsync(res, false, limit.Token);
            await using var source = await res.Content.ReadAsStreamAsync(limit.Token);
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, limit.Token)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new GoogleDriveException(TooLarge);
                }

                await target.WriteAsync(buffer.AsMemory(0, read), limit.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GoogleDriveException("The download took too long.", transient: true);
        }
        catch (HttpRequestException)
        {
            throw new GoogleDriveException("The download broke off.", transient: true);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, HttpCompletionOption option, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(req, option, ct);
        }
        catch (HttpRequestException)
        {
            throw new GoogleDriveException("Google Drive could not be reached.", transient: true);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GoogleDriveException("Google Drive did not answer in time.", transient: true);
        }
    }

    /// <summary>
    /// Throws for a failed answer. Lost access: 401, or a 403 whose reason is an insufficient
    /// scope (or, on the listing, insufficient permissions); a 403 on one file is that file's
    /// (not downloadable). Rate limits, 429 and 5xx are transient.
    /// </summary>
    private static async Task EnsureAsync(HttpResponseMessage res, bool listing, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode)
        {
            return;
        }

        var body = await res.Content.ReadAsStringAsync(ct);
        var code = (int)res.StatusCode;
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new GoogleDriveException("Google Drive did not accept the sign-in.", unauthorized: true);
        }

        if (res.StatusCode == HttpStatusCode.Forbidden)
        {
            var reasons = Reasons(body);
            if (reasons.Contains("ACCESS_TOKEN_SCOPE_INSUFFICIENT") || (listing && reasons.Contains("insufficientPermissions")))
            {
                throw new GoogleDriveException("Drive access is not allowed.", unauthorized: true);
            }

            if (reasons.Contains("rateLimitExceeded") || reasons.Contains("userRateLimitExceeded"))
            {
                throw new GoogleDriveException("Google Drive asked to slow down.", transient: true);
            }
        }

        throw new GoogleDriveException($"Google Drive answered {code}.", transient: code == 429 || code >= 500);
    }

    /// <summary>The reasons of a Google error: error.errors[].reason and error.details[].reason</summary>
    internal static HashSet<string> Reasons(string body)
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return reasons;
            }

            foreach (var list in new[] { "errors", "details" })
            {
                if (error.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("reason", out var r) && r.GetString() is { } reason)
                        {
                            reasons.Add(reason);
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: no reasons
        }

        return reasons;
    }
}

/// <summary>The Owner's reviews read out of a Takeout zip (AD-39 rule 1, AD-40 rule 4)</summary>
public static partial class GoogleReviewsParser
{
    /// <summary>A reviews file larger than this (uncompressed) is not read</summary>
    public const long MaxEntryBytes = 20L * 1024 * 1024;

    private static readonly string[] ReviewFields =
        { "five_star_rating_published", "five_star_rating_draft", "review_text_published", "review_text_draft" };

    [GeneratedRegex(@"(^|/)reviews\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex ReviewsName();

    [GeneratedRegex(@"(^|/)[^/]*(maps|מפות)[^/]*/[^/]+\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex InMapsFolder();

    /// <summary>A zip entry that may be the reviews file: Reviews.json anywhere, or a JSON file directly in a Maps folder (any language)</summary>
    public static bool Candidate(string path)
    {
        var p = path.Replace('\\', '/');
        return ReviewsName().IsMatch(p) || InMapsFolder().IsMatch(p);
    }

    /// <summary>
    /// The reviews in a Takeout zip: the first candidate entry that is a reviews
    /// FeatureCollection. Null when the zip holds none (a note says why).
    /// </summary>
    public static (List<ReviewInput>? Reviews, string? Note) FromZip(ZipArchive zip)
    {
        var candidates = zip.Entries.Where(e => Candidate(e.FullName)).OrderByDescending(e => ReviewsName().IsMatch(e.FullName.Replace('\\', '/'))).ToList();
        if (candidates.Count == 0)
        {
            return (null, "no Maps (your places) reviews in it");
        }

        string? note = null;
        foreach (var e in candidates)
        {
            if (e.Length > MaxEntryBytes)
            {
                note = $"{e.Name} is larger than {MaxEntryBytes / 1024 / 1024} MB";
                continue;
            }

            JsonDocument doc;
            try
            {
                // At most the limit is read, whatever the entry claims (a zip bomb lies about its size)
                using var stream = e.Open();
                using var bounded = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (bounded.Length + read > MaxEntryBytes)
                    {
                        throw new InvalidDataException("too large");
                    }

                    bounded.Write(buffer, 0, read);
                }

                bounded.Position = 0;
                doc = JsonDocument.Parse(bounded);
            }
            catch (InvalidDataException)
            {
                note = $"{e.Name} is larger than {MaxEntryBytes / 1024 / 1024} MB or damaged";
                continue;
            }
            catch (JsonException)
            {
                note = $"{e.Name} is not readable JSON";
                continue;
            }

            using (doc)
            {
                if (IsReviews(doc.RootElement))
                {
                    return (Parse(doc.RootElement), null);
                }
            }
        }

        return (null, note ?? "no Maps (your places) reviews in it");
    }

    /// <summary>A FeatureCollection whose features carry review fields (Saved Places has the same shape without them)</summary>
    public static bool IsReviews(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && Str(root, "type") == "FeatureCollection"
        && root.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array
        && features.EnumerateArray().Any(f => f.ValueKind == JsonValueKind.Object && f.TryGetProperty("properties", out var p)
                                              && p.ValueKind == JsonValueKind.Object
                                              && ReviewFields.Any(k => p.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null));

    /// <summary>
    /// The reviews of a Reviews.json: the published stars and text, or the draft's when
    /// nothing was published; skipped without stars or text, without a name, or at [0, 0]
    /// </summary>
    public static List<ReviewInput> Parse(JsonElement root)
    {
        var list = new List<ReviewInput>();
        if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var f in features.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object || !f.TryGetProperty("properties", out var p) || p.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!f.TryGetProperty("geometry", out var g) || g.ValueKind != JsonValueKind.Object || !g.TryGetProperty("coordinates", out var c)
                || c.ValueKind != JsonValueKind.Array || c.GetArrayLength() < 2
                || c[0].ValueKind != JsonValueKind.Number || c[1].ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var (lon, lat) = (c[0].GetDouble(), c[1].GetDouble());
            var location = p.TryGetProperty("location", out var l)
                ? l.ValueKind == JsonValueKind.Array && l.GetArrayLength() > 0 ? l[0] : l
                : default;
            var name = location.ValueKind == JsonValueKind.Object ? Str(location, "name")?.Trim() : null;
            if (string.IsNullOrEmpty(name) || (lat == 0 && lon == 0))
            {
                continue;
            }

            var published = Has(p, "five_star_rating_published") || !string.IsNullOrEmpty(Str(p, "review_text_published"));
            var rating = Int(p, published ? "five_star_rating_published" : "five_star_rating_draft");
            var text = Str(p, published ? "review_text_published" : "review_text_draft");
            var stars = rating is >= 1 and <= 5 ? rating : null;
            var words = string.IsNullOrWhiteSpace(text) ? null : text;
            if (stars == null && words == null)
            {
                continue;
            }

            DateTime? date = DateTime.TryParse(Str(p, "date"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
                ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
                : null;
            var answers = new List<ReviewAnswer>();
            if (p.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array)
            {
                foreach (var q in qs.EnumerateArray())
                {
                    var question = q.ValueKind == JsonValueKind.Object ? Str(q, "question")?.Trim() : null;
                    var answer = q.ValueKind == JsonValueKind.Object ? Answer(q) : null;
                    if (!string.IsNullOrEmpty(question) && !string.IsNullOrEmpty(answer))
                    {
                        answers.Add(new ReviewAnswer(question, answer));
                    }
                }
            }

            list.Add(new ReviewInput(null, name, Str(location, "address"), Str(location, "country_code"), lat, lon, stars, words, published, date,
                PlaceReviews.SafeUrl(Str(p, "google_maps_url")), answers));
        }

        return list;
    }

    /// <summary>One answer as text: the option, the options joined, the text answer, or n/5</summary>
    private static string? Answer(JsonElement q)
    {
        if (Str(q, "selected_option") is { } option && !string.IsNullOrWhiteSpace(option))
        {
            return option.Trim();
        }

        if (q.TryGetProperty("selected_options", out var options) && options.ValueKind == JsonValueKind.Array && options.GetArrayLength() > 0)
        {
            return string.Join(", ", options.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.String).Select(o => o.GetString()));
        }

        if (Str(q, "text_answer") is { } text && !string.IsNullOrWhiteSpace(text))
        {
            return text.Trim();
        }

        return Int(q, "rating") is { } r ? $"{r}/5" : null;
    }

    private static bool Has(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : null;
}
