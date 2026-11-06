/**
 * @fileoverview Keeps the user's Google access token valid without asking them to sign in again
 * @module ContactsApi.WebApi.Services
 */

using ContactsApi.WebApi.Configuration;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace ContactsApi.WebApi.Services;

/// <summary>
/// Hands out a valid Google access token for the current session, refreshing it
/// when it is about to expire, and remembers the user's refresh token across
/// sessions so a later sign-in does not need Google's consent screen again.
/// </summary>
public class GoogleTokenService
{
    /// <summary>Refresh this long before the token expires</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    private readonly GoogleOAuthOptions _options;
    private readonly IDistributedCache _cache;
    private readonly IDataProtector _protector;
    private readonly IDataProtector _accessProtector;
    private readonly IGoogleSignIn _google;
    private readonly RedisTicketStore _tickets;
    private readonly ILogger<GoogleTokenService> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string AccessToken, DateTime ExpiresAt)> _userTokens = new();

    public GoogleTokenService(
        IOptions<GoogleOAuthOptions> options,
        IDistributedCache cache,
        IDataProtectionProvider dataProtection,
        IGoogleSignIn google,
        RedisTicketStore tickets,
        ILogger<GoogleTokenService> logger)
    {
        _options = options.Value;
        _cache = cache;
        _protector = dataProtection.CreateProtector(RefreshTokenPurpose);
        _accessProtector = dataProtection.CreateProtector(EchoSessionDefaults.AccessTokenPurpose);
        _google = google;
        _tickets = tickets;
        _logger = logger;
    }

    /// <summary>Data Protection purpose of the remembered refresh tokens</summary>
    public const string RefreshTokenPurpose = "ContactsApi.GoogleRefreshToken.v1";

    /// <summary>
    /// Puts the Google access token into a session ticket's properties, protected with the
    /// purpose <c>ContactsApi.Session.GoogleAccessToken.v1</c> (AD-50 rule 3): the ticket in
    /// Redis never holds it in clear.
    /// </summary>
    public void SetAccessToken(AuthenticationProperties properties, string accessToken, DateTime expiresAt)
    {
        properties.Items[EchoSessionDefaults.AccessTokenItem] = _accessProtector.Protect(accessToken);
        properties.Items[EchoSessionDefaults.AccessTokenExpiresItem] = expiresAt.ToString("o");
    }

    /// <summary>The access token in a ticket's properties, or null (none, or no longer readable)</summary>
    public string? ReadAccessToken(AuthenticationProperties properties)
    {
        if (!properties.Items.TryGetValue(EchoSessionDefaults.AccessTokenItem, out var stored) || string.IsNullOrEmpty(stored))
        {
            return null;
        }

        try
        {
            return _accessProtector.Unprotect(stored);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>When the session's access token expires (UTC), or null</summary>
    public static DateTime? AccessTokenExpiry(AuthenticationProperties properties) =>
        DateTime.TryParse((properties.Items.TryGetValue(EchoSessionDefaults.AccessTokenExpiresItem, out var at) ? at : null), null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var expiresAt)
            ? expiresAt
            : null;

    /// <summary>
    /// A Google access token valid for at least two more minutes, or null when the
    /// user must sign in again (no session, or the refresh token was revoked/expired).
    /// </summary>
    /// <param name="http">Current request</param>
    /// <returns>Access token or null</returns>
    public async Task<string?> GetAccessTokenAsync(HttpContext http)
    {
        var userId = http.OwnerId();
        if (userId == null)
        {
            return null;
        }

        // Cached for the request: the session middleware already authenticated it
        var auth = await http.AuthenticateAsync(EchoSessionDefaults.Scheme);
        if (!auth.Succeeded || auth.Properties == null || auth.Principal == null)
        {
            return null;
        }

        var properties = auth.Properties;
        var accessToken = ReadAccessToken(properties);
        if (accessToken != null && AccessTokenExpiry(properties) is { } expiresAt && expiresAt - DateTime.UtcNow > RefreshMargin)
        {
            return accessToken;
        }

        // Only the encrypted copy exists; the ticket holds just the account id
        var refreshToken = await GetStoredRefreshTokenAsync(userId);

        if (string.IsNullOrEmpty(refreshToken))
        {
            _logger.LogInformation("Access token expired and no refresh token is available");
            return null;
        }

        var refreshed = await RefreshAsync(refreshToken);
        if (refreshed == null)
        {
            // Google refused (revoked, or 7 days old while the app is in Testing):
            // end the session so the status check stops reporting "signed in".
            await http.SignOutAsync(EchoSessionDefaults.Scheme);
            http.Session.Clear();
            http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
            return null;
        }

        SetAccessToken(properties, refreshed.Value.AccessToken, refreshed.Value.ExpiresAt);
        if (properties.Items.TryGetValue(EchoSessionDefaults.TicketKeyItem, out var key) && key != null)
        {
            await _tickets.RenewAsync(key, new AuthenticationTicket(auth.Principal, properties, EchoSessionDefaults.Scheme));
        }

        _logger.LogInformation("Google access token refreshed");
        return refreshed.Value.AccessToken;
    }

    /// <summary>
    /// An access token for a user outside any request (background work), from the
    /// remembered refresh token. Kept in memory until 10 minutes before it expires.
    /// </summary>
    /// <param name="userId">Google account id ("sub")</param>
    /// <returns>Access token, or null when the user must sign in again</returns>
    public async Task<string?> GetAccessTokenForUserAsync(string userId)
    {
        if (_userTokens.TryGetValue(userId, out var cached) && cached.ExpiresAt - DateTime.UtcNow > TimeSpan.FromMinutes(10))
        {
            return cached.AccessToken;
        }

        var refreshToken = await GetStoredRefreshTokenAsync(userId);
        var refreshed = refreshToken == null ? null : await RefreshAsync(refreshToken);
        if (refreshed == null)
        {
            return null;
        }

        _userTokens[userId] = refreshed.Value;
        return refreshed.Value.AccessToken;
    }

    private static readonly HttpClient TokenInfo = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// The scopes a user's remembered refresh token carries (via Google's tokeninfo).
    /// </summary>
    /// <param name="userId">Google account id</param>
    /// <returns>Scopes, or null when unknown</returns>
    public async Task<string[]?> StoredGrantScopesAsync(string userId)
    {
        var token = await GetAccessTokenForUserAsync(userId);
        if (token == null)
        {
            return null;
        }

        try
        {
            var json = await TokenInfo.GetStringAsync(
                $"https://oauth2.googleapis.com/tokeninfo?access_token={Uri.EscapeDataString(token)}");
            var scope = System.Text.Json.Nodes.JsonNode.Parse(json)?["scope"]?.GetValue<string>();
            return scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "tokeninfo failed");
            return null;
        }
    }

    /// <summary>
    /// Exchanges a refresh token for a new access token.
    /// </summary>
    /// <param name="refreshToken">Refresh token</param>
    /// <returns>New token and its expiry, or null when Google refuses</returns>
    public Task<(string AccessToken, DateTime ExpiresAt)?> RefreshAsync(string refreshToken) =>
        _google.RefreshAsync(refreshToken);

    /// <summary>
    /// An access token for a user's stored sign-in, telling a refused sign-in (none stored,
    /// or Google answers invalid_grant: sign in again) from Google not being reachable now
    /// (try later). Used by background pickups that must not give up on a network blip.
    /// </summary>
    /// <param name="userId">Google account id ("sub")</param>
    /// <returns>The token, or null and whether the sign-in was refused</returns>
    public async Task<(string? AccessToken, bool Refused)> GetAccessTokenOrWhyAsync(string userId)
    {
        if (_userTokens.TryGetValue(userId, out var cached) && cached.ExpiresAt - DateTime.UtcNow > TimeSpan.FromMinutes(10))
        {
            return (cached.AccessToken, false);
        }

        var refreshToken = await GetStoredRefreshTokenAsync(userId);
        if (refreshToken == null)
        {
            return (null, true);
        }

        try
        {
            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets { ClientId = _options.ClientId, ClientSecret = _options.ClientSecret }
            });
            var response = await flow.RefreshTokenAsync("user", refreshToken, CancellationToken.None);
            var token = (response.AccessToken, DateTime.UtcNow.AddSeconds(response.ExpiresInSeconds ?? 3600));
            _userTokens[userId] = token;
            return (token.AccessToken, false);
        }
        catch (Google.Apis.Auth.OAuth2.Responses.TokenResponseException ex)
            when (ex.Error?.Error is "invalid_grant" or "unauthorized_client" or "invalid_client")
        {
            _logger.LogWarning("Google refused the refresh token ({Error})", ex.Error?.Error);
            return (null, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google could not be reached to renew a sign-in");
            return (null, false);
        }
    }

    /// <summary>Remembers a user's refresh token beyond the session, encrypted</summary>
    /// <param name="userId">Google account id ("sub")</param>
    /// <param name="refreshToken">Refresh token</param>
    public Task StoreRefreshTokenAsync(string userId, string refreshToken)
    {
        // A new grant (e.g. Gmail added) makes a cached token's scopes outdated
        _userTokens.TryRemove(userId, out _);
        return _cache.SetStringAsync(CacheKey(userId), _protector.Protect(refreshToken),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(180) });
    }

    /// <summary>The refresh token remembered for a user, or null</summary>
    /// <param name="userId">Google account id ("sub")</param>
    /// <returns>Refresh token or null</returns>
    public async Task<string?> GetStoredRefreshTokenAsync(string userId)
    {
        var stored = await _cache.GetStringAsync(CacheKey(userId));
        if (stored == null)
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(stored);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // Kept, not deleted: a key ring that is only briefly unreadable (a certificate
            // roll-over, AD-50 rule 2) must not cost the user their grant. A new sign-in
            // overwrites it.
            _logger.LogWarning(ex, "Stored refresh token could not be decrypted");
            return null;
        }
    }

    /// <summary>Forgets a user's remembered refresh token</summary>
    /// <param name="userId">Google account id ("sub")</param>
    public Task ForgetRefreshTokenAsync(string userId)
    {
        _userTokens.TryRemove(userId, out _);
        return _cache.RemoveAsync(CacheKey(userId));
    }

    private static string CacheKey(string userId) => $"refresh-token:{userId}";
}
