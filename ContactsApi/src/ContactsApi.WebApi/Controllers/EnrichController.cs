/**
 * @fileoverview Contact details from email signatures (opt-in Gmail read access)
 * @module ContactsApi.WebApi.Controllers
 */
using System.Text.Json.Nodes;
using ContactsApi.Infrastructure.Activity;
using ContactsApi.WebApi.Models.Responses;
using ContactsApi.WebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace ContactsApi.WebApi.Controllers;

/// <summary>A contact to read signatures for</summary>
public class SignatureTarget
{
    /// <summary>Contact id</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>Contact name (tells the model whose signature to read)</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>The contact's email addresses</summary>
    public List<string> Emails { get; set; } = new();
}

/// <summary>Request: up to 10 contacts</summary>
public class SignatureRequest
{
    /// <summary>Contacts</summary>
    public List<SignatureTarget> Contacts { get; set; } = new();
}

/// <summary>
/// Reads the latest emails each contact sent, keeps the tail of their own text,
/// and has Gemini pull phone numbers, title, company and links out of the
/// signature. Bodies are processed in memory only; nothing is stored.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class EnrichController : ControllerBase
{
    /// <summary>Emails read per contact</summary>
    private const int EmailsPerContact = 3;

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "OBJECT",
        ["properties"] = new JsonObject
        {
            ["phones"] = new JsonObject
            {
                ["type"] = "ARRAY",
                ["items"] = new JsonObject
                {
                    ["type"] = "OBJECT",
                    ["properties"] = new JsonObject
                    {
                        ["value"] = new JsonObject { ["type"] = "STRING" },
                        ["type"] = new JsonObject { ["type"] = "STRING", ["description"] = "mobile, work, home, fax or other" }
                    },
                    ["required"] = new JsonArray("value")
                }
            },
            ["jobTitle"] = new JsonObject { ["type"] = "STRING" },
            ["organization"] = new JsonObject { ["type"] = "STRING" },
            ["department"] = new JsonObject { ["type"] = "STRING" },
            ["websites"] = new JsonObject { ["type"] = "ARRAY", ["items"] = new JsonObject { ["type"] = "STRING" } },
            ["linkedin"] = new JsonObject { ["type"] = "STRING" },
            ["address"] = new JsonObject { ["type"] = "STRING" }
        }
    };

    private const string Instructions =
        "You read email signatures. You get the last lines of recent emails written by one sender. " +
        "Extract the SENDER's own details from their signature: phone numbers (exactly as written, with their type), " +
        "job title, organization, department, websites, LinkedIn URL and postal address. " +
        "Use only the signature block of that sender: ignore legal disclaimers, other people's names and numbers, " +
        "quoted text, and phrases like 'Sent from my iPhone'. When emails disagree, prefer the most recent one. " +
        "Leave a field empty when it is not in a signature. Never guess.";

    private readonly GoogleTokenService _tokens;
    private readonly GeminiService _gemini;
    private readonly ILogger<EnrichController> _logger;

    /// <summary>Creates the controller</summary>
    public EnrichController(GoogleTokenService tokens, GeminiService gemini, ILogger<EnrichController> logger)
    {
        _tokens = tokens;
        _gemini = gemini;
        _logger = logger;
    }

    /// <summary>Details from the signatures of up to 10 contacts</summary>
    /// <param name="request">Contacts</param>
    /// <returns>Per contact: whether mail was found, and the details</returns>
    [HttpPost("signatures")]
    public async Task<IActionResult> Signatures([FromBody] SignatureRequest request)
    {
        if (await _tokens.GetAccessTokenAsync(HttpContext) == null)
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
        }

        var granted = (HttpContext.Session.GetString(ActivityScopes.SessionKey) ?? string.Empty).Split(' ');
        if (!granted.Contains(ActivityScopes.GmailRead))
        {
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.ErrorResponse(
                "SIGNATURES_NOT_CONNECTED", "Allow reading email content first."));
        }

        if (!_gemini.Enabled)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResponse<object>.ErrorResponse("ASSISTANT_DISABLED", "Gemini is not configured."));
        }

        var targets = (request.Contacts ?? new()).Where(c => c.Emails.Count > 0).Take(10).ToList();
        var user = HttpContext.Session.GetString(GoogleTokenService.UserIdKey) ?? string.Empty;
        var reader = new SignatureReader(async _ =>
            await _tokens.GetAccessTokenAsync(HttpContext) ?? throw new ActivityScanException("NOT_CONNECTED", "Session ended"));
        var results = new JsonObject();
        var rateLimited = false;
        foreach (var t in targets)
        {
            // Counted before reading Gmail, so contacts without mail use the allowance too
            if (_gemini.TryConsume(user, "enrich", 60, 600) != null)
            {
                rateLimited = true;
                break;
            }

            List<SignatureSample> samples;
            try
            {
                samples = await reader.RecentFromAsync(t.Emails, EmailsPerContact, HttpContext.RequestAborted);
            }
            catch (ActivityScanException ex) when (ex.Code is "NOT_CONNECTED" or "SCOPE_CONFLICT")
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.ErrorResponse(
                    ex.Code == "SCOPE_CONFLICT" ? ex.Code : "SIGNATURES_NOT_CONNECTED",
                    ex.Code == "SCOPE_CONFLICT" ? ex.Message : "Gmail did not allow reading email content. Allow it again."));
            }
            catch (ActivityScanException ex)
            {
                // One contact failing does not lose the others
                results[t.Id] = new JsonObject { ["found"] = false, ["emails"] = 0, ["error"] = ex.Message };
                continue;
            }

            if (samples.Count == 0)
            {
                results[t.Id] = new JsonObject { ["found"] = false, ["emails"] = 0 };
                continue;
            }

            var text = $"Sender: {t.Name} <{string.Join(", ", t.Emails)}>\n\n" + string.Join("\n\n", samples.Select((s, i) =>
                $"Email {i + 1} ({s.At:yyyy-MM-dd}), last lines:\n{s.Tail}"));
            var result = await _gemini.GenerateAsync(new JsonObject
            {
                ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = Instructions }) },
                ["contents"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = text })
                }),
                ["generationConfig"] = new JsonObject
                {
                    ["responseMimeType"] = "application/json",
                    ["responseSchema"] = Schema.DeepClone()
                }
            }, HttpContext.RequestAborted);
            if (!result.Ok)
            {
                results[t.Id] = new JsonObject { ["found"] = false, ["emails"] = samples.Count, ["error"] = result.Error };
                if (result.Status == StatusCodes.Status429TooManyRequests)
                {
                    rateLimited = true;
                    break;
                }

                continue;
            }

            JsonNode? details;
            try
            {
                details = JsonNode.Parse(GeminiService.TextOf(result.Body));
            }
            catch (System.Text.Json.JsonException)
            {
                _logger.LogWarning("Signature extraction returned no JSON");
                details = null;
            }

            results[t.Id] = new JsonObject
            {
                ["found"] = details != null,
                ["emails"] = samples.Count,
                ["lastEmailAt"] = samples[0].At.ToString("o"),
                ["details"] = details
            };
        }

        // Counts only: which contacts and what was found stay out of the logs
        _logger.LogInformation("Signatures read for {Count} contacts", targets.Count);
        return Ok(ApiResponse<JsonNode>.SuccessResponse(new JsonObject { ["results"] = results, ["rateLimited"] = rateLimited }));
    }
}
