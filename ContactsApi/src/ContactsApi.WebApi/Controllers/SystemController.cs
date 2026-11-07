/**
 * @fileoverview Operator-only system status + on-demand Grafana (Epic 22). This
 * controller is the app's HALF of a deliberately narrow contract: it reads a
 * health snapshot the cluster-side status-agent writes to Redis, and it writes a
 * "wake Grafana until" timestamp the agent reads. The app has NO Kubernetes
 * access of any kind — all cluster privilege lives in the agent. See
 * docs/planning/epic-22-ops-status-and-ondemand-grafana.md.
 * @module ContactsApi.WebApi.Controllers
 */
using System.Text.Json;
using ContactsApi.WebApi.Models.Responses;
using ContactsApi.WebApi.Services;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace ContactsApi.WebApi.Controllers;

/// <summary>
/// Owner-only ops endpoints. Every action requires an authenticated session whose
/// email is on <c>Echo:AdminEmails</c>; family members get 403 and the frontend
/// never renders the bar for them. State-changing calls also pass the global CSRF
/// check (X-Echo-Request + same-origin), like every other write.
/// </summary>
[ApiController]
[Route("api/system")]
public class SystemController : ControllerBase
{
    /// <summary>Redis key the status-agent writes the health snapshot to.</summary>
    private const string HealthKey = "ops:health";

    /// <summary>Redis key the status-agent reads to decide Grafana's replica count.</summary>
    private const string WakeKey = "ops:grafana:wake_until";

    /// <summary>How long one "Open Grafana" click keeps Grafana awake.</summary>
    private static readonly TimeSpan WakeWindow = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;
    private readonly AdminEmails _admin;
    private readonly ILogger<SystemController> _logger;

    public SystemController(IConnectionMultiplexer redis, AdminEmails admin, ILogger<SystemController> logger)
    {
        _redis = redis;
        _admin = admin;
        _logger = logger;
    }

    /// <summary>
    /// The current system-health snapshot for the status bar. Reads it straight
    /// from Redis; if it is missing or stale (the agent sets a TTL), returns an
    /// explicit <c>unknown</c> snapshot rather than guessing green.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(ApiResponse<SystemStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetStatus()
    {
        var gate = RequireAdmin();
        if (gate != null) return gate;

        try
        {
            var raw = await _redis.GetDatabase().StringGetAsync(HealthKey);
            if (raw.IsNullOrEmpty)
            {
                return Ok(ApiResponse<SystemStatusResponse>.SuccessResponse(Unknown("No recent snapshot (the status-agent may be down)")));
            }

            var snapshot = JsonSerializer.Deserialize<SystemStatusResponse>((string)raw!, JsonOpts);
            if (snapshot == null)
            {
                return Ok(ApiResponse<SystemStatusResponse>.SuccessResponse(Unknown("Unreadable snapshot")));
            }

            return Ok(ApiResponse<SystemStatusResponse>.SuccessResponse(snapshot));
        }
        catch (Exception ex) when (ex is RedisException or JsonException)
        {
            _logger.LogWarning(ex, "System status unavailable (Redis/JSON)");
            return Ok(ApiResponse<SystemStatusResponse>.SuccessResponse(Unknown("Status store unreachable")));
        }
    }

    /// <summary>
    /// Request that Grafana be woken (scaled up) for the next 30 minutes. The app
    /// only writes the timestamp; the cluster-side agent does the scaling. Safe to
    /// call repeatedly — each call extends the window.
    /// </summary>
    [HttpPost("grafana/wake")]
    [ProducesResponseType(typeof(ApiResponse<GrafanaState>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> WakeGrafana()
    {
        var gate = RequireAdmin();
        if (gate != null) return gate;

        var wakeUntil = DateTimeOffset.UtcNow.Add(WakeWindow).ToUnixTimeSeconds();
        try
        {
            // Expiry a little past the window so a stale key self-cleans if the
            // agent ever stops reading it.
            await _redis.GetDatabase().StringSetAsync(
                WakeKey,
                wakeUntil.ToString(System.Globalization.CultureInfo.InvariantCulture),
                WakeWindow.Add(TimeSpan.FromMinutes(5)));

            _logger.LogInformation("Grafana wake requested until {WakeUntil} (unix)", wakeUntil);
            return Ok(ApiResponse<GrafanaState>.SuccessResponse(new GrafanaState
            {
                Awake = true,
                Ready = false, // the agent brings the pod up; poll /status for readiness
                WakeUntil = wakeUntil
            }));
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Could not request Grafana wake (Redis)");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResponse<GrafanaState>.ErrorResponse("STORE_UNAVAILABLE", "Could not reach the status store. Try again shortly."));
        }
    }

    /// <summary>401 if not signed in, 403 if signed in but not an admin, else null.</summary>
    private IActionResult? RequireAdmin()
    {
        if (HttpContext.OwnerId() == null)
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse("UNAUTHENTICATED", "Sign in required."));
        }

        if (!_admin.IsAdmin(HttpContext.UserEmail()))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                ApiResponse<object>.ErrorResponse("FORBIDDEN", "This area is for the system operator."));
        }

        return null;
    }

    private static SystemStatusResponse Unknown(string detail) => new()
    {
        GeneratedAt = null,
        Overall = "unknown",
        Lamps = new List<SystemLamp>
        {
            new() { Id = "nodes",   Label = "Nodes",      State = "unknown", Detail = detail },
            new() { Id = "data",    Label = "Data",       State = "unknown", Detail = detail },
            new() { Id = "app",     Label = "App",        State = "unknown", Detail = detail },
            new() { Id = "vault",   Label = "Vault",      State = "unknown", Detail = detail },
            new() { Id = "monitor", Label = "Monitoring", State = "unknown", Detail = detail },
        },
        Grafana = new GrafanaState { Awake = false, Ready = false, WakeUntil = null }
    };
}
