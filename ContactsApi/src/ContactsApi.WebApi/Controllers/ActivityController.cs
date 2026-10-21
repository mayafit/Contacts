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

    /// <summary>Gmail message content granted (signatures)</summary>
    public bool Signatures { get; set; }
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

    /// <summary>Whether the name directory (find a person by name) exists yet</summary>
    public bool HasDirectory { get; set; }
}

/// <summary>A directory match</summary>
public class DirectoryMatch
{
    /// <summary>Address</summary>
    public string Email { get; set; } = string.Empty;
    /// <summary>Names it appeared under</summary>
    public List<string> Names { get; set; } = new();
    /// <summary>Emails sent to them</summary>
    public int Sent { get; set; }
    /// <summary>Emails received from them</summary>
    public int Received { get; set; }
    /// <summary>Emails both received</summary>
    public int CopiedTogether { get; set; }
    /// <summary>Meetings together</summary>
    public int Meetings { get; set; }
    /// <summary>Last seen</summary>
    public DateTime? LastSeen { get; set; }
    /// <summary>Name match strength (suggestions): 3 same name, 2 address spells it, 1 name plus a word</summary>
    public int Score { get; set; }
}

/// <summary>Status plus the activity per address</summary>
public class ActivityResponse : ActivityStatusResponse
{
    /// <summary>Activity per lower-case email address</summary>
    public Dictionary<string, PersonActivity> People { get; set; } = new();

    /// <summary>Recent threads with people, newest first</summary>
    public List<MailThread> Threads { get; set; } = new();
}

/// <summary>A contact without an email, with the names to look for</summary>
public class NameQuery
{
    /// <summary>Contact id</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>Name variants (Hebrew name, phonetic name, nickname)</summary>
    public List<string> Names { get; set; } = new();
}

/// <summary>Request for email suggestions</summary>
public class SuggestEmailsRequest
{
    /// <summary>Contacts to find addresses for (at most 2,000)</summary>
    public List<NameQuery> Contacts { get; set; } = new();
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
        response.Threads = (snapshot.Threads ?? new()).Values.OrderByDescending(t => t.LastAt).ToList();
        return Ok(ApiResponse<ActivityResponse>.SuccessResponse(response));
    }

    /// <summary>
    /// People seen in mail or meetings whose name or address matches one of the
    /// queries (variants of a name, e.g. Hebrew and English). Every word of a query
    /// must appear, in any order, case-insensitively.
    /// </summary>
    /// <param name="q">Queries separated by "|"</param>
    /// <param name="limit">Most matches (1-50)</param>
    /// <returns>Best matches first</returns>
    [HttpGet("directory")]
    public async Task<IActionResult> Directory([FromQuery] string q = "", [FromQuery] int limit = 10)
    {
        var userId = await SignedInUserAsync();
        if (userId == null)
        {
            return SessionExpired();
        }

        var snapshot = await _store.GetAsync(userId);
        var queries = q.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(words => words.Length > 0)
            .Take(6)
            .ToList();
        var matches = (snapshot.Directory ?? new Dictionary<string, DirectoryEntry>())
            .Where(kv =>
            {
                var text = (kv.Key + " " + string.Join(" ", kv.Value.Names)).ToLowerInvariant();
                return queries.Any(words => words.All(w => text.Contains(w)));
            })
            .OrderByDescending(kv => kv.Value.Sent * 3 + kv.Value.Meetings * 3 + kv.Value.Received + kv.Value.CopiedTogether)
            .ThenByDescending(kv => kv.Value.LastSeen)
            .Take(Math.Clamp(limit, 1, 50))
            .Select(kv => new DirectoryMatch
            {
                Email = kv.Key,
                Names = kv.Value.Names,
                Sent = kv.Value.Sent,
                Received = kv.Value.Received,
                CopiedTogether = kv.Value.CopiedTogether,
                Meetings = kv.Value.Meetings,
                LastSeen = kv.Value.LastSeen
            })
            .ToList();
        return Ok(ApiResponse<object>.SuccessResponse(new { available = snapshot.Directory != null, matches }));
    }

    /// <summary>
    /// For contacts without an email, addresses from mail and meetings whose
    /// display name (or the address itself, e.g. dana.cohen@) matches one of the
    /// contact's names. Exact name matches first.
    /// </summary>
    /// <param name="request">Contacts and their name variants</param>
    /// <returns>Candidates per contact id</returns>
    [HttpPost("suggest-emails")]
    public async Task<IActionResult> SuggestEmails([FromBody] SuggestEmailsRequest request)
    {
        var userId = await SignedInUserAsync();
        if (userId == null)
        {
            return SessionExpired();
        }

        var snapshot = await _store.GetAsync(userId);
        // Normalized once per request, not once per contact
        var directory = (snapshot.Directory ?? new Dictionary<string, DirectoryEntry>())
            .Select(kv => (kv.Key, Entry: kv.Value, Names: kv.Value.Names.Select(NameMatcher.Normalize).ToList()))
            .ToList();
        var result = new Dictionary<string, List<DirectoryMatch>>();
        foreach (var c in (request.Contacts ?? new()).Take(2000))
        {
            var variants = c.Names.Select(NameMatcher.Normalize).Where(v => v.Contains(' ')).Distinct().ToList();
            if (variants.Count == 0)
            {
                continue;
            }

            var found = directory
                .Select(d => (d, score: NameMatcher.Score(variants, d.Key, d.Names)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .ThenByDescending(x => x.d.Entry.Sent * 3 + x.d.Entry.Meetings * 3 + x.d.Entry.Received)
                .Take(3)
                .Select(x => new DirectoryMatch
                {
                    Email = x.d.Key,
                    Names = x.d.Entry.Names,
                    Sent = x.d.Entry.Sent,
                    Received = x.d.Entry.Received,
                    CopiedTogether = x.d.Entry.CopiedTogether,
                    Meetings = x.d.Entry.Meetings,
                    LastSeen = x.d.Entry.LastSeen,
                    Score = x.score
                })
                .ToList();
            if (found.Count > 0)
            {
                result[c.Id] = found;
            }
        }

        return Ok(ApiResponse<object>.SuccessResponse(new { available = snapshot.Directory != null, suggestions = result }));
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
            // gmail.readonly (email content opt-in) allows everything metadata does
            Gmail = granted.Contains(ActivityScopes.Gmail) || granted.Contains(ActivityScopes.GmailRead),
            Calendar = granted.Contains(ActivityScopes.Calendar),
            Signatures = granted.Contains(ActivityScopes.GmailRead)
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
        response.HasDirectory = snapshot.Directory != null;
        return response;
    }

    private UnauthorizedObjectResult SessionExpired() =>
        Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
}
