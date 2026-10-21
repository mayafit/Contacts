/**
 * @fileoverview One place that calls Gemini and enforces the usage limits
 * @module ContactsApi.WebApi.Services
 */
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContactsApi.WebApi.Controllers;
using Microsoft.Extensions.Options;

namespace ContactsApi.WebApi.Services;

/// <summary>Outcome of a Gemini call</summary>
/// <param name="Ok">Gemini answered</param>
/// <param name="Status">HTTP status to report on failure</param>
/// <param name="Code">Error code on failure</param>
/// <param name="Error">Message on failure</param>
/// <param name="Body">Gemini's response</param>
public record GeminiResult(bool Ok, int Status, string? Code, string? Error, JsonNode? Body);

/// <summary>
/// Gemini generateContent with the server's key, plus the limits that protect
/// that key: per user per minute and per day, and a global daily cap, shared by
/// the assistant and contact enrichment.
/// </summary>
public class GeminiService
{
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _calls = new();
    private readonly IHttpClientFactory _http;
    private readonly GeminiOptions _options;
    private readonly ILogger<GeminiService> _logger;

    /// <summary>Creates the service</summary>
    public GeminiService(IHttpClientFactory http, IOptions<GeminiOptions> options, ILogger<GeminiService> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>A key is configured</summary>
    public bool Enabled => !string.IsNullOrEmpty(_options.ApiKey);

    /// <summary>Model in use</summary>
    public string Model => _options.Model;

    /// <summary>
    /// Takes one call from the user's and the global allowance.
    /// </summary>
    /// <param name="user">Google account id</param>
    /// <param name="kind">Limit bucket (generate, search, enrich)</param>
    /// <param name="perMinute">Calls per minute for this bucket</param>
    /// <param name="perDay">Calls per day for this user (all buckets)</param>
    /// <returns>Why the call is refused, or null when allowed</returns>
    public string? TryConsume(string user, string kind, int perMinute, int perDay)
    {
        if (!Take("day:*", TimeSpan.FromDays(1), _options.GlobalDailyLimit))
        {
            _logger.LogWarning("Gemini global daily limit reached ({Limit})", _options.GlobalDailyLimit);
            return "The assistant is busy today. Try again tomorrow.";
        }

        if (!Take($"day:{user}", TimeSpan.FromDays(1), perDay))
        {
            return "The daily limit is reached. Try again tomorrow.";
        }

        return Take($"{kind}:{user}", TimeSpan.FromMinutes(1), perMinute)
            ? null
            : "Too many requests. Wait a minute.";
    }

    private bool Take(string key, TimeSpan window, int limit)
    {
        var q = _calls.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (q)
        {
            var now = DateTime.UtcNow;
            while (q.Count > 0 && now - q.Peek() > window)
            {
                q.Dequeue();
            }

            if (q.Count >= limit)
            {
                return false;
            }

            q.Enqueue(now);
            return true;
        }
    }

    /// <summary>
    /// One generateContent call.
    /// </summary>
    /// <param name="request">Request body</param>
    /// <param name="ct">Cancellation</param>
    /// <returns>Result</returns>
    public async Task<GeminiResult> GenerateAsync(JsonObject request, CancellationToken ct)
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
            response = await client.SendAsync(message, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Gemini unreachable");
            return new GeminiResult(false, StatusCodes.Status502BadGateway, "ASSISTANT_UNAVAILABLE", "Gemini did not answer. Try again.", null);
        }

        var text = await response.Content.ReadAsStringAsync(ct);
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
            return new GeminiResult(false, status, code, $"Gemini: {detail}", null);
        }

        return new GeminiResult(true, 200, null, null, JsonNode.Parse(text) ?? new JsonObject());
    }

    /// <summary>The text of the first candidate</summary>
    /// <param name="response">Gemini response</param>
    /// <returns>Text, '' when none</returns>
    public static string TextOf(JsonNode? response) =>
        string.Concat((response?["candidates"]?[0]?["content"]?["parts"] as JsonArray ?? new JsonArray())
            .Where(p => p?["thought"]?.GetValue<bool>() != true)
            .Select(p => p?["text"]?.GetValue<string>() ?? string.Empty));
}
