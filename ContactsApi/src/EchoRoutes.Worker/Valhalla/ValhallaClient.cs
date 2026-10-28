/**
 * @fileoverview The routing engine (Valhalla, 127.0.0.1:8002 in the same pod)
 * @module EchoRoutes.Worker.Valhalla
 */
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EchoRoutes.Worker.Valhalla;

/// <summary>The routing engine, behind an interface so tests run without it (and OSRM could stand in, AD-32)</summary>
public interface IValhallaClient
{
    /// <summary>Whether the engine answers (GET /status)</summary>
    Task<bool> PingAsync(CancellationToken ct);

    /// <summary>The engine's version, from /status; null when it does not say</summary>
    Task<string?> VersionAsync(CancellationToken ct);

    /// <summary>One trace_attributes call (stage B, story 12.4); one at a time, 10 s timeout</summary>
    Task<JsonDocument> TraceAttributesAsync(JsonObject request, CancellationToken ct);
}

/// <summary>Valhalla over HTTP</summary>
public class ValhallaClient : IValhallaClient
{
    /// <summary>The named HttpClient</summary>
    public const string HttpClientName = "valhalla";

    private readonly IHttpClientFactory _http;

    /// <summary>Creates the client</summary>
    public ValhallaClient(IHttpClientFactory http)
    {
        _http = http;
    }

    /// <inheritdoc />
    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.CreateClient(HttpClientName).GetAsync("status", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<string?> VersionAsync(CancellationToken ct)
    {
        try
        {
            var status = await _http.CreateClient(HttpClientName).GetFromJsonAsync<JsonElement>("status", ct);
            return status.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<JsonDocument> TraceAttributesAsync(JsonObject request, CancellationToken ct)
    {
        using var response = await _http.CreateClient(HttpClientName).PostAsJsonAsync("trace_attributes", request, ct);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }
}
