/**
 * @fileoverview OneDrive (personal) through Microsoft Graph: sign-in with the
 * authorization code flow, the drive's changes (delta) as photo details, thumbnails
 * @module ContactsApi.Infrastructure.Atlas
 */
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ContactsApi.Infrastructure.Atlas;

/// <summary>A OneDrive call failed; Code says why (OneDrive codes in <see cref="OneDriveClient"/>)</summary>
public class OneDriveException : Exception
{
    /// <summary>ONEDRIVE_SIGNIN_INVALID (sign in again), ONEDRIVE_RESYNC (start the listing over), ONEDRIVE_FAILED</summary>
    public string Code { get; }

    /// <summary>Creates the exception</summary>
    public OneDriveException(string code, string message) : base(message) => Code = code;
}

/// <summary>Tokens of a sign-in (the refresh token is the one kept)</summary>
public record OneDriveTokens(string AccessToken, string? RefreshToken, DateTime ExpiresAt);

/// <summary>A photo or video of the drive, as the delta lists it</summary>
public record OneDrivePhoto(
    string Id, string Name, string? MimeType, bool IsVideo, long? Size, int? Width, int? Height,
    string? CameraMake, string? CameraModel, DateTime TakenAt, double? Lat, double? Lon, double? Altitude,
    string? WebUrl, string? Folder);

/// <summary>One page of changes: photos added or changed, ids removed, and where to go on</summary>
public record OneDriveDeltaPage(List<OneDrivePhoto> Photos, List<string> Removed, string? NextLink, string? DeltaLink, int Items);

/// <summary>
/// Microsoft Graph for a personal OneDrive. Only reads: the files' details and
/// thumbnails (Files.Read). Graph is asked again after a 429/503 as it says.
/// </summary>
public class OneDriveClient
{
    /// <summary>Name of the HttpClient</summary>
    public const string HttpClientName = "onedrive";

    /// <summary>Personal Microsoft accounts only</summary>
    public const string Authority = "https://login.microsoftonline.com/consumers/oauth2/v2.0";

    /// <summary>Reading files, staying signed in, and the account's name</summary>
    public const string Scopes = "offline_access Files.Read User.Read";

    private const string Graph = "https://graph.microsoft.com/v1.0";

    /// <summary>The fields a photo needs (delta returns all items of the drive)</summary>
    private const string Select = "id,name,file,photo,location,image,video,size,webUrl,deleted,parentReference,fileSystemInfo";

    private readonly HttpClient _http;

    /// <summary>Creates the client</summary>
    public OneDriveClient(HttpClient http) => _http = http;

    /// <summary>Where the browser signs in (Microsoft comes back to the redirect with a code)</summary>
    public static string AuthorizeUrl(string clientId, string redirectUri, string state) =>
        $"{Authority}/authorize?client_id={Uri.EscapeDataString(clientId)}&response_type=code" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&response_mode=query" +
        $"&scope={Uri.EscapeDataString(Scopes)}&state={Uri.EscapeDataString(state)}&prompt=select_account";

