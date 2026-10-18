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
}

/// <summary>Web search request</summary>
public class AssistantSearchRequest
{
    /// <summary>What to look up</summary>
    public string Query { get; set; } = string.Empty;
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

    private static readonly ConcurrentDictionary<string, Queue<DateTime>> Calls = new();

    private readonly IHttpClientFactory _http;
    private readonly GeminiOptions _options;
    private readonly GoogleTokenService _tokens;
    private readonly ILogger<AssistantController> _logger;

    /// <summary>Creates the controller</summary>
    public AssistantController(
        IHttpClientFactory http,
        Microsoft.Extensions.Options.IOptions<GeminiOptions> options,
        GoogleTokenService tokens,
        ILogger<AssistantController> logger)
    {
        _http = http;
        _options = options.Value;
        _tokens = tokens;
        _logger = logger;
    }

    /// <summary>Whether the assistant is available</summary>
    /// <returns>Enabled flag and model</returns>
    [HttpGet("status")]
    public IActionResult Status() =>
        Ok(ApiResponse<object>.SuccessResponse(new { enabled = !string.IsNullOrEmpty(_options.ApiKey), model = _options.Model }));

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

    /// <summary>Signed in, configured and within the per-user rate limit; otherwise the refusal</summary>
    private async Task<IActionResult?> CheckAsync(string kind, int perMinute)
    {
        if (await _tokens.GetAccessTokenAsync(HttpContext) == null)
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
        }

        if (string.IsNullOrEmpty(_options.ApiKey))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResponse<object>.ErrorResponse("ASSISTANT_DISABLED", "The assistant is not configured (no Gemini key)."));
        }

        var user = HttpContext.Session.GetString(GoogleTokenService.UserIdKey) ?? string.Empty;
        var day = Calls.GetOrAdd($"day:{user}", _ => new Queue<DateTime>());
        lock (day)
        {
            while (day.Count > 0 && DateTime.UtcNow - day.Peek() > TimeSpan.FromDays(1))
            {
                day.Dequeue();
            }

            if (day.Count >= DailyLimit)
            {
                return StatusCode(StatusCodes.Status429TooManyRequests,
                    ApiResponse<object>.ErrorResponse("RATE_LIMITED", "The assistant's daily limit is reached. Try again tomorrow."));
            }

            day.Enqueue(DateTime.UtcNow);
        }

        var calls = Calls.GetOrAdd($"{kind}:{user}", _ => new Queue<DateTime>());
        lock (calls)
        {
            var now = DateTime.UtcNow;
            while (calls.Count > 0 && now - calls.Peek() > TimeSpan.FromMinutes(1))
            {
                calls.Dequeue();
            }

            if (calls.Count >= perMinute)
            {
                return StatusCode(StatusCodes.Status429TooManyRequests,
                    ApiResponse<object>.ErrorResponse("RATE_LIMITED", "Too many assistant requests. Wait a minute."));
            }

            calls.Enqueue(now);
        }

        return null;
    }

    /// <summary>Calls generateContent and shapes the reply</summary>
    private async Task<IActionResult> CallAsync(JsonObject request, Func<JsonNode, JsonNode> shape)
    {
        var client = _http.CreateClient("gemini");
        using var message = new HttpRequestMessage(
            HttpMethod.Post, $"v1beta/models/{Uri.EscapeDataString(_options.Model)}:generateContent")
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Headers.Add("x-goog-api-key", _options.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Gemini unreachable");
            return StatusCode(StatusCodes.Status502BadGateway,
                ApiResponse<object>.ErrorResponse("ASSISTANT_UNAVAILABLE", "Gemini did not answer. Try again."));
        }

        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            string detail;
            try
            {
                detail = JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() ?? text;
            }
            catch (JsonException)
            {
                detail = text;
            }

            _logger.LogWarning("Gemini returned {Status}: {Detail}", (int)response.StatusCode, detail);
            var (status, code) = (int)response.StatusCode switch
            {
                429 => (StatusCodes.Status429TooManyRequests, "RATE_LIMITED"),
                400 => (StatusCodes.Status400BadRequest, "ASSISTANT_REQUEST_INVALID"),
                _ => (StatusCodes.Status502BadGateway, "ASSISTANT_UNAVAILABLE")
            };
            return StatusCode(status, ApiResponse<object>.ErrorResponse(code, $"Gemini: {detail}"));
        }

        var node = JsonNode.Parse(text) ?? new JsonObject();
        return Ok(ApiResponse<JsonNode>.SuccessResponse(shape(node)));
    }
}
