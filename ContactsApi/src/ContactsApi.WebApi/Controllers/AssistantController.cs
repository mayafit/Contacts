/**
 * @fileoverview Gemini proxy for the contacts assistant
 * @module ContactsApi.WebApi.Controllers
 */
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContactsApi.WebApi.Models.Responses;
using ContactsApi.WebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace ContactsApi.WebApi.Controllers;

/// <summary>Gemini settings (Gemini__ApiKey comes from Vault)</summary>
public class GeminiOptions
{
    /// <summary>API key</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Model for the assistant. A rolling alias by default: pinned Gemini versions
    /// are retired and then answer 404.
    /// </summary>
    public string Model { get; set; } = "gemini-flash-latest";

    /// <summary>Calls per day across all users: one shared key pays for everyone</summary>
    public int GlobalDailyLimit { get; set; } = 3000;
}

/// <summary>Web search request</summary>
public class AssistantSearchRequest
{
    /// <summary>What to look up</summary>
    public string Query { get; set; } = string.Empty;
}

/// <summary>Hebrew name words to spell (FR-77)</summary>
public class TransliterateRequest
{
    /// <summary>At most 20 words</summary>
    public List<string> Words { get; set; } = new();

    /// <summary>The whole name's words in order, for Google Translate's context (optional)</summary>
    public List<string>? Name { get; set; }
}

