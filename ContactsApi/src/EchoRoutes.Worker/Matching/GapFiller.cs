/**
 * @fileoverview Gap filling (AD-35 rule 7, routes.v3, story 12.4a): every stretch the
 * matcher left unbridged is routed with one Valhalla /route call between its two
 * anchors (longest first, at most N per drive), and the route is kept only when its
 * time fits the time that passed, its length fits the straight line and it does not
 * back-track near either end. A headed route rejected for its length or a back-track
 * is asked once more without headings. A rejected or failed fill leaves the straight gap.
 * @module EchoRoutes.Worker.Matching
 */
using System.Text.Json;
using System.Text.Json.Nodes;
using ContactsApi.Infrastructure.Atlas;
using EchoRoutes.Worker.Valhalla;

namespace EchoRoutes.Worker.Matching;

/// <summary>The gap-filling numbers (all configuration, RoutesOptions.GapFill*)</summary>
public record GapFillSettings(bool Enabled, double MaxM, double TimeFactor, double TimeSlackS, double MinTimeFactor, double ShortElapsedS,
    double LengthFactor, double LengthSlackM, double RoutedOnlyWeight, int MaxRoutedOnlyLegs)
{
    /// <summary>From the service's options</summary>
    public static GapFillSettings From(RoutesOptions o) => new(
        o.GapFill, o.GapFillMaxKm * 1000, o.GapFillTimeFactor, o.GapFillTimeSlackSeconds, o.GapFillMinTimeFactor,
        o.GapFillShortElapsedSeconds, o.GapFillLengthFactor, o.GapFillLengthSlackM, o.GapFillRoutedOnlyWeight, Math.Max(1, o.GapFillMaxRoutedOnlyLegs))
    {
        MaxCallsPerDrive = Math.Max(1, o.GapFillMaxCallsPerDrive),
        HeadingTolerance = o.GapFillHeadingTolerance,
        TimeoutsInARow = Math.Max(1, o.GapFillTimeoutsInARow),
        Retries = Math.Max(0, o.GapFillRetries)
    };

    /// <summary>Most /route calls of an anchored drive; the shorter stretches beyond it stay gaps</summary>
    public int MaxCallsPerDrive { get; init; } = 20;

    /// <summary>heading_tolerance sent with each anchor's heading (degrees); 0 sends no heading</summary>
    public double HeadingTolerance { get; init; } = 45;

    /// <summary>/route timeouts in a row (in one record) that count once for the breaker and stop the record's filling</summary>
    public int TimeoutsInARow { get; init; } = 3;

    /// <summary>Re-matches of a record whose filling timed out; after them its result is kept</summary>
    public int Retries { get; init; } = 3;

    /// <summary>The longest time a route may take for <paramref name="elapsedS"/></summary>
    public double MaxTimeS(double elapsedS) => elapsedS * TimeFactor + TimeSlackS;

    /// <summary>The length slack for a straight line: min(LengthSlackM, max(100, 2 x straight))</summary>
    public double SlackM(double straightM) => Math.Min(LengthSlackM, Math.Max(100, 2 * straightM));
}

/// <summary>An unbridged stretch of the skeleton, between two anchors (or recorded end points)</summary>
/// <param name="From">Its first vertex in the skeleton</param>
/// <param name="To">Its last vertex in the skeleton</param>
/// <param name="ElapsedS">Time between the two points' timestamps</param>
/// <param name="StraightM">Straight line between the two positions</param>
/// <param name="LineM">The skeleton's line between them (what a gap draws)</param>
/// <param name="Breaks">Engine breaks inside it (bridged when it is filled)</param>
/// <param name="RoutedOnly">Part of a drive with no anchor (routed point to point)</param>
/// <param name="TrackM">The recorded track between the two points</param>
/// <param name="StartHeading">The matched shape's bearing arriving at the first anchor (null: none)</param>
/// <param name="EndHeading">The matched shape's bearing leaving the last anchor (null: none)</param>
public record GapStretch(int From, int To, (double Lat, double Lon) Start, (double Lat, double Lon) End, double ElapsedS, double StraightM,
    double LineM, int Breaks, bool RoutedOnly, double TrackM = 0, double? StartHeading = null, double? EndHeading = null);

/// <summary>A /route answer: its shape (precision 6 decoded), length and time</summary>
public record RouteFill(List<(double Lat, double Lon)> Shape, double LengthM, double TimeS);

