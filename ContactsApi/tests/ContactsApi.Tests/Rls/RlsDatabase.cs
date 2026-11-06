using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Atlas.Flights;
using ContactsApi.Infrastructure.Routes;
using ContactsApi.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ContactsApi.Tests.Rls;

/// <summary>A fact that runs only where Docker answers (the Testcontainers database of AD-49 rule 9)</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    /// <summary>Why Docker is not usable here, or null when it is</summary>
    public static readonly Lazy<string?> Unavailable = new(Probe);

    /// <summary>Skips the fact when Docker is not available</summary>
    public DockerFactAttribute()
    {
        if (Unavailable.Value is { } reason)
        {
            Skip = "Docker is not available, so the PostgreSQL 18 row-level security tests (AD-49 rule 9) did not run: " + reason;
        }
    }

    private static string? Probe()
    {
        if (Environment.GetEnvironmentVariable("ECHO_SKIP_DOCKER_TESTS") is { Length: > 0 })
        {
            return "ECHO_SKIP_DOCKER_TESTS is set";
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            })!;
            if (!p.WaitForExit(20_000))
            {
                p.Kill(true);
                return "`docker info` did not answer within 20 s";
            }

            return p.ExitCode == 0 ? null : "`docker info` failed: " + p.StandardError.ReadToEnd().Trim();
        }
        catch (Exception ex)
        {
            return "the docker CLI could not be run (" + ex.Message + ")";
        }
    }
}

/// <summary>The xunit collection sharing one PostgreSQL 18 container</summary>
[CollectionDefinition(Name)]
public sealed class RlsCollection : ICollectionFixture<RlsDatabase>
{
    /// <summary>The collection name</summary>
    public const string Name = "rls-postgres";
}

/// <summary>
/// PostgreSQL 18 in a container, set up as production will be (AD-49 rule 9): the same
/// db-roles.sql, the migrations applied as atlas_migrator and routes_migrator (policies
/// on), db-roles.sql again, and Owners A, B and C seeded in every table through the
/// migrators. The tests then run as the runtime roles with the real interceptor.
/// </summary>
public sealed class RlsDatabase : IAsyncLifetime
{
    /// <summary>The seeded Owners (C is the one the destructive tests may delete)</summary>
    public const string A = "owner-a-1001", B = "owner-b-2002", C = "owner-c-3003";

    private PostgreSqlContainer? _pg;
    private readonly Dictionary<string, string> _passwords = new();

    /// <summary>The seeded activity of each Owner (a flight with two points)</summary>
    public Dictionary<string, Guid> ActivityOf { get; } = new();

    /// <summary>The seeded place of each Owner</summary>
    public Dictionary<string, Guid> PlaceOf { get; } = new();

    /// <summary>The PAT of each Owner (its SHA-256 is stored)</summary>
    public static string TokenOf(string owner) => "atl_token-of-" + owner;

    /// <summary>The superuser's connection string</summary>
    public string Superuser { get; private set; } = string.Empty;

    /// <summary>The container (pg_dump runs inside it)</summary>
    public PostgreSqlContainer Container => _pg ?? throw new InvalidOperationException("Docker is not available");

    /// <summary>The atlas tables with OwnerId and the routes tables, as the policy migrations list them</summary>
    public static IEnumerable<string> AtlasOwnerTables => Infrastructure.Atlas.Migrations.AtlasRowLevelSecurity.OwnerTables;

    /// <summary>The routes tables</summary>
    public static IEnumerable<string> RoutesOwnerTables => Infrastructure.Routes.Migrations.RoutesRowLevelSecurity.OwnerTables;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        if (DockerFactAttribute.Unavailable.Value != null)
        {
            return;
        }

        _pg = new PostgreSqlBuilder("postgres:18")
            .WithDatabase("contactsdb").WithUsername("postgres").WithPassword(NewPassword())
            .Build();
        await _pg.StartAsync();
        Superuser = _pg.GetConnectionString();

