/**
 * @fileoverview When the user was last in touch with each contact (Gmail + Calendar)
 * @module ContactsApi.WebApi.Controllers
 */
using ContactsApi.Infrastructure.Activity;
using ContactsApi.WebApi.Models.Responses;
using ContactsApi.WebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace ContactsApi.WebApi.Controllers;

/// <summary>Which activity sources the user has granted</summary>
public class ActivityConnection
{
    /// <summary>Gmail metadata granted</summary>
    public bool Gmail { get; set; }

    /// <summary>Calendar events granted</summary>
    public bool Calendar { get; set; }
}

/// <summary>Activity snapshot state, without the per-address data</summary>
public class ActivityStatusResponse
{
    /// <summary>Granted sources</summary>
    public ActivityConnection Connected { get; set; } = new();
    /// <summary>none, running, ready or error</summary>
    public string Status { get; set; } = ActivityStatus.None;
    /// <summary>Progress while running</summary>
    public ActivityProgress? Progress { get; set; }
    /// <summary>Failure message</summary>
    public string? Error { get; set; }
    /// <summary>Failure code</summary>
    public string? ErrorCode { get; set; }
    /// <summary>When the data was last brought up to date</summary>
    public DateTime? UpdatedAt { get; set; }
    /// <summary>Oldest date the email data covers</summary>
    public DateTime? Since { get; set; }
    /// <summary>Addresses with activity</summary>
    public int PeopleCount { get; set; }
}

/// <summary>Status plus the activity per address</summary>
public class ActivityResponse : ActivityStatusResponse
{
    /// <summary>Activity per lower-case email address</summary>
    public Dictionary<string, PersonActivity> People { get; set; } = new();
}

/// <summary>
/// Email and meeting activity per address. Built by <see cref="ActivityWorker"/>
/// after the user connects Gmail and Calendar (<c>/api/auth/login?activity=true</c>).
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ActivityController : ControllerBase
{
    private readonly ActivityStore _store;
    private readonly ActivityWorker _worker;
    private readonly GoogleTokenService _tokens;

    /// <summary>Creates the controller</summary>
    public ActivityController(ActivityStore store, ActivityWorker worker, GoogleTokenService tokens)
    {
        _store = store;
        _worker = worker;
        _tokens = tokens;
    }

    /// <summary>Snapshot state, cheap enough to poll during a scan</summary>
    /// <returns>Status</returns>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var userId = await SignedInUserAsync();
        if (userId == null)
        {
            return SessionExpired();
        }

        var snapshot = await _store.GetAsync(userId);
        return Ok(ApiResponse<ActivityStatusResponse>.SuccessResponse(Fill(new ActivityStatusResponse(), snapshot)));
    }

    /// <summary>Snapshot with the activity per address</summary>
    /// <returns>Activity</returns>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var userId = await SignedInUserAsync();
        if (userId == null)
        {
            return SessionExpired();
        }

        var snapshot = await _store.GetAsync(userId);
        var response = Fill(new ActivityResponse(), snapshot);
        response.People = snapshot.People;
        return Ok(ApiResponse<ActivityResponse>.SuccessResponse(response));
    }

    /// <summary>Starts bringing the snapshot up to date (no-op while a scan runs)</summary>
    /// <param name="full">Rescan the whole window instead of only new mail</param>
    /// <returns>Status after queueing</returns>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromQuery] bool full = false)
    {
        var userId = await SignedInUserAsync();
        if (userId == null)
        {
            return SessionExpired();
        }

        var connected = Connection();
        if (!connected.Gmail && !connected.Calendar)
        {
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.ErrorResponse(
                "NOT_CONNECTED", "Connect Gmail & Calendar first."));
        }

        var snapshot = await _worker.EnqueueAsync(
            new ActivityJob(
                userId, HttpContext.Session.GetString("UserEmail") ?? string.Empty, full, connected.Gmail, connected.Calendar));
        return Ok(ApiResponse<ActivityStatusResponse>.SuccessResponse(Fill(new ActivityStatusResponse(), snapshot)));
    }

    /// <summary>Forgets the stored activity (Google access stays until revoked in the Google account)</summary>
    /// <returns>Empty status</returns>
    [HttpDelete]
    public async Task<IActionResult> Forget()
    {
        var userId = await SignedInUserAsync();
        if (userId == null)
        {
            return SessionExpired();
        }

        await _store.RemoveAsync(userId);
        return Ok(ApiResponse<ActivityStatusResponse>.SuccessResponse(Fill(new ActivityStatusResponse(), new ActivitySnapshot())));
    }

    /// <summary>The signed-in Google account, with a valid token, or null</summary>
    private async Task<string?> SignedInUserAsync() =>
        await _tokens.GetAccessTokenAsync(HttpContext) == null
            ? null
            : HttpContext.Session.GetString(GoogleTokenService.UserIdKey);

    private ActivityConnection Connection()
    {
        var granted = (HttpContext.Session.GetString(ActivityScopes.SessionKey) ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new ActivityConnection
        {
            Gmail = granted.Contains(ActivityScopes.Gmail),
            Calendar = granted.Contains(ActivityScopes.Calendar)
        };
    }

    private T Fill<T>(T response, ActivitySnapshot snapshot) where T : ActivityStatusResponse
    {
        response.Connected = Connection();
        response.Status = snapshot.Status == ActivityStatus.Running && !ActivityWorker.IsRunning(snapshot)
            ? ActivityStatus.Error
            : snapshot.Status;
        response.Progress = snapshot.Progress;
        response.Error = response.Status == ActivityStatus.Error && snapshot.Status == ActivityStatus.Running
            ? "The last scan stopped unexpectedly. Refresh to start it again."
            : snapshot.Error;
        response.ErrorCode = snapshot.ErrorCode;
        response.UpdatedAt = snapshot.UpdatedAt;
        response.Since = snapshot.Since;
        response.PeopleCount = snapshot.People.Count;
        return response;
    }

    private UnauthorizedObjectResult SessionExpired() =>
        Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
}