/// <summary>What became of a stretch</summary>
/// <param name="Result">filled, rejected_time, rejected_length, rejected_reversal, too_long, no_route, timeout, skipped (over the per-drive cap), deferred (the record's filling stopped early)</param>
public record FillOutcome(GapStretch Stretch, string Result, RouteFill? Route)
{
    /// <summary>The headed route was rejected and the stretch was asked once more without headings</summary>
    public bool Retried { get; init; }

    /// <summary>The route replaces the gap</summary>
    public bool Accepted => Result == GapFiller.Filled && Route != null;
}

/// <summary>Routes the stretches, one /route call each, longest first</summary>
public static class GapFiller
{
    public const string Filled = "filled";
    public const string RejectedTime = "rejected_time";
    public const string RejectedLength = "rejected_length";
    public const string RejectedReversal = "rejected_reversal";
    public const string TooLong = "too_long";
    public const string NoRoute = "no_route";
    public const string Timeout = "timeout";
    public const string Skipped = "skipped";
    public const string Deferred = "deferred";

    /// <summary>Two directions further apart than this are opposite (a back-track)</summary>
    public const double ReversalDeg = 150;

    /// <summary>How far from each end of a routed shape a back-track may start</summary>
    public const double ReversalWindowM = 300;

    /// <summary>A back-track comes back within this of where the shape went out (a U-turn on the same road or across a median)</summary>
    public const double BackTrackM = 50;

    /// <summary>The least distance along the shape between the way out and the way back</summary>
    public const double BackTrackLoopM = 150;

    /// <summary>The spacing of the samples a back-track is looked for at</summary>
    private const double SampleM = 10;

    /// <summary>The chord a direction along a routed shape is measured over (smooths roundabouts)</summary>
    private const double ChordM = 50;

    /// <summary>
    /// The /route request between the stretch's two positions; each anchor carries the
    /// matched shape's heading (within the tolerance) so the route leaves and arrives the
    /// way the trip went, not by a U-turn
    /// </summary>
    public static JsonObject BuildRequest(GapStretch s, string costing, double headingTolerance = 45) => new()
    {
        ["locations"] = new JsonArray(Location(s.Start, s.StartHeading, headingTolerance), Location(s.End, s.EndHeading, headingTolerance)),
        ["costing"] = costing,
        ["units"] = "kilometers",
        ["directions_type"] = "none"
    };

    private static JsonObject Location((double Lat, double Lon) p, double? heading, double tolerance)
    {
        var o = new JsonObject { ["lat"] = Math.Round(p.Lat, 6), ["lon"] = Math.Round(p.Lon, 6), ["type"] = "break" };
        if (heading.HasValue && tolerance > 0)
        {
            o["heading"] = Math.Round(heading.Value) % 360;
            o["heading_tolerance"] = Math.Round(tolerance);
        }

        return o;
    }

