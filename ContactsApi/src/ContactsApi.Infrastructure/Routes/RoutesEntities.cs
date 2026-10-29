/**
 * @fileoverview The routes schema (AD-34 rule 1): derived quality and matched routes,
 * the Owner's decisions and settings, and the service's jobs and cursors.
 * Every row carries OwnerId (AD-28).
 * @module ContactsApi.Infrastructure.Routes
 */
namespace ContactsApi.Infrastructure.Routes;

/// <summary>The stage A assessment of one source record (derived)</summary>
public class RouteQualityRow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = string.Empty;
    /// <summary>The source record's id</summary>
    public Guid ActivityId { get; set; }
    /// <summary>source|externalId, or activity|id</summary>
    public string SourceKey { get; set; } = string.Empty;
    /// <summary>The type used (the fused type at assessment)</summary>
    public string Type { get; set; } = "other";
    /// <summary>The source's own type text (in_bus…)</summary>
    public string? SourceType { get; set; }
    /// <summary>SHA-256 of the points' times and coordinates (rounded to 1e-6), hex</summary>
    public string InputHash { get; set; } = string.Empty;
    /// <summary>The record's UpdatedAt as assessed</summary>
    public DateTime SourceUpdatedAt { get; set; }
    /// <summary>The record's point count as assessed</summary>
    public int PointCount { get; set; }
    public double? TrackM { get; set; }
    /// <summary>JSON: the stage A metrics</summary>
    public string Metrics { get; set; } = "{}";
    /// <summary>0-100; null without a track</summary>
    public int? Score { get; set; }
    /// <summary>good, fair, poor; null without a track</summary>
    public string? Grade { get; set; }
    /// <summary>Comma separated: sparse, straight, implausible_speed, endpoint_far and the unmatchable reason</summary>
    public string Flags { get; set; } = string.Empty;
    public bool NeedsImprovement { get; set; }
    /// <summary>yes, no_track, type_never, outside_coverage, rail, unknown</summary>
    public string Matchable { get; set; } = "yes";
    public string RuleVersion { get; set; } = string.Empty;
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The Matched Route of one source record (derived; filled by stage B, story 12.4)</summary>
public class MatchedRouteRow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = string.Empty;
    public Guid ActivityId { get; set; }
    public string SourceKey { get; set; } = string.Empty;
    public string InputHash { get; set; } = string.Empty;
    /// <summary>The record's UpdatedAt, point count and fused type at match time (the read-time check)</summary>
    public DateTime SourceUpdatedAt { get; set; }
    public int SourcePointCount { get; set; }
    public string FusedType { get; set; } = string.Empty;
    /// <summary>auto, bus, bicycle, pedestrian</summary>
    public string? Costing { get; set; }
    public string Engine { get; set; } = "valhalla";
    public string? EngineVersion { get; set; }
    public string? GraphVersion { get; set; }
    public string RuleVersion { get; set; } = string.Empty;
    /// <summary>matched, low_confidence, failed</summary>
    public string Status { get; set; } = "failed";
    public double? Confidence { get; set; }
    /// <summary>Google encoded at precision 5: the full line (at most MaxStoredVertices), for the detail view</summary>
    public string? Polyline { get; set; }
    /// <summary>The line simplified to the recorded lines' budget (400 vertices), for lists and the map</summary>
    public string? ListPolyline { get; set; }
    /// <summary>The line simplified for the density map (80 vertices, ~30 m)</summary>
    public string? DensityPolyline { get; set; }
    /// <summary>JSON: [{fromM, toM, kind: observed / inferred / gap}]</summary>
    public string Pieces { get; set; } = "[]";
    public double? MatchedDistanceM { get; set; }
    /// <summary>JSON: the roads the line follows, in travel order: [{name, ref, m, fromM}] (AD-37); null without a line</summary>
    public string? Roads { get; set; }
    /// <summary>The roads' names alone, for the road filter (AD-37); null without a line</summary>
    public string[]? RoadNames { get; set; }
    /// <summary>How the line was made (AD-38): auto, similar (rebuilt from a similar drive), waypoints (through the Owner's points)</summary>
    public string Method { get; set; } = "auto";
    /// <summary>JSON: what a similar rebuild was based on (the basis drive, fit, counts); null otherwise</summary>
    public string? Basis { get; set; }
    /// <summary>The record's start time and its first and last point (the similar-drive pool, AD-38)</summary>
    public DateTime? StartedAt { get; set; }
    public double? StartLat { get; set; }
    public double? StartLon { get; set; }
    public double? EndLat { get; set; }
    public double? EndLon { get; set; }
    /// <summary>JSON: the metrics on the matched line</summary>
    public string? MetricsAfter { get; set; }
    public string? Reason { get; set; }
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The Owner's decision about a record's route, by its natural key (never a derived id)</summary>
public class RouteDecision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
    /// <summary>reject or accept</summary>
    public string Kind { get; set; } = "reject";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The points the Owner marked for a record's route (AD-38), by its natural key: a decision</summary>
public class RouteWaypoints
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
    /// <summary>JSON: [[lat, lon]…] in the order marked</summary>
    public string Points { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The Owner's route settings, as JSON (threshold, matched types, shown by default)</summary>
public class RouteSetting
{
    public string OwnerId { get; set; } = string.Empty;
    public string Json { get; set; } = "{}";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A queued piece of route work of one Owner</summary>
public class RouteJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = string.Empty;
    /// <summary>assess or improve</summary>
    public string Kind { get; set; } = RouteJobKinds.Assess;
    /// <summary>interactive or batch</summary>
    public string Priority { get; set; } = RouteJobPriorities.Batch;
    /// <summary>JSON: {activityIds} or {all: true}</summary>
    public string Scope { get; set; } = "{}";
    /// <summary>queued, running, done, failed</summary>
    public string State { get; set; } = RouteJobStates.Queued;
    public int Total { get; set; }
    public int Done { get; set; }
    /// <summary>JSON: counts per outcome</summary>
    public string Counts { get; set; } = "{}";
    public int Attempts { get; set; }
    /// <summary>Not run before then (retry backoff, or an open engine breaker)</summary>
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    /// <summary>A time-range job's paging cursor (durable, so the job resumes after a restart)</summary>
    public string? Cursor { get; set; }
    /// <summary>JSON: a rebuild's suggestion (AD-38); null for other jobs</summary>
    public string? Result { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

/// <summary>Where the service's pull of an Owner's change feed stands (durable across restarts)</summary>
public class OwnerCursor
{
    public string OwnerId { get; set; } = string.Empty;
    /// <summary>The feed cursor; null before the first (backfill) run</summary>
    public string? Cursor { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastRunAt { get; set; }
    public string? LastError { get; set; }
    /// <summary>When the last orphan sweep of this Owner finished</summary>
    public DateTime? SweptAt { get; set; }
    /// <summary>A sweep in progress: the last record id it checked</summary>
    public Guid? SweepAfter { get; set; }
}

/// <summary>Job kinds</summary>
public static class RouteJobKinds
{
    public const string Assess = "assess";
    public const string Improve = "improve";
    /// <summary>One record rebuilt on demand (AD-38): the result is a suggestion on the job</summary>
    public const string Rebuild = "rebuild";
}

/// <summary>Job priorities</summary>
public static class RouteJobPriorities
{
    public const string Interactive = "interactive";
    public const string Batch = "batch";
}

/// <summary>Job states</summary>
public static class RouteJobStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
}
