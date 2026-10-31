/**
 * @fileoverview Public profiles (AD-46): turning the Gravatar look-up on and off, sending the
 * contacts' email hashes, reading what was found, and the links in a contact's signatures
 * @module ContactsApi.WebApi.Controllers
 */
using ContactsApi.Infrastructure.Activity;
using ContactsApi.WebApi.Models.Responses;
using ContactsApi.WebApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ContactsApi.WebApi.Controllers;

/// <summary>Email hashes to look up</summary>
public class ProfileHashesRequest
{
    /// <summary>SHA-256 of each contact address (trimmed, lower case), at most 20,000</summary>
    public List<string> Hashes { get; set; } = new();

    /// <summary>These are all the Owner's hashes: what is not among them is dropped</summary>
    public bool Complete { get; set; }
}

/// <summary>One contact's addresses, for its signatures</summary>
public class ProfileSignaturesRequest
{
    /// <summary>At most 5 addresses</summary>
    public List<string> Emails { get; set; } = new();
}

/// <summary>Public profiles of the Owner's contacts</summary>
[ApiController]
[Route("api/[controller]")]
public class ProfilesController : ControllerBase
{
    /// <summary>Emails read per contact for its signature links</summary>
    private const int EmailsPerContact = 3;

    private readonly ProfileStore _store;
    private readonly ProfileQueue _queue;
    private readonly GoogleTokenService _tokens;
    private readonly GravatarOptions _gravatar;
    private readonly ILogger<ProfilesController> _logger;

    /// <summary>Creates the controller</summary>
    public ProfilesController(ProfileStore store, ProfileQueue queue, GoogleTokenService tokens,
        IOptions<GravatarOptions> gravatar, ILogger<ProfilesController> logger)
    {
        _store = store;
        _queue = queue;
        _tokens = tokens;
        _gravatar = gravatar.Value;
        _logger = logger;
    }

    /// <summary>Whether the look-up is on, its progress, and the profiles found by hash</summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var owner = await OwnerAsync();
        if (owner == null)
        {
            return SessionExpired();
        }

        var doc = await _store.GetAsync(owner);
        // After a restart the queue of Owners is empty until a browser sends hashes again
        if (doc.Enabled && doc.Queue.Count > 0)
        {
            _queue.Add(owner);
        }

