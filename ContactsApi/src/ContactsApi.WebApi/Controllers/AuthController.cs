using ContactsApi.WebApi.Configuration;
using ContactsApi.WebApi.Models.Responses;
using ContactsApi.WebApi.Services;
using Google.Apis.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace ContactsApi.WebApi.Controllers;

/// <summary>
/// Authentication controller for Google OAuth flow
/// Handles login, callback, logout, and session management
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    /// <summary>Read/write Google Contacts scope; required for the app to work</summary>
    private const string ContactsScope = "https://www.googleapis.com/auth/contacts";

    /// <summary>Session key: where to go after the callback (set by an opt-in flow)</summary>
    private const string AfterLoginKey = "AfterLogin";

    private readonly ILogger<AuthController> _logger;
    private readonly IConfiguration _configuration;
    private readonly GoogleOAuthOptions _oauthOptions;
    private readonly GoogleTokenService _tokens;
    private readonly ScopePreferences _prefs;

    /// <summary>Session: opt-ins this sign-in asks for (JSON)</summary>
    private const string RequestedKey = "RequestedOptIns";

    /// <summary>Session: the scopes the next sign-in step must ask for</summary>
    private const string DesiredScopesKey = "DesiredScopes";

    public AuthController(
        ILogger<AuthController> logger,
        IConfiguration configuration,
        IOptions<GoogleOAuthOptions> oauthOptions,
        GoogleTokenService tokens,
        ScopePreferences prefs)
    {
        _prefs = prefs;
        _logger = logger;
        _configuration = configuration;
        _oauthOptions = oauthOptions.Value;
        _tokens = tokens;
    }

    /// <summary>
    /// Initiate Google OAuth flow
    /// </summary>
    /// <returns>Redirect to Google OAuth consent screen</returns>
    [HttpGet("login")]
    /// <param name="consent">Show Google's consent screen (to get a new refresh token)</param>
    /// <param name="activity">Also ask for Gmail metadata and Calendar access (opt-in)</param>
    /// <param name="again">Second step: ask for the scopes the callback found missing</param>
    /// <param name="hint">Account to sign in (second step)</param>
    public async Task<IActionResult> Login(
        [FromQuery] bool consent = false, [FromQuery] bool activity = false, [FromQuery] bool signatures = false,
        [FromQuery] bool again = false, [FromQuery] string? hint = null)
    {
        try
        {
            _logger.LogInformation("Initiating Google OAuth flow");

            var clientId = _oauthOptions.ClientId;
            var redirectUri = _oauthOptions.RedirectUri;
            // Exactly the scopes this user opted into (not include_granted_scopes:
            // Gmail refuses content reads when gmail.metadata is also on the token)
            var baseScopes = _oauthOptions.Scopes ?? Array.Empty<string>();
            var userId = HttpContext.Session.GetString(GoogleTokenService.UserIdKey);
            var optIns = userId == null ? new OptIns() : await _prefs.GetAsync(userId);
            var desired = HttpContext.Session.GetString(DesiredScopesKey);
            if (!again && desired == null)
            {
                HttpContext.Session.Remove(AfterLoginKey);
            }

            if (activity)
            {
                optIns.Activity = true;
                consent = true;
                HttpContext.Session.SetString(AfterLoginKey, "/?connected=activity");
            }

            if (signatures)
            {
                optIns.Signatures = true;
                consent = true;
                HttpContext.Session.SetString(AfterLoginKey, "/?connected=signatures");
            }

            HttpContext.Session.SetString(RequestedKey, System.Text.Json.JsonSerializer.Serialize(optIns));
            var scopes = desired != null && !activity && !signatures
                ? desired.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList()
                : ScopePreferences.ScopesFor(baseScopes, optIns);
            HttpContext.Session.Remove(DesiredScopesKey);
            // A second step only follows a plain sign-in that did not know the account
            // (never a consent screen: unticked boxes must not bring it back)
            HttpContext.Session.SetString("AgainStep", again || consent || desired != null ? "1" : "0");

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(redirectUri))
            {
                _logger.LogError("OAuth configuration missing");
                return BadRequest(ApiResponse<object>.ErrorResponse(
                    "OAUTH_CONFIG_ERROR",
                    "OAuth configuration is not properly set up"));
            }

            // Generate state parameter for CSRF protection
            var state = GenerateStateToken();
            HttpContext.Session.SetString("OAuthState", state);

            // CRITICAL: Explicitly commit session to Redis before redirecting
            // This ensures the state is persisted and can be validated on callback
            await HttpContext.Session.CommitAsync();

            // Build Google OAuth URL
            var authUrl = new StringBuilder("https://accounts.google.com/o/oauth2/v2/auth");
            authUrl.Append($"?client_id={Uri.EscapeDataString(clientId)}");
            authUrl.Append($"&redirect_uri={Uri.EscapeDataString(redirectUri)}");
            authUrl.Append($"&response_type=code");
            authUrl.Append($"&scope={Uri.EscapeDataString(string.Join(" ", scopes))}");
            authUrl.Append($"&state={Uri.EscapeDataString(state)}");
            authUrl.Append($"&access_type=offline"); // Request refresh token
            if (!string.IsNullOrEmpty(hint))
            {
                authUrl.Append($"&login_hint={Uri.EscapeDataString(hint)}");
            }
            // The consent screen is shown only when we need a new refresh token
            // (first sign-in, or the remembered one stopped working). Otherwise
            // Google signs the user straight in.
            if (consent)
            {
                authUrl.Append($"&prompt=consent");
            }

            _logger.LogInformation("Redirecting to Google OAuth consent screen");
            return Redirect(authUrl.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initiating OAuth flow");
            throw;
        }
    }

    /// <summary>The opt-ins the current sign-in asked for</summary>
    private OptIns RequestedOptIns()
    {
        var json = HttpContext.Session.GetString(RequestedKey);
        return json == null ? new OptIns() : System.Text.Json.JsonSerializer.Deserialize<OptIns>(json) ?? new OptIns();
    }

    /// <summary>
    /// Handle OAuth callback from Google
    /// </summary>
    /// <param name="code">Authorization code</param>
    /// <param name="state">State parameter for CSRF validation</param>
    /// <param name="error">Error from OAuth provider</param>
    /// <returns>Redirect to frontend with session established</returns>
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error)
    {
        try
        {
            // Handle OAuth errors
            if (!string.IsNullOrEmpty(error))
            {
                _logger.LogWarning("OAuth error: {Error}", error);
                // Declining an opt-in (Gmail & Calendar, or email content) leaves the
                // existing sign-in as it was
                var optIn = HttpContext.Session.GetString(AfterLoginKey);
                if (optIn is "/?connected=activity" or "/?connected=signatures"
                    && HttpContext.Session.GetString(GoogleTokenService.UserIdKey) != null)
                {
                    HttpContext.Session.Remove(AfterLoginKey);
                    return RedirectToFrontend($"{optIn}&declined=1");
                }

                return RedirectToFrontend($"/login?error={Uri.EscapeDataString(error)}");
            }

            // Validate state parameter (CSRF protection)
            var expectedState = HttpContext.Session.GetString("OAuthState");
            if (string.IsNullOrEmpty(expectedState) || expectedState != state)
            {
                _logger.LogWarning("Invalid state parameter");
                return RedirectToFrontend("/login?error=invalid_state");
            }

            // Clear state from session
            HttpContext.Session.Remove("OAuthState");

            if (string.IsNullOrEmpty(code))
            {
                _logger.LogWarning("No authorization code received");
                return RedirectToFrontend("/login?error=no_code");
            }

            _logger.LogInformation("Exchanging authorization code for tokens");

            // Exchange authorization code for tokens
            var clientId = _oauthOptions.ClientId;
            var clientSecret = _oauthOptions.ClientSecret;
            var redirectUri = _oauthOptions.RedirectUri;

            if (string.IsNullOrEmpty(clientSecret))
            {
                _logger.LogError("Client secret not configured or not loaded from Vault");
                return RedirectToFrontend("/login?error=config_error");
            }

            // Create OAuth flow
            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = clientId,
                    ClientSecret = clientSecret
                }
            });

            // Exchange code for tokens
            var tokenResponse = await flow.ExchangeCodeForTokenAsync(
                "user", // user ID - will be replaced with actual user ID after getting user info
                code,
                redirectUri,
                CancellationToken.None);

            // Google's consent screen lets the user untick individual permissions.
            // Without contacts access every later call fails with 403, so stop here
            // with an explanation instead of logging in to an app that cannot work.
            var grantedScopes = (tokenResponse.Scope ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!grantedScopes.Contains(ContactsScope))
            {
                _logger.LogWarning(
                    "Login completed without the contacts scope (granted: {Scopes})",
                    tokenResponse.Scope);
                return RedirectToFrontend("/login?error=missing_scope");
            }

            // Store tokens in session (temporary - will be moved to Redis/DB)
            // TODO: Encrypt tokens before storing
            // TODO: Store in Redis cache + SQL Server
            // Who signed in, from the verified ID token
            var payload = await GoogleJsonWebSignature.ValidateAsync(
                tokenResponse.IdToken,
                new GoogleJsonWebSignature.ValidationSettings { Audience = new[] { clientId } });

            // A refresh token is only issued on the consent screen. Without one,
            // use the one remembered from an earlier sign-in; if that is missing
            // or no longer accepted, go through consent once to get a new one.
            var refreshToken = tokenResponse.RefreshToken;
            if (!string.IsNullOrEmpty(refreshToken))
            {
                await _tokens.StoreRefreshTokenAsync(payload.Subject, refreshToken);
            }
            else
            {
                refreshToken = await _tokens.GetStoredRefreshTokenAsync(payload.Subject);
                if (refreshToken == null || await _tokens.RefreshAsync(refreshToken) == null)
                {
                    await _tokens.ForgetRefreshTokenAsync(payload.Subject);
                    _logger.LogInformation("No usable refresh token; asking for consent once");
                    // The new refresh token must cover the user's opt-ins, not just the base scopes
                    var stored = await _prefs.GetAsync(payload.Subject);
                    HttpContext.Session.SetString(DesiredScopesKey, string.Join(" ",
                        ScopePreferences.ScopesFor(_oauthOptions.Scopes ?? Array.Empty<string>(), stored.With(RequestedOptIns()))));
                    await HttpContext.Session.CommitAsync();
                    return Redirect($"/api/auth/login?consent=true&hint={Uri.EscapeDataString(payload.Email ?? string.Empty)}");
                }
            }

            // A first step that did not know the account asked for the base scopes only:
            // ask once more, silently, for what this account opted into
            var savedOptIns = await _prefs.FindAsync(payload.Subject);
            if (savedOptIns == null)
            {
                // Accounts from before opt-ins were saved: take them from what the
                // remembered refresh token was granted
                var granted = await _tokens.StoredGrantScopesAsync(payload.Subject);
                savedOptIns = granted == null ? new OptIns() : ScopePreferences.FromScopes(granted);
                if (granted != null)
                {
                    await _prefs.SaveAsync(payload.Subject, savedOptIns);
                }
            }

            var wanted = ScopePreferences.ScopesFor(_oauthOptions.Scopes ?? Array.Empty<string>(), savedOptIns.With(RequestedOptIns()));
            if (HttpContext.Session.GetString("AgainStep") != "1" && wanted.Except(grantedScopes).Any())
            {
                HttpContext.Session.SetString(DesiredScopesKey, string.Join(" ", wanted));
                await HttpContext.Session.CommitAsync();
                return Redirect($"/api/auth/login?again=true&hint={Uri.EscapeDataString(payload.Email ?? string.Empty)}");
            }

            // What the user ended up granting is their opt-in (they can untick boxes)
            var requested = RequestedOptIns();
            if (requested.Activity || requested.Signatures || savedOptIns.Activity || savedOptIns.Signatures)
            {
                await _prefs.SaveAsync(payload.Subject, new OptIns
                {
                    Signatures = grantedScopes.Contains(ActivityScopes.GmailRead),
                    Activity = grantedScopes.Contains(ActivityScopes.Gmail) || grantedScopes.Contains(ActivityScopes.Calendar)
                        || (grantedScopes.Contains(ActivityScopes.GmailRead) && (requested.Activity || savedOptIns.Activity))
                });
            }

            HttpContext.Session.SetString(GoogleTokenService.AccessTokenKey, tokenResponse.AccessToken);
            HttpContext.Session.SetString(GoogleTokenService.UserIdKey, payload.Subject);
            HttpContext.Session.SetString("UserEmail", payload.Email ?? string.Empty);
            HttpContext.Session.SetString("UserName", payload.Name ?? payload.Email ?? string.Empty);
            HttpContext.Session.SetString("UserPicture", payload.Picture ?? string.Empty);

            // The scopes of this sign-in (the user's opt-ins)
            HttpContext.Session.SetString(ActivityScopes.SessionKey, tokenResponse.Scope ?? string.Empty);

            // Store token expiry
            var expiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresInSeconds ?? 3600);
            HttpContext.Session.SetString("TokenExpiresAt", expiresAt.ToString("o"));

            // TODO: Fetch user info from Google and store in session/database
            // For now, just set a flag indicating authentication
            HttpContext.Session.SetString("IsAuthenticated", "true");

            // CRITICAL: Explicitly commit session to Redis before redirecting
            // This ensures the session cookie is set and data is persisted before the redirect
            await HttpContext.Session.CommitAsync();

            _logger.LogInformation("OAuth flow completed successfully");

            // Redirect to frontend (back to the opt-in's page when there was one)
            var afterLogin = HttpContext.Session.GetString(AfterLoginKey);
            HttpContext.Session.Remove(AfterLoginKey);
            await HttpContext.Session.CommitAsync();
            return RedirectToFrontend(afterLogin is "/?connected=activity" or "/?connected=signatures" ? afterLogin : "/");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OAuth callback");
            return RedirectToFrontend("/login?error=auth_failed");
        }
    }

    /// <summary>
    /// Check authentication status
    /// </summary>
    /// <returns>Authentication status and user info</returns>
    [HttpGet("status")]
    [ProducesResponseType(typeof(ApiResponse<AuthStatusResponse>), StatusCodes.Status200OK)]
    public IActionResult GetAuthStatus()
    {
        try
        {
            var isAuthenticated = HttpContext.Session.GetString("IsAuthenticated") == "true";
            var accessToken = HttpContext.Session.GetString("AccessToken");

            // DIAGNOSTIC: Log session state
            _logger.LogInformation(
                "Auth status check: IsAuthenticated={IsAuthenticated}, HasAccessToken={HasAccessToken}, SessionId={SessionId}, SessionAvailable={SessionAvailable}",
                isAuthenticated,
                !string.IsNullOrEmpty(accessToken),
                HttpContext.Session.Id,
                HttpContext.Session.IsAvailable);

            if (!isAuthenticated || string.IsNullOrEmpty(accessToken))
            {
                _logger.LogWarning("User not authenticated or missing access token");
                return Ok(ApiResponse<AuthStatusResponse>.SuccessResponse(new AuthStatusResponse
                {
                    IsAuthenticated = false,
                    User = null
                }));
            }

            // TODO: Fetch actual user info from database or Google
            // For now, return placeholder
            var picture = HttpContext.Session.GetString("UserPicture");
            var response = new AuthStatusResponse
            {
                IsAuthenticated = true,
                User = new UserInfo
                {
                    Id = HttpContext.Session.GetString(GoogleTokenService.UserIdKey) ?? string.Empty,
                    Email = HttpContext.Session.GetString("UserEmail") ?? string.Empty,
                    Name = HttpContext.Session.GetString("UserName") ?? string.Empty,
                    Picture = string.IsNullOrEmpty(picture) ? null : picture
                }
            };

            return Ok(ApiResponse<AuthStatusResponse>.SuccessResponse(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking auth status");
            throw;
        }
    }

    /// <summary>
    /// Logout and clear session
    /// </summary>
    /// <returns>Success response</returns>
    [HttpPost("logout")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public IActionResult Logout()
    {
        try
        {
            _logger.LogInformation("Logging out user");

            // TODO: Remove tokens from Redis and database

            // Clear session
            HttpContext.Session.Clear();

            _logger.LogInformation("User logged out successfully");

            return Ok(ApiResponse<object>.SuccessResponse(new { success = true }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during logout");
            throw;
        }
    }

    /// <summary>
    /// Refresh access token using refresh token
    /// </summary>
    /// <returns>Success response with new expiry time</returns>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RefreshToken()
    {
        // Same path every contacts call takes: refreshes only when needed, and
        // ends the session when Google no longer accepts the refresh token.
        var accessToken = await _tokens.GetAccessTokenAsync(HttpContext);
        if (string.IsNullOrEmpty(accessToken))
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse(
                "SESSION_EXPIRED",
                "Your Google session expired. Please sign in again."));
        }

        return Ok(ApiResponse<object>.SuccessResponse(new
        {
            success = true,
            expiresAt = HttpContext.Session.GetString(GoogleTokenService.ExpiresAtKey)
        }));
    }

    /// <summary>
    /// Redirect the browser to a path in the frontend app.
    /// The base URL comes from Frontend:BaseUrl so the same build works locally
    /// (http://localhost:3002) and behind the cluster's public hostname.
    /// </summary>
    /// <param name="pathAndQuery">Path starting with "/", optionally with a query string</param>
    /// <returns>Redirect result to the frontend</returns>
    private RedirectResult RedirectToFrontend(string pathAndQuery)
    {
        var baseUrl = (_configuration["Frontend:BaseUrl"] ?? "http://localhost:3002").TrimEnd('/');
        return Redirect(baseUrl + pathAndQuery);
    }

    /// <summary>
    /// Generate a cryptographically secure state token for CSRF protection
    /// </summary>
    private static string GenerateStateToken()
    {
        var randomBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(randomBytes);
        }
        return Convert.ToBase64String(randomBytes);
    }
}
