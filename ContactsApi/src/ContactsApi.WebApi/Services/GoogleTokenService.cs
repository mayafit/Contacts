/**
 * @fileoverview Keeps the user's Google access token valid without asking them to sign in again
 * @module ContactsApi.WebApi.Services
 */

using ContactsApi.WebApi.Configuration;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
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
    /// <summary>Session keys</summary>
    public const string AccessTokenKey = "AccessToken";
    public const string ExpiresAtKey = "TokenExpiresAt";
    public const string UserIdKey = "UserId";

    /// <summary>Refresh this long before the token expires</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    private readonly GoogleOAuthOptions _options;
    private readonly IDistributedCache _cache;
    private readonly IDataProtector _protector;
    private readonly ILogger<GoogleTokenService> _logger;

    public GoogleTokenService(
        IOptions<GoogleOAuthOptions> options,
        IDistributedCache cache,
        IDataProtectionProvider dataProtection,
        ILogger<GoogleTokenService> logger)
    {
        _options = options.Value;
        _cache = cache;
        _protector = dataProtection.CreateProtector("ContactsApi.GoogleRefreshToken.v1");
        _logger = logger;
    }

    /// <summary>
    /// A Google access token valid for at least two more minutes, or null when the
    /// user must sign in again (no session, or the refresh token was revoked/expired).
    /// </summary>
    /// <param name="http">Current request</param>
    /// <returns>Access token or null</returns>
    public async Task<string?> GetAccessTokenAsync(HttpContext http)
    {
        var session = http.Session;
        var accessToken = session.GetString(AccessTokenKey);
        if (string.IsNullOrEmpty(accessToken))
        {
            return null;
        }

        if (DateTime.TryParse(session.GetString(ExpiresAtKey), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var expiresAt)
            && expiresAt - DateTime.UtcNow > RefreshMargin)
        {
            return accessToken;
        }

        // Only the encrypted copy exists; the session holds just the account id
        var userId = session.GetString(UserIdKey);
        var refreshToken = userId == null ? null : await GetStoredRefreshTokenAsync(userId);

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
            session.Clear();
            await session.CommitAsync();
            return null;
        }

        session.SetString(AccessTokenKey, refreshed.Value.AccessToken);
        session.SetString(ExpiresAtKey, refreshed.Value.ExpiresAt.ToString("o"));
        await session.CommitAsync();
        _logger.LogInformation("Google access token refreshed");
        return refreshed.Value.AccessToken;
    }

    /// <summary>
    /// Exchanges a refresh token for a new access token.
    /// </summary>
    /// <param name="refreshToken">Refresh token</param>
    /// <returns>New token and its expiry, or null when Google refuses</returns>
    public async Task<(string AccessToken, DateTime ExpiresAt)?> RefreshAsync(string refreshToken)
    {
        try
        {
            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets { ClientId = _options.ClientId, ClientSecret = _options.ClientSecret }
            });
            var response = await flow.RefreshTokenAsync("user", refreshToken, CancellationToken.None);
            return (response.AccessToken, DateTime.UtcNow.AddSeconds(response.ExpiresInSeconds ?? 3600));
        }
        catch (Exception ex)
        {
            // Revoked, expired (7 days for apps in Testing mode) or invalid
            _logger.LogWarning(ex, "Google refused the refresh token");
            return null;
        }
    }

    /// <summary>Remembers a user's refresh token beyond the session, encrypted</summary>
    /// <param name="userId">Google account id ("sub")</param>
    /// <param name="refreshToken">Refresh token</param>
    public Task StoreRefreshTokenAsync(string userId, string refreshToken) =>
        _cache.SetStringAsync(CacheKey(userId), _protector.Protect(refreshToken),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(180) });

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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stored refresh token could not be decrypted; discarding it");
            await ForgetRefreshTokenAsync(userId);
            return null;
        }
    }

    /// <summary>Forgets a user's remembered refresh token</summary>
    /// <param name="userId">Google account id ("sub")</param>
    public Task ForgetRefreshTokenAsync(string userId) => _cache.RemoveAsync(CacheKey(userId));

    private static string CacheKey(string userId) => $"refresh-token:{userId}";
}
