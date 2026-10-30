/**
 * @fileoverview The airports with scheduled flights, bundled with the backend (AD-42 rule 2)
 * @module ContactsApi.Infrastructure.Atlas.Flights
 */
using System.Globalization;
using System.Reflection;

namespace ContactsApi.Infrastructure.Atlas.Flights;

/// <summary>An airport with scheduled flights</summary>
public record Airport(string Icao, string Iata, string Name, string City, string Country, double Lat, double Lon, string Tz)
{
    /// <summary>As shown with a flight</summary>
    public FlightAirport ToFlight() => new(Icao, Iata, Name, City, Country, Tz, Lat, Lon);

    /// <summary>The airport's time zone (UTC when the system does not know it)</summary>
    public TimeZoneInfo Zone => Airports.ZoneOf(Tz);
}

/// <summary>The bundled airports: by code, and the nearest to a point</summary>
public static class Airports
{
    /// <summary>How far from a flight's end an airport is looked for (km)</summary>
    public const double NearKm = 50;

    /// <summary>Airports kept per end</summary>
    public const int NearCount = 3;

    private static readonly Lazy<List<Airport>> All = new(Load);
    private static readonly Lazy<Dictionary<string, Airport>> ByIataCode = new(() => All.Value.ToDictionary(a => a.Iata, StringComparer.Ordinal));
    private static readonly Lazy<Dictionary<string, Airport>> ByIcaoCode = new(() => All.Value.ToDictionary(a => a.Icao, StringComparer.Ordinal));

    /// <summary>Every airport</summary>
    public static IReadOnlyList<Airport> List => All.Value;

    /// <summary>An airport by its IATA code (TLV)</summary>
    public static Airport? ByIata(string? code) =>
        code != null && ByIataCode.Value.TryGetValue(code.ToUpperInvariant(), out var a) ? a : null;

    /// <summary>An airport by its ICAO code (LLBG)</summary>
    public static Airport? ByIcao(string? code) =>
        code != null && ByIcaoCode.Value.TryGetValue(code.Trim().ToUpperInvariant(), out var a) ? a : null;

    /// <summary>Whether a word is an airport's IATA code</summary>
    public static bool IsIata(string word) => ByIataCode.Value.ContainsKey(word);

    /// <summary>The airports within 50 km of a point, nearest first, at most 3</summary>
    public static List<Airport> Near(double lat, double lon) =>
        All.Value
            .Select(a => (a, km: Km(lat, lon, a.Lat, a.Lon)))
            .Where(x => x.km <= NearKm)
            .OrderBy(x => x.km)
            .Take(NearCount)
            .Select(x => x.a)
            .ToList();

    /// <summary>Great-circle distance (km)</summary>
    public static double Km(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = Math.PI / 180;
        var a = Math.Pow(Math.Sin((lat2 - lat1) * r / 2), 2)
            + Math.Cos(lat1 * r) * Math.Cos(lat2 * r) * Math.Pow(Math.Sin((lon2 - lon1) * r / 2), 2);
        return 12742 * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>An IANA time zone (UTC when unknown)</summary>
    public static TimeZoneInfo ZoneOf(string tz)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(tz);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static List<Airport> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ContactsApi.Infrastructure.Atlas.Flights.airports.csv")
            ?? throw new InvalidOperationException("airports.csv is not embedded");
        using var reader = new StreamReader(stream);
        var list = new List<Airport>();
        var header = true;
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('#') || line.Length == 0)
            {
                continue;
            }

            if (header)
            {
                header = false;
                continue;
            }

            var f = Csv(line);
            if (f.Count == 8)
            {
                list.Add(new Airport(f[0], f[1], f[2], f[3], f[4],
                    double.Parse(f[5], CultureInfo.InvariantCulture), double.Parse(f[6], CultureInfo.InvariantCulture), f[7]));
            }
        }

        return list;
    }

    /// <summary>One CSV line (fields may be quoted, with "" for a quote)</summary>
    internal static List<string> Csv(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        fields.Add(sb.ToString());
        return fields;
    }
}
