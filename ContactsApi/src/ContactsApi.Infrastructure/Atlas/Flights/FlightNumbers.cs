/**
 * @fileoverview Flight numbers and call signs (AD-42 rule 3)
 * @module ContactsApi.Infrastructure.Atlas.Flights
 */
using System.Text.RegularExpressions;

namespace ContactsApi.Infrastructure.Atlas.Flights;

/// <summary>A flight number: an airline's designator and the number</summary>
/// <param name="Airline">Two characters (IATA) or three letters (ICAO)</param>
/// <param name="Number">Digits without leading zeros</param>
/// <param name="Suffix">An optional letter</param>
public record FlightNo(string Airline, string Number, string Suffix)
{
    /// <summary>Whether the airline is written as an ICAO designator</summary>
    public bool IsIcao => Airline.Length == 3;

    /// <inheritdoc />
    public override string ToString() => Airline + Number + Suffix;
}

/// <summary>Reads flight numbers and matches call signs to them</summary>
public static class FlightNumbers
{
    private static readonly Regex Iata = new(@"^([A-Z0-9]{2})[ \-]?0*([0-9]{1,4})([A-Z]?)$", RegexOptions.Compiled);
    private static readonly Regex Icao = new(@"^([A-Z]{3})[ \-]?0*([0-9]{1,4})([A-Z]?)$", RegexOptions.Compiled);
    private static readonly Regex Callsign = new(@"^([A-Z]{3})0*([0-9]{1,4})([A-Z]{0,2})$", RegexOptions.Compiled);

    /// <summary>A flight number as written on a ticket, or null when it is not one</summary>
    /// <param name="text">EY593, EY 593, ey0593, 6E 2045, ETD593</param>
    public static FlightNo? Parse(string? text)
    {
        var t = (text ?? string.Empty).Trim().ToUpperInvariant();
        var m = Icao.Match(t);
        if (!m.Success)
        {
            m = Iata.Match(t);
        }

        if (!m.Success || !m.Groups[1].Value.Any(char.IsLetter))
        {
            return null;
        }

        // "0" alone stays a number
        var number = m.Groups[2].Value.TrimStart('0');
        return new FlightNo(m.Groups[1].Value, number.Length == 0 ? "0" : number, m.Groups[3].Value);
    }

    /// <summary>Whether a call sign (ETD593, ETD5SM) is this flight number under one of the airline's ICAO codes</summary>
    /// <param name="callsign">The call sign as broadcast</param>
    /// <param name="flight">The flight number (IATA form)</param>
    /// <param name="icaoCodes">The ICAO codes of the flight number's airline</param>
    public static bool Matches(string? callsign, FlightNo flight, IEnumerable<string> icaoCodes)
    {
        var m = Callsign.Match((callsign ?? string.Empty).Trim().ToUpperInvariant());
        if (!m.Success)
        {
            return false;
        }

        var digits = m.Groups[2].Value.TrimStart('0');
        var codes = flight.IsIcao ? new[] { flight.Airline } : icaoCodes;
        return codes.Contains(m.Groups[1].Value, StringComparer.Ordinal)
            && (digits.Length == 0 ? "0" : digits) == flight.Number
            && (flight.Suffix.Length == 0 ? m.Groups[3].Value.Length == 0 : m.Groups[3].Value == flight.Suffix);
    }

    /// <summary>The digits of a call sign after its three-letter airline, when it has only digits (ETD593 → 593)</summary>
    public static string? CallsignDigits(string? callsign)
    {
        var m = Callsign.Match((callsign ?? string.Empty).Trim().ToUpperInvariant());
        return m.Success && m.Groups[3].Value.Length == 0 ? m.Groups[2].Value.TrimStart('0') : null;
    }

    /// <summary>The three-letter airline of a call sign (ETD5SM → ETD)</summary>
    public static string? CallsignAirline(string? callsign)
    {
        var m = Callsign.Match((callsign ?? string.Empty).Trim().ToUpperInvariant());
        return m.Success ? m.Groups[1].Value : null;
    }
}
