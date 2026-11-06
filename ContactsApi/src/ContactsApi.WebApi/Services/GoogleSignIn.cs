/**
 * @fileoverview The calls the sign-in makes to Google: the code exchange, the ID-token
 * check, refreshing an access token and revoking a grant (behind an interface so the
 * callback can be tested without Google)
 * @module ContactsApi.WebApi.Services
 */
using ContactsApi.WebApi.Configuration;
using Google.Apis.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.Options;

namespace ContactsApi.WebApi.Services;

/// <summary>Google's OAuth endpoints as the sign-in uses them</summary>
public interface IGoogleSignIn
{
    /// <summary>Exchanges an authorization code for tokens</summary>
    Task<TokenResponse> ExchangeCodeAsync(string code, CancellationToken ct);

    /// <summary>Validates the ID token (signature, audience, expiry) and returns its payload</summary>
    Task<GoogleJsonWebSignature.Payload> ValidateIdTokenAsync(string idToken);

    /// <summary>Exchanges a refresh token for a new access token; null when Google refuses</summary>
    Task<(string AccessToken, DateTime ExpiresAt)?> RefreshAsync(string refreshToken);

    /// <summary>Revokes a grant (a refresh token revokes the whole grant); never throws</summary>
    Task RevokeAsync(string token, CancellationToken ct);
}

/// <summary>The real Google endpoints</summary>
public class GoogleSignIn : IGoogleSignIn
{
    /// <summary>Named HttpClient for the revoke call</summary>
    public const string HttpClientName = "google-oauth";

    private readonly GoogleOAuthOptions _options;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<GoogleSignIn> _logger;

    /// <summary>Creates the client</summary>
    public GoogleSignIn(IOptions<GoogleOAuthOptions> options, IHttpClientFactory http, ILogger<GoogleSignIn> logger)
    {
        _options = options.Value;
        _http = http;
        _logger = logger;
    }

    private GoogleAuthorizationCodeFlow Flow() => new(new GoogleAuthorizationCodeFlow.Initializer
    {
        ClientSecrets = new ClientSecrets { ClientId = _options.ClientId, ClientSecret = _options.ClientSecret }
    });

    /// <inheritdoc />
    public Task<TokenResponse> ExchangeCodeAsync(string code, CancellationToken ct) =>
        Flow().ExchangeCodeForTokenAsync("user", code, _options.RedirectUri, ct);

    /// <inheritdoc />
    public Task<GoogleJsonWebSignature.Payload> ValidateIdTokenAsync(string idToken) =>
        GoogleJsonWebSignature.ValidateAsync(idToken,
            new GoogleJsonWebSignature.ValidationSettings { Audience = new[] { _options.ClientId } });

    /// <inheritdoc />
    public async Task<(string AccessToken, DateTime ExpiresAt)?> RefreshAsync(string refreshToken)
    {
        try
        {
            var response = await Flow().RefreshTokenAsync("user", refreshToken, CancellationToken.None);
            return (response.AccessToken, DateTime.UtcNow.AddSeconds(response.ExpiresInSeconds ?? 3600));
        }
        catch (Exception ex)
        {
            // Revoked, expired (7 days for apps in Testing mode) or invalid
            _logger.LogWarning(ex, "Google refused the refresh token");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
            using var response = await _http.CreateClient(HttpClientName)
                .PostAsync("https://oauth2.googleapis.com/revoke", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Google did not revoke the grant (HTTP {Status})", (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Revoking the grant failed");
        }
    }
}
