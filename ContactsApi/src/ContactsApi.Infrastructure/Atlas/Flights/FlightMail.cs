/**
 * @fileoverview Flights in mail (AD-42 rule 4): booking confirmations, changes and boarding
 * passes read for flight numbers with their route, date and time, and matched to a
 * recorded flight. Pure: no mail is fetched or kept here.
 * @module ContactsApi.Infrastructure.Atlas.Flights
 */
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ContactsApi.Infrastructure.Atlas.Flights;

/// <summary>A mail as read for flights</summary>
public record FlightMailMessage(string Id, string Subject, string From, DateTime Date, string? Html, string? Plain);

/// <summary>A flight number found in a mail, with the text around it, or a schema.org reservation</summary>
public record MailSegment(
    FlightNo Number,
    FlightMailRef Mail,
    string Forward,
    string Back,
    string? DepIata = null,
    string? ArrIata = null,
    DateTime? DepLocal = null,
    bool DepHasTime = false,
    DateTime? ArrLocal = null,
    bool TimesUtc = false,
    bool RouteBefore = false)
{
    // TimesUtc: the reservation's times carried an offset, so they are UTC, not local.
    // RouteBefore: the mail writes each flight's route and times before its number.
    /// <summary>From a schema.org FlightReservation</summary>
    public bool Structured => DepIata != null;
}

/// <summary>A recorded flight as the mail is matched to it</summary>
/// <param name="Departures">Departure candidates, nearest first</param>
/// <param name="Arrivals">Arrival candidates, nearest first</param>
/// <param name="Start">Recorded take-off (UTC)</param>
public record FlightContext(IReadOnlyList<Airport> Departures, IReadOnlyList<Airport> Arrivals, DateTime Start);

/// <summary>The mail's flight for a recorded flight</summary>
public record MailMatch(FlightNo Number, FlightMailRef Mail, Airport Departure, Airport Arrival, DateTime? ScheduledDeparture, DateTime? ScheduledArrival, int Score);

/// <summary>Reads mails for flights and matches them to recorded flights</summary>
public static class FlightMail
{
    /// <summary>Longest forward window (characters)</summary>
    public const int ForwardMax = 800;

    /// <summary>Longest back window (characters)</summary>
    public const int BackMax = 300;

