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

    /// <summary>The scan period in years (AD-47 rule 1)</summary>
    public int WindowYears { get; set; } = 1;
}

/// <summary>The scan period to set</summary>
public class ActivityWindowRequest
{
    /// <summary>1, 2, 3 or 5</summary>
    public int Years { get; set; }
}

/// <summary>A Contact whose whole history to search</summary>
public class ContactHistoryRequest
{
    /// <summary>The Contact's resource name</summary>
    public string Contact { get; set; } = string.Empty;

    /// <summary>Its addresses</summary>
    public List<string>? Emails { get; set; }
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

    /// <summary>The user's own addresses (left out of threads and people)</summary>
    public List<string> MyEmails { get; set; } = new();
}

/// <summary>Addresses that are the user's own</summary>
public class SelfRequest
{
    /// <summary>Email addresses</summary>
    public List<string> Emails { get; set; } = new();
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

/// <summary>One contact's names, to find its address (FR-78)</summary>
public class FindEmailRequest
{
    /// <summary>The contact's names, two words or more each (at most 6 are used)</summary>
    public List<string> Names { get; set; } = new();
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
    private readonly ILogger<ActivityController> _logger;

    /// <summary>Creates the controller</summary>
    public ActivityController(ActivityStore store, ActivityWorker worker, GoogleTokenService tokens, ILogger<ActivityController> logger)
    {
        _store = store;
        _worker = worker;
        _tokens = tokens;
        _logger = logger;
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
        var status = Fill(new ActivityStatusResponse(), snapshot);
        status.WindowYears = await _store.GetWindowYearsAsync(userId);
        return Ok(ApiResponse<ActivityStatusResponse>.SuccessResponse(status));
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

        // The user's own addresses, including ones marked after the last scan: mail
        // between them is not a conversation with someone
        var mine = (snapshot.MyEmails ?? new())
            .Concat(await _store.GetSelfAsync(userId))
            .Select(OwnAddresses.Normalize)
            .Where(e => e.Length > 0)
            .ToHashSet();
        response.MyEmails = mine.OrderBy(e => e).ToList();
        response.People = snapshot.People.Where(p => !mine.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        response.Threads = (snapshot.Threads ?? new()).Values
            .Select(t =>
            {
                var others = t.Counterparts.Where(c => !mine.Contains(OwnAddresses.Normalize(c))).ToList();
                return others.Count == t.Counterparts.Count ? t : others.Count == 0 ? null : CopyWith(t, others);
            })
            .OfType<MailThread>()
            .OrderByDescending(t => t.LastAt)
            .ToList();
        return Ok(ApiResponse<ActivityResponse>.SuccessResponse(response));
    }

    private static MailThread CopyWith(MailThread t, List<string> counterparts) => new()
    {
        ThreadId = t.ThreadId, LastAt = t.LastAt, LastFromMe = t.LastFromMe, Counterparts = counterparts,
        Subject = t.Subject, InInbox = t.InInbox, Messages = t.Messages, LastMessageId = t.LastMessageId
    };

    /// <summary>
    /// Marks addresses as the user's own ("This is me"): left out of replies,
    /// rhythm and suggestions from now on (and in the next scan)
    /// </summary>
    [HttpPost("self")]
    public async Task<IActionResult> AddSelf([FromBody] SelfRequest request)
    {
        var userId = await SignedInUserAsync();
        if (userId == null)
        {
            return SessionExpired();
        }

        var add = (request.Emails ?? new()).Select(OwnAddresses.Normalize).Where(OwnAddresses.IsAddress).ToList();
        if (add.Count == 0)
        {
            return BadRequest(ApiResponse<object>.ErrorResponse("INVALID", "No email address given."));
        }

        var all = (await _store.GetSelfAsync(userId)).Concat(add).Distinct().ToList();
        await _store.SaveSelfAsync(userId, all);
        return Ok(ApiResponse<List<string>>.SuccessResponse(all));
    }

    /// <summary>Takes back a "This is me"</summary>
    [HttpDelete("self/{email}")]
    public async Task<IActionResult> RemoveSelf(string email)
    {
        var userId = await SignedInUserAsync();
        if (userId == null)
        {
            return SessionExpired();
        }

        var all = (await _store.GetSelfAsync(userId)).Where(e => e != OwnAddresses.Normalize(email)).ToList();
        await _store.SaveSelfAsync(userId, all);
        return Ok(ApiResponse<List<string>>.SuccessResponse(all));
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

    /// <summary>
    /// One contact's address (AD-43 rule 7): the directory's names, and, with email
    /// content allowed, the senders Gmail finds for the contact's names (the From header
    /// only). A Gmail fault still returns what the directory knows.
    /// </summary>
    /// <param name="request">The contact's names</param>
    /// <returns>Up to 3 candidates, whether the directory exists, and the mailbox search's state</returns>
    [HttpPost("find-email")]
    public async Task<IActionResult> FindEmail([FromBody] FindEmailRequest request)
    {
        var userId = await SignedInUserAsync();
        if (userId == null)
        {
            return SessionExpired();
        }

        var names = (request?.Names ?? new())
            .Where(n => !string.IsNullOrWhiteSpace(n) && n.Length <= 100 && NameMatcher.Normalize(n).Contains(' '))
            .Distinct()
            .Take(6)
            .ToList();
        var variants = names.Select(NameMatcher.Normalize).Distinct().ToList();
        if (variants.Count == 0)
        {
            return BadRequest(ApiResponse<object>.ErrorResponse("INVALID_INPUT", "A first and last name is needed."));
        }

        var snapshot = await _store.GetAsync(userId);
        var mine = (snapshot.MyEmails ?? new())
            .Concat(await _store.GetSelfAsync(userId))
            .Select(OwnAddresses.Normalize)
            .Where(e => e.Length > 0)
            .ToHashSet();
        var fromDirectory = (snapshot.Directory ?? new Dictionary<string, DirectoryEntry>())
            .Select(kv => new FoundEmail
            {
                Email = kv.Key,
                Names = kv.Value.Names.Take(3).ToList(),
                Score = NameMatcher.Score(variants, kv.Key, kv.Value.Names),
                Mails = kv.Value.Sent + kv.Value.Received,
                LastSeen = kv.Value.LastSeen,
                Sources = new() { "directory" }
            })
            .Where(f => f.Score > 0)
            .ToList();

        var fromMail = new List<FoundEmail>();
        string mail;
        var query = SenderSearch.Query(names);
        if (!Connection().Signatures)
        {
            mail = "not_allowed";
        }
        else if (query == null)
        {
            mail = "searched";
        }
        else
        {
            try
            {
                var senders = await new GmailMailSenders(async _ =>
                        await _tokens.GetAccessTokenAsync(HttpContext) ?? throw new ActivityScanException("NOT_CONNECTED", "Session ended"))
                    .SearchAsync(query, SenderSearch.MaxMessages, HttpContext.RequestAborted);
                fromMail = SenderSearch.FromMail(variants, senders);
                mail = "searched";
            }
            catch (ActivityScanException ex) when (ex.Code == "NOT_CONNECTED")
            {
                mail = "sign_in";
            }
            catch (ActivityScanException)
            {
                mail = "failed";
            }
            catch (Exception ex) when (!HttpContext.RequestAborted.IsCancellationRequested)
            {
                // A timeout or an unexpected answer: the directory's candidates still count
                _logger.LogWarning(ex, "Searching the mailbox for a contact's address failed");
                mail = "failed";
            }
        }

        return Ok(ApiResponse<object>.SuccessResponse(new
        {
            candidates = SenderSearch.Merge(fromDirectory, fromMail, mine),
            directory = snapshot.Directory != null,
            mail
        }));
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
        await _store.RemoveHistoriesAsync(userId);
        var status = Fill(new ActivityStatusResponse(), new ActivitySnapshot());
        // The period is a setting: Forget keeps it (AD-47 rule 1)
        status.WindowYears = await _store.GetWindowYearsAsync(userId);
        return Ok(ApiResponse<ActivityStatusResponse>.SuccessResponse(status));
    }

    /// <summary>
    /// Sets how many years back the scan reads, and starts a full scan when Gmail or
    /// Calendar is connected (AD-47 rule 1)
    /// </summary>
    /// <param name="request">The period</param>
    /// <returns>Status</returns>
    [HttpPut("window")]
    public async Task<IActionResult> SetWindow([FromBody] ActivityWindowRequest request)
    {
        var userId = await SignedInUserAsync();
        if (userId == null)
        {
            return SessionExpired();
        }

        if (request == null || !ActivityStore.WindowYears.Contains(request.Years))
        {
            return BadRequest(ApiResponse<object>.ErrorResponse(
                "INVALID_INPUT", $"The scan period is {string.Join(", ", ActivityStore.WindowYears)} years."));
        }

        var changed = await _store.GetWindowYearsAsync(userId) != request.Years;
        await _store.SaveWindowYearsAsync(userId, request.Years);
        var connected = Connection();
        var snapshot = changed && (connected.Gmail || connected.Calendar)
            ? await _worker.EnqueueAsync(new ActivityJob(
                userId, HttpContext.Session.GetString("UserEmail") ?? string.Empty, true, connected.Gmail, connected.Calendar))
            : await _store.GetAsync(userId);
        var status = Fill(new ActivityStatusResponse(), snapshot);
        status.WindowYears = request.Years;
        return Ok(ApiResponse<ActivityStatusResponse>.SuccessResponse(status));
    }

    /// <summary>A Contact's whole history as last searched (AD-47 rule 6), or 204</summary>
    /// <param name="contact">The Contact's resource name</param>
    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] string contact)
    {
        var userId = await SignedInUserAsync();
        if (userId == null)
        {
            return SessionExpired();
        }

        var all = await _store.GetHistoriesAsync(userId);
        return !string.IsNullOrEmpty(contact) && all.TryGetValue(contact, out var history)
            ? Ok(ApiResponse<ContactHistory>.SuccessResponse(history))
            : NoContent();
    }

