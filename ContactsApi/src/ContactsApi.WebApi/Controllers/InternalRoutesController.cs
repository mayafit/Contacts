/**
 * @fileoverview The backend's internal API for the route-quality service (AD-33 rule 5).
 * Cluster-only (nginx never proxies /internal/**), EchoService token of the routes
 * service, read-only, owner-checked.
 * @module ContactsApi.WebApi.Controllers
 */
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Routes;
using ContactsApi.WebApi.Models.Responses;
using ContactsApi.WebApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ContactsApi.WebApi.Controllers;

/// <summary>Which of these records still exist</summary>
public record RouteExistsRequest(string OwnerId, List<Guid> Ids);

/// <summary>
/// Owners, the change feed, tracks and existence, for the route-quality service.
/// The service passes the ownerId (it has no session); every query is scoped to it.
/// </summary>
[ApiController]
[Route("internal/routes")]
[Authorize(Policy = EchoServiceDefaults.RoutesPolicy)]
[YieldToInteractive]
public class InternalRoutesController : ControllerBase
{
    private readonly AtlasReadiness _readiness;
    private readonly AtlasRoutesFeed _feed;

    /// <summary>Creates the controller</summary>
    public InternalRoutesController(AtlasReadiness readiness, AtlasRoutesFeed feed)
    {
        _readiness = readiness;
        _feed = feed;
    }

    /// <summary>The ids of Owners with Atlas data</summary>
    [HttpGet("owners")]
    public Task<IActionResult> Owners() => Run(null, () => _feed.OwnersAsync(HttpContext.RequestAborted), ownerRequired: false);

    /// <summary>Records that may have changed after the cursor; 409 while fusion is not settled</summary>
    [HttpGet("changes")]
    public Task<IActionResult> Changes([FromQuery] string? ownerId, [FromQuery] string? after, [FromQuery] int limit = RoutesFeedLimits.ChangesMax) =>
        Run(ownerId, () => _feed.ChangesAsync(ownerId!, after, limit, HttpContext.RequestAborted));

    /// <summary>Tracks of at most 25 records, with the Visits around them</summary>
    [HttpGet("tracks")]
    public Task<IActionResult> Tracks([FromQuery] string? ownerId, [FromQuery] List<Guid>? ids) =>
        Run(ownerId, () => _feed.TracksAsync(ownerId!, ids ?? new List<Guid>(), HttpContext.RequestAborted));

    /// <summary>Which records still exist (few ids, in the query)</summary>
    [HttpGet("exists")]
    public Task<IActionResult> Exists([FromQuery] string? ownerId, [FromQuery] List<Guid>? ids) =>
        Run(ownerId, () => _feed.ExistsAsync(ownerId!, ids ?? new List<Guid>(), HttpContext.RequestAborted));

    /// <summary>Which records still exist (up to 2,000 ids, in the body: too many for a URL)</summary>
    [HttpPost("exists")]
    public Task<IActionResult> ExistsMany([FromBody] RouteExistsRequest request) =>
        Run(request.OwnerId, () => _feed.ExistsAsync(request.OwnerId, request.Ids ?? new List<Guid>(), HttpContext.RequestAborted));

    private async Task<IActionResult> Run<T>(string? ownerId, Func<Task<T>> action, bool ownerRequired = true)
    {
        if (!_readiness.Ready)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ApiResponse<object>.ErrorResponse(
                "ATLAS_NOT_READY", "The Atlas database is starting. Try again in a minute."));
        }

        if (ownerRequired && (string.IsNullOrWhiteSpace(ownerId) || ownerId.Length > 64))
        {
            return BadRequest(ApiResponse<object>.ErrorResponse("INVALID_INPUT", "ownerId is required."));
        }

        try
        {
            return Ok(ApiResponse<T>.SuccessResponse(await action()));
        }
        catch (DerivationNotReadyException ex)
        {
            return Conflict(ApiResponse<object>.ErrorResponse("DERIVATION_NOT_READY", ex.Message));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<object>.ErrorResponse("INVALID_INPUT", ex.Message));
        }
    }
}

/// <summary>
/// Answers batch calls 429 with Retry-After while a map or timeline read is in flight;
/// a call for an interactive job (X-Echo-Priority: interactive) is never refused
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class YieldToInteractiveAttribute : Attribute, IAsyncActionFilter
{
    /// <inheritdoc />
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (ShouldYield(http.Request.Headers[EchoPriority.Header].ToString(), http.RequestServices.GetRequiredService<InteractiveLoad>()))
        {
            http.Response.Headers.RetryAfter = InteractiveLoad.RetryAfterSeconds.ToString();
            context.Result = new ObjectResult(ApiResponse<object>.ErrorResponse("BACKEND_BUSY",
                "Interactive reads are in flight. Try again shortly.")) { StatusCode = StatusCodes.Status429TooManyRequests };
            return Task.CompletedTask;
        }

        return next();
    }

    /// <summary>Whether a call with this priority header waits for the interactive load</summary>
    public static bool ShouldYield(string? priority, InteractiveLoad load) =>
        load.Busy && !string.Equals(priority, EchoPriority.Interactive, StringComparison.OrdinalIgnoreCase);
}