        var roles = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Rls", "db-roles.sql"));
        await ExecAsync(Superuser, roles);
        foreach (var role in new[] { "contactsapi", "echo_routes", "atlas_migrator", "routes_migrator", "contacts_backup" })
        {
            _passwords[role] = NewPassword();
            await ExecAsync(Superuser, $"ALTER ROLE {role} PASSWORD '{_passwords[role]}'");
        }

        var log = NullLogger.Instance;
        await using (var atlas = MigratorAtlas())
        {
            await EchoMigrator.MigrateAsync(atlas, Infrastructure.Atlas.Migrations.AtlasRowLevelSecurity.Id, true, log, default);
        }

        await using (var routes = MigratorRoutes())
        {
            await EchoMigrator.MigrateAsync(routes, Infrastructure.Routes.Migrations.RoutesRowLevelSecurity.Id, true, log, default);
        }

        // Again, now that the tables exist (README of the script: a fresh database needs two runs)
        await ExecAsync(Superuser, roles);
        foreach (var owner in new[] { A, B, C })
        {
            await SeedAsync(owner);
        }
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_pg != null)
        {
            await _pg.DisposeAsync();
        }
    }

    /// <summary>A role's connection string (optionally with a pool size)</summary>
    public string As(string role, int? maxPool = null, bool pooling = true)
    {
        var b = new NpgsqlConnectionStringBuilder(Superuser) { Username = role, Password = _passwords[role], Pooling = pooling };
        if (maxPool is { } n)
        {
            b.MaxPoolSize = n;
            // A distinct pool per test that asks for a size (the key is the string)
            b.ApplicationName = "rls-test-" + Guid.NewGuid().ToString("N")[..8];
        }

        return b.ConnectionString;
    }

    /// <summary>A role's password (pg_dump inside the container)</summary>
    public string PasswordOf(string role) => _passwords[role];

    /// <summary>The atlas context as atlas_migrator (no interceptor; BYPASSRLS)</summary>
    public AtlasDbContext MigratorAtlas() =>
        new(new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql(As("atlas_migrator"), n => n.MigrationsHistoryTable("__ef_migrations", "atlas")).Options);

    /// <summary>The routes context as routes_migrator</summary>
    public RoutesDbContext MigratorRoutes() =>
        new(new DbContextOptionsBuilder<RoutesDbContext>()
            .UseNpgsql(As("routes_migrator"), n => n.MigrationsHistoryTable(RoutesDbContext.HistoryTable, RoutesDbContext.Schema)).Options);

    /// <summary>
    /// The backend's services as production registers them: contactsapi, the owner scope
    /// and the interceptor on AtlasDbContext and RoutesDbContext
    /// </summary>
    public ServiceProvider Backend(OwnerRlsMode mode = OwnerRlsMode.Enforce, int? maxPool = null, ILoggerProvider? logs = null)
    {
        var cs = As("contactsapi", maxPool);
        var services = new ServiceCollection();
        services.AddLogging(l =>
        {
            if (logs != null)
            {
                l.AddProvider(logs);
            }
        });
        services.AddOwnerScoping(new RowLevelSecurityOptions { Mode = mode });
        services.AddDbContext<AtlasDbContext>((sp, o) =>
            o.UseNpgsql(cs, n => n.MigrationsHistoryTable("__ef_migrations", "atlas")).UseOwnerRls(sp));
        services.AddDbContext<RoutesDbContext>((sp, o) => o.UseNpgsql(cs).UseOwnerRls(sp));
        services.AddScoped<AtlasRoutesFeed>();
        return services.BuildServiceProvider();
    }

    /// <summary>The route service's services: echo_routes, the owner scope and the interceptor</summary>
    public ServiceCollection Worker(OwnerRlsMode mode = OwnerRlsMode.Enforce)
    {
        var cs = As("echo_routes");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOwnerScoping(new RowLevelSecurityOptions { Mode = mode });
        services.AddDbContext<RoutesDbContext>((sp, o) =>
            o.UseNpgsql(cs, n => n.MigrationsHistoryTable(RoutesDbContext.HistoryTable, RoutesDbContext.Schema)).UseOwnerRls(sp));
        return services;
    }

    /// <summary>Runs SQL on a fresh unpooled connection</summary>
    public static async Task ExecAsync(string cs, string sql)
    {
        await using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A scalar on a fresh unpooled connection</summary>
    public static async Task<T> ScalarAsync<T>(string cs, string sql)
    {
        await using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    /// <summary>Seeds one Owner's rows in every owner table, through the migrators</summary>
    public async Task SeedAsync(string owner)
    {
        var t = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);
        await using var db = MigratorAtlas();
        var act = new AtlasActivity
        {
            OwnerId = owner, Type = "flight", Source = "gpx", ExternalId = "act-" + owner, StartTime = t, EndTime = t.AddHours(1), PointCount = 2,
            Points =
            {
                new TrackPoint { Time = t, Lat = 32.0, Lon = 34.8 },
                new TrackPoint { Time = t.AddMinutes(30), Lat = 32.1, Lon = 34.9 }
            }
        };
        var place = new AtlasPlace { OwnerId = owner, Name = "Somewhere", Lat = 32.0, Lon = 34.8 };
        db.Activities.Add(act);
        db.Places.Add(place);
        db.Fusions.Add(new ActivityFusion { ActivityId = act.Id, OwnerId = owner, StartTime = t, EndTime = t.AddHours(1) });
        db.Efforts.Add(new ActivityEffort { ActivityId = act.Id, OwnerId = owner, Type = "running" });
        db.FusionDecisions.Add(new FusionDecision { OwnerId = owner, ActivityA = Guid.NewGuid(), ActivityB = Guid.NewGuid() });
        db.FusionQueue.Add(new FusionQueueItem { OwnerId = owner, Full = true });
        db.FusionSettings.Add(new FusionSetting { OwnerId = owner });
        db.HealthSamples.Add(new HealthSample { OwnerId = owner, StartTime = t, Value = 60 });
        db.Imports.Add(new ImportRun { OwnerId = owner, Source = "gpx" });
        db.OwnerSettings.Add(new OwnerSetting { OwnerId = owner });
        db.Photos.Add(new AtlasPhoto { OwnerId = owner, ExternalId = "photo-" + owner, Name = "p.jpg", TakenAt = t, TakenAtRaw = t });
        db.Visits.Add(new PlaceVisit { OwnerId = owner, PlaceId = place.Id, StartTime = t, EndTime = t.AddHours(1), Lat = 32.0, Lon = 34.8 });
        db.Reviews.Add(new PlaceReview { OwnerId = owner, PlaceId = place.Id, Name = "Somewhere" });
        db.ReviewReminders.Add(new ReviewReminder { OwnerId = owner, PlaceId = place.Id });
        db.Flights.Add(new AtlasFlight { OwnerId = owner, ActivityId = act.Id });
        db.Connections.Add(new SourceConnection { OwnerId = owner, Provider = "onedrive", SecretProtected = "protected", Status = "idle" });
        db.AccessTokens.Add(new AccessToken { OwnerId = owner, Name = "test", TokenHash = Hash(TokenOf(owner)), Prefix = "atl_toke" });
        await db.SaveChangesAsync();
        ActivityOf[owner] = act.Id;
        PlaceOf[owner] = place.Id;

        await using var routes = MigratorRoutes();
        routes.Quality.Add(new RouteQualityRow { OwnerId = owner, ActivityId = act.Id, SourceKey = "k-" + owner, SourceUpdatedAt = t });
        routes.MatchedRoutes.Add(new MatchedRouteRow { OwnerId = owner, ActivityId = act.Id, SourceKey = "k-" + owner, SourceUpdatedAt = t });
        routes.Decisions.Add(new RouteDecision { OwnerId = owner, SourceKey = "k-" + owner });
        routes.Waypoints.Add(new RouteWaypoints { OwnerId = owner, SourceKey = "k-" + owner });
        routes.Settings.Add(new RouteSetting { OwnerId = owner });
        routes.Jobs.Add(new RouteJob { OwnerId = owner, Priority = RouteJobPriorities.Interactive, State = RouteJobStates.Queued });
        routes.Cursors.Add(new OwnerCursor { OwnerId = owner });
        await routes.SaveChangesAsync();
    }

    /// <summary>A PAT's stored hash (as AccessTokenService stores it)</summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string NewPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(18));
}
