/**
 * @fileoverview The routing graph the tiles image carries (graph.json), and readiness:
 * ready once the engine answers and the graph version is known (AD-33 rule 4)
 * @module EchoRoutes.Worker
 */
using System.Text.Json;
using EchoRoutes.Worker.Valhalla;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace EchoRoutes.Worker;

/// <summary>What graph.json says about the routing graph</summary>
/// <param name="Version">regions@extract date+valhalla-version (AD-32 rule 5)</param>
public record GraphInfo(string Version, List<string> Regions, string? ValhallaVersion, string? ExtractDate);

/// <summary>Reads graph.json once it is there (the initContainer writes it before the service starts)</summary>
public class GraphInfoSource
{
    private readonly string _path;
    private GraphInfo? _info;

    /// <summary>Creates the source</summary>
    public GraphInfoSource(IOptions<RoutesOptions> options)
    {
        _path = options.Value.GraphJsonPath;
    }

    /// <summary>The graph, or null while graph.json cannot be read</summary>
    public GraphInfo? Current => _info ??= TryRead(_path);

    /// <summary>Parses graph.json; lenient about which fields the CI build wrote</summary>
    public static GraphInfo? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Parses the text of graph.json</summary>
    public static GraphInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var regions = root.TryGetProperty("regions", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?").ToList()
            : new List<string>();
        var valhalla = root.TryGetProperty("valhallaVersion", out var v) ? v.GetString()
            : root.TryGetProperty("valhalla_version", out var v2) ? v2.GetString() : null;
        var extract = root.TryGetProperty("extractDate", out var e) ? e.GetString()
            : root.TryGetProperty("extract_date", out var e2) ? e2.GetString() : null;
        var version = root.TryGetProperty("graphVersion", out var g) && g.GetString() is { Length: > 0 } given
            ? given
            : $"{(regions.Count > 0 ? string.Join(",", regions) : "unknown")}@{extract ?? "unknown"}+valhalla-{valhalla ?? "unknown"}";
        return new GraphInfo(version, regions, valhalla, extract);
    }
}

/// <summary>Ready once graph.json is read and Valhalla answers</summary>
public class RoutesReadyCheck : IHealthCheck
{
    private readonly GraphInfoSource _graph;
    private readonly IValhallaClient _valhalla;

    /// <summary>Creates the check</summary>
    public RoutesReadyCheck(GraphInfoSource graph, IValhallaClient valhalla)
    {
        _graph = graph;
        _valhalla = valhalla;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (_graph.Current == null)
        {
            return HealthCheckResult.Unhealthy("graph.json not read yet");
        }

        return await _valhalla.PingAsync(ct)
            ? HealthCheckResult.Healthy(_graph.Current.Version)
            : HealthCheckResult.Unhealthy("the routing engine does not answer");
    }
}
