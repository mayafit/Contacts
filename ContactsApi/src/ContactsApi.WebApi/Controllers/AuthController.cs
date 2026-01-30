using ContactsApi.WebApi.Configuration;
using ContactsApi.WebApi.Models.Responses;
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
    private readonly ILogger<AuthController> _logger;
    private readonly IConfiguration _configuration;
    private readonly GoogleOAuthOptions _oauthOptions;

    public AuthController(
        ILogger<AuthController> logger,
        IConfiguration configuration,
        IOptions<GoogleOAuthOptions> oauthOptions)
    {
        _logger = logger;
        _configuration = configuration;
        _oauthOptions = oauthOptions.Value;
    }

    /// <summary>
    /// Initiate Google OAuth flow
    /// </summary>
    /// <returns>Redirect to Google OAuth consent screen</returns>
    [HttpGet("login")]
    public async Task<IActionResult> Login()
    {
        try
        {
            _logger.LogInformation("Initiating Google OAuth flow");

            var clientId = _oauthOptions.ClientId;
            var redirectUri = _oauthOptions.RedirectUri;
            var scopes = _oauthOptions.Scopes;

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
            authUrl.Append($"&scope={Uri.EscapeDataString(string.Join(" ", scopes ?? Array.Empty<string>()))}");
            authUrl.Append($"&state={Uri.EscapeDataString(state)}");
            authUrl.Append($"&access_type=offline"); // Request refresh token
            authUrl.Append($"&prompt=consent"); // Force consent to get refresh token

            _logger.LogInformation("Redirecting to Google OAuth consent screen");
            return Redirect(authUrl.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initiating OAuth flow");
            throw;
        }
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
                return Redirect($"http://localhost:3002/login?error={Uri.EscapeDataString(error)}");
            }

            // Validate state parameter (CSRF protection)
            var expectedState = HttpContext.Session.GetString("OAuthState");
            if (string.IsNullOrEmpty(expectedState) || expectedState != state)
            {
                _logger.LogWarning("Invalid state parameter");
                return Redirect("http://localhost:3002/login?error=invalid_state");
            }

            // Clear state from session
            HttpContext.Session.Remove("OAuthState");

            if (string.IsNullOrEmpty(code))
            {
                _logger.LogWarning("No authorization code received");
                return Redirect("http://localhost:3002/login?error=no_code");
            }

            _logger.LogInformation("Exchanging authorization code for tokens");

            // Exchange authorization code for tokens
            var clientId = _oauthOptions.ClientId;
            var clientSecret = _oauthOptions.ClientSecret;
            var redirectUri = _oauthOptions.RedirectUri;

            if (string.IsNullOrEmpty(clientSecret))
            {
                _logger.LogError("Client secret not configured or not loaded from Vault");
                return Redirect("http://localhost:3002/login?error=config_error");
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

            // Store tokens in session (temporary - will be moved to Redis/DB)
            // TODO: Encrypt tokens before storing
            // TODO: Store in Redis cache + SQL Server
            HttpContext.Session.SetString("AccessToken", tokenResponse.AccessToken);
            if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
            {
                HttpContext.Session.SetString("RefreshToken", tokenResponse.RefreshToken);
            }

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

            // Redirect to frontend
            return Redirect("http://localhost:3002/");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OAuth callback");
            return Redirect($"http://localhost:3002/login?error={Uri.EscapeDataString("auth_failed")}");
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
            var response = new AuthStatusResponse
            {
                IsAuthenticated = true,
                User = new UserInfo
                {
                    Id = "temp-user-id",
                    Email = "user@example.com",
                    Name = "User",
                    Picture = null
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
        try
        {
            _logger.LogInformation("Refreshing access token");

            var refreshToken = HttpContext.Session.GetString("RefreshToken");
            if (string.IsNullOrEmpty(refreshToken))
            {
                _logger.LogWarning("No refresh token found");
                return Unauthorized(ApiResponse<object>.ErrorResponse(
                    "NO_REFRESH_TOKEN",
                    "No refresh token available. Please sign in again."));
            }

            var clientId = _oauthOptions.ClientId;
            var clientSecret = _oauthOptions.ClientSecret;

            if (string.IsNullOrEmpty(clientSecret))
            {
                _logger.LogError("Client secret not configured or not loaded from Vault");
                return StatusCode(500, ApiResponse<object>.ErrorResponse(
                    "CONFIG_ERROR",
                    "OAuth configuration error"));
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

            // Refresh the token
            var tokenResponse = await flow.RefreshTokenAsync(
                "user",
                refreshToken,
                CancellationToken.None);

            // Update session with new access token
            HttpContext.Session.SetString("AccessToken", tokenResponse.AccessToken);
            var expiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresInSeconds ?? 3600);
            HttpContext.Session.SetString("TokenExpiresAt", expiresAt.ToString("o"));

            _logger.LogInformation("Access token refreshed successfully");

            return Ok(ApiResponse<object>.SuccessResponse(new
            {
                success = true,
                expiresAt = expiresAt.ToString("o")
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing token");

            // If refresh fails, user needs to re-authenticate
            HttpContext.Session.Clear();

            return Unauthorized(ApiResponse<object>.ErrorResponse(
                "REFRESH_FAILED",
                "Token refresh failed. Please sign in again."));
        }
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
