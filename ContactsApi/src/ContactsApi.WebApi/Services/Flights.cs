/**
 * @fileoverview Flights (AD-42): which flight each flight activity was, from the Owner, their
 * mail or public data, looked up by a worker; the API's operations
 * @module ContactsApi.WebApi.Services
 */
using System.Threading.Channels;
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Atlas.Flights;
using Microsoft.EntityFrameworkCore;

namespace ContactsApi.WebApi.Services;

/// <summary>A flight as the API returns it</summary>
public record FlightDto(
    Guid ActivityId, string Status, string? Note, string? FlightNumber, DateOnly? FlightDate, string? Source,
    string? MailState, FlightDetails Details, DateTime? CheckedAt, bool PublicData);

/// <summary>Why the mail could not be searched</summary>
public static class MailStates
{
    public const string Searched = "searched";
    public const string NotAllowed = "not_allowed";
    public const string SignIn = "sign_in";
    public const string Failed = "failed";
}

/// <summary>The Owner's mailbox for flights, when they allowed email content (T2)</summary>
public interface IFlightMail
{
    /// <summary>The mailbox, or null and why not (a <see cref="MailStates"/> value)</summary>
    Task<(IFlightMailbox? Box, string State)> OpenAsync(string owner, CancellationToken ct);
}

/// <summary>Gmail through the Owner's stored sign-in, when it carries gmail.readonly</summary>
public class GmailFlightMail : IFlightMail
{
    private readonly GoogleTokenService _tokens;

    /// <summary>Creates the opener</summary>
    public GmailFlightMail(GoogleTokenService tokens) => _tokens = tokens;

    /// <inheritdoc />
    public async Task<(IFlightMailbox? Box, string State)> OpenAsync(string owner, CancellationToken ct)
    {
        var (token, refused) = await _tokens.GetAccessTokenOrWhyAsync(owner);
        if (token == null)
        {
            return (null, refused ? MailStates.SignIn : MailStates.Failed);
        }

        var scopes = await _tokens.StoredGrantScopesAsync(owner);
        if (scopes == null)
        {
            return (null, MailStates.Failed);
        }

        if (!scopes.Contains(ActivityScopes.GmailRead))
        {
            return (null, MailStates.NotAllowed);
        }

        return (new GmailFlightReader(async _ =>
            (await _tokens.GetAccessTokenOrWhyAsync(owner)).AccessToken ?? throw new MailRefusedException("The sign-in ended")), MailStates.Searched);
    }
}