        return Ok(ApiResponse<object>.SuccessResponse(Status(doc, true)));
    }

    /// <summary>Turns the look-up on (the browser then sends the hashes)</summary>
    [HttpPost("enable")]
    public async Task<IActionResult> Enable()
    {
        var owner = await OwnerAsync();
        if (owner == null)
        {
            return SessionExpired();
        }

        var doc = await _store.UpdateAsync(owner, d =>
        {
            if (d.Enabled)
            {
                return false;
            }

            d.Enabled = true;
            return true;
        });
        _logger.LogInformation("Public profile look-up turned on");
        return Ok(ApiResponse<object>.SuccessResponse(Status(doc, false)));
    }

    /// <summary>Turns the look-up off and deletes everything found</summary>
    [HttpDelete]
    public async Task<IActionResult> Forget()
    {
        var owner = await OwnerAsync();
        if (owner == null)
        {
            return SessionExpired();
        }

        _queue.Remove(owner);
        await _store.RemoveAsync(owner);
        _logger.LogInformation("Public profiles forgotten");
        return Ok(ApiResponse<object>.SuccessResponse(Status(new ProfileDoc(), false)));
    }

    /// <summary>
    /// Queues the hashes not looked up in the last 30 days (only while the look-up is on)
    /// </summary>
    /// <param name="request">The hashes</param>
    [HttpPost("hashes")]
    public async Task<IActionResult> Hashes([FromBody] ProfileHashesRequest request)
    {
        var owner = await OwnerAsync();
        if (owner == null)
        {
            return SessionExpired();
        }

        var hashes = (request?.Hashes ?? new()).Select(h => (h ?? string.Empty).Trim().ToLowerInvariant()).ToList();
        if (hashes.Count > ProfileStore.MaxHashes || hashes.Any(h => !GravatarClient.IsHash(h)))
        {
            return BadRequest(ApiResponse<object>.ErrorResponse("INVALID_INPUT",
                $"Up to {ProfileStore.MaxHashes} SHA-256 hashes (64 hex characters)"));
        }

        var now = DateTime.UtcNow;
        var doc = await _store.UpdateAsync(owner, d =>
        {
            if (!d.Enabled)
            {
                return false;
            }

            // Both run: the "|" is deliberate
            return (request!.Complete && ProfileStore.Prune(d, hashes.ToHashSet()))
                | ProfileStore.Enqueue(d, hashes, now) > 0;
        });
        if (doc.Enabled && doc.Queue.Count > 0)
        {
            _queue.Add(owner);
        }

        return Ok(ApiResponse<object>.SuccessResponse(Status(doc, false)));
    }

    /// <summary>
    /// The links in the signatures of the contact's last 3 emails (T2), by pattern;
    /// nothing is stored
    /// </summary>
    /// <param name="request">The contact's addresses</param>
    [HttpPost("signatures")]
    public async Task<IActionResult> Signatures([FromBody] ProfileSignaturesRequest request)
    {
        if (await _tokens.GetAccessTokenAsync(HttpContext) == null)
        {
            return SessionExpired();
        }

        var granted = (HttpContext.Session.GetString(ActivityScopes.SessionKey) ?? string.Empty).Split(' ');
        if (!granted.Contains(ActivityScopes.GmailRead))
        {
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.ErrorResponse(
                "SIGNATURES_NOT_CONNECTED", "Allow reading email content first."));
        }

        var emails = (request?.Emails ?? new()).Where(e => !string.IsNullOrWhiteSpace(e)).Take(5).ToList();
        if (emails.Count == 0)
        {
            return BadRequest(ApiResponse<object>.ErrorResponse("INVALID_INPUT", "The contact has no email address."));
        }

        var reader = new SignatureReader(async _ =>
            await _tokens.GetAccessTokenAsync(HttpContext) ?? throw new ActivityScanException("NOT_CONNECTED", "Session ended"));
        try
        {
            var read = await reader.RecentFromAsync(emails, EmailsPerContact, HttpContext.RequestAborted);
            return Ok(ApiResponse<object>.SuccessResponse(new
            {
                matched = read.Matched,
                emails = read.Samples.Count,
                urls = SignatureLinks.Extract(read.Samples.Select(s => s.Tail))
            }));
        }
        catch (ActivityScanException ex) when (ex.Code is "NOT_CONNECTED" or "SCOPE_CONFLICT")
        {
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.ErrorResponse(
                "SIGNATURES_NOT_CONNECTED", "Gmail did not allow reading email content. Allow it again."));
        }
        catch (ActivityScanException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, ApiResponse<object>.ErrorResponse("GMAIL_FAILED", ex.Message));
        }
    }

    /// <summary>The look-up's state; the found profiles only when asked</summary>
    private object Status(ProfileDoc doc, bool withProfiles) => new
    {
        enabled = doc.Enabled,
        @checked = doc.Entries.Count,
        pending = doc.Queue.Count,
        found = doc.Entries.Count(e => e.Value.Found),
        hourlyLimit = string.IsNullOrWhiteSpace(_gravatar.ApiKey) ? GravatarClient.HourlyLimitWithoutKey : GravatarClient.HourlyLimitWithKey,
        limitedUntil = _queue.LimitedUntil,
        profiles = withProfiles
            ? doc.Entries.Where(e => e.Value.Found && e.Value.Profile != null).ToDictionary(e => e.Key, e => e.Value.Profile!)
            : null
    };

    private async Task<string?> OwnerAsync() =>
        await _tokens.GetAccessTokenAsync(HttpContext) == null
            ? null
            : HttpContext.Session.GetString(GoogleTokenService.UserIdKey);

    private IActionResult SessionExpired() =>
        Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
}