/// <summary>
/// Forwards the assistant's conversation to Gemini so the API key stays on the
/// server. The agent loop and its tools run in the browser; this only adds the
/// key and the model, and limits who can call it and how often.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AssistantController : ControllerBase
{
    /// <summary>Request fields passed through to generateContent; everything else is dropped</summary>
    private static readonly string[] PassThrough = { "systemInstruction", "contents" };

    /// <summary>Calls per user per day, on top of the per-minute limit</summary>
    private const int DailyLimit = 600;


    private readonly GeminiService _gemini;
    private readonly GoogleTokenService _tokens;
    private readonly NameTranslator _translator;
    private readonly ILogger<AssistantController> _logger;

    /// <summary>Creates the controller</summary>
    public AssistantController(
        GeminiService gemini,
        GoogleTokenService tokens,
        NameTranslator translator,
        ILogger<AssistantController> logger)
    {
        _gemini = gemini;
        _tokens = tokens;
        _translator = translator;
        _logger = logger;
    }

    /// <summary>Whether the assistant is available</summary>
    /// <returns>Enabled flag and model</returns>
    [HttpGet("status")]
    public IActionResult Status() =>
        Ok(ApiResponse<object>.SuccessResponse(new { enabled = _gemini.Enabled, model = _gemini.Model }));

    /// <summary>
    /// One generateContent call. Body: systemInstruction, contents, tools, toolConfig
    /// as in the Gemini API; the reply is Gemini's response unchanged.
    /// </summary>
    /// <param name="body">Request</param>
    /// <returns>Gemini response</returns>
    [HttpPost("generate")]
    [RequestSizeLimit(2_000_000)]
    public async Task<IActionResult> Generate([FromBody] JsonObject body)
    {
        var refusal = await CheckAsync("generate", 40);
        if (refusal != null)
        {
            return refusal;
        }

        var request = new JsonObject();
        foreach (var key in PassThrough)
        {
            if (body[key] is { } value)
            {
                request[key] = value.DeepClone();
            }
        }

        // Only the app's own function declarations: built-in tools (search, code
        // execution, URL fetch) would bypass the search limit and cost more
        var declarations = new JsonArray();
        foreach (var tool in body["tools"] as JsonArray ?? new JsonArray())
        {
            foreach (var fn in tool?["functionDeclarations"] as JsonArray ?? new JsonArray())
            {
                declarations.Add(fn?.DeepClone());
            }
        }

        if (declarations.Count > 0)
        {
            request["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
        }

        if (request["contents"] is not JsonArray { Count: > 0 })
        {
            return BadRequest(ApiResponse<object>.ErrorResponse("INVALID_REQUEST", "contents is required"));
        }

        return await CallAsync(request, response => response);
    }

    /// <summary>
    /// Looks something up on the web with Gemini's Google Search grounding.
    /// </summary>
    /// <param name="body">Query</param>
    /// <returns>Answer text and its sources</returns>
    [HttpPost("search")]
    public async Task<IActionResult> Search([FromBody] AssistantSearchRequest body)
    {
        var refusal = await CheckAsync("search", 10);
        if (refusal != null)
        {
            return refusal;
        }

        var query = (body.Query ?? string.Empty).Trim();
        if (query.Length is 0 or > 500)
        {
            return BadRequest(ApiResponse<object>.ErrorResponse("INVALID_REQUEST", "Give a query of up to 500 characters"));
        }

        var request = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject
                {
                    ["text"] = "Search the web and answer factually and briefly, with the facts found " +
                               "(company, role, location, public profile links). Say when nothing reliable was found.\n\n" + query
                })
            }),
            ["tools"] = new JsonArray(new JsonObject { ["google_search"] = new JsonObject() })
        };

        return await CallAsync(request, response =>
        {
            var candidate = response["candidates"]?[0];
            var text = string.Concat((candidate?["content"]?["parts"] as JsonArray ?? new JsonArray())
                .Select(p => p?["text"]?.GetValue<string>() ?? string.Empty));
            var sources = new JsonArray();
            foreach (var chunk in candidate?["groundingMetadata"]?["groundingChunks"] as JsonArray ?? new JsonArray())
            {
                if (chunk?["web"] is JsonObject web)
                {
                    sources.Add(new JsonObject { ["title"] = web["title"]?.DeepClone(), ["uri"] = web["uri"]?.DeepClone() });
                }
            }

            return new JsonObject { ["text"] = text, ["sources"] = sources };
        });
    }

    /// <summary>
    /// Latin spellings of Hebrew name words for the phonetic fill (AD-43 rule 4): Google
    /// Translate first, with the whole name for context; Gemini for the words it did not
    /// spell. Only spellings of Latin letters come back.
    /// </summary>
    /// <param name="body">The words, and the name they are in</param>
    /// <returns>Word → spelling, and word → its source (translate, gemini)</returns>
    [HttpPost("transliterate")]
    public async Task<IActionResult> Transliterate([FromBody] TransliterateRequest body)
    {
        var words = Transliteration.Check(body?.Words);
        // The name is only context: its odd words are dropped, not refused
        var name = Transliteration.CheckName(body?.Name);
        if (words == null)
        {
            return BadRequest(ApiResponse<object>.ErrorResponse("INVALID_INPUT",
                $"Up to {Transliteration.MaxWords} Hebrew name words, each up to 40 letters"));
        }

        if (await _tokens.GetAccessTokenAsync(HttpContext) == null)
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
        }

        var user = HttpContext.Session.GetString(GoogleTokenService.UserIdKey) ?? string.Empty;
        var spellings = new JsonObject();
        var sources = new JsonObject();
        // Why Google Translate gave nothing, said when nothing else spells the words
        string? translateWhy = null;
        // The same allowance as the Assistant's, in its own per-minute bucket; refused, the
        // words go to Gemini
        if (_translator.Enabled && (translateWhy = _gemini.TryConsume(user, "translate", 30, DailyLimit)) == null)
        {
            try
            {
                // Without a name, the words are their own context
                var spelled = await _translator.SpellAsync(name ?? words, HttpContext.RequestAborted);
                foreach (var w in words.Where(spelled.ContainsKey))
                {
                    spellings[w] = spelled[w];
                    sources[w] = "translate";
                }
            }
            catch (Exception ex) when (!HttpContext.RequestAborted.IsCancellationRequested)
            {
                // Refused, unreachable, too slow or an odd answer: Gemini is asked instead
                _logger.LogWarning(ex, "Google Translate did not spell a name");
                translateWhy = "Google Translate did not answer";
            }
        }

        var left = words.Where(w => !spellings.ContainsKey(w)).ToList();
        if (left.Count > 0 && _gemini.Enabled)
        {
            var refusal = _gemini.TryConsume(user, "phonetic", 30, DailyLimit);
            if (refusal == null)
            {
                var result = await _gemini.GenerateAsync(Transliteration.Request(left), HttpContext.RequestAborted);
                if (result.Ok)
                {
                    foreach (var (word, spelling) in Transliteration.Parse(result.Body, left))
                    {
                        spellings[word] = spelling;
                        sources[word] = "gemini";
                    }
                }
                else if (spellings.Count == 0)
                {
                    return StatusCode(result.Status, ApiResponse<object>.ErrorResponse(result.Code!, result.Error!));
                }
            }
            else if (spellings.Count == 0)
            {
                return StatusCode(StatusCodes.Status429TooManyRequests, ApiResponse<object>.ErrorResponse("RATE_LIMITED", refusal));
            }
        }
        else if (left.Count > 0 && spellings.Count == 0)
        {
            return !_translator.Enabled
                ? StatusCode(StatusCodes.Status503ServiceUnavailable,
                    ApiResponse<object>.ErrorResponse("ASSISTANT_DISABLED", "Neither Google Translate nor the assistant is configured."))
                : translateWhy != null
                    ? StatusCode(StatusCodes.Status502BadGateway, ApiResponse<object>.ErrorResponse("TRANSLATE_FAILED", translateWhy))
                    : Ok(ApiResponse<JsonNode>.SuccessResponse(new JsonObject
                    {
                        ["spellings"] = spellings,
                        ["sources"] = sources,
                        ["note"] = "Google Translate's answer did not match the name word for word"
                    }));
        }

        return Ok(ApiResponse<JsonNode>.SuccessResponse(new JsonObject { ["spellings"] = spellings, ["sources"] = sources }));
    }

    /// <summary>Signed in, configured and within the limits; otherwise the refusal</summary>
    private async Task<IActionResult?> CheckAsync(string kind, int perMinute)
    {
        if (await _tokens.GetAccessTokenAsync(HttpContext) == null)
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
        }

        if (!_gemini.Enabled)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResponse<object>.ErrorResponse("ASSISTANT_DISABLED", "The assistant is not configured (no Gemini key)."));
        }

        var user = HttpContext.Session.GetString(GoogleTokenService.UserIdKey) ?? string.Empty;
        var refusal = _gemini.TryConsume(user, kind, perMinute, DailyLimit);
        return refusal == null
            ? null
            : StatusCode(StatusCodes.Status429TooManyRequests, ApiResponse<object>.ErrorResponse("RATE_LIMITED", refusal));
    }

    /// <summary>Calls generateContent and shapes the reply</summary>
    private async Task<IActionResult> CallAsync(JsonObject request, Func<JsonNode, JsonNode> shape)
    {
        var result = await _gemini.GenerateAsync(request, HttpContext.RequestAborted);
        return result.Ok
            ? Ok(ApiResponse<JsonNode>.SuccessResponse(shape(result.Body!)))
            : StatusCode(result.Status, ApiResponse<object>.ErrorResponse(result.Code!, result.Error!));
    }
}
