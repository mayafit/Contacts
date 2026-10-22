/**
 * @fileoverview Atlas REST API for the web app (signed-in Google session)
 * @module ContactsApi.WebApi.Controllers
 */
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.WebApi.Models.Responses;
using ContactsApi.WebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace ContactsApi.WebApi.Controllers;

/// <summary>Request to create an access token</summary>
public record CreateTokenRequest(string Name);

/// <summary>
/// Activities, places, visits and health data of the signed-in user, plus
/// imports and the personal access tokens for MCP clients.
/// </summary>
[ApiController]
[Route("api/atlas")]
public class AtlasController : ControllerBase
{
    private readonly IServiceProvider _services;
    private readonly AtlasReadiness _readiness;
    private readonly GoogleTokenService _tokens;

    /// <summary>Creates the controller</summary>
    public AtlasController(IServiceProvider services, AtlasReadiness readiness, GoogleTokenService tokens)
    {
        _services = services;
        _readiness = readiness;
        _tokens = tokens;
    }

    /// <summary>Whether Atlas can serve (database configured and migrated)</summary>
    [HttpGet("status")]
    public IActionResult Status() =>
        Ok(ApiResponse<object>.SuccessResponse(new { configured = _readiness.Configured, ready = _readiness.Ready }));

    /// <summary>Adds or updates a batch of imported items</summary>
    [HttpPost("ingest")]
    [RequestSizeLimit(20_000_000)]
    public Task<IActionResult> Ingest([FromBody] IngestRequest request) =>
        Run((svc, owner) => svc.IngestAsync(owner, request, HttpContext.RequestAborted));

    /// <summary>Activities and visits by time, area, type and text</summary>
    [HttpPost("search")]
    public Task<IActionResult> Search([FromBody] AtlasQuery query) =>
        Run((svc, owner) => svc.SearchAsync(owner, query, HttpContext.RequestAborted));

    /// <summary>Counts per time bucket (timeline)</summary>
    [HttpGet("histogram")]
    public Task<IActionResult> Histogram([FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] string bucket = "day", [FromQuery] string? tz = null) =>
        Run((svc, owner) => svc.HistogramAsync(owner, from, to, bucket, HttpContext.RequestAborted, tz));

    /// <summary>Totals and imports</summary>
    [HttpGet("summary")]
    public Task<IActionResult> Summary() => Run((svc, owner) => svc.SummaryAsync(owner, HttpContext.RequestAborted));

    /// <summary>An activity with its track and heart rate</summary>
    [HttpGet("activities/{id:guid}")]
    public Task<IActionResult> Activity(Guid id, [FromQuery] int maxPoints = 2000) =>
        Run((svc, owner) => svc.GetActivityAsync(owner, id, maxPoints, HttpContext.RequestAborted));

    /// <summary>Changes an activity</summary>
    [HttpPatch("activities/{id:guid}")]
    public Task<IActionResult> UpdateActivity(Guid id, [FromBody] ActivityPatch patch) =>
        Run((svc, owner) => svc.UpdateActivityAsync(owner, id, patch, HttpContext.RequestAborted));

    /// <summary>Deletes an activity</summary>
    [HttpDelete("activities/{id:guid}")]
    public Task<IActionResult> DeleteActivity(Guid id) =>
        Run(async (svc, owner) => { await svc.DeleteActivityAsync(owner, id, HttpContext.RequestAborted); return new { deleted = id }; });

    /// <summary>Health values in a range</summary>
    [HttpGet("health")]
    public Task<IActionResult> Health([FromQuery] string type, [FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] int maxPoints = 1500) =>
        Run((svc, owner) => svc.HealthAsync(owner, type, from, to, maxPoints, HttpContext.RequestAborted));

    /// <summary>Visits and activities near a point</summary>
    [HttpGet("near")]
    public Task<IActionResult> Near([FromQuery] double lat, [FromQuery] double lon, [FromQuery] double radiusM = 500,
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null) =>
        Run((svc, owner) => svc.NearAsync(owner, lat, lon, radiusM, from, to, HttpContext.RequestAborted));

    /// <summary>Creates a place</summary>
    [HttpPost("places")]
    public Task<IActionResult> CreatePlace([FromBody] PlaceInput place) =>
        Run((svc, owner) => svc.CreatePlaceAsync(owner, place, HttpContext.RequestAborted));

    /// <summary>Changes a place</summary>
    [HttpPatch("places/{id:guid}")]
    public Task<IActionResult> UpdatePlace(Guid id, [FromBody] PlacePatch patch) =>
        Run((svc, owner) => svc.UpdatePlaceAsync(owner, id, patch, HttpContext.RequestAborted));

    /// <summary>Deletes a place</summary>
    [HttpDelete("places/{id:guid}")]
    public Task<IActionResult> DeletePlace(Guid id) =>
        Run(async (svc, owner) => { await svc.DeletePlaceAsync(owner, id, HttpContext.RequestAborted); return new { deleted = id }; });

    /// <summary>Deletes a visit</summary>
    [HttpDelete("visits/{id:guid}")]
    public Task<IActionResult> DeleteVisit(Guid id) =>
        Run(async (svc, owner) => { await svc.DeleteVisitAsync(owner, id, HttpContext.RequestAborted); return new { deleted = id }; });

    /// <summary>Deletes an import and what it brought in</summary>
    [HttpDelete("imports/{id:guid}")]
    public Task<IActionResult> DeleteImport(Guid id) =>
        Run(async (svc, owner) => new { removed = await svc.DeleteImportAsync(owner, id, HttpContext.RequestAborted) });

    /// <summary>The user's MCP access tokens</summary>
    [HttpGet("tokens")]
    public Task<IActionResult> Tokens() =>
        RunWith<AccessTokenService, List<AccessTokenInfo>>((svc, owner) => svc.ListAsync(owner));

    /// <summary>Creates an MCP access token (the token is returned only now)</summary>
    [HttpPost("tokens")]
    public Task<IActionResult> CreateToken([FromBody] CreateTokenRequest request) =>
        RunWith<AccessTokenService, object>(async (svc, owner) =>
        {
            var (token, info) = await svc.CreateAsync(owner, request.Name);
            return new { token, info };
        });

    /// <summary>Revokes an MCP access token</summary>
    [HttpDelete("tokens/{id:guid}")]
    public Task<IActionResult> RevokeToken(Guid id) =>
        RunWith<AccessTokenService, object>(async (svc, owner) => new { revoked = await svc.RevokeAsync(owner, id) });

    private Task<IActionResult> Run<T>(Func<AtlasService, string, Task<T>> action) => RunWith(action);

    /// <summary>Signed in, database ready, then the action; not found → 404</summary>
    private async Task<IActionResult> RunWith<TService, T>(Func<TService, string, Task<T>> action) where TService : notnull
    {
        if (await _tokens.GetAccessTokenAsync(HttpContext) == null)
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
        }

        if (!_readiness.Ready)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ApiResponse<object>.ErrorResponse(
                "ATLAS_NOT_READY", _readiness.Configured ? "The Atlas database is starting. Try again in a minute." : "Atlas is not configured."));
        }

        var owner = HttpContext.Session.GetString(GoogleTokenService.UserIdKey)!;
        try
        {
            var service = HttpContext.RequestServices.GetRequiredService<TService>();
            return Ok(ApiResponse<T>.SuccessResponse(await action(service, owner)));
        }
        catch (AtlasNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.ErrorResponse("NOT_FOUND", ex.Message));
        }
    }
}
