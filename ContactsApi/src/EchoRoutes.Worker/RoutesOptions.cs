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

    // ---- Stage B: the engine. Every engine-facing number is configuration (AD-36;
    // the Pi spike sets them), never a constant in the code.

    /// <summary>Engine requests in flight at once (AD-36 rule 3: one)</summary>
    public int EngineConcurrency { get; set; } = 1;

    /// <summary>Hard per-call timeout, in seconds; a timeout is an engine failure</summary>
    public int EngineTimeoutSeconds { get; set; } = 15;

    /// <summary>Valhalla breakage_distance in metres</summary>
    public double BreakageDistanceM { get; set; } = 5000;

    /// <summary>Valhalla search_radius in metres (never below the source's gps_accuracy)</summary>
    public double SearchRadiusM { get; set; } = 100;

    /// <summary>gps_accuracy for Google Timeline points, in metres</summary>
    public double GpsAccuracyTimelineM { get; set; } = 75;

    /// <summary>gps_accuracy for dense GPS sources (Sports Tracker, Google Fit, GPX…), in metres</summary>
    public double GpsAccuracyDenseM { get; set; } = 15;

    /// <summary>A gap longer than this (metres) is cut before the call and becomes a gap piece</summary>
    public double SplitGapM { get; set; } = 5000;

    /// <summary>A gap longer than this (minutes) is cut before the call and becomes a gap piece</summary>
    public double SplitGapMinutes { get; set; } = 15;

    /// <summary>Most points of one engine call (service_limits.trace.max_shape)</summary>
    public int MaxPointsPerCall { get; set; } = 2000;

    /// <summary>Most kilometres of one engine call (service_limits.trace.max_distance: 200 km in the tiles image's config, spike 12.1)</summary>
    public double MaxKmPerCall { get; set; } = 200;

    /// <summary>Records per stage B batch (AD-36 rule 3)</summary>
    public int MatchBatch { get; set; } = 25;

    /// <summary>Pause after each stage B batch, in milliseconds</summary>
    public int MatchPauseMs { get; set; } = 1000;

    /// <summary>Per-record backoff after an engine failure, in minutes, comma separated; one more failure is final</summary>
    public string BackoffMinutes { get; set; } = "1,10,60";

    /// <summary>Consecutive engine failures (errors or timeouts) that open the circuit breaker</summary>
    public int BreakerFailures { get; set; } = 5;

    /// <summary>How long an open breaker pauses all matching, in minutes</summary>
    public double BreakerPauseMinutes { get; set; } = 10;

    /// <summary>Most vertices of a stored full matched line, read only by the detail view (AD-34 rule 1); lists and the density map read the 400- and 80-vertex lines stored beside it</summary>
    public int MaxStoredVertices { get; set; } = 1000;

    // ---- Gap filling (AD-35 rule 7, story 12.4a): every stretch the matcher left
    // unbridged is routed with /route and kept when the route fits the elapsed time.

    /// <summary>Fill unbridged stretches with a /route call (Routes__GapFill)</summary>
    public bool GapFill { get; set; } = true;

    /// <summary>A stretch whose straight line or route is longer than this (km) stays a gap</summary>
    public double GapFillMaxKm { get; set; } = 60;

    /// <summary>A route is accepted when its time is at most elapsed × this + GapFillTimeSlackSeconds</summary>
    public double GapFillTimeFactor { get; set; } = 1.6;

    /// <summary>Seconds added to the elapsed-time limit of a route</summary>
    public double GapFillTimeSlackSeconds { get; set; } = 120;

    /// <summary>A route faster than elapsed × this is rejected (the trip went elsewhere), unless the elapsed time is short</summary>
    public double GapFillMinTimeFactor { get; set; } = 0.25;

    /// <summary>An elapsed time up to this (seconds) is short: the lower time limit does not apply</summary>
    public double GapFillShortElapsedSeconds { get; set; } = 180;

    /// <summary>A route is accepted when its length is at most the straight line × this + GapFillLengthSlackM</summary>
    public double GapFillLengthFactor { get; set; } = 2.5;

    /// <summary>Metres added to the length limit of a route</summary>
    public double GapFillLengthSlackM { get; set; } = 500;

    /// <summary>The anchor-quality weight of a drive built only from routes (no point matched)</summary>
    public double GapFillRoutedOnlyWeight { get; set; } = 0.6;

    /// <summary>Most /route legs of a drive built only from routes; more points than that stay failed</summary>
    public int GapFillMaxRoutedOnlyLegs { get; set; } = 40;

    /// <summary>A Google Timeline anchor counts as off-road only beyond this (metres; its gps_accuracy is 75 m)</summary>
    public double OffRoadTimelineM { get; set; } = 100;

    /// <summary>Days between orphan sweeps of an Owner (AD-34 rule 7: weekly)</summary>
    public double SweepDays { get; set; } = 7;

    /// <summary>The backoff steps, parsed</summary>
    public TimeSpan[] Backoff() =>
        BackoffMinutes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 0)
            .Where(m => m > 0).Select(TimeSpan.FromMinutes).ToArray();
}
