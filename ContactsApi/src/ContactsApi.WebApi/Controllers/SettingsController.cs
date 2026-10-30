/**
 * @fileoverview The Owner's settings documents (AD-41): /api/settings/{kind}
 * @module ContactsApi.WebApi.Controllers
 */
using System.Text.Json;
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.WebApi.Models.Responses;
using ContactsApi.WebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace ContactsApi.WebApi.Controllers;

/// <summary>Reads, saves and resets the signed-in Owner's settings</summary>
[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly AtlasReadiness _readiness;
    private readonly GoogleTokenService _tokens;

    /// <summary>Creates the controller</summary>
    public SettingsController(AtlasReadiness readiness, GoogleTokenService tokens)
    {
        _readiness = readiness;
        _tokens = tokens;
    }

    /// <summary>The Owner's settings of a kind (an empty object when none were saved)</summary>
    [HttpGet("{kind}")]
    public Task<IActionResult> Get(string kind) =>
        Run((svc, owner) => svc.GetAsync(owner, kind, HttpContext.RequestAborted));

    /// <summary>Saves the Owner's settings of a kind (a JSON object of at most 64 KB)</summary>
    [HttpPut("{kind}")]
    public Task<IActionResult> Save(string kind, [FromBody] JsonElement json) =>
        Run((svc, owner) => svc.SaveAsync(owner, kind, json, HttpContext.RequestAborted));

    /// <summary>Forgets the Owner's settings of a kind (the defaults apply)</summary>
    [HttpDelete("{kind}")]
    public Task<IActionResult> Reset(string kind) =>
        Run<object>(async (svc, owner) => new { reset = await svc.ResetAsync(owner, kind, HttpContext.RequestAborted) });

    /// <summary>Signed in, database ready, then the action; a bad kind or document → 400</summary>
    private async Task<IActionResult> Run<T>(Func<OwnerSettingsService, string, Task<T>> action)
    {
        if (await _tokens.GetAccessTokenAsync(HttpContext) == null)
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
        }

        if (!_readiness.Ready)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResponse<object>.ErrorResponse("ATLAS_NOT_READY", "The database is starting. Try again in a minute."));
        }

        var owner = HttpContext.Session.GetString(GoogleTokenService.UserIdKey)!;
        try
        {
            return Ok(ApiResponse<T>.SuccessResponse(await action(HttpContext.RequestServices.GetRequiredService<OwnerSettingsService>(), owner)));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<object>.ErrorResponse("INVALID_INPUT", ex.Message));
        }
    }
}
