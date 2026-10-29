/**
 * @fileoverview The on-demand route endpoints (AD-33 rule 7): queue assess or improve,
 * a job's progress and the service's status, for the signed-in Owner. The Owner comes
 * only from the session; nothing in a request body names one.
 * @module ContactsApi.WebApi.Controllers
 */
using System.Text.Json;
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Routes;
using ContactsApi.WebApi.Models.Responses;
using ContactsApi.WebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace ContactsApi.WebApi.Controllers;

/// <summary>What to assess or improve: ids (≤ 2,000), a time range, or everything</summary>
/// <param name="Force">improve: match even what does not need it (never against "Keep the original")</param>
public record RoutesRunRequest(List<Guid>? ActivityIds, DateTime? From, DateTime? To, bool? All, bool? Force);

/// <summary>An Owner's decision about an Activity's route: reject ("Keep the original") or accept</summary>
public record RouteDecisionRequest(string? Kind);

/// <summary>/api/routes: on-demand route quality for the signed-in Owner</summary>
[ApiController]
[Route("api/routes")]
public class RoutesController : ControllerBase
{
    /// <summary>Most ids of one request (the backend resolves a query into at most this many)</summary>
    public const int IdsMax = 2000;

    /// <summary>A UI job of at most this many ids is interactive (AD-33 rule 4)</summary>
    public const int InteractiveMax = 50;

    private readonly GoogleTokenService _tokens;
    private readonly RoutesServiceClient _routes;
    private readonly IServiceProvider _services;

    /// <summary>Creates the controller</summary>
    /// <param name="services">Resolves the Atlas and Routes data services only on the endpoints that use them</param>
    public RoutesController(GoogleTokenService tokens, RoutesServiceClient routes, IServiceProvider services)
    {
        _tokens = tokens;
        _routes = routes;
        _services = services;
    }

    /// <summary>
    /// Records the Owner's decision for an Activity's route, keyed by each drawn record's
    /// natural key. "reject" ("Keep the original") draws the recorded line again everywhere
    /// and is never matched again automatically.
    /// </summary>
    [HttpPut("activities/{id:guid}/decision")]
    public Task<IActionResult> Decide(Guid id, [FromBody] RouteDecisionRequest request) =>
        WithRecords(id, async (owner, keys, data, ct) =>
        {
            var kind = request.Kind?.Trim().ToLowerInvariant();
            if (kind != RouteDecisionKinds.Reject && kind != RouteDecisionKinds.Accept)
            {
                return BadRequest(ApiResponse<object>.ErrorResponse("INVALID_INPUT", "kind is reject or accept."));
            }

            await data.SetDecisionAsync(owner, keys, kind, ct);
            return Ok(ApiResponse<object>.SuccessResponse(new { kind, records = keys.Count }));
        });

    /// <summary>Removes the Owner's decision for an Activity's route ("Try again" first deletes the reject)</summary>
    [HttpDelete("activities/{id:guid}/decision")]
    public Task<IActionResult> ClearDecision(Guid id) =>
        WithRecords(id, async (owner, keys, data, ct) =>
        {
            var removed = await data.ClearDecisionAsync(owner, keys, ct);
            return Ok(ApiResponse<object>.SuccessResponse(new { removed }));
        });

    /// <summary>
    /// The Owner's routes data as JSON (AD-34 rule 8): decisions, settings, quality metrics
    /// and matched routes as GeoJSON with their piece kinds. Streamed.
    /// </summary>
    [HttpGet("export")]
    public async Task Export()
    {
        var owner = await OwnerAsync();
        if (owner == null)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            await Response.WriteAsJsonAsync(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
            return;
        }

        var data = _services.GetRequiredService<RouteOwnerData>();
        Response.ContentType = "application/json";
        Response.Headers.ContentDisposition = "attachment; filename=\"echo-routes.json\"";
        try
        {
            await using var writer = new Utf8JsonWriter(Response.Body);
            await data.ExportAsync(owner, writer, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !Response.HasStarted)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            Response.Headers.ContentDisposition = default;
            await Response.WriteAsJsonAsync(ApiResponse<object>.ErrorResponse("ROUTES_UNAVAILABLE", "Route data cannot be read right now."));
        }
    }