    /// <summary>
    /// Searches a Contact's whole mail (T2) and calendar (T1) without a date limit;
    /// only counts and dates are read and kept (AD-47 rules 3 to 6)
    /// </summary>
    /// <param name="request">The Contact and its addresses</param>
    [HttpPost("history")]
    public async Task<IActionResult> SearchHistory([FromBody] ContactHistoryRequest request)
    {
        var userId = await SignedInUserAsync();
        if (userId == null)
        {
            return SessionExpired();
        }

        var addresses = ContactHistoryReader.Addresses(request?.Emails);
        if (string.IsNullOrWhiteSpace(request?.Contact) || addresses.Count == 0)
        {
            return BadRequest(ApiResponse<object>.ErrorResponse("INVALID_INPUT", "The contact has no email address."));
        }

        var connected = Connection();
        if (!connected.Signatures && !connected.Calendar)
        {
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.ErrorResponse(
                "NOT_CONNECTED", "Allow reading email content or connect Calendar first."));
        }

        var reader = new ContactHistoryReader(async _ =>
            await _tokens.GetAccessTokenAsync(HttpContext) ?? throw new ActivityScanException("NOT_CONNECTED", "Session ended"));
        var ct = HttpContext.RequestAborted;
        var history = new ContactHistory { CheckedAt = DateTime.UtcNow };
        try
        {
            if (connected.Signatures)
            {
                history.Received = await reader.CountMailAsync(ContactHistoryReader.ReceivedQuery(addresses), ct);
                history.Sent = await reader.CountMailAsync(ContactHistoryReader.SentQuery(addresses), ct);
                history.Mail = "searched";
            }

            if (connected.Calendar)
            {
                history.Meetings = await reader.CountMeetingsAsync(addresses, DateTime.UtcNow, ct);
                history.Calendar = "searched";
            }
        }
        catch (ActivityScanException ex) when (ex.Code == "NOT_CONNECTED")
        {
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.ErrorResponse(
                "NOT_CONNECTED", "Google refused the request. Sign in again."));
        }
        catch (ActivityScanException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, ApiResponse<object>.ErrorResponse("SEARCH_FAILED", ex.Message));
        }

        await _store.SaveHistoryAsync(userId, request!.Contact, history);
        return Ok(ApiResponse<ContactHistory>.SuccessResponse(history));
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