    /// <summary>Score of a match by date alone (no time)</summary>
    public const int DateOnlyScore = 600;

    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly Regex Token = new(@"(?<![A-Za-z0-9])([A-Z]{2}|[A-Z][0-9]|[0-9][A-Z])([ \-]?)([0-9]{1,4})([A-Z]?)(?![A-Za-z0-9])", Opts);
    private static readonly Regex AircraftWord = new(@"airbus|boeing|embraer|bombardier|aircraft|equipment|מטוס", Opts | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> AircraftPrefixes = new(StringComparer.Ordinal) { "A2", "A3", "B7", "E1", "E2", "E7", "E9" };
    private static readonly Regex Word3 = new(@"(?<![A-Za-z])[A-Z]{3}(?![A-Za-z])", Opts);
    private static readonly Regex LdJson = new(@"<script[^>]*application/ld\+json[^>]*>(.*?)</script>", Opts | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex Hidden = new(@"<(script|style|head)[^>]*>.*?</\1>", Opts | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex Block = new(@"<\s*(br|/p|/div|/tr|/li|/h\d|/table)\s*/?>", Opts | RegexOptions.IgnoreCase);
    private static readonly Regex Tag = new(@"<[^>]+>", Opts);
    private static readonly Regex Spaces = new(@"[ \t ​‌‎‏]+", Opts);

    private const string Month = @"(Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|June?|July?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)(?![A-Za-z])";
    private static readonly Regex DayMonth = new(@"(?<!\d)(\d{1,2})(?:st|nd|rd|th)?[ \-]?(?:of )?" + Month + @"\.?,?(?:[ \-]?(\d{4}|\d{2})(?![\d:]))?", Opts | RegexOptions.IgnoreCase);
    private static readonly Regex MonthDay = new(@"(?<![A-Za-z])" + Month + @"\.? (\d{1,2})(?:st|nd|rd|th)?(?![\d:])(?:,? (\d{4}))?", Opts | RegexOptions.IgnoreCase);
    private static readonly Regex IsoDate = new(@"(?<!\d)(\d{4})-(\d{2})-(\d{2})(?!\d)", Opts);
    private static readonly Regex NumDate = new(@"(?<![\d.:/])(\d{1,2})[./](\d{1,2})[./](\d{4}|\d{2})(?![\d.:/])", Opts);
    private static readonly Regex HebDate = new(@"(?<!\d)(\d{1,2})\s*ב?(ינואר|פברואר|מרץ|מרס|אפריל|מאי|יוני|יולי|אוגוסט|ספטמבר|אוקטובר|נובמבר|דצמבר)(?:\s*(\d{4}))?", Opts);
    private static readonly Regex Time = new(@"(?<![\d:])([01]?\d|2[0-3]):([0-5]\d)(?:\s?([AaPp])\.?[Mm]\.?(?![A-Za-z]))?(?![\d:])", Opts);

    private static readonly string[] HebMonths = { "ינואר", "פברואר", "מרץ", "אפריל", "מאי", "יוני", "יולי", "אוגוסט", "ספטמבר", "אוקטובר", "נובמבר", "דצמבר" };

    /// <summary>The flight numbers of a mail with their windows, and its schema.org reservations</summary>
    public static List<MailSegment> Parse(FlightMailMessage message)
    {
        var mail = new FlightMailRef(message.Id, Clip(message.Subject, 200), message.From, message.Date);
        var segments = Structured(message.Html, mail);
        var text = message.Subject + "\n" + (message.Html != null ? HtmlText(message.Html) : message.Plain ?? string.Empty);
        text = Spaces.Replace(text, " ");

        var tokens = new List<(Match m, FlightNo no)>();
        foreach (Match m in Token.Matches(text))
        {
            var designator = m.Groups[1].Value;
            // A320, B787, E195 written as one word are aircraft, not flights
            if (m.Groups[2].Value.Length == 0 && m.Length == 4 && AircraftPrefixes.Contains(designator))
            {
                continue;
            }

            var before = text.Substring(Math.Max(0, m.Index - 15), Math.Min(15, m.Index));
            if (AircraftWord.IsMatch(before))
            {
                continue;
            }

            var no = FlightNumbers.Parse(m.Value);
            if (no != null)
            {
                tokens.Add((m, no));
            }
        }

        // A mail whose text before its first number already names an airport, a date and a
        // time writes each flight's route before its number: then the text before each number
        // is that flight's, and the text after it the next one's
        var routeBefore = false;
        if (tokens.Count > 0)
        {
            var lead = text.Substring(Math.Max(0, tokens[0].m.Index - BackMax), Math.Min(BackMax, tokens[0].m.Index));
            routeBefore = Word3.Matches(lead).Any(w => Airports.IsIata(w.Value)) && Time.IsMatch(lead)
                && (DayMonth.IsMatch(lead) || MonthDay.IsMatch(lead) || IsoDate.IsMatch(lead) || NumDate.IsMatch(lead) || HebDate.IsMatch(lead));
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var (m, no) = tokens[i];
            var end = m.Index + m.Length;
            var next = i + 1 < tokens.Count ? tokens[i + 1].m.Index : text.Length;
            var forward = text.Substring(end, Math.Min(next - end, ForwardMax));
            var prev = i > 0 ? tokens[i - 1].m.Index + tokens[i - 1].m.Length : 0;
            var backStart = Math.Max(prev, m.Index - BackMax);
            var back = text.Substring(backStart, m.Index - backStart);
            segments.Add(new MailSegment(no, mail, forward, back, RouteBefore: routeBefore));
        }

        return segments;
    }

    /// <summary>
    /// The mail's flight for a recorded flight: the segment naming its airports in order,
    /// on its day, at a time near the recorded take-off; the nearest, then the newest mail
    /// </summary>
    public static MailMatch? Match(IEnumerable<MailSegment> segments, FlightContext flight)
    {
        if (flight.Departures.Count == 0 || flight.Arrivals.Count == 0)
        {
            return null;
        }

        MailMatch? best = null;
        foreach (var s in segments)
        {
            var m = s.Structured ? MatchStructured(s, flight) : MatchText(s, flight);
            if (m != null && (best == null || m.Score < best.Score || (m.Score == best.Score && m.Mail.Date > best.Mail.Date)))
            {
                best = m;
            }
        }

        return best;
    }

    /// <summary>HTML as text: every tag a space, blocks a line break, entities decoded</summary>
    internal static string HtmlText(string html) =>
        WebUtility.HtmlDecode(Tag.Replace(Block.Replace(Hidden.Replace(html, " "), "\n"), " "));

    private static MailMatch? MatchStructured(MailSegment s, FlightContext f)
    {
        var dep = f.Departures.FirstOrDefault(a => a.Iata == s.DepIata);
        var arr = f.Arrivals.FirstOrDefault(a => a.Iata == s.ArrIata);
        if (dep == null || arr == null || s.DepLocal == null)
        {
            return null;
        }

        var target = Local(f.Start, dep.Zone).Date;
        var date = s.TimesUtc ? Local(s.DepLocal.Value, dep.Zone).Date : s.DepLocal.Value.Date;
        if (!s.DepHasTime)
        {
            return date == target ? new MailMatch(s.Number, s.Mail, dep, arr, null, null, DateOnlyScore) : null;
        }

        var sched = s.TimesUtc ? DateTime.SpecifyKind(s.DepLocal.Value, DateTimeKind.Utc) : Utc(s.DepLocal.Value, dep.Zone);
        if (!InWindow(sched, f.Start))
        {
            return null;
        }

        DateTime? arrival = s.ArrLocal == null ? null
            : s.TimesUtc ? DateTime.SpecifyKind(s.ArrLocal.Value, DateTimeKind.Utc) : Utc(s.ArrLocal.Value, arr.Zone);
        if (arrival != null && !ArrivalFits(sched, arrival.Value))
        {
            arrival = null;
        }

        return new MailMatch(s.Number, s.Mail, dep, arr, sched, arrival, Minutes(sched, f.Start));
    }

    private static MailMatch? MatchText(MailSegment s, FlightContext f)
    {
        // The flight's own text: before the number in a mail that writes routes first; else
        // after it, and before it only when the text after names no airport (otherwise the
        // text before is the previous flight's)
        var (first, second) = s.RouteBefore ? (s.Back, s.Forward) : (s.Forward, s.Back);
        var route = s.RouteBefore
            ? Route(s.Back, f)
            : Route(s.Forward, f) ?? (Word3.Matches(s.Forward).Any(w => Airports.IsIata(w.Value)) ? null : Route(s.Back, f));
        if (route == null)
        {
            return null;
        }

        var (dep, arr) = route.Value;
        var target = Local(f.Start, dep.Zone).Date;
        // Dates and times in the flight's own text; the other window when it has no date
        var window = first;
        var dates = Dates(window, target);
        if (dates.Count == 0)
        {
            window = second;
            dates = Dates(window, target);
        }

        if (dates.Count == 0)
        {
            return null;
        }

        // The departure time is the window's first (a mail lists it before the arrival's),
        // on the fitting date nearest it
        var times = Times(window);
        if (times.Count == 0)
        {
            return dates.Any(d => d.date == target) ? new MailMatch(s.Number, s.Mail, dep, arr, null, null, DateOnlyScore) : null;
        }

        var (time, timePos) = times[0];
        var near = dates.Where(d => Math.Abs(d.pos - timePos) <= 60).OrderBy(d => Math.Abs(d.pos - timePos)).ToList();
        var date = (near.Count > 0 ? near[0] : dates[0]).date;
        var sched = Utc(date + time, dep.Zone);
        if (!InWindow(sched, f.Start))
        {
            return null;
        }

        var arrival = Arrival(window, timePos, date, sched, arr, target);
        return new MailMatch(s.Number, s.Mail, dep, arr, sched, arrival, Minutes(sched, f.Start));
    }

    /// <summary>A departure candidate's code, then an arrival candidate's code after it</summary>
    private static (Airport dep, Airport arr)? Route(string window, FlightContext f)
    {
        var words = Word3.Matches(window).Select(w => (w.Value, w.Index)).ToList();
        foreach (var (code, index) in words)
        {
            var dep = f.Departures.FirstOrDefault(a => a.Iata == code);
            if (dep == null)
            {
                continue;
            }

            foreach (var (code2, index2) in words)
            {
                if (index2 > index && f.Arrivals.FirstOrDefault(a => a.Iata == code2 && a.Iata != dep.Iata) is { } arr)
                {
                    return (dep, arr);
                }
            }
        }

        return null;
    }

    /// <summary>The scheduled arrival: the next time after the departure time, when it fits</summary>
    private static DateTime? Arrival(string window, int depPos, DateTime depDate, DateTime sched, Airport arr, DateTime target)
    {
        var next = Times(window).Where(t => t.pos > depPos).Select(t => ((TimeSpan, int)?)t).FirstOrDefault();
        if (next == null)
        {
            return null;
        }

        var (time, pos) = next.Value;
        var date = Dates(window, target).Where(d => d.pos > pos && d.pos - pos <= 60).Select(d => (DateTime?)d.date).FirstOrDefault() ?? depDate;
        foreach (var day in new[] { date, date.AddDays(1) })
        {
            var at = Utc(day + time, arr.Zone);
            if (ArrivalFits(sched, at))
            {
                return at;
            }
        }

        return null;
    }

    private static bool ArrivalFits(DateTime sched, DateTime arrival) =>
        arrival - sched >= TimeSpan.FromMinutes(20) && arrival - sched <= TimeSpan.FromHours(20);

    /// <summary>A scheduled departure from 4 hours before to 2 hours after the recorded take-off</summary>
    private static bool InWindow(DateTime sched, DateTime start) =>
        sched >= start - TimeSpan.FromHours(4) && sched <= start + TimeSpan.FromHours(2);

    private static int Minutes(DateTime a, DateTime b) => (int)Math.Round(Math.Abs((a - b).TotalMinutes));

    /// <summary>The dates in a window that are the target day, the day before or after, with their positions</summary>
    internal static List<(DateTime date, int pos)> Dates(string window, DateTime target)
    {
        var found = new List<(DateTime, int)>();
        void Add(int? year, int month, int day, int pos)
        {
            var years = year != null ? new[] { year.Value } : new[] { target.Year, target.Year - 1, target.Year + 1 };
            foreach (var y in years)
            {
                if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(Math.Clamp(y, 1, 9999), month))
                {
                    continue;
                }

                var d = new DateTime(y, month, day);
                if (Math.Abs((d - target).TotalDays) <= 1)
                {
                    found.Add((d, pos));
                    return;
                }
            }
        }

        static int? Year(string s) => s.Length == 0 ? null : s.Length == 2 ? 2000 + int.Parse(s, CultureInfo.InvariantCulture) : int.Parse(s, CultureInfo.InvariantCulture);

        foreach (Match m in DayMonth.Matches(window))
        {
            Add(Year(m.Groups[3].Value), MonthOf(m.Groups[2].Value), int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Index);
        }

        foreach (Match m in MonthDay.Matches(window))
        {
            Add(Year(m.Groups[3].Value), MonthOf(m.Groups[1].Value), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), m.Index);
        }

        foreach (Match m in IsoDate.Matches(window))
        {
            Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), m.Index);
        }

