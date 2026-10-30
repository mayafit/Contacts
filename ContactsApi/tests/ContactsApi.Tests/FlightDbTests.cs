using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Atlas.Flights;
using ContactsApi.WebApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContactsApi.Tests;

/// <summary>Flights looked up against fake mail and public sources and a real PostgreSQL (ATLAS_TEST_DB; skipped otherwise)</summary>
public class FlightDbTests
{
    private sealed class Box : IFlightMailbox
    {
        public readonly List<FlightMailMessage> Messages = new();
        public readonly List<string> Queries = new();
        public int Reads;

        public Task<List<string>> SearchAsync(string query, int max, CancellationToken ct)
        {
            Queries.Add(query);
            return Task.FromResult(Messages.Select(m => m.Id).Take(max).ToList());
        }

        public Task<FlightMailMessage?> GetAsync(string id, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(Messages.FirstOrDefault(m => m.Id == id));
        }
    }

    private sealed class Mail(Box box) : IFlightMail
    {
        public string State = MailStates.Searched;

        public Task<(IFlightMailbox? Box, string State)> OpenAsync(string owner, CancellationToken ct) =>
            Task.FromResult(State == MailStates.Searched ? ((IFlightMailbox?)box, State) : (null, State));
    }

    private sealed class Sky : IOpenSky
    {
        public readonly List<OpenSkyFlight> Flights = new();
        public readonly List<string> Asked = new();
        public bool Limited;
        public List<double[]>? Track;
        public bool Configured => true;

        public Task<List<OpenSkyFlight>> DeparturesAsync(string airportIcao, DateTime begin, DateTime end, CancellationToken ct)
        {
            if (Limited)
            {
                throw new OpenSkyLimitException("used");
            }

            Asked.Add(airportIcao);
            Assert.True(end - begin <= TimeSpan.FromDays(2));
            return Task.FromResult(Flights.Where(f => f.Departure == airportIcao && f.FirstSeen >= begin && f.FirstSeen <= end).ToList());
        }

        public Task<List<double[]>?> TrackAsync(string icao24, DateTime time, CancellationToken ct) => Task.FromResult(Track);
    }

    private sealed class Adsbdb : IAdsbdb
    {
        public Task<List<FlightAirline>> AirlinesAsync(string code, CancellationToken ct) => Task.FromResult(code switch
        {
            "EY" or "ETD" => new List<FlightAirline> { new("Etihad Airways", "EY", "ETD") },
            "LY" or "ELY" => new List<FlightAirline> { new("El Al Israel Airlines", "LY", "ELY") },
            "PG" or "BKP" => new List<FlightAirline> { new("Bangkok Airways", "PG", "BKP") },
            _ => new List<FlightAirline>()
        });

        public Task<(string? FlightNumber, FlightAirline? Airline)?> CallsignAsync(string callsign, CancellationToken ct) =>
            Task.FromResult<(string?, FlightAirline?)?>(callsign switch
            {
                "ETD5SM" => ("EY593", new FlightAirline("Etihad Airways", "EY", "ETD")),
                "ETD607" => ("EY607", new FlightAirline("Etihad Airways", "EY", "ETD")),
                _ => null
            });

        public Task<FlightAircraft?> AircraftAsync(string icao24, CancellationToken ct) =>
            Task.FromResult<FlightAircraft?>(new FlightAircraft("A321neo", "A21N", "Airbus", "A6-AEA", "Etihad Airways"));
    }

