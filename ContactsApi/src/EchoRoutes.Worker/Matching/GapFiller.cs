/**
 * @fileoverview Gap filling (AD-35 rule 7, routes.v2, story 12.4a): every stretch the
 * matcher left unbridged is routed with one Valhalla /route call between its two
 * anchors, and the route is kept only when its time fits the time that passed and its
 * length fits the straight line. A rejected or failed fill leaves the straight gap.
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
        o.GapFillShortElapsedSeconds, o.GapFillLengthFactor, o.GapFillLengthSlackM, o.GapFillRoutedOnlyWeight, Math.Max(1, o.GapFillMaxRoutedOnlyLegs));

    /// <summary>The longest time a route may take for <paramref name="elapsedS"/></summary>
    public double MaxTimeS(double elapsedS) => elapsedS * TimeFactor + TimeSlackS;
}

/// <summary>An unbridged stretch of the skeleton, between two anchors (or recorded end points)</summary>
/// <param name="From">Its first vertex in the skeleton</param>
/// <param name="To">Its last vertex in the skeleton</param>
/// <param name="ElapsedS">Time between the two points' timestamps</param>
/// <param name="StraightM">Straight line between the two positions</param>
/// <param name="LineM">The skeleton's line between them (what a gap draws)</param>
/// <param name="Breaks">Engine breaks inside it (bridged when it is filled)</param>
/// <param name="RoutedOnly">Part of a drive with no anchor (routed point to point)</param>
public record GapStretch(int From, int To, (double Lat, double Lon) Start, (double Lat, double Lon) End, double ElapsedS, double StraightM,
    double LineM, int Breaks, bool RoutedOnly);

/// <summary>A /route answer: its shape (precision 6 decoded), length and time</summary>
public record RouteFill(List<(double Lat, double Lon)> Shape, double LengthM, double TimeS);

/// <summary>What became of a stretch</summary>
/// <param name="Result">filled, rejected_time, rejected_length, too_long, no_route, timeout, skipped</param>
public record FillOutcome(GapStretch Stretch, string Result, RouteFill? Route)
{
    /// <summary>The route replaces the gap</summary>
    public bool Accepted => Result == GapFiller.Filled && Route != null;
}

/// <summary>Routes the stretches, one /route call each, in order</summary>
public static class GapFiller
{
    public const string Filled = "filled";
    public const string RejectedTime = "rejected_time";
    public const string RejectedLength = "rejected_length";
    public const string TooLong = "too_long";
    public const string NoRoute = "no_route";
    public const string Timeout = "timeout";
    public const string Skipped = "skipped";

    /// <summary>The /route request between the stretch's two positions</summary>
    public static JsonObject BuildRequest(GapStretch s, string costing) => new()
    {
        ["locations"] = new JsonArray(
            new JsonObject { ["lat"] = Math.Round(s.Start.Lat, 6), ["lon"] = Math.Round(s.Start.Lon, 6), ["type"] = "break" },
            new JsonObject { ["lat"] = Math.Round(s.End.Lat, 6), ["lon"] = Math.Round(s.End.Lon, 6), ["type"] = "break" }),
        ["costing"] = costing,
        ["units"] = "kilometers",
        ["directions_type"] = "none"
    };

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

        if (r.LengthM > g.LengthFactor * s.StraightM + g.LengthSlackM)
        {
            return RejectedLength;
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
    /// Routes every stretch, one call each, in order. A refusal (no route) leaves the gap.
    /// A timeout or engine error leaves the gap and skips the record's remaining stretches
    /// (the record is still stored: it is not a per-record failure). When
    /// <paramref name="engineDown"/> says the engine is down (it also counts the failure
    /// for the breaker), the exception is rethrown: the record waits for the engine.
    /// </summary>
    public static async Task<List<FillOutcome>> FillAsync(IReadOnlyList<GapStretch> stretches, string costing, GapFillSettings g,
        double? maxSpeedKmh, IValhallaClient engine, Func<EngineUnavailableException, bool> engineDown, Action success, CancellationToken ct)
    {
        var outcomes = new List<FillOutcome>(stretches.Count);
        var stop = false;
        foreach (var s in stretches)
        {
            var pre = PreCheck(s, g, maxSpeedKmh);
            if (pre != null || stop)
            {
                outcomes.Add(new FillOutcome(s, pre ?? Skipped, null));
                continue;
            }

            try
            {
                using var doc = await engine.RouteAsync(BuildRequest(s, costing), ct);
                success();
                var route = Parse(doc.RootElement);
                outcomes.Add(route == null ? new FillOutcome(s, NoRoute, null) : new FillOutcome(s, Judge(s, route, g), route));
            }
            catch (EngineRejectedException)
            {
                success();
                outcomes.Add(new FillOutcome(s, NoRoute, null));
            }
            catch (EngineUnavailableException ex)
            {
                if (engineDown(ex))
                {
                    throw;
                }

                outcomes.Add(new FillOutcome(s, Timeout, null));
                stop = true;
            }
        }

        return outcomes;
    }
}
