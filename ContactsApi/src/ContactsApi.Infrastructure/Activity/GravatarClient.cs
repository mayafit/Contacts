/**
 * @fileoverview A public profile from Gravatar by an email's SHA-256 hash (AD-46 rule 3):
 * the call, its rate-limit headers, and the profile's fields kept
 * @module ContactsApi.Infrastructure.Activity
 */
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ContactsApi.Infrastructure.Activity;

/// <summary>A link of a profile</summary>
/// <param name="Label">What it is ("LinkedIn", "Blog")</param>
/// <param name="Url">The https URL</param>
public record ProfileLink(string Label, string Url);

/// <summary>The public profile a person keeps on Gravatar</summary>
public class GravatarProfile
{
    /// <summary>Their display name</summary>
    public string? DisplayName { get; set; }
    /// <summary>Job title</summary>
    public string? JobTitle { get; set; }
    /// <summary>Company</summary>
    public string? Company { get; set; }
    /// <summary>Location</summary>
    public string? Location { get; set; }
    /// <summary>Their "about me", at most 500 characters</summary>
    public string? Description { get; set; }
    /// <summary>The Gravatar profile page</summary>
    public string? ProfileUrl { get; set; }
    /// <summary>Accounts they verified (X, LinkedIn, GitHub…), at most 10</summary>
    public List<ProfileLink> VerifiedAccounts { get; set; } = new();
    /// <summary>Other links they list, at most 10</summary>
    public List<ProfileLink> Links { get; set; } = new();
}

/// <summary>How a look-up went</summary>
public enum GravatarOutcome
{
    /// <summary>A profile</summary>
    Found,
    /// <summary>No profile for the hash</summary>
    NotFound,
    /// <summary>The hourly limit is reached: wait until <see cref="GravatarAnswer.ResetAt"/></summary>
    Limited,
    /// <summary>Another status or no answer: try again later</summary>
    Failed
}

/// <summary>A look-up's answer</summary>
/// <param name="Outcome">How it went</param>
/// <param name="Profile">The profile when found</param>
/// <param name="Remaining">Requests left in the hour, when said</param>
/// <param name="ResetAt">When the hour's allowance resets, when said</param>
public record GravatarAnswer(GravatarOutcome Outcome, GravatarProfile? Profile, int? Remaining, DateTime? ResetAt);

/// <summary>Gravatar's public profiles API (v3)</summary>
public class GravatarClient
{
    /// <summary>Requests an hour without a key; with one, 1,000</summary>
    public const int HourlyLimitWithoutKey = 100;

    /// <summary>Requests an hour with a key</summary>
    public const int HourlyLimitWithKey = 1000;

    private const int MaxLinks = 10;
    private const int MaxDescription = 500;
    private static readonly Regex Hash = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly string? _apiKey;

    /// <summary>Creates the client</summary>
    /// <param name="http">HTTP client</param>
    /// <param name="apiKey">Gravatar API key, or null</param>
    public GravatarClient(HttpClient http, string? apiKey)
    {
        _http = http;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
    }

    /// <summary>An email's hash as Gravatar keys it: SHA-256 of the trimmed, lower-case address</summary>
    /// <param name="hash">Candidate</param>
    /// <returns>Whether it has that form</returns>
    public static bool IsHash(string? hash) => hash != null && Hash.IsMatch(hash);

    /// <summary>The profile of a hash</summary>
    /// <param name="hash">SHA-256 of an email address</param>
    /// <param name="ct">Cancellation</param>
    /// <returns>The answer</returns>
    public async Task<GravatarAnswer> LookUpAsync(string hash, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.gravatar.com/v3/profiles/{hash}");
        request.Headers.UserAgent.ParseAdd("Echo-Contacts/1.0");
        if (_apiKey != null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new GravatarAnswer(GravatarOutcome.Failed, null, null, null);
        }

        using (response)
        {
            var remaining = Header(response, "X-RateLimit-Remaining") is { } r && int.TryParse(r, out var n) ? n : (int?)null;
            var reset = Header(response, "X-RateLimit-Reset") is { } s && long.TryParse(s, out var t)
                ? DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime
                : (DateTime?)null;
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    try
                    {
                        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
                        return new GravatarAnswer(GravatarOutcome.Found, Parse(json), remaining, reset);
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        return new GravatarAnswer(GravatarOutcome.Failed, null, remaining, reset);
                    }
                case HttpStatusCode.NotFound:
                    return new GravatarAnswer(GravatarOutcome.NotFound, null, remaining, reset);
                case HttpStatusCode.TooManyRequests:
                    return new GravatarAnswer(GravatarOutcome.Limited, null, 0, reset);
                default:
                    return new GravatarAnswer(GravatarOutcome.Failed, null, remaining, reset);
            }
        }
    }

    /// <summary>
    /// The fields kept from a profile: names and texts trimmed, the description cut at 500
    /// characters, and only https links (at most 10 of each kind)
    /// </summary>
    /// <param name="json">The v3 profile</param>
    /// <returns>The profile</returns>
    public static GravatarProfile Parse(JsonNode? json)
    {
        string? Text(string key, int max = 200)
        {
            var v = (json?[key] as JsonValue)?.TryGetValue<string>(out var s) == true ? s.Trim() : null;
            return string.IsNullOrEmpty(v) ? null : v.Length > max ? v[..max] : v;
        }

        List<ProfileLink> Links(string key, string labelKey)
        {
            var list = new List<ProfileLink>();
            foreach (var item in json?[key] as JsonArray ?? new JsonArray())
            {
                var url = (item?["url"] as JsonValue)?.TryGetValue<string>(out var u) == true ? u.Trim() : null;
                var label = (item?[labelKey] as JsonValue)?.TryGetValue<string>(out var l) == true ? l.Trim() : null;
                if (url != null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                {
                    list.Add(new ProfileLink(string.IsNullOrEmpty(label) ? uri.Host : label, uri.AbsoluteUri));
                }

                if (list.Count == MaxLinks)
                {
                    break;
                }
            }

            return list;
        }

        var profileUrl = Text("profile_url", 300);
        return new GravatarProfile
        {
            DisplayName = Text("display_name"),
            JobTitle = Text("job_title"),
            Company = Text("company"),
            Location = Text("location"),
            Description = Text("description", MaxDescription),
            ProfileUrl = profileUrl != null && profileUrl.StartsWith("https://", StringComparison.Ordinal) ? profileUrl : null,
            VerifiedAccounts = Links("verified_accounts", "service_label"),
            Links = Links("links", "label")
        };
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