    private static AtlasDbContext? Db()
    {
        var cs = Environment.GetEnvironmentVariable("ATLAS_TEST_DB");
        if (string.IsNullOrEmpty(cs))
        {
            return null;
        }

        var db = new AtlasDbContext(new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations", "atlas")).Options);
        db.Database.Migrate();
        return db;
    }

    private static DateTime Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    /// <summary>A flight recorded from near one point to near another</summary>
    private static async Task<Guid> FlightAsync(AtlasDbContext db, string owner, DateTime start, DateTime end, (double, double) from, (double, double) to, string type = "flight")
    {
        var a = new AtlasActivity
        {
            OwnerId = owner, Type = type, StartTime = start, EndTime = end, PointCount = 2,
            Points = new()
            {
                new TrackPoint { Time = start, Lat = from.Item1, Lon = from.Item2 },
                new TrackPoint { Time = end, Lat = to.Item1, Lon = to.Item2 }
            }
        };
        db.Activities.Add(a);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return a.Id;
    }

    private static readonly (double, double) NearAuh = (24.45, 54.66);
    private static readonly (double, double) NearTlv = (32.02, 34.88);

    private static (FlightService Svc, FlightQueue Queue) Service(AtlasDbContext db, IFlightMail mail, IOpenSky sky) =>
        Service2(db, mail, sky);

    private static (FlightService, FlightQueue) Service2(AtlasDbContext db, IFlightMail mail, IOpenSky sky)
    {
        var queue = new FlightQueue();
        return (new FlightService(db, mail, sky, new Adsbdb(), queue, NullLogger<FlightService>.Instance), queue);
    }

    private static async Task<AtlasFlight> RowAsync(AtlasDbContext db, Guid activity)
    {
        db.ChangeTracker.Clear();
        return await db.Flights.AsNoTracking().SingleAsync(f => f.ActivityId == activity);
    }

    [Fact]
    public async Task The_mail_names_the_flight_and_public_data_matches_it_by_route_and_time()
    {
        await using var db = Db();
        if (db == null)
        {
            return;
        }

        var owner = "flights-" + Guid.NewGuid();
        // AUH 08:50 (UTC+4) is 04:50 UTC; the phone recorded 05:11 to 07:30
        var id = await FlightAsync(db, owner, Utc(2026, 3, 17, 5, 11), Utc(2026, 3, 17, 7, 30), NearAuh, NearTlv);
        var box = new Box();
        box.Messages.Add(new FlightMailMessage("b1", "Your booking confirmation", "noreply@airline.example", Utc(2026, 1, 10, 0, 0), null,
            "Etihad Airways EY593\nAUH TLV\n08:50 17 Mar 2026 Zayed International\n11:10 17 Mar 2026 Ben Gurion"));
        box.Messages.Add(new FlightMailMessage("x", "Newsletter", "news@example.com", Utc(2026, 1, 11, 0, 0), null, "Fly AUH TLV from 99"));
        var sky = new Sky();
        // Two Etihad flights fit the route; EY593 flies as ETD5SM (adsbdb says so)
        sky.Flights.Add(new OpenSkyFlight("896123", "ETD5SM", Utc(2026, 3, 17, 5, 12), Utc(2026, 3, 17, 7, 31), "OMAA", "LLBG"));
        sky.Flights.Add(new OpenSkyFlight("896124", "ETD607", Utc(2026, 3, 17, 5, 30), Utc(2026, 3, 17, 7, 50), "OMAA", "LLBG"));
        var (svc, _) = Service(db, new Mail(box), sky);

        await svc.CheckAsync(owner, default);

        var row = await RowAsync(db, id);
        Assert.Equal((FlightStatus.Found, "EY593", "mail", MailStates.Searched, new DateOnly(2026, 3, 17)),
            (row.Status, row.FlightNumber, row.Source, row.MailState, row.FlightDate));
        var d = row.Details;
        Assert.Equal(("b1", "Your booking confirmation"), (d.Mail!.Id, d.Mail.Subject));
        // TLV 11:10 is UTC+2 before Israel's clocks change (27 Mar 2026)
        Assert.Equal((Utc(2026, 3, 17, 4, 50), Utc(2026, 3, 17, 9, 10)), (d.ScheduledDeparture!.Value, d.ScheduledArrival!.Value));
        Assert.Equal(("ETD5SM", "896123", "callsign", "Etihad Airways"), (d.Callsign, d.Icao24, d.Match, d.Airline!.Name));
        Assert.Equal((Utc(2026, 3, 17, 5, 12), Utc(2026, 3, 17, 7, 31)), (d.Takeoff!.Value, d.Landing!.Value));
        Assert.Equal(("AUH", "TLV", "A6-AEA"), (d.Departure!.Iata, d.Arrival!.Iata, d.Aircraft!.Registration));
        Assert.Null(d.OperatedBy);
        Assert.Null(d.Track);
        Assert.Contains("30 days", d.TrackNote);
        Assert.Contains("(AUH", box.Queries.Single());
        Assert.Contains("(TLV", box.Queries.Single());
        Assert.Equal(new[] { "OMAA" }, sky.Asked);

        // The Owner types another number: theirs wins, the mail is not searched
        await svc.SetNumberAsync(owner, id, "ey 607", null, default);
        await svc.CheckAsync(owner, default);
        row = await RowAsync(db, id);
        Assert.Equal((FlightStatus.Found, "EY607", "user", "ETD607"), (row.Status, row.FlightNumber, row.Source, row.Details.Callsign));
        Assert.Single(box.Queries);
        Assert.Null(row.Details.Mail);

        // Cleared: found in the mail again
        await svc.ClearAsync(owner, id, default);
        await svc.CheckAsync(owner, default);
        Assert.Equal(("EY593", "mail"), ((await RowAsync(db, id)).FlightNumber, (await RowAsync(db, id)).Source));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SetNumberAsync(owner, id, "hello", null, default));
    }

