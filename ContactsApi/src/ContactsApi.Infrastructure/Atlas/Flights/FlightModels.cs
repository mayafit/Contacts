/**
 * @fileoverview A flight's identity and its public facts (AD-42): the row per flight
 * activity and the JSON details it carries
 * @module ContactsApi.Infrastructure.Atlas.Flights
 */
namespace ContactsApi.Infrastructure.Atlas.Flights;

/// <summary>Which flight a flight activity was, and what public data says of it (AD-42 rule 1)</summary>
public class AtlasFlight
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = string.Empty;
    public Guid ActivityId { get; set; }

    /// <summary>IATA form without leading zeros (EY593)</summary>
    public string? FlightNumber { get; set; }

    /// <summary>The departure's local date (scheduled, else recorded)</summary>
    public DateOnly? FlightDate { get; set; }

    /// <summary>user, confirmed, mail, or null</summary>
    public string? Source { get; set; }

    /// <summary>pending, found, mail_only, suggested, not_found, not_a_flight, no_airports, error</summary>
    public string Status { get; set; } = FlightStatus.Pending;

    public string? Note { get; set; }

    /// <summary>searched, not_allowed, sign_in, failed, or null (not looked at)</summary>
    public string? MailState { get; set; }

    public int Attempts { get; set; }
    public DateTime? CheckedAt { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The airports, the mail, the airline, the aircraft, the times, the track (jsonb)</summary>
    public FlightDetails Details { get; set; } = new();
}

/// <summary>The statuses of a flight row</summary>
public static class FlightStatus
{
    public const string Pending = "pending";
    public const string Found = "found";
    public const string MailOnly = "mail_only";
    public const string Suggested = "suggested";
    public const string NotFound = "not_found";
    public const string NotAFlight = "not_a_flight";
    public const string NoAirports = "no_airports";
    public const string Error = "error";
}

/// <summary>Everything known of a flight beyond its identity (AD-42 rule 9)</summary>
public class FlightDetails
{
    public FlightAirport? Departure { get; set; }
    public FlightAirport? Arrival { get; set; }
    public List<string> DepartureCandidates { get; set; } = new();
    public List<string> ArrivalCandidates { get; set; } = new();
    public DateTime? ScheduledDeparture { get; set; }
    public DateTime? ScheduledArrival { get; set; }
    public DateTime? Takeoff { get; set; }
    public DateTime? Landing { get; set; }
    public FlightMailRef? Mail { get; set; }
    public FlightAirline? Airline { get; set; }
    public FlightAirline? OperatedBy { get; set; }
    public string? Callsign { get; set; }
    public string? Icao24 { get; set; }

    /// <summary>callsign or route (by route and time)</summary>
    public string? Match { get; set; }

    public FlightAircraft? Aircraft { get; set; }

    /// <summary>[unix seconds, lat, lon, altitude m]</summary>
    public List<double[]>? Track { get; set; }

    public string? TrackNote { get; set; }
    public List<FlightCandidate> Candidates { get; set; } = new();
    public FlightChecked Checked { get; set; } = new();
}

/// <summary>An airport as shown</summary>
public record FlightAirport(string Icao, string Iata, string Name, string City, string Country, string Tz, double Lat, double Lon);

/// <summary>The mail a flight number came from (never its text)</summary>
public record FlightMailRef(string Id, string Subject, string From, DateTime Date);

/// <summary>An airline</summary>
public record FlightAirline(string Name, string? Iata, string? Icao);

/// <summary>An aircraft</summary>
public record FlightAircraft(string? Type, string? IcaoType, string? Manufacturer, string? Registration, string? Owner);

/// <summary>A public flight that fits (a suggestion, or one of several that fit a number)</summary>
public record FlightCandidate(
    string Callsign, string Icao24, DateTime FirstSeen, DateTime LastSeen,
    string? Departure, string? Arrival, string? Airline, string? FlightNumber, int Score);

/// <summary>When each public source was last read</summary>
public class FlightChecked
{
    public DateTime? OpenSky { get; set; }
    public DateTime? Adsbdb { get; set; }
}