    /// <summary>Exchanges the sign-in's code for tokens</summary>
    public Task<OneDriveTokens> RedeemCodeAsync(string clientId, string secret, string redirectUri, string code, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["client_secret"] = secret, ["grant_type"] = "authorization_code",
            ["code"] = code, ["redirect_uri"] = redirectUri, ["scope"] = Scopes
        }, ct);

    /// <summary>A new access token (and usually a new refresh token, which replaces the old one)</summary>
    public Task<OneDriveTokens> RefreshAsync(string clientId, string secret, string refreshToken, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["client_secret"] = secret, ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken, ["scope"] = Scopes
        }, ct);

    private async Task<OneDriveTokens> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var res = await _http.PostAsync($"{Authority}/token", new FormUrlEncodedContent(form), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var root = doc.RootElement;
        if (!res.IsSuccessStatusCode)
        {
            var error = Str(root, "error");
            // invalid_grant: revoked, expired, or the password changed
            throw error is "invalid_grant" or "interaction_required" or "invalid_client"
                ? new OneDriveException("ONEDRIVE_SIGNIN_INVALID", "The OneDrive sign-in is no longer valid. Connect again.")
                : new OneDriveException("ONEDRIVE_FAILED", $"Microsoft sign-in failed ({(int)res.StatusCode} {error}).");
        }

        var access = Str(root, "access_token") ?? throw new OneDriveException("ONEDRIVE_FAILED", "Microsoft sign-in returned no token.");
        var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
        return new OneDriveTokens(access, Str(root, "refresh_token"), DateTime.UtcNow.AddSeconds(seconds - 120));
    }

    /// <summary>The signed-in account's email or name (shown in the app)</summary>
    public async Task<string?> AccountAsync(string accessToken, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(accessToken, $"{Graph}/me?$select=displayName,userPrincipalName,mail", ct);
        var root = doc.RootElement;
        return Str(root, "mail") ?? Str(root, "userPrincipalName") ?? Str(root, "displayName");
    }

    /// <summary>
    /// One page of the drive's changes: from the start (link null), the next page,
    /// or what changed since the last sync's delta link
    /// </summary>
    /// <exception cref="OneDriveException">ONEDRIVE_RESYNC when the link is too old (list from the start)</exception>
    public async Task<OneDriveDeltaPage> DeltaAsync(string accessToken, string? link, CancellationToken ct)
    {
        var url = link ?? $"{Graph}/me/drive/root/delta?$select={Select}";
        using var doc = await GetJsonAsync(accessToken, url, ct);
        return ParseDelta(doc.RootElement);
    }

    /// <summary>A page of a delta answer</summary>
    public static OneDriveDeltaPage ParseDelta(JsonElement root)
    {
        var photos = new List<OneDrivePhoto>();
        var removed = new List<string>();
        var items = 0;
        if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                items++;
                var id = Str(item, "id");
                if (id == null)
                {
                    continue;
                }

                if (item.TryGetProperty("deleted", out _))
                {
                    removed.Add(id);
                    continue;
                }

                var photo = ParsePhoto(item);
                if (photo != null)
                {
                    photos.Add(photo);
                }
                else if (item.TryGetProperty("file", out _))
                {
                    // A file that is no longer a photo (or never was): not ours to keep
                    removed.Add(id);
                }
            }
        }

        return new OneDriveDeltaPage(photos, removed, Str(root, "@odata.nextLink"), Str(root, "@odata.deltaLink"), items);
    }

    /// <summary>
    /// A drive item as a photo: a file with photo details (when it was taken), or a
    /// video (its time from the photo details, else when the file was created).
    /// Other files (documents, screenshots without camera data) are not photos.
    /// </summary>
    public static OneDrivePhoto? ParsePhoto(JsonElement item)
    {
        if (!item.TryGetProperty("file", out var file))
        {
            return null;
        }

        var mime = Str(file, "mimeType");
        var hasPhoto = item.TryGetProperty("photo", out var photo) && photo.ValueKind == JsonValueKind.Object;
        var isVideo = item.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object
                      || mime?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;
        DateTime? taken = hasPhoto ? Date(photo, "takenDateTime") : null;
        if (taken == null && isVideo && item.TryGetProperty("fileSystemInfo", out var fs))
        {
            taken = Date(fs, "createdDateTime");
        }

        if (taken == null || !(hasPhoto || isVideo))
        {
            return null;
        }

        double? lat = null, lon = null, alt = null;
        if (item.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object)
        {
            lat = Num(loc, "latitude");
            lon = Num(loc, "longitude");
            alt = Num(loc, "altitude");
            // 0,0 is a camera without a fix
            if (lat is null || lon is null || (Math.Abs(lat.Value) < 1e-6 && Math.Abs(lon.Value) < 1e-6)
                || Math.Abs(lat.Value) > 90 || Math.Abs(lon.Value) > 180)
            {
                lat = lon = alt = null;
            }
        }

        int? width = null, height = null;
        var size = item.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object ? image
            : isVideo && video.ValueKind == JsonValueKind.Object ? video : default;
        if (size.ValueKind == JsonValueKind.Object)
        {
            width = (int?)Num(size, "width");
            height = (int?)Num(size, "height");
        }

        string? folder = null;
        if (item.TryGetProperty("parentReference", out var parent) && Str(parent, "path") is { } path)
        {
            var i = path.IndexOf(':');
            folder = i >= 0 ? path[(i + 1)..] : path;
        }

        return new OneDrivePhoto(
            Str(item, "id")!, Str(item, "name") ?? "", mime, isVideo,
            item.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var bytes) ? bytes : null,
            width, height,
            hasPhoto ? Str(photo, "cameraMake") : null, hasPhoto ? Str(photo, "cameraModel") : null,
            taken.Value, lat, lon, alt, Str(item, "webUrl"), folder);
    }

    /// <summary>
    /// A thumbnail's bytes: small (96 px), medium (176), large (800), or a custom
    /// size like c400x400 (cropped) — null when the item has none
    /// </summary>
    public async Task<(byte[] Bytes, string ContentType)?> ThumbnailAsync(string accessToken, string itemId, string size, CancellationToken ct)
    {
        using var res = await SendAsync(accessToken, $"{Graph}/me/drive/items/{Uri.EscapeDataString(itemId)}/thumbnails/0/{size}/content", ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        Check(res);
        return (await res.Content.ReadAsByteArrayAsync(ct), res.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
    }

    private async Task<JsonDocument> GetJsonAsync(string accessToken, string url, CancellationToken ct)
    {
        using var res = await SendAsync(accessToken, url, ct);
        if (res.StatusCode == HttpStatusCode.Gone)
        {
            throw new OneDriveException("ONEDRIVE_RESYNC", "OneDrive asks to list the drive from the start.");
        }

        Check(res);
        return JsonDocument.Parse(await res.Content.ReadAsStreamAsync(ct));
    }

    /// <summary>A GET with the token; waits and retries when Graph is busy (429/503/504)</summary>
    private async Task<HttpResponseMessage> SendAsync(string accessToken, string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var busy = res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
            if (!busy || attempt >= 4)
            {
                return res;
            }

            var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * (attempt + 1));
            res.Dispose();
            await Task.Delay(wait > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait, ct);
        }
    }

    private static void Check(HttpResponseMessage res)
    {
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new OneDriveException("ONEDRIVE_TOKEN_EXPIRED", "The OneDrive access token expired.");
        }

        if (!res.IsSuccessStatusCode)
        {
            throw new OneDriveException("ONEDRIVE_FAILED", $"OneDrive answered {(int)res.StatusCode}.");
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static DateTime? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? d.UtcDateTime
            : null;
}
