/**
 * @fileoverview Other contacts for the suggestions of FR-92, and the dismissed ones (AD-48)
 * @module ContactsApi.WebApi.Controllers
 */
using ContactsApi.Infrastructure.Activity;
using ContactsApi.Infrastructure.Google;
using ContactsApi.WebApi.Models.Responses;
using ContactsApi.WebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace ContactsApi.WebApi.Controllers;

/// <summary>Suggestion keys to dismiss</summary>
public class DismissRequest
{
    /// <summary>Keys (<c>u:…</c> or <c>n:…</c>)</summary>
    public List<string>? Keys { get; set; }
}

/// <summary>
/// Reads Other contacts live (after the opt-in <c>/api/auth/login?other=true</c>);
/// only the dismissals are kept.
/// </summary>
[ApiController]
[Route("api/other-contacts")]
public class OtherContactsController : ControllerBase
{
    private readonly OtherContactsStore _store;
    private readonly GoogleTokenService _tokens;
    private readonly ILogger<OtherContactsController> _logger;

    /// <summary>Creates the controller</summary>
    public OtherContactsController(OtherContactsStore store, GoogleTokenService tokens, ILogger<OtherContactsController> logger)
    {
        _store = store;
        _tokens = tokens;
        _logger = logger;
    }

    /// <summary>Every Other contact with a person's address (AD-48 rules 2, 3)</summary>
    /// <returns>Entries, how many were read and how many were automated</returns>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var userId = await SignedInUserAsync();
        if (userId == null)
        {
            return SessionExpired();
        }

        var granted = (HttpContext.Session.GetString(ActivityScopes.SessionKey) ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!granted.Contains(OtherContactsScopes.Read))
        {
            return NotConnected("Allow reading Other contacts first.");
        }

        var reader = new OtherContactsReader(async _ =>
            await _tokens.GetAccessTokenAsync(HttpContext) ?? throw new ActivityScanException("NOT_CONNECTED", "Session ended"));
        try
        {
            var read = await reader.ReadAsync(HttpContext.RequestAborted);
            return Ok(ApiResponse<OtherContactsRead>.SuccessResponse(read));
        }
        catch (ActivityScanException ex) when (ex.Code == "NOT_CONNECTED")
        {
            return NotConnected(ex.Message);
        }
        catch (ActivityScanException ex)
        {
            _logger.LogWarning(ex, "Reading Other contacts failed");
            return StatusCode(StatusCodes.Status502BadGateway, ApiResponse<object>.ErrorResponse(ex.Code, ex.Message));
        }
    }

    /// <summary>The dismissed suggestion keys</summary>
    [HttpGet("dismissed")]
    public async Task<IActionResult> GetDismissed()
    {
        var userId = await SignedInUserAsync();
        return userId == null
            ? SessionExpired()
            : Ok(ApiResponse<List<string>>.SuccessResponse(await _store.GetDismissedAsync(userId)));
    }

    /// <summary>Dismisses suggestions (AD-48 rule 7)</summary>
    /// <param name="request">Keys</param>
    [HttpPost("dismissed")]
    public async Task<IActionResult> Dismiss([FromBody] DismissRequest request)
    {
        var userId = await SignedInUserAsync();
        if (userId == null)
        {
            return SessionExpired();
        }

        if (request?.Keys == null || request.Keys.Count == 0 || request.Keys.Count > 1000)
        {
            return BadRequest(ApiResponse<object>.ErrorResponse("INVALID_INPUT", "Send 1 to 1,000 keys."));
        }

        return Ok(ApiResponse<List<string>>.SuccessResponse(await _store.AddDismissedAsync(userId, request.Keys)));
    }

    /// <summary>Brings every dismissed suggestion back</summary>
    [HttpDelete("dismissed")]
    public async Task<IActionResult> ClearDismissed()
    {
        var userId = await SignedInUserAsync();
        if (userId == null)
        {
            return SessionExpired();
        }

        await _store.ClearDismissedAsync(userId);
        return Ok(ApiResponse<List<string>>.SuccessResponse(new List<string>()));
    }

    /// <summary>The signed-in Google account, with a valid token, or null</summary>
    private async Task<string?> SignedInUserAsync() =>
        await _tokens.GetAccessTokenAsync(HttpContext) == null
            ? null
            : HttpContext.Session.GetString(GoogleTokenService.UserIdKey);

    private ObjectResult NotConnected(string message) =>
        StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.ErrorResponse("NOT_CONNECTED", message));

    private UnauthorizedObjectResult SessionExpired() =>
        Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
}