    /// <summary>Reads a /route answer; null when it has no leg with a shape</summary>
    public static RouteFill? Parse(JsonElement root)
    {
        if (!root.TryGetProperty("trip", out var trip) || !trip.TryGetProperty("legs", out var legs) || legs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var shape = new List<(double Lat, double Lon)>();
        foreach (var leg in legs.EnumerateArray())
        {
            if (leg.TryGetProperty("shape", out var s) && s.ValueKind == JsonValueKind.String)
            {
                shape.AddRange(ValhallaTrace.DecodePolyline(s.GetString()!, 6));
            }
        }

        if (shape.Count < 2)
        {
            return null;
        }

        double lengthM = 0;
        for (var i = 1; i < shape.Count; i++)
        {
            lengthM += Geo.Haversine(shape[i - 1].Lat, shape[i - 1].Lon, shape[i].Lat, shape[i].Lon);
        }

        double timeS = 0;
        if (trip.TryGetProperty("summary", out var summary))
        {
            if (summary.TryGetProperty("length", out var l) && l.TryGetDouble(out var km))
            {
                lengthM = km * 1000;
            }

            if (summary.TryGetProperty("time", out var t) && t.TryGetDouble(out var sec))
            {
                timeS = sec;
            }
        }

        return new RouteFill(shape, lengthM, timeS);
    }

    /// <summary>
    /// Whether a stretch is worth a call: not longer than the cap, and not faster in a
    /// straight line than the type's maximum speed allows in the time limit. Null when it is.
    /// </summary>
    public static string? PreCheck(GapStretch s, GapFillSettings g, double? maxSpeedKmh)
    {
        if (s.StraightM > g.MaxM)
        {
            return TooLong;
        }

        return maxSpeedKmh is > 0 && s.StraightM / (maxSpeedKmh.Value / 3.6) > g.MaxTimeS(s.ElapsedS) ? RejectedTime : null;
    }

    /// <summary>Whether a route fits the stretch (AD-35 rule 7)</summary>
    public static string Judge(GapStretch s, RouteFill r, GapFillSettings g)
    {
        if (r.LengthM > g.MaxM)
        {
            return TooLong;
        }

        // The slack scales with the straight line, so a short stretch cannot hide a loop
        if (r.LengthM > g.LengthFactor * s.StraightM + g.SlackM(s.StraightM))
        {
            return RejectedLength;
        }

        if (Reverses(r.Shape))
        {
            return RejectedReversal;
        }

        if (r.TimeS > g.MaxTimeS(s.ElapsedS))
        {
            return RejectedTime;
        }

        // Much faster than the time that passed: the trip went elsewhere (or stopped)
        if (s.ElapsedS > g.ShortElapsedS && r.TimeS < s.ElapsedS * g.MinTimeFactor)
        {
            return RejectedTime;
        }

        return Filled;
    }

    /// <summary>
    /// Whether a routed shape back-tracks near either end: within 300 m of an end it
    /// goes out, turns, and comes back the opposite way within 50 m of where it went
    /// out, at least 150 m further along (a U-turn on the same road or across a median,
    /// or a loop to reach an anchor from the wrong side). A change of bearing alone is
    /// not one (routes.v2 rejected on it): a motorway loop ramp turns 180 to 270 degrees
    /// but its way out and way back are far apart, a cloverleaf crosses its way in at a
    /// right angle, and a roundabout passed straight through is shorter than 150 m.
    /// Directions are 50 m chords.
    /// </summary>
    public static bool Reverses(IReadOnlyList<(double Lat, double Lon)> shape) =>
        BacksAtStart(shape) || BacksAtStart(shape.Reverse().ToList());

    private static bool BacksAtStart(IReadOnlyList<(double Lat, double Lon)> shape)
    {
        if (shape.Count < 3)
        {
            return false;
        }

        var cum = new double[shape.Count];
        for (var i = 1; i < shape.Count; i++)
        {
            cum[i] = cum[i - 1] + Geo.Haversine(shape[i - 1].Lat, shape[i - 1].Lon, shape[i].Lat, shape[i].Lon);
        }

        var length = cum[^1];
        if (length < BackTrackLoopM)
        {
            return false;
        }

        (double Lat, double Lon) At(double m)
        {
            var k = Array.BinarySearch(cum, m);
            if (k >= 0)
            {
                return shape[k];
            }

            k = Math.Clamp(~k, 1, shape.Count - 1);
            var span = cum[k] - cum[k - 1];
            var t = span > 0 ? Math.Clamp((m - cum[k - 1]) / span, 0, 1) : 0;
            return (shape[k - 1].Lat + t * (shape[k].Lat - shape[k - 1].Lat), shape[k - 1].Lon + t * (shape[k].Lon - shape[k - 1].Lon));
        }

        // Samples every 10 m up to twice the window (the way back of a turn within it)
        var end = Math.Min(length - ChordM, 2 * ReversalWindowM);
        var at = new List<(double Lat, double Lon)>();
        var dir = new List<double>();
        for (double m = 0; m <= end; m += SampleM)
        {
            at.Add(At(m));
            dir.Add(Bearing(At(m), At(m + ChordM)));
        }

        var loop = (int)Math.Ceiling(BackTrackLoopM / SampleM);
        for (var i = 0; i < at.Count && i * SampleM <= ReversalWindowM; i++)
        {
            for (var j = i + loop; j < at.Count; j++)
            {
                if (Turn(dir[i], dir[j]) > ReversalDeg && Geo.Haversine(at[i].Lat, at[i].Lon, at[j].Lat, at[j].Lon) <= BackTrackM)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The initial bearing from a to b, degrees clockwise from north</summary>
    public static double Bearing((double Lat, double Lon) a, (double Lat, double Lon) b)
    {
        var f1 = a.Lat * Math.PI / 180;
        var f2 = b.Lat * Math.PI / 180;
        var dl = (b.Lon - a.Lon) * Math.PI / 180;
        var y = Math.Sin(dl) * Math.Cos(f2);
        var x = Math.Cos(f1) * Math.Sin(f2) - Math.Sin(f1) * Math.Cos(f2) * Math.Cos(dl);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    /// <summary>The angle between two bearings, 0 to 180</summary>
    public static double Turn(double a, double b)
    {
        var d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    /// <summary>
    /// Routes the stretches, one call each, longest (by the skeleton's line) first. An
    /// anchored drive makes at most MaxCallsPerDrive calls; the shorter stretches beyond
    /// that stay gaps (skipped). A refusal (no route) leaves the gap. A timeout leaves
    /// that gap and filling goes on; TimeoutsInARow timeouts in a row (or any other engine
    /// error) count once for the breaker and stop the record's filling (the rest are
    /// deferred). When <paramref name="engineDown"/> says the engine is down, the exception
    /// is rethrown: the record waits for the engine. <paramref name="yield"/> is asked
    /// between calls: when interactive work waits, the rest are deferred too.
    /// Outcomes come back in the stretches' order.
    /// </summary>
    public static async Task<List<FillOutcome>> FillAsync(IReadOnlyList<GapStretch> stretches, string costing, GapFillSettings g,
        double? maxSpeedKmh, IValhallaClient engine, Func<EngineUnavailableException, bool> engineDown, Action success, CancellationToken ct,
        Func<bool>? yield = null)
    {
        var outcomes = new FillOutcome?[stretches.Count];
        var order = new List<int>();
        for (var i = 0; i < stretches.Count; i++)
        {
            if (PreCheck(stretches[i], g, maxSpeedKmh) is { } pre)
            {
                outcomes[i] = new FillOutcome(stretches[i], pre, null);
            }
            else
            {
                order.Add(i);
            }
        }

        // A drive routed point to point has its own cap (MaxRoutedOnlyLegs, in Stretches)
        var cap = stretches.Count > 0 && stretches[0].RoutedOnly ? int.MaxValue : g.MaxCallsPerDrive;
        var calls = 0;
        var inARow = 0;
        var stop = false;
        foreach (var i in order.OrderByDescending(i => stretches[i].LineM).ThenBy(i => i))
        {
            var s = stretches[i];
            if (!stop && calls > 0 && calls < cap && yield != null && yield())
            {
                stop = true;
            }

            if (stop || calls >= cap)
            {
                outcomes[i] = new FillOutcome(s, stop ? Deferred : Skipped, null);
                continue;
            }

            calls++;
            try
            {
                outcomes[i] = await RouteAsync(s, costing, g, g.HeadingTolerance, engine, ct);
                success();
                inARow = 0;

                // A heading can force a detour (an anchor placed on the other carriageway):
                // one more call without headings, within the per-drive cap
                var headed = g.HeadingTolerance > 0 && (s.StartHeading.HasValue || s.EndHeading.HasValue);
                if (headed && outcomes[i]!.Result is RejectedLength or RejectedReversal && calls < cap)
                {
                    calls++;
                    var retry = await RouteAsync(s, costing, g, 0, engine, ct);
                    success();
                    outcomes[i] = (retry.Accepted ? retry : outcomes[i]!) with { Retried = true };
                }
            }
            catch (EngineRejectedException)
            {
                success();
                inARow = 0;
                // A retry the engine refused keeps the headed route's verdict
                outcomes[i] = outcomes[i] is { } first ? first with { Retried = true } : new FillOutcome(s, NoRoute, null);
            }
            catch (EngineUnavailableException ex)
            {
                outcomes[i] = outcomes[i] is { } first ? first with { Retried = true } : new FillOutcome(s, Timeout, null);
                // A lone timeout is this stretch's (a hard route on a slow engine), not the engine's
                if (ex.Timeout && !ex.Unreachable && ++inARow < g.TimeoutsInARow)
                {
                    continue;
                }

                if (engineDown(ex))
                {
                    throw;
                }

                stop = true;
            }
        }

        return outcomes.Select(o => o!).ToList();
    }

    /// <summary>One /route call for a stretch (headings within <paramref name="headingTolerance"/>; 0: none) and its verdict</summary>
    private static async Task<FillOutcome> RouteAsync(GapStretch s, string costing, GapFillSettings g, double headingTolerance,
        IValhallaClient engine, CancellationToken ct)
    {
        using var doc = await engine.RouteAsync(BuildRequest(s, costing, headingTolerance), ct);
        var route = Parse(doc.RootElement);
        return route == null ? new FillOutcome(s, NoRoute, null) : new FillOutcome(s, Judge(s, route, g), route);
    }
}
