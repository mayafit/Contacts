using ContactsApi.Infrastructure.Atlas.Flights;

namespace ContactsApi.Tests;

/// <summary>Flight numbers, airports, mail and public matching (AD-42; pure)</summary>
public class FlightTests
{
    private static Airport A(string iata) => Airports.ByIata(iata)!;

    private static FlightContext Ctx(string dep, string arr, DateTime start) => new(new[] { A(dep) }, new[] { A(arr) }, start);

    private static DateTime Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static FlightMailMessage Mail(string id, string subject, string? html = null, string? plain = null, DateTime? date = null) =>
        new(id, subject, "noreply@airline.example", date ?? Utc(2026, 2, 1, 9, 0), html, plain);

    [Theory]
    [InlineData("EY593", "EY593")]
    [InlineData(" ey 0593 ", "EY593")]
    [InlineData("LY0001", "LY1")]
    [InlineData("6E-2045", "6E2045")]
    [InlineData("ETD593", "ETD593")]
    [InlineData("BA123A", "BA123A")]
    public void Flight_numbers_are_read_as_on_a_ticket(string text, string expected) =>
        Assert.Equal(expected, FlightNumbers.Parse(text)!.ToString());

    [Theory]
    [InlineData("12 345")]
    [InlineData("EY")]
    [InlineData("EY12345")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_text_is_not_a_flight_number(string? text) => Assert.Null(FlightNumbers.Parse(text));

    [Fact]
    public void A_call_sign_matches_a_flight_number_under_the_airline_s_ICAO_code()
    {
        var ey593 = FlightNumbers.Parse("EY593")!;
        Assert.True(FlightNumbers.Matches("ETD593 ", ey593, new[] { "ETD" }));
        Assert.True(FlightNumbers.Matches("ETD0593", ey593, new[] { "ETD" }));
        Assert.False(FlightNumbers.Matches("ETD5SM", ey593, new[] { "ETD" }));
        Assert.False(FlightNumbers.Matches("ETD593", ey593, new[] { "ELY" }));
        Assert.False(FlightNumbers.Matches("ETD5930", ey593, new[] { "ETD" }));
        // An ICAO-form number needs no airline lookup
        Assert.True(FlightNumbers.Matches("ETD593", FlightNumbers.Parse("ETD593")!, Array.Empty<string>()));
        Assert.Equal(("ETD", (string?)null, "593"), (FlightNumbers.CallsignAirline("ETD5SM"), FlightNumbers.CallsignDigits("ETD5SM"), FlightNumbers.CallsignDigits("ETD0593")));
    }

    [Fact]
    public void Airports_are_bundled_with_their_time_zones_and_found_near_a_point()
    {
        Assert.InRange(Airports.List.Count, 3500, 4000);
        var tlv = A("TLV");
        Assert.Equal(("LLBG", "Asia/Jerusalem"), (tlv.Icao, tlv.Tz));
        Assert.Equal(tlv, Airports.ByIcao("llbg "));
        Assert.Equal(TimeSpan.FromHours(4), A("AUH").Zone.BaseUtcOffset);
        // 30 km out: still Ben Gurion first
        Assert.Equal("TLV", Airports.Near(32.2, 34.95)[0].Iata);
        Assert.Empty(Airports.Near(0, -30));
        Assert.True(Airports.Near(13.69, 100.75).Count <= Airports.NearCount);
    }

    // A booking confirmation laid out as a table, as airlines send them (invented)
    private const string Booking = """
        <html><head><style>td{color:red}</style></head><body><table>
        <tr><td>Trip details</td></tr>
        <tr><td>Etihad Airways EY608</td></tr>
        <tr><td>TLV</td><td>AUH</td></tr>
        <tr><td>Tel Aviv Yafo</td><td>03h 10m &bull; Non Stop</td><td>Abu Dhabi</td></tr>
        <tr><td>AIRBUS A321NEO</td></tr>
        <tr><td>15:15</td><td>07 Mar 2026</td><td>Ben Gurion Intl Terminal 3</td><td>19:25</td><td>07 Mar 2026</td><td>Zayed International Airport</td></tr>
        <tr><td>Etihad Airways EY402</td></tr>
        <tr><td>AUH</td><td>BKK</td></tr>
        <tr><td>AIRBUS A350-1000</td><td>B787</td></tr>
        <tr><td>21:10</td><td>07 Mar 2026</td><td>06:45</td><td>08 Mar 2026</td></tr>
        <tr><td>Seat 22A</td><td>Gate C6</td><td>Booking reference: 8WXYZQ</td></tr>
        </table></body></html>
        """;

    [Fact]
    public void A_booking_names_each_flight_with_its_route_date_and_times()
    {
        var segments = FlightMail.Parse(Mail("m1", "Your booking confirmation", Booking));

        // TLV 15:15 (UTC+2 in March) is 13:15 UTC; the phone recorded the take-off at 13:40
        var first = FlightMail.Match(segments, Ctx("TLV", "AUH", Utc(2026, 3, 7, 13, 40)))!;
        Assert.Equal(("EY608", "TLV", "AUH", 25), (first.Number.ToString(), first.Departure.Iata, first.Arrival.Iata, first.Score));
        Assert.Equal(Utc(2026, 3, 7, 13, 15), first.ScheduledDeparture);
        Assert.Equal(Utc(2026, 3, 7, 15, 25), first.ScheduledArrival);
        Assert.Equal(new FlightMailRef("m1", "Your booking confirmation", "noreply@airline.example", Utc(2026, 2, 1, 9, 0)), first.Mail);

        // The aircraft model between two flights does not split the second one's text
        var second = FlightMail.Match(segments, Ctx("AUH", "BKK", Utc(2026, 3, 7, 17, 30)))!;
        Assert.Equal("EY402", second.Number.ToString());
        Assert.Equal(Utc(2026, 3, 7, 17, 10), second.ScheduledDeparture);
        Assert.Equal(Utc(2026, 3, 7, 23, 45), second.ScheduledArrival);

        // Not this route, or not this day
        Assert.Null(FlightMail.Match(segments, Ctx("BKK", "CNX", Utc(2026, 3, 8, 3, 0))));
        Assert.Null(FlightMail.Match(segments, Ctx("TLV", "AUH", Utc(2026, 3, 9, 13, 40))));
        // A scheduled time over 4 hours before the take-off is another flight
        Assert.Null(FlightMail.Match(segments, Ctx("TLV", "AUH", Utc(2026, 3, 7, 17, 30))));
    }

    [Fact]
    public void A_schema_org_reservation_is_read_first()
    {
        var html = """
            <script type="application/ld+json">{"@context":"http://schema.org","@graph":[{"@type":"FlightReservation",
              "reservationFor":{"@type":"Flight","flightNumber":"315","airline":{"iataCode":"LY"},
              "departureAirport":{"iataCode":"TLV"},"arrivalAirport":{"iataCode":"LHR"},
              "departureTime":"2026-03-10T08:00:00+02:00","arrivalTime":"2026-03-10T11:40:00+00:00"}}]}</script>
            <p>Your trip</p>
            """;
        var m = FlightMail.Match(FlightMail.Parse(Mail("m2", "Itinerary", html)), Ctx("TLV", "LHR", Utc(2026, 3, 10, 6, 20)))!;
        Assert.Equal(("LY315", 20), (m.Number.ToString(), m.Score));
        Assert.Equal((Utc(2026, 3, 10, 6, 0), Utc(2026, 3, 10, 11, 40)), (m.ScheduledDeparture, m.ScheduledArrival));
    }

    [Fact]
    public void A_mail_that_writes_each_route_before_its_number_gives_each_flight_its_own()
    {
        var plain = "Your trip\nDeparture 7 Aug 2026 10:05 TLV, Arrival 15:20 AUH, Flight EY598\n"
            + "Departure 14 Aug 2026 09:00 AUH, Arrival 11:30 TLV, Flight EY599\n";
        var segments = FlightMail.Parse(Mail("m7", "Itinerary", plain: plain));
        // TLV 10:05 (UTC+3 in August) is 07:05 UTC; AUH 09:00 (UTC+4) is 05:00 UTC
        Assert.Equal("EY598", FlightMail.Match(segments, Ctx("TLV", "AUH", Utc(2026, 8, 7, 7, 25)))!.Number.ToString());
        Assert.Equal("EY599", FlightMail.Match(segments, Ctx("AUH", "TLV", Utc(2026, 8, 14, 5, 20)))!.Number.ToString());
    }

    [Fact]
    public void A_reservation_time_with_an_offset_or_Z_is_UTC()
    {
        var html = """
            <script type="application/ld+json">{"@type":"FlightReservation","reservationFor":{"flightNumber":"LY5",
              "airline":{"iataCode":"LY"},"departureAirport":{"iataCode":"TLV"},"arrivalAirport":{"iataCode":"AUH"},
              "departureTime":"2026-08-07T07:05:00Z","arrivalTime":"2026-08-07T10:15:00Z"}}</script>
            """;
        var m = FlightMail.Match(FlightMail.Parse(Mail("m8", "x", html)), Ctx("TLV", "AUH", Utc(2026, 8, 7, 7, 20)))!;
        Assert.Equal((Utc(2026, 8, 7, 7, 5), Utc(2026, 8, 7, 10, 15)), (m.ScheduledDeparture!.Value, m.ScheduledArrival!.Value));
    }

    [Fact]
    public void The_route_may_come_before_the_number_and_dates_may_be_numeric_or_Hebrew()
    {
        var plain = "טיסה תל אביב - אתונה TLV ATH\nטיסה LY 543 בתאריך 05/03/2026 בשעה 10:30";
        var m = FlightMail.Match(FlightMail.Parse(Mail("m3", "אישור הזמנה", plain: plain)), Ctx("TLV", "ATH", Utc(2026, 3, 5, 8, 50)))!;
        Assert.Equal(("LY543", (DateTime?)Utc(2026, 3, 5, 8, 30)), (m.Number.ToString(), m.ScheduledDeparture));

        var hebrew = "LY 543 TLV ATH 5 במרץ 2026 10:30";
        Assert.Equal("LY543", FlightMail.Match(FlightMail.Parse(Mail("m4", "x", plain: hebrew)), Ctx("TLV", "ATH", Utc(2026, 3, 5, 8, 50)))!.Number.ToString());

        // Month first when day first does not fit (3 May is not the flight's day)
        var us = "Flight UA 91 TLV EWR 03/05/2026 11:00 PM";
        var m2 = FlightMail.Match(FlightMail.Parse(Mail("m5", "x", plain: us)), Ctx("TLV", "EWR", Utc(2026, 3, 5, 21, 30)))!;
        Assert.Equal(("UA91", (DateTime?)Utc(2026, 3, 5, 21, 0)), (m2.Number.ToString(), m2.ScheduledDeparture));

        // Without a time: by the day alone
        var dateOnly = "Boarding pass LY 543 TLV-ATH 05MAR";
        var m3 = FlightMail.Match(FlightMail.Parse(Mail("m6", "x", plain: dateOnly)), Ctx("TLV", "ATH", Utc(2026, 3, 5, 8, 50)))!;
        Assert.Equal((FlightMail.DateOnlyScore, (DateTime?)null), (m3.Score, m3.ScheduledDeparture));
    }

    [Fact]
    public void Between_equal_matches_the_newest_mail_wins_and_nearer_times_win_over_it()
    {
        string Text(string no, string time) => $"{no} TLV AUH 07 Mar 2026 {time}";
        var older = FlightMail.Parse(Mail("old", "Booking", plain: Text("EY608", "15:15"), date: Utc(2026, 1, 1, 0, 0)));
        var newer = FlightMail.Parse(Mail("new", "Schedule change", plain: Text("EY610", "15:15"), date: Utc(2026, 2, 1, 0, 0)));
        Assert.Equal("EY610", FlightMail.Match(older.Concat(newer), Ctx("TLV", "AUH", Utc(2026, 3, 7, 13, 40)))!.Number.ToString());

        var nearer = FlightMail.Parse(Mail("near", "Booking", plain: Text("EY612", "15:35"), date: Utc(2025, 12, 1, 0, 0)));
        Assert.Equal("EY612", FlightMail.Match(older.Concat(newer).Concat(nearer), Ctx("TLV", "AUH", Utc(2026, 3, 7, 13, 40)))!.Number.ToString());
    }

    private static OpenSkyFlight F(string cs, string? arr, DateTime first, DateTime last, string icao24 = "896123") =>
        new(icao24, cs, first, last, "OMAA", arr);

    [Fact]
    public void A_public_flight_is_matched_by_call_sign_by_adsbdb_s_number_or_alone_on_its_route()
    {
        var no = FlightNumbers.Parse("EY593")!;
        var sched = Utc(2026, 8, 27, 4, 50);
        var arrivals = new HashSet<string> { "LLBG" };
        var none = new Dictionary<string, string?>();
        var a = F("ETD5SM", "LLBG", Utc(2026, 8, 27, 5, 12), Utc(2026, 8, 27, 7, 31), "a");
        var b = F("ETD607", "LLBG", Utc(2026, 8, 27, 5, 30), Utc(2026, 8, 27, 7, 50), "b");
        var c = F("ETD593", "LLBG", Utc(2026, 8, 27, 5, 40), Utc(2026, 8, 27, 7, 55), "c");

        Assert.Equal(("c", "callsign"), Pick(FlightMatch.ByNumber(no, new[] { "ETD" }, new[] { a, b, c }, arrivals, sched, sched, sched, none)));
        // ETD5SM is EY593 by adsbdb
        Assert.Equal(new[] { "ETD5SM", "ETD607" }, FlightMatch.ToLookUp(new[] { a, b }, arrivals, sched));
        Assert.Equal(("a", "callsign"), Pick(FlightMatch.ByNumber(no, new[] { "ETD" }, new[] { a, b }, arrivals, sched, sched, sched,
            new Dictionary<string, string?> { ["ETD5SM"] = "EY593", ["ETD607"] = "EY607" })));
        // Two fit the route and time: none chosen; one alone: by route
        Assert.Null(FlightMatch.ByNumber(no, new[] { "ETD" }, new[] { a, b }, arrivals, sched, sched, sched, none));
        Assert.Equal(("a", "route"), Pick(FlightMatch.ByNumber(no, new[] { "ETD" }, new[] { a, F("ETD9", "OMDB", a.FirstSeen, a.LastSeen) }, arrivals, sched, sched, sched, none)));
        // A long flight whose landing no receiver saw: the number's airline's only flight in the window
        var open = F("ETD434", null, sched.AddMinutes(10), sched.AddHours(1), "o");
        var otherAirline = F("UAE1", null, sched.AddMinutes(12), sched.AddHours(1), "u");
        Assert.Equal(("o", "route"), Pick(FlightMatch.ByNumber(no, new[] { "ETD" }, new[] { open, otherAirline }, arrivals, sched, sched, sched, none)));
        // Without a schedule it is not enough
        Assert.Empty(FlightMatch.RouteFits(new[] { open }, arrivals, null, sched, sched, new[] { "ETD" }));
        // Too late after the schedule
        Assert.Empty(FlightMatch.RouteFits(new[] { F("X", "LLBG", sched.AddHours(4), sched.AddHours(6)) }, arrivals, sched, sched, sched));

        static (string, string)? Pick(PublicMatch? m) => m == null ? null : (m.Flight.Icao24, m.By);
    }

    [Fact]
    public void Suggestions_fit_the_recorded_take_off_and_landing()
    {
        var start = Utc(2026, 8, 8, 2, 53);
        var end = start.AddMinutes(70);
        var flights = new[]
        {
            F("BKP223", "VTCC", start.AddMinutes(10), end.AddMinutes(15), "1"),
            F("NOK124", "VTCC", start.AddMinutes(-20), end.AddMinutes(-30), "2"),
            F("THA100", "RJAA", start, end, "3"),
            F("AIQ35", null, start.AddMinutes(5), end.AddMinutes(10), "4"),
            F("AIQ36", "VTCC", start.AddMinutes(80), end.AddMinutes(80), "5"),
        };
        var s = FlightMatch.Suggestions(flights, new HashSet<string> { "VTCC" }, start, end);
        Assert.Equal(new[] { ("4", 15), ("1", 25), ("2", 50) }, s.Select(x => (x.Flight.Icao24, x.Score)));
    }

    [Fact]
    public void A_track_is_simplified_to_at_most_500_points()
    {
        var line = Enumerable.Range(0, 1000).Select(i => new double[] { i, 30 + i * 0.001, 35 + i * 0.001, 10000 }).ToList();
        Assert.Equal(2, FlightMatch.Simplify(line).Count);
        var zigzag = Enumerable.Range(0, 2000).Select(i => new double[] { i, 30 + (i % 2) * 0.1, 35 + i * 0.01, 10000 }).ToList();
        var kept = FlightMatch.Simplify(zigzag);
        Assert.Equal(500, kept.Count);
        Assert.Equal((0d, 1999d), (kept[0][0], kept[^1][0]));
    }
}
