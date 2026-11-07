namespace ContactsApi.WebApi.Models.Responses;

/// <summary>
/// A system-health snapshot for the owner status bar (Epic 22). The shape mirrors
/// the JSON that the cluster-side <c>status-agent</c> writes to Redis
/// (<c>ops:health</c>); the backend only reads it through and never computes it,
/// so it holds no Kubernetes access. See
/// <c>docs/planning/epic-22-ops-status-and-ondemand-grafana.md</c>.
/// </summary>
public class SystemStatusResponse
{
    /// <summary>When the snapshot was produced (ISO-8601 UTC); used to detect staleness.</summary>
    public string? GeneratedAt { get; set; }

    /// <summary>Worst of the lamp states: green | yellow | red | unknown.</summary>
    public string Overall { get; set; } = "unknown";

    /// <summary>The individual lamps (nodes, data, app, vault, monitoring).</summary>
    public List<SystemLamp> Lamps { get; set; } = new();

    /// <summary>On-demand Grafana state for the "Open Grafana" button.</summary>
    public GrafanaState Grafana { get; set; } = new();
}

/// <summary>One traffic-light in the status bar.</summary>
public class SystemLamp
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    /// <summary>green | yellow | red | unknown</summary>
    public string State { get; set; } = "unknown";
    public string? Detail { get; set; }
}

/// <summary>The on-demand Grafana lifecycle state.</summary>
public class GrafanaState
{
    /// <summary>True while a wake request is live (the agent is keeping Grafana scaled up).</summary>
    public bool Awake { get; set; }

    /// <summary>True once the Grafana pod is actually ready to serve.</summary>
    public bool Ready { get; set; }

    /// <summary>Unix seconds until which Grafana stays awake, or null when asleep.</summary>
    public long? WakeUntil { get; set; }
}