        foreach (Match m in NumDate.Matches(window))
        {
            var a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var b = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var y = Year(m.Groups[3].Value);
            var before = found.Count;
            // Day first; month first only when that fits and day first does not
            Add(y, b, a, m.Index);
            if (found.Count == before)
            {
                Add(y, a, b, m.Index);
            }
        }

        foreach (Match m in HebDate.Matches(window))
        {
            var name = m.Groups[2].Value == "מרס" ? "מרץ" : m.Groups[2].Value;
            Add(Year(m.Groups[3].Value), Array.IndexOf(HebMonths, name) + 1, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Index);
        }

        return found.OrderBy(f => f.Item2).ToList();
    }

    /// <summary>The times of day in a window, with their positions</summary>
    internal static List<(TimeSpan time, int pos)> Times(string window)
    {
        var list = new List<(TimeSpan, int)>();
        foreach (Match m in Time.Matches(window))
        {
            var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var ampm = m.Groups[3].Value.ToUpperInvariant();
            if (ampm.Length > 0)
            {
                if (h is < 1 or > 12)
                {
                    continue;
                }

                h = ampm == "P" ? h % 12 + 12 : h % 12;
            }

            list.Add((new TimeSpan(h, min, 0), m.Index));
        }

        return list;
    }

    private static int MonthOf(string name) =>
        Array.FindIndex(CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames,
            a => a.Length > 0 && name.StartsWith(a, StringComparison.OrdinalIgnoreCase)) + 1;

    /// <summary>schema.org FlightReservations in the mail's JSON-LD</summary>
    private static List<MailSegment> Structured(string? html, FlightMailRef mail)
    {
        var list = new List<MailSegment>();
        if (html == null)
        {
            return list;
        }

        foreach (Match m in LdJson.Matches(html))
        {
            try
            {
                using var doc = JsonDocument.Parse(WebUtility.HtmlDecode(m.Groups[1].Value));
                Walk(doc.RootElement, list, mail);
            }
            catch (JsonException)
            {
                // Not JSON: the text is read anyway
            }
        }

        return list;
    }

    private static void Walk(JsonElement e, List<MailSegment> list, FlightMailRef mail)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
            {
                Walk(item, list, mail);
            }

            return;
        }

        if (e.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (e.TryGetProperty("@graph", out var graph))
        {
            Walk(graph, list, mail);
        }

        if (Str(e, "@type") != "FlightReservation" || !e.TryGetProperty("reservationFor", out var f) || f.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var airline = f.TryGetProperty("airline", out var al) ? Str(al, "iataCode") : null;
        var number = Str(f, "flightNumber");
        var no = FlightNumbers.Parse(number != null && airline != null && !number.StartsWith(airline, StringComparison.OrdinalIgnoreCase) ? airline + number : number);
        var dep = f.TryGetProperty("departureAirport", out var da) ? Str(da, "iataCode") : null;
        var arr = f.TryGetProperty("arrivalAirport", out var aa) ? Str(aa, "iataCode") : null;
        var (depTime, hasTime, depUtc) = ReservationTime(Str(f, "departureTime"));
        var (arrTime, _, arrUtc) = ReservationTime(Str(f, "arrivalTime"));
        if (no != null && dep != null && arr != null && depTime != null)
        {
            // Both times in one kind: an arrival of the other kind is dropped
            list.Add(new MailSegment(no, mail, string.Empty, string.Empty, dep.ToUpperInvariant(), arr.ToUpperInvariant(),
                depTime, hasTime, arrUtc == depUtc ? arrTime : null, depUtc));
        }
    }

    private static readonly Regex Offset = new(@"(Z|[+\-]\d{2}:?\d{2})$", Opts | RegexOptions.IgnoreCase);

    /// <summary>
    /// A reservation's ISO time: with an offset or Z, the UTC time it names; without one, the
    /// airport's local time
    /// </summary>
    private static (DateTime? time, bool hasTime, bool utc) ReservationTime(string? iso)
    {
        if (iso == null)
        {
            return (null, false, false);
        }

        iso = iso.Trim();
        var hasTime = iso.Contains('T') || iso.Contains(':');
        if (hasTime && Offset.IsMatch(iso)
            && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
        {
            return (dto.UtcDateTime, true, true);
        }

        return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? (DateTime.SpecifyKind(d, DateTimeKind.Unspecified), hasTime, false)
            : (null, false, false);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime Local(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    private static DateTime Utc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // A time skipped by a clock change is read an hour later
        if (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}
