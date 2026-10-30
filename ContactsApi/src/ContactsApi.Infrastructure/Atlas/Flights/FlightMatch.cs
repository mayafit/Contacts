/**
 * @fileoverview Which public flight a recorded flight was (AD-42 rule 7) and its path
 * (rule 8). Pure: the flights and lookups are passed in.
 * @module ContactsApi.Infrastructure.Atlas.Flights
 */
namespace ContactsApi.Infrastructure.Atlas.Flights;

/// <summary>A public flight matched to a flight number, and how</summary>
/// <param name="Flight">The flight</param>
/// <param name="By">callsign or route</param>
public record PublicMatch(OpenSkyFlight Flight, string By);

/// <summary>Matching public flights to recorded ones</summary>
public static class FlightMatch
{
    /// <summary>adsbdb's call sign lookups asked per flight at most</summary>
    public const int CallsignLookups = 5;

    /// <summary>Suggestions kept up to this score (minutes off at take-off plus at landing)</summary>
    public const int SuggestionMaxScore = 120;

    /// <summary>Suggestions shown</summary>
    public const int SuggestionCount = 3;

    /// <summary>
    /// The public flight of a flight number: by call sign; else by adsbdb's IATA number of
    /// the call sign; else the only flight to an arrival candidate in the time window
    /// </summary>
    /// <param name="number">The flight number</param>
    /// <param name="airlineIcaos">The ICAO codes of its airline</param>
    /// <param name="flights">OpenSky's departures</param>
    /// <param name="arrivals">ICAO codes of the arrival candidates</param>
    /// <param name="sched">Scheduled departure, when known</param>
    /// <param name="start">Recorded take-off</param>
    /// <param name="end">Recorded landing</param>
    /// <param name="callsignNumbers">adsbdb's IATA flight number per call sign (for <see cref="ToLookUp"/>)</param>
    public static PublicMatch? ByNumber(
        FlightNo number, IReadOnlyCollection<string> airlineIcaos, IReadOnlyList<OpenSkyFlight> flights, IReadOnlySet<string> arrivals,
        DateTime? sched, DateTime start, DateTime end, IReadOnlyDictionary<string, string?> callsignNumbers)
    {
        var anchor = sched ?? start;
        var byCallsign = flights.Where(f => FlightNumbers.Matches(f.Callsign, number, airlineIcaos))
            .OrderBy(f => Math.Abs((f.FirstSeen - anchor).TotalMinutes)).FirstOrDefault();
        if (byCallsign != null)
        {
            return new PublicMatch(byCallsign, "callsign");
        }

        var byLookup = flights.Where(f => callsignNumbers.TryGetValue(f.Callsign, out var n) && FlightNumbers.Parse(n)?.ToString() == number.ToString())
            .OrderBy(f => Math.Abs((f.FirstSeen - anchor).TotalMinutes)).FirstOrDefault();
        if (byLookup != null)
        {
            return new PublicMatch(byLookup, "callsign");
        }

        var fits = RouteFits(flights, arrivals, sched, start, end);
        return fits.Count == 1 ? new PublicMatch(fits[0], "route") : null;
    }

    /// <summary>The call signs worth asking adsbdb about: flights to an arrival candidate, nearest first</summary>
    public static List<string> ToLookUp(IReadOnlyList<OpenSkyFlight> flights, IReadOnlySet<string> arrivals, DateTime anchor) =>
        flights.Where(f => f.Callsign.Length > 0 && f.Arrival != null && arrivals.Contains(f.Arrival))
            .OrderBy(f => Math.Abs((f.FirstSeen - anchor).TotalMinutes))
            .Select(f => f.Callsign).Distinct().Take(CallsignLookups).ToList();

    /// <summary>
    /// The flights to an arrival candidate that fit the time: first seen from 30 minutes before
    /// to 3 hours after the scheduled departure; without one, within 60 minutes of the
    /// recorded take-off and last seen within 90 minutes of the recorded landing
    /// </summary>
    public static List<OpenSkyFlight> RouteFits(IReadOnlyList<OpenSkyFlight> flights, IReadOnlySet<string> arrivals, DateTime? sched, DateTime start, DateTime end) =>
        flights.Where(f => f.Arrival != null && arrivals.Contains(f.Arrival))
            .Where(f => sched != null
                ? f.FirstSeen >= sched.Value.AddMinutes(-30) && f.FirstSeen <= sched.Value.AddHours(3)
                : Math.Abs((f.FirstSeen - start).TotalMinutes) <= 60 && Math.Abs((f.LastSeen - end).TotalMinutes) <= 90)
            .OrderBy(f => Math.Abs((f.FirstSeen - (sched ?? start)).TotalMinutes))
            .ToList();

    /// <summary>
    /// Suggestions for a flight without a number: to an arrival candidate (or with no estimated
    /// arrival and last seen within 60 minutes of the landing), scored by minutes off at
    /// take-off plus at landing, up to 120, best 3
    /// </summary>
    public static List<(OpenSkyFlight Flight, int Score)> Suggestions(IReadOnlyList<OpenSkyFlight> flights, IReadOnlySet<string> arrivals, DateTime start, DateTime end) =>
        flights
            .Where(f => (f.Arrival != null && arrivals.Contains(f.Arrival))
                || (f.Arrival == null && Math.Abs((f.LastSeen - end).TotalMinutes) <= 60))
            .Select(f => (f, score: (int)Math.Round(Math.Abs((f.FirstSeen - start).TotalMinutes) + Math.Abs((f.LastSeen - end).TotalMinutes))))
            .Where(x => x.score <= SuggestionMaxScore)
            .OrderBy(x => x.score)
            .GroupBy(x => x.f.Icao24 + x.f.FirstSeen.Ticks).Select(g => g.First())
            .Take(SuggestionCount)
            .ToList();

    /// <summary>A track simplified (Douglas–Peucker on lat/lon) to at most <paramref name="max"/> points</summary>
    /// <param name="points">[unix, lat, lon, altitude]</param>
    /// <param name="tolerance">Degrees</param>
    /// <param name="max">Most points kept</param>
    public static List<double[]> Simplify(IReadOnlyList<double[]> points, double tolerance = 0.005, int max = 500)
    {
        if (points.Count <= 2)
        {
            return points.ToList();
        }

        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int a, int b)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            var (worst, at) = (0.0, -1);
            for (var i = a + 1; i < b; i++)
            {
                var d = Distance(points[i], points[a], points[b]);
                if (d > worst)
                {
                    (worst, at) = (d, i);
                }
            }

            if (at >= 0 && worst > tolerance)
            {
                keep[at] = true;
                stack.Push((a, at));
                stack.Push((at, b));
            }
        }

        var kept = points.Where((_, i) => keep[i]).ToList();
        if (kept.Count <= max)
        {
            return kept;
        }

        // Still too many: every n-th, ends kept
        var step = (double)(kept.Count - 1) / (max - 1);
        return Enumerable.Range(0, max).Select(i => kept[(int)Math.Round(i * step)]).ToList();
    }

    private static double Distance(double[] p, double[] a, double[] b)
    {
        double x = p[2], y = p[1], x1 = a[2], y1 = a[1], x2 = b[2], y2 = b[1];
        var dx = x2 - x1;
        var dy = y2 - y1;
        var len = dx * dx + dy * dy;
        var t = len == 0 ? 0 : Math.Clamp(((x - x1) * dx + (y - y1) * dy) / len, 0, 1);
        var (px, py) = (x1 + t * dx, y1 + t * dy);
        return Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
    }
}