    private async Task<IActionResult> WithRecords(Guid id,
        Func<string, List<string>, RouteOwnerData, CancellationToken, Task<IActionResult>> action)
    {
        var owner = await OwnerAsync();
        if (owner == null)
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
        }

        if (!_services.GetRequiredService<RouteGeometryOptions>().Enabled)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResponse<object>.ErrorResponse("ROUTES_UNAVAILABLE", "Route quality is not enabled."));
        }

        try
        {
            var records = await _services.GetRequiredService<AtlasService>().RouteRecordsAsync(owner, id, HttpContext.RequestAborted);
            return await action(owner, records.Select(r => r.SourceKey).ToList(), _services.GetRequiredService<RouteOwnerData>(),
                HttpContext.RequestAborted);
        }
        catch (AtlasNotFoundException)
        {
            return NotFound(ApiResponse<object>.ErrorResponse("NOT_FOUND", "Activity not found."));
        }
        catch (Exception ex) when (ex is Microsoft.EntityFrameworkCore.DbUpdateException or Npgsql.NpgsqlException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResponse<object>.ErrorResponse("ROUTES_UNAVAILABLE", "Route decisions cannot be saved right now."));
        }
    }

    /// <summary>Queues assessment and matching ("Fix this route", "Improve routes")</summary>
    [HttpPost("improve")]
    public Task<IActionResult> Improve([FromBody] RoutesRunRequest request) => Queue("improve", request);

    /// <summary>Queues assessment only</summary>
    [HttpPost("assess")]
    public Task<IActionResult> Assess([FromBody] RoutesRunRequest request) => Queue("assess", request);

    /// <summary>A job's progress (polling it never counts as interactive load)</summary>
    [HttpGet("jobs/{id:guid}")]
    public Task<IActionResult> Job(Guid id) => Run(false, (owner, ct) => _routes.JobAsync(owner, id, ct));

    /// <summary>What the route service is doing for the Owner; answers even when the service is down</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var owner = await OwnerAsync();
        if (owner == null)
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
        }

        try
        {
            return Pass(await _routes.StatusAsync(owner, HttpContext.RequestAborted));
        }
        catch (RoutesUnavailableException)
        {
            return Ok(ApiResponse<object>.SuccessResponse(new { available = false, message = "route-quality service unavailable" }));
        }
    }

    private Task<IActionResult> Queue(string kind, RoutesRunRequest request)
    {
        var ids = request.ActivityIds?.Distinct().ToList();
        var range = request.From != null || request.To != null;
        var forms = (ids is { Count: > 0 } ? 1 : 0) + (request.All == true ? 1 : 0) + (range ? 1 : 0);
        if (forms != 1 || ids?.Count > IdsMax || (range && (request.From == null || request.To == null || request.From >= request.To)))
        {
            return Task.FromResult<IActionResult>(BadRequest(ApiResponse<object>.ErrorResponse("INVALID_INPUT",
                $"Give one of activityIds (1 to {IdsMax}), from and to, or all.")));
        }

        object scope = ids is { Count: > 0 } ? new { activityIds = ids }
            : range ? new { from = request.From!.Value.ToUniversalTime(), to = request.To!.Value.ToUniversalTime() }
            : new { all = true };
        var interactive = ids is { Count: > 0 and <= InteractiveMax };
        return Run(true, (owner, ct) => _routes.QueueAsync(kind, owner, scope, interactive, request.Force == true, ct));
    }

    private async Task<IActionResult> Run(bool write, Func<string, CancellationToken, Task<RoutesServiceAnswer>> call)
    {
        var owner = await OwnerAsync();
        if (owner == null)
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
        }

        try
        {
            return Pass(await call(owner, HttpContext.RequestAborted));
        }
        catch (RoutesUnavailableException ex)
        {
            Response.Headers.RetryAfter = "30";
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResponse<object>.ErrorResponse("ROUTES_UNAVAILABLE", write ? ex.Message : "route-quality service unavailable"));
        }
    }

    /// <summary>The Owner from the session only (AD-3 rule 2)</summary>
    private async Task<string?> OwnerAsync() =>
        await _tokens.GetAccessTokenAsync(HttpContext) == null ? null : HttpContext.Session.GetString(GoogleTokenService.UserIdKey);

    private static IActionResult Pass(RoutesServiceAnswer answer) =>
        new ContentResult { StatusCode = answer.Status, Content = answer.Body, ContentType = "application/json" };
}