/// <summary>Owners whose flights wait to be looked up</summary>
public class FlightQueue
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();

    /// <summary>Asks for a look-up of the Owner's pending flights</summary>
    public void Enqueue(string owner) => _queue.Writer.TryWrite(owner);

    /// <summary>The next owner, or null when none asked within the wait</summary>
    public async Task<string?> NextAsync(TimeSpan wait, CancellationToken ct)
    {
        if (_queue.Reader.TryRead(out var owner))
        {
            return owner;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        try
        {
            return await _queue.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}

/// <summary>Looks up an Owner's flights and serves the API (AD-42 rules 1 to 11)</summary>
public class FlightService
{
    /// <summary>Flights looked up per Owner per round</summary>
    public const int PerRound = 25;

    /// <summary>Tries of a flight that failed for a passing reason</summary>
    public const int MaxAttempts = 5;

    /// <summary>A claim older than this is free again</summary>
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(15);

    /// <summary>OpenSky keeps tracks this long (29 days, a day short of its 30)</summary>
    public static readonly TimeSpan TrackKept = TimeSpan.FromDays(29);

    private readonly AtlasDbContext _db;
    private readonly IFlightMail _mail;
    private readonly IOpenSky _sky;
    private readonly IAdsbdb _adsbdb;
    private readonly FlightQueue _queue;
    private readonly ILogger<FlightService> _logger;

    /// <summary>Creates the service</summary>
    public FlightService(AtlasDbContext db, IFlightMail mail, IOpenSky sky, IAdsbdb adsbdb, FlightQueue queue, ILogger<FlightService> logger)
    {
        _db = db;
        _mail = mail;
        _sky = sky;
        _adsbdb = adsbdb;
        _queue = queue;
        _logger = logger;
    }

    /// <summary>The flight of an activity (made pending and queued when it has no row); null when not the Owner's flight</summary>
    public async Task<FlightDto?> GetAsync(string owner, Guid activityId, CancellationToken ct)
    {
        var row = await RowAsync(owner, activityId, ct);
        return row == null ? null : Dto(row);
    }

    /// <summary>The Owner types the flight number (FR-74)</summary>
    /// <exception cref="ArgumentException">Not a flight number</exception>
    public async Task<FlightDto?> SetNumberAsync(string owner, Guid activityId, string? text, DateOnly? date, CancellationToken ct)
    {
        var no = FlightNumbers.Parse(text) ?? throw new ArgumentException("Not a flight number (for example EY593)");
        var row = await RowAsync(owner, activityId, ct);
        if (row == null)
        {
            return null;
        }

        if (no.IsIcao)
        {
            // Kept in the IATA form when adsbdb knows the airline
            try
            {
                var iata = (await _adsbdb.AirlinesAsync(no.Airline, ct)).Select(a => a.Iata).FirstOrDefault(i => i is { Length: 2 });
                if (iata != null)
                {
                    no = no with { Airline = iata };
                }
            }
            catch (FlightSourceException)
            {
                // Kept as typed
            }
        }

        row.FlightNumber = no.ToString();
        row.FlightDate = date ?? row.FlightDate ?? await LocalDateAsync(activityId, ct);
        row.Source = "user";
        await ReopenAsync(row, ct);
        return Dto(row);
    }

    /// <summary>Clears the Owner's flight number: looked up again by itself</summary>
    public async Task<FlightDto?> ClearAsync(string owner, Guid activityId, CancellationToken ct)
    {
        var row = await RowAsync(owner, activityId, ct);
        if (row == null)
        {
            return null;
        }

        row.FlightNumber = null;
        row.FlightDate = null;
        row.Source = null;
        row.Details = new FlightDetails();
        await ReopenAsync(row, ct);
        return Dto(row);
    }

    /// <summary>Looks the flight up again</summary>
    public async Task<FlightDto?> SearchAsync(string owner, Guid activityId, CancellationToken ct)
    {
        var row = await RowAsync(owner, activityId, ct);
        if (row == null)
        {
            return null;
        }

        await ReopenAsync(row, ct);
        return Dto(row);
    }

    /// <summary>The Owner confirms a suggestion (AD-42 rule 7)</summary>
    /// <exception cref="ArgumentException">No such suggestion</exception>
    public async Task<FlightDto?> ConfirmAsync(string owner, Guid activityId, int index, CancellationToken ct)
    {
        var row = await RowAsync(owner, activityId, ct);
        if (row == null)
        {
            return null;
        }

        if (index < 0 || index >= row.Details.Candidates.Count)
        {
            throw new ArgumentException("No such suggestion");
        }

        var c = row.Details.Candidates[index];
        var no = FlightNumbers.Parse(c.FlightNumber);
        if (no == null && c.Airline != null && FlightNumbers.CallsignDigits(c.Callsign) is { } digits)
        {
            try
            {
                var iata = (await _adsbdb.AirlinesAsync(FlightNumbers.CallsignAirline(c.Callsign)!, ct)).Select(a => a.Iata).FirstOrDefault(i => i is { Length: 2 });
                no = iata == null ? null : FlightNumbers.Parse(iata + digits);
            }
            catch (FlightSourceException)
            {
                // The call sign stands for it
            }
        }

        var details = row.Details;
        row.FlightNumber = no?.ToString();
        row.Source = "confirmed";
        var dep = Airports.ByIcao(c.Departure);
        row.FlightDate = DateOnly.FromDateTime(dep == null ? c.FirstSeen : TimeZoneInfo.ConvertTimeFromUtc(c.FirstSeen, dep.Zone));
        details.Callsign = c.Callsign;
        details.Icao24 = c.Icao24;
        details.Takeoff = c.FirstSeen;
        details.Landing = c.LastSeen;
        row.Details = details;
        await ReopenAsync(row, ct);
        return Dto(row);
    }

    /// <summary>Makes rows for new flights and moves rows to a new fusion primary (AD-42 rule 1)</summary>
    public async Task EnsureRowsAsync(string owner, CancellationToken ct)
    {
        var flights = await _db.Activities.AsNoTracking()
            .Where(a => a.OwnerId == owner && a.Type == "flight" && a.PointCount >= 2 && (a.ClusterId == null || a.ClusterId == a.Id))
            .Select(a => a.Id).ToListAsync(ct);
        var rows = await _db.Flights.Where(f => f.OwnerId == owner).ToListAsync(ct);
        var byActivity = rows.ToDictionary(r => r.ActivityId);
        var missing = flights.Where(id => !byActivity.ContainsKey(id)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        // A row of a cluster member moves to the cluster's primary
        var orphanIds = rows.Select(r => r.ActivityId).Except(flights).ToList();
        var clusterOf = orphanIds.Count == 0
            ? new Dictionary<Guid, Guid?>()
            : await _db.Activities.AsNoTracking().Where(a => a.OwnerId == owner && orphanIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.ClusterId, ct);
        foreach (var id in missing)
        {
            var moved = rows.FirstOrDefault(r => clusterOf.TryGetValue(r.ActivityId, out var cluster) && cluster == id);
            if (moved != null)
            {
                moved.ActivityId = id;
                moved.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                _db.Flights.Add(new AtlasFlight { OwnerId = owner, ActivityId = id });
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>One round for an Owner: new rows, then up to 25 due flights, newest first (AD-42 rule 10)</summary>
    public async Task CheckAsync(string owner, CancellationToken ct)
    {
        await EnsureRowsAsync(owner, ct);
        var now = DateTime.UtcNow;
        var retryBefore = now.AddHours(-1);
        var due = await (from f in _db.Flights.AsNoTracking()
                         join a in _db.Activities.AsNoTracking() on f.ActivityId equals a.Id
                         where f.OwnerId == owner && a.Type == "flight" && (a.ClusterId == null || a.ClusterId == a.Id)
                               && (f.Status == FlightStatus.Pending
                                   || (f.Status == FlightStatus.Error && f.Attempts < MaxAttempts && (f.CheckedAt == null || f.CheckedAt < retryBefore))
                                   || ((f.MailState == MailStates.NotAllowed || f.MailState == MailStates.SignIn) && (f.Source == null || f.Source == "mail")))
                         orderby a.StartTime descending
                         select new { f.Id, f.Status, f.MailState }).ToListAsync(ct);
        if (due.Count == 0)
        {
            return;
        }

        var round = new Round();
        // Rows waiting for the mail permission are looked up again only once it is there
        if (due.Any(d => d.Status != FlightStatus.Pending && d.Status != FlightStatus.Error))
        {
            await round.MailboxAsync(_mail, owner, ct);
        }

        var ids = due.Where(d => d.Status is FlightStatus.Pending or FlightStatus.Error || round.State == MailStates.Searched)
            .Select(d => d.Id).Take(PerRound).ToList();
        foreach (var id in ids)
        {
            var stale = DateTime.UtcNow - ClaimTimeout;
            var claimed = await _db.Flights
                .Where(f => f.Id == id && (f.ClaimedAt == null || f.ClaimedAt < stale))
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.ClaimedAt, DateTime.UtcNow), ct);
            if (claimed == 0)
            {
                continue;
            }

            _db.ChangeTracker.Clear();
            var row = await _db.Flights.FirstAsync(f => f.Id == id, ct);
            try
            {
                await LookUpAsync(row, round, ct);
                row.Attempts = row.Status == FlightStatus.Error ? row.Attempts + 1 : 0;
            }
            catch (OpenSkyLimitException ex)
            {
                // Not the flight's fault: left as it was, for the next round
                _logger.LogInformation("Flights: {Message}; the round stops", ex.Message);
                await ReleaseAsync(id, ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Flights: looking up a flight failed");
                row.Status = FlightStatus.Error;
                row.Note = ex is FlightSourceException ? $"{ex.Message}. It is tried again within the hour." : "Looking up the flight failed. It is tried again within the hour.";
                row.Attempts++;
            }

            row.CheckedAt = DateTime.UtcNow;
            row.ClaimedAt = null;
            row.UpdatedAt = DateTime.UtcNow;
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The Owner changed the flight meanwhile: their change stands, and it is looked up again
                _db.ChangeTracker.Clear();
                await ReleaseAsync(id, ct);
            }
        }
    }

    /// <summary>A round's mailbox and parsed mails (each mail read once per round)</summary>
    private sealed class Round
    {
        public IFlightMailbox? Box;
        public string? State;
        public readonly Dictionary<string, List<MailSegment>> Parsed = new(StringComparer.Ordinal);

        public async Task MailboxAsync(IFlightMail mail, string owner, CancellationToken ct)
        {
            if (State == null)
            {
                (Box, State) = await mail.OpenAsync(owner, ct);
            }
        }
    }

    /// <summary>Airports, the mail, then the public data; the outcome written to the row</summary>
    private async Task LookUpAsync(AtlasFlight row, Round round, CancellationToken ct)
    {
        var activity = await _db.Activities.AsNoTracking().FirstAsync(a => a.Id == row.ActivityId, ct);
        var ends = _db.TrackPoints.AsNoTracking().Where(p => p.ActivityId == row.ActivityId);
        var first = await ends.OrderBy(p => p.Time).FirstOrDefaultAsync(ct);
        var last = await ends.OrderByDescending(p => p.Time).FirstOrDefaultAsync(ct);
        if (first == null || last == null)
        {
            (row.Status, row.Note) = (FlightStatus.NoAirports, "The flight has no recorded points.");
            return;
        }

        var deps = Airports.Near(first.Lat, first.Lon);
        var arrs = Airports.Near(last.Lat, last.Lon);
        var old = row.Details;
        var details = new FlightDetails
        {
            DepartureCandidates = deps.Select(a => a.Iata).ToList(),
            ArrivalCandidates = arrs.Select(a => a.Iata).ToList(),
            Checked = old.Checked
        };
        var owned = row.Source is "user" or "confirmed";
        if (row.Source == "confirmed")
        {
            (details.Callsign, details.Icao24, details.Takeoff, details.Landing) = (old.Callsign, old.Icao24, old.Takeoff, old.Landing);
        }

        row.Details = details;
        row.Note = null;
        if (!owned && (deps.Count == 0 || arrs.Count == 0))
        {
            (row.Status, row.Note, row.FlightNumber, row.Source) = (FlightStatus.NoAirports,
                $"No airport with scheduled flights within {Airports.NearKm:0} km of where the flight {(deps.Count == 0 ? "starts" : "ends")}.", null, null);
            return;
        }

        if (!owned && deps[0].Iata == arrs[0].Iata && Airports.Km(first.Lat, first.Lon, last.Lat, last.Lon) < 100)
        {
            (row.Status, row.Note, row.FlightNumber, row.Source) = (FlightStatus.NotAFlight, $"Starts and ends near {deps[0].Iata}: probably not a flight.", null, null);
            return;
        }

        var start = activity.StartTime;
        var end = activity.EndTime;

        // The mail (AD-42 rule 4), unless the Owner gave the number
        if (!owned)
        {
            await round.MailboxAsync(_mail, row.OwnerId, ct);
            row.MailState = round.State;
            MailMatch? found = null;
            if (round.Box != null)
            {
                try
                {
                    found = await MailAsync(round, deps, arrs, start, ct);
                }
                catch (MailRefusedException)
                {
                    (round.Box, round.State, row.MailState) = (null, MailStates.SignIn, MailStates.SignIn);
                }
                catch (FlightSourceException)
                {
                    row.MailState = MailStates.Failed;
                    throw;
                }
            }

            if (found != null)
            {
                row.FlightNumber = found.Number.ToString();
                row.Source = "mail";
                row.FlightDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(found.ScheduledDeparture ?? start, found.Departure.Zone));
                details.Mail = found.Mail;
                details.ScheduledDeparture = found.ScheduledDeparture;
                details.ScheduledArrival = found.ScheduledArrival;
                details.Departure = found.Departure.ToFlight();
                details.Arrival = found.Arrival.ToFlight();
            }
            else if (round.Box != null || row.Source != "mail")
            {
                // Searched and nothing names it, or never from mail: no number
                row.FlightNumber = null;
                row.Source = null;
                row.FlightDate = null;
            }
            else
            {
                // The mail could not be read this time: the number it gave stays
                (details.Mail, details.ScheduledDeparture, details.ScheduledArrival, details.Departure, details.Arrival) =
                    (old.Mail, old.ScheduledDeparture, old.ScheduledArrival, old.Departure, old.Arrival);
            }
        }

        details.Departure ??= deps.FirstOrDefault()?.ToFlight();
        details.Arrival ??= arrs.FirstOrDefault()?.ToFlight();
        var number = FlightNumbers.Parse(row.FlightNumber);
        if (number != null)
        {
            try
            {
                var airlines = await _adsbdb.AirlinesAsync(number.Airline, ct);
                details.Airline = airlines.FirstOrDefault();
                details.Checked.Adsbdb = DateTime.UtcNow;
            }
            catch (FlightSourceException ex)
            {
                _logger.LogInformation("Flights: adsbdb: {Message}", ex.Message);
            }
        }

        if (!_sky.Configured)
        {
            row.Status = number != null || row.Source == "confirmed" ? FlightStatus.MailOnly : FlightStatus.NotFound;
            row.Note = "Public flight data is not set up.";
            return;
        }

        if (deps.Count == 0 && details.Departure == null)
        {
            row.Status = FlightStatus.MailOnly;
            row.Note = $"No airport with scheduled flights within {Airports.NearKm:0} km of where the flight starts, so no public record can be looked up.";
            return;
        }

        await PublicAsync(row, details, deps, arrs, number, start, end, ct);
    }

    /// <summary>The best mail segment for the flight, over the newest 20 mails naming both airports</summary>
    private static async Task<MailMatch?> MailAsync(Round round, List<Airport> deps, List<Airport> arrs, DateTime start, CancellationToken ct)
    {
        var query = GmailFlightReader.Query(deps.Select(a => a.Iata), arrs.Select(a => a.Iata), start.Date);
        var segments = new List<MailSegment>();
        foreach (var id in await round.Box!.SearchAsync(query, GmailFlightReader.MaxMessages, ct))
        {
            if (!round.Parsed.TryGetValue(id, out var parsed))
            {
                var message = await round.Box.GetAsync(id, ct);
                parsed = message == null ? new List<MailSegment>() : FlightMail.Parse(message);
                round.Parsed[id] = parsed;
            }

            segments.AddRange(parsed);
        }

        return FlightMail.Match(segments, new FlightContext(deps, arrs, start));
    }

    /// <summary>The public record of the flight, or suggestions (AD-42 rules 5 to 8)</summary>
    private async Task PublicAsync(AtlasFlight row, FlightDetails details, List<Airport> deps, List<Airport> arrs, FlightNo? number,
        DateTime start, DateTime end, CancellationToken ct)
    {
        var sched = details.ScheduledDeparture;
        var anchor = sched ?? start;
        var (begin, until) = sched != null ? (anchor.AddHours(-2), anchor.AddHours(6)) : (anchor.AddHours(-3), anchor.AddHours(3));
        var mailDep = details.Mail != null ? Airports.ByIata(details.Departure?.Iata) : null;
        var asked = mailDep != null ? new List<Airport> { mailDep } : deps.Take(2).ToList();
        var flights = new List<OpenSkyFlight>();
        foreach (var airport in asked)
        {
            flights.AddRange(await _sky.DeparturesAsync(airport.Icao, begin, until, ct));
        }

        details.Checked.OpenSky = DateTime.UtcNow;
        var arrivals = arrs.Select(a => a.Icao).ToHashSet(StringComparer.Ordinal);
        if (details.Mail != null && details.Arrival != null)
        {
            arrivals.Add(details.Arrival.Icao);
        }

        PublicMatch? match = null;
        if (row.Source == "confirmed" && details.Icao24 != null)
        {
            var f = flights.Where(x => x.Icao24 == details.Icao24 && details.Takeoff != null && Math.Abs((x.FirstSeen - details.Takeoff.Value).TotalMinutes) <= 10)
                .Select(x => (OpenSkyFlight?)x).FirstOrDefault()
                ?? new OpenSkyFlight(details.Icao24, details.Callsign ?? string.Empty, details.Takeoff ?? start, details.Landing ?? end, asked.FirstOrDefault()?.Icao, null);
            match = new PublicMatch(f, "confirmed");
        }
        else if (number != null)
        {
            var icaos = number.IsIcao
                ? new List<string> { number.Airline }
                : (await AirlinesAsync(number.Airline, ct)).Select(a => a.Icao).OfType<string>().ToList();
            var lookups = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (!flights.Any(f => FlightNumbers.Matches(f.Callsign, number, icaos)))
            {
                foreach (var cs in FlightMatch.ToLookUp(flights, arrivals, anchor))
                {
                    lookups[cs] = (await CallsignAsync(cs, ct))?.FlightNumber;
                }
            }

            match = FlightMatch.ByNumber(number, icaos, flights, arrivals, sched, start, end, lookups);
            if (match == null)
            {
                details.Candidates = await CandidatesAsync(FlightMatch.RouteFits(flights, arrivals, sched, start, end).Take(FlightMatch.SuggestionCount)
                    .Select(f => (f, 0)), ct);
                row.Status = FlightStatus.MailOnly;
                row.Note = details.Candidates.Count > 0
                    ? $"{details.Candidates.Count} public flight{(details.Candidates.Count == 1 ? "" : "s")} fit; none under {number}."
                    : $"No public record matched {number}.";
                details.TrackNote = null;
                return;
            }
        }

        if (match == null)
        {
            details.Candidates = await CandidatesAsync(FlightMatch.Suggestions(flights, arrivals, start, end), ct);
            row.Status = details.Candidates.Count > 0 ? FlightStatus.Suggested : FlightStatus.NotFound;
            row.Note = details.Candidates.Count > 0
                ? null
                : row.MailState == MailStates.Searched ? "No mail names this flight, and no public flight fits it." : "No public flight fits it.";
            return;
        }

        var flight = match.Flight;
        details.Callsign = flight.Callsign;
        details.Icao24 = flight.Icao24;
        details.Takeoff = flight.FirstSeen;
        details.Landing = flight.LastSeen;
        details.Match = match.By;
        if (Airports.ByIcao(flight.Departure) is { } d && details.Mail == null)
        {
            details.Departure = d.ToFlight();
        }

        if (Airports.ByIcao(flight.Arrival) is { } a && details.Mail == null)
        {
            details.Arrival = a.ToFlight();
        }

        // The airline: of the number (the one flying it when several share the code); operated by:
        // the call sign's airline when it is not the number's (a codeshare)
        var callsignAirline = FlightNumbers.CallsignAirline(flight.Callsign);
        var numberAirlines = number == null ? new List<FlightAirline>() : await AirlinesAsync(number.Airline, ct);
        if (numberAirlines.FirstOrDefault(x => x.Icao == callsignAirline) is { } own)
        {
            details.Airline = own;
        }
        else if (callsignAirline != null)
        {
            var operating = (await AirlinesAsync(callsignAirline, ct)).FirstOrDefault(x => x.Icao == callsignAirline);
            if (details.Airline == null)
            {
                details.Airline = operating;
            }
            else if (number != null && match.By != "confirmed")
            {
                details.OperatedBy = operating;
            }
        }

        try
        {
            details.Aircraft = await _adsbdb.AircraftAsync(flight.Icao24, ct);
            details.Checked.Adsbdb = DateTime.UtcNow;
        }
        catch (FlightSourceException ex)
        {
            _logger.LogInformation("Flights: adsbdb aircraft: {Message}", ex.Message);
        }

        if (flight.LastSeen >= DateTime.UtcNow - TrackKept)
        {
            try
            {
                var track = await _sky.TrackAsync(flight.Icao24, flight.FirstSeen.AddMinutes(5), ct);
                details.Track = track == null ? null : FlightMatch.Simplify(track);
                details.TrackNote = track == null ? "The public source has no path for this flight." : null;
            }
            catch (FlightSourceException)
            {
                details.TrackNote = "The flown path could not be read; it is read again with Search again within 30 days of the flight.";
            }
        }
        else
        {
            details.TrackNote = "The flown path is kept publicly for 30 days only; the map shows the great-circle route between the airports.";
        }

        row.Status = FlightStatus.Found;
        row.Note = match.By == "route" ? "Matched by route and time: the aircraft flew under another call sign." : null;
    }

    /// <summary>Candidates with their airline and flight number from adsbdb</summary>
    private async Task<List<FlightCandidate>> CandidatesAsync(IEnumerable<(OpenSkyFlight Flight, int Score)> fits, CancellationToken ct)
    {
        var list = new List<FlightCandidate>();
        foreach (var (f, score) in fits)
        {
            var info = f.Callsign.Length > 0 ? await CallsignAsync(f.Callsign, ct) : null;
            var airline = info?.Airline?.Name;
            if (airline == null && FlightNumbers.CallsignAirline(f.Callsign) is { } code)
            {
                airline = (await AirlinesAsync(code, ct)).FirstOrDefault()?.Name;
            }

            var no = FlightNumbers.Parse(info?.FlightNumber);
            list.Add(new FlightCandidate(f.Callsign, f.Icao24, f.FirstSeen, f.LastSeen, f.Departure, f.Arrival, airline, no?.ToString(), score));
        }

        return list;
    }

    private async Task<List<FlightAirline>> AirlinesAsync(string code, CancellationToken ct)
    {
        try
        {
            return await _adsbdb.AirlinesAsync(code, ct);
        }
        catch (FlightSourceException)
        {
            return new();
        }
    }

    private async Task<(string? FlightNumber, FlightAirline? Airline)?> CallsignAsync(string callsign, CancellationToken ct)
    {
        try
        {
            return await _adsbdb.CallsignAsync(callsign, ct);
        }
        catch (FlightSourceException)
        {
            return null;
        }
    }

    private Task ReleaseAsync(Guid id, CancellationToken ct) =>
        _db.Flights.Where(f => f.Id == id).ExecuteUpdateAsync(s => s.SetProperty(f => f.ClaimedAt, (DateTime?)null), ct);

    /// <summary>Back to pending, tried afresh, queued</summary>
    private async Task ReopenAsync(AtlasFlight row, CancellationToken ct)
    {
        row.Status = FlightStatus.Pending;
        row.Note = null;
        row.Attempts = 0;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _queue.Enqueue(row.OwnerId);
    }

    /// <summary>The Owner's flight row of an activity (made when missing); null when not their flight</summary>
    private async Task<AtlasFlight?> RowAsync(string owner, Guid activityId, CancellationToken ct)
    {
        var isFlight = await _db.Activities.AsNoTracking().AnyAsync(a => a.Id == activityId && a.OwnerId == owner && a.Type == "flight", ct);
        if (!isFlight)
        {
            return null;
        }

        var row = await _db.Flights.FirstOrDefaultAsync(f => f.OwnerId == owner && f.ActivityId == activityId, ct);
        if (row == null)
        {
            row = new AtlasFlight { OwnerId = owner, ActivityId = activityId };
            _db.Flights.Add(row);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Made meanwhile by the worker
                _db.ChangeTracker.Clear();
                return await _db.Flights.FirstAsync(f => f.OwnerId == owner && f.ActivityId == activityId, ct);
            }

            _queue.Enqueue(owner);
        }

        return row;
    }

    /// <summary>The flight's recorded departure date in its departure airport's time (UTC without one)</summary>
    private async Task<DateOnly> LocalDateAsync(Guid activityId, CancellationToken ct)
    {
        var start = await _db.Activities.Where(a => a.Id == activityId).Select(a => a.StartTime).FirstAsync(ct);
        var first = await _db.TrackPoints.AsNoTracking().Where(p => p.ActivityId == activityId).OrderBy(p => p.Time).FirstOrDefaultAsync(ct);
        var zone = first == null ? null : Airports.Near(first.Lat, first.Lon).FirstOrDefault()?.Zone;
        return DateOnly.FromDateTime(zone == null ? start : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(start, DateTimeKind.Utc), zone));
    }

    private FlightDto Dto(AtlasFlight r) =>
        new(r.ActivityId, r.Status, r.Note, r.FlightNumber, r.FlightDate, r.Source, r.MailState, r.Details, r.CheckedAt, _sky.Configured);
}

/// <summary>Looks up flights: every hour for every Owner with flights, and at once when asked (AD-42 rule 10)</summary>
public class FlightWorker : BackgroundService
{
    private static readonly TimeSpan Round = TimeSpan.FromHours(1);
    private readonly IServiceScopeFactory _scopes;
    private readonly FlightQueue _queue;
    private readonly AtlasReadiness _readiness;
    private readonly ILogger<FlightWorker> _logger;

    /// <summary>Creates the worker</summary>
    public FlightWorker(IServiceScopeFactory scopes, FlightQueue queue, AtlasReadiness readiness, ILogger<FlightWorker> logger)
    {
        _scopes = scopes;
        _queue = queue;
        _readiness = readiness;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!_readiness.Ready && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        await EnqueueAllAsync(stoppingToken);
        var nextRound = DateTime.UtcNow + Round;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var wait = nextRound - DateTime.UtcNow;
                var owner = await _queue.NextAsync(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, stoppingToken);
                if (owner == null)
                {
                    nextRound = DateTime.UtcNow + Round;
                    await EnqueueAllAsync(stoppingToken);
                    continue;
                }

                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<FlightService>().CheckAsync(owner, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Flights: a round failed");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    /// <summary>Queues every Owner with flights</summary>
    private async Task EnqueueAllAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var owners = await scope.ServiceProvider.GetRequiredService<AtlasDbContext>().Activities.AsNoTracking()
                .Where(a => a.Type == "flight").Select(a => a.OwnerId).Distinct().ToListAsync(ct);
            owners.ForEach(_queue.Enqueue);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Flights: listing the Owners failed");
        }
    }
}
