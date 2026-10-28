/**
 * @fileoverview The route-quality service's settings (section "Routes"; secrets come
 * from Vault as Routes__ServiceToken, Routes__BackendToken, Routes__ConnectionString)
 * @module EchoRoutes.Worker
 */
namespace EchoRoutes.Worker;

/// <summary>The service's settings</summary>
public class RoutesOptions
{
    /// <summary>The routing engine, on the pod's loopback only</summary>
    public string ValhallaUrl { get; set; } = "http://127.0.0.1:8002";

    /// <summary>The backend Service (its /internal/routes/*)</summary>
    public string BackendUrl { get; set; } = "http://contacts-backend:5000";

    /// <summary>Sent to the backend (accepted there only on /internal/routes/*)</summary>
    public string? ServiceToken { get; set; }

    /// <summary>The only token this service accepts on its /internal/v1/*</summary>
    public string? BackendToken { get; set; }

    /// <summary>Postgres as the echo_routes role (schema routes only)</summary>
    public string? ConnectionString { get; set; }

    /// <summary>The tiles image's graph.json (regions, extract dates, engine version)</summary>
    public string GraphJsonPath { get; set; } = "/tiles/graph.json";

    /// <summary>A directory of region outlines (*.poly), or one .poly file</summary>
    public string RegionsPath { get; set; } = "/tiles";

    /// <summary>Records per stage A batch (AD-36 rule 3)</summary>
    public int AssessBatch { get; set; } = 500;

    /// <summary>Pause after each batch, in milliseconds (AD-8)</summary>
    public int PauseMs { get; set; } = 400;

    /// <summary>How often each Owner's change feed is polled, in seconds (AD-33 rule 6)</summary>
    public int PollSeconds { get; set; } = 120;
}
