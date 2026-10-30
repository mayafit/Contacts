/**
 * @fileoverview A flight's identity and details (AD-42 rule 11): /api/atlas/flights/{activityId}
 * @module ContactsApi.WebApi.Controllers
 */
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.WebApi.Models.Responses;
using ContactsApi.WebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace ContactsApi.WebApi.Controllers;

/// <summary>The flight number the Owner types</summary>
public class FlightNumberRequest
{
    /// <summary>EY593, EY 593, ETD593</summary>
    public string? FlightNumber { get; set; }

    /// <summary>The departure's local date (by default the flight's)</summary>
    public DateOnly? Date { get; set; }
}

/// <summary>The suggestion the Owner confirms</summary>
public class FlightConfirmRequest
{
    /// <summary>Its position in the suggestions</summary>
    public int Index { get; set; }
}

/// <summary>Reads a flight and takes the Owner's flight number, confirmations and new searches</summary>
[ApiController]
[Route("api/atlas/flights")]
public class FlightsController : ControllerBase
{
    private readonly AtlasReadiness _readiness;
    private readonly GoogleTokenService _tokens;

    /// <summary>Creates the controller</summary>
    public FlightsController(AtlasReadiness readiness, GoogleTokenService tokens)
    {
        _readiness = readiness;
        _tokens = tokens;
    }

    /// <summary>The flight of an activity (looked up when it never was)</summary>
    [HttpGet("{activityId:guid}")]
    public Task<IActionResult> Get(Guid activityId) =>
        Run((svc, owner) => svc.GetAsync(owner, activityId, HttpContext.RequestAborted));

    /// <summary>The Owner types the flight number</summary>
    [HttpPut("{activityId:guid}")]
    public Task<IActionResult> SetNumber(Guid activityId, [FromBody] FlightNumberRequest request) =>
        Run((svc, owner) => svc.SetNumberAsync(owner, activityId, request.FlightNumber, request.Date, HttpContext.RequestAborted));

    /// <summary>Clears the Owner's flight number</summary>
    [HttpDelete("{activityId:guid}/number")]
    public Task<IActionResult> Clear(Guid activityId) =>
        Run((svc, owner) => svc.ClearAsync(owner, activityId, HttpContext.RequestAborted));

    /// <summary>Looks the flight up again</summary>
    [HttpPost("{activityId:guid}/search")]
    public Task<IActionResult> Search(Guid activityId) =>
        Run((svc, owner) => svc.SearchAsync(owner, activityId, HttpContext.RequestAborted));

    /// <summary>The Owner confirms a suggestion</summary>
    [HttpPost("{activityId:guid}/confirm")]
    public Task<IActionResult> Confirm(Guid activityId, [FromBody] FlightConfirmRequest request) =>
        Run((svc, owner) => svc.ConfirmAsync(owner, activityId, request.Index, HttpContext.RequestAborted));

    /// <summary>Signed in, database ready, then the action; not the Owner's flight → 404, bad input → 400</summary>
    private async Task<IActionResult> Run(Func<FlightService, string, Task<FlightDto?>> action)
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
            var flight = await action(HttpContext.RequestServices.GetRequiredService<FlightService>(), owner);
            return flight == null
                ? NotFound(ApiResponse<object>.ErrorResponse("NOT_FOUND", "No such flight."))
                : Ok(ApiResponse<FlightDto>.SuccessResponse(flight));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<object>.ErrorResponse("INVALID_INPUT", ex.Message));
        }
    }
}
