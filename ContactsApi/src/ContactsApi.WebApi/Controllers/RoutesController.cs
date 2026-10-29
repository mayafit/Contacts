/**
 * @fileoverview The on-demand route endpoints (AD-33 rule 7): queue assess or improve,
 * a job's progress and the service's status, for the signed-in Owner. The Owner comes
 * only from the session; nothing in a request body names one.
 * @module ContactsApi.WebApi.Controllers
 */
using ContactsApi.WebApi.Models.Responses;
using ContactsApi.WebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace ContactsApi.WebApi.Controllers;

/// <summary>What to assess or improve: ids (≤ 2,000), a time range, or everything</summary>
/// <param name="Force">improve: match even what does not need it (never against "Keep the original")</param>
public record RoutesRunRequest(List<Guid>? ActivityIds, DateTime? From, DateTime? To, bool? All, bool? Force);

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

    /// <summary>Creates the controller</summary>
    public RoutesController(GoogleTokenService tokens, RoutesServiceClient routes)
    {
        _tokens = tokens;
        _routes = routes;
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