    [Fact]
    public async Task Without_mail_public_flights_are_suggested_and_a_confirmed_one_is_kept()
    {
        await using var db = Db();
        if (db == null)
        {
            return;
        }

        var owner = "flights-" + Guid.NewGuid();
        // A recent flight: its path is still public
        var start = DateTime.UtcNow.Date.AddDays(-3).AddHours(5).AddMinutes(11);
        var id = await FlightAsync(db, owner, start, start.AddMinutes(139), NearAuh, NearTlv);
        var mail = new Mail(new Box()) { State = MailStates.NotAllowed };
        var sky = new Sky
        {
            Track = Enumerable.Range(0, 900).Select(i => new double[] { i, 24.45 + i * 0.008, 54.66 - i * 0.022, 10000 }).ToList()
        };
        sky.Flights.Add(new OpenSkyFlight("896123", "ETD5SM", start.AddMinutes(1), start.AddMinutes(140), "OMAA", "LLBG"));
        sky.Flights.Add(new OpenSkyFlight("896124", "ETD607", start.AddMinutes(19), start.AddMinutes(160), "OMAA", "LLBG"));
        sky.Flights.Add(new OpenSkyFlight("896125", "ETD11", start.AddMinutes(5), start.AddMinutes(150), "OMAA", "OMDB"));
        var (svc, queue) = Service(db, mail, sky);

        // Opening the panel makes the row and queues the Owner
        Assert.Equal(FlightStatus.Pending, (await svc.GetAsync(owner, id, default))!.Status);
        Assert.Equal(owner, await queue.NextAsync(TimeSpan.Zero, default));
        Assert.Null(await svc.GetAsync("someone-else", id, default));
        await svc.CheckAsync(owner, default);

        var row = await RowAsync(db, id);
        Assert.Equal((FlightStatus.Suggested, MailStates.NotAllowed, (string?)null), (row.Status, row.MailState, row.FlightNumber));
        Assert.Equal(new[] { ("ETD5SM", "EY593", "Etihad Airways", 2), ("ETD607", "EY607", "Etihad Airways", 40) },
            row.Details.Candidates.Select(c => (c.Callsign, c.FlightNumber!, c.Airline!, c.Score)));

        await Assert.ThrowsAsync<ArgumentException>(() => svc.ConfirmAsync(owner, id, 5, default));
        await svc.ConfirmAsync(owner, id, 0, default);
        await svc.CheckAsync(owner, default);
        row = await RowAsync(db, id);
        Assert.Equal((FlightStatus.Found, "EY593", "confirmed", "confirmed"), (row.Status, row.FlightNumber, row.Source, row.Details.Match));
        Assert.InRange(row.Details.Track!.Count, 2, 500);
        Assert.Null(row.Details.TrackNote);

        // Allowed later: rows not searched in mail are looked up again, the Owner's confirmed one is not
        mail.State = MailStates.Searched;
        await svc.CheckAsync(owner, default);
        Assert.Equal("confirmed", (await RowAsync(db, id)).Source);
    }

    [Fact]
    public async Task Odd_flights_are_told_apart_and_a_used_allowance_leaves_them_pending()
    {
        await using var db = Db();
        if (db == null)
        {
            return;
        }

        var owner = "flights-" + Guid.NewGuid();
        var t = Utc(2026, 5, 10, 14, 0);
        var loop = await FlightAsync(db, owner, t, t.AddMinutes(89), NearTlv, (32.2, 34.95));
        var nowhere = await FlightAsync(db, owner, t.AddDays(1), t.AddDays(1).AddHours(3), (0, -30), NearTlv);
        var real = await FlightAsync(db, owner, t.AddDays(2), t.AddDays(2).AddHours(3), NearTlv, NearAuh);
        // Not a flight, and a flight that is not the primary of its cluster: no row
        await FlightAsync(db, owner, t, t.AddHours(1), NearTlv, NearAuh, "drive");
        var sky = new Sky();
        var (svc, _) = Service(db, new Mail(new Box()), sky);

        await svc.CheckAsync(owner, default);

        Assert.Equal(FlightStatus.NotAFlight, (await RowAsync(db, loop)).Status);
        Assert.Contains("near TLV", (await RowAsync(db, loop)).Note);
        Assert.Equal(FlightStatus.NoAirports, (await RowAsync(db, nowhere)).Status);
        Assert.Equal((FlightStatus.NotFound, MailStates.Searched), ((await RowAsync(db, real)).Status, (await RowAsync(db, real)).MailState));

        // OpenSky's allowance used: the flight stays pending, unclaimed, no attempt counted
        sky.Limited = true;
        await svc.SearchAsync(owner, real, default);
        await svc.CheckAsync(owner, default);
        var pending = await RowAsync(db, real);
        Assert.Equal((FlightStatus.Pending, 0, (DateTime?)null), (pending.Status, pending.Attempts, pending.ClaimedAt));
        Assert.Equal(3, await db.Flights.CountAsync(f => f.OwnerId == owner));

        // A fusion run makes another member the primary: the row follows it
        var other = await FlightAsync(db, owner, t.AddDays(2), t.AddDays(2).AddHours(3), NearTlv, NearAuh);
        await db.Activities.Where(a => a.Id == real).ExecuteUpdateAsync(s => s.SetProperty(a => a.ClusterId, other));
        await db.Activities.Where(a => a.Id == other).ExecuteUpdateAsync(s => s.SetProperty(a => a.ClusterId, other));
        await svc.SetNumberAsync(owner, real, "LY 1", null, default);
        await svc.EnsureRowsAsync(owner, default);
        Assert.Equal(("LY1", "user"), ((await RowAsync(db, other)).FlightNumber, (await RowAsync(db, other)).Source));
        Assert.False(await db.Flights.AnyAsync(f => f.ActivityId == real));
    }
}
