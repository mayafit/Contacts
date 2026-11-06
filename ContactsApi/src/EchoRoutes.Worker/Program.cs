/**
 * @fileoverview EchoRoutes.Worker: the route-quality service (AD-33). Reads Atlas only
 * through the backend's /internal/routes/*, writes only the routes schema, and talks
 * to Valhalla on the pod's loopback.
 * @module EchoRoutes.Worker
 */
using ContactsApi.Infrastructure.Routes;
using ContactsApi.Infrastructure.Tenancy;
using EchoRoutes.Worker;
using EchoRoutes.Worker.Api;
using EchoRoutes.Worker.Assess;
using EchoRoutes.Worker.Backend;
using EchoRoutes.Worker.Matching;
using EchoRoutes.Worker.Scheduling;
using EchoRoutes.Worker.Valhalla;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateLogger();
builder.Host.UseSerilog();

builder.Services.Configure<RoutesOptions>(builder.Configuration.GetSection("Routes"));
var options = builder.Configuration.GetSection("Routes").Get<RoutesOptions>() ?? new RoutesOptions();
var rls = builder.Configuration.GetSection(RowLevelSecurityOptions.Section).Get<RowLevelSecurityOptions>() ?? new RowLevelSecurityOptions();

// The migrate-only init container (AD-49 rule 3): apply the routes migrations as routes_migrator and exit
if (EchoMigrator.MigrateOnlyRequested(args))
{
    Environment.ExitCode = await EchoMigrator.RunMigrateOnlyAsync<RoutesDbContext>(
        builder.Configuration["Routes:MigrationConnectionString"],
        cs => new DbContextOptionsBuilder<RoutesDbContext>()
            .UseNpgsql(cs, n => n.MigrationsHistoryTable(RoutesDbContext.HistoryTable, RoutesDbContext.Schema)).Options,
        ContactsApi.Infrastructure.Routes.Migrations.RoutesRowLevelSecurity.Id, rls.Policies,
        new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger).CreateLogger("EchoMigrator"));
    Log.CloseAndFlush();
    return;
}

// Row-level security (AD-49 rule 4): the owner scope and its interceptor, and the
// connection string guards (no multiplexing, reset on close, application name, pool 10)
builder.Services.AddOwnerScoping(rls);
var routesConnection = OwnerConnectionStrings.Prepare(options.ConnectionString, "echo-routes", 10);
builder.Services.AddDbContext<RoutesDbContext>((sp, o) =>
    o.UseNpgsql(string.IsNullOrWhiteSpace(routesConnection) ? "Host=unset" : routesConnection,
        npgsql => npgsql.MigrationsHistoryTable(RoutesDbContext.HistoryTable, RoutesDbContext.Schema)).UseOwnerRls(sp));

builder.Services.AddHttpClient(ValhallaClient.HttpClientName, c =>
{
    c.BaseAddress = new Uri(options.ValhallaUrl.TrimEnd('/') + "/");
    // A backstop only: ValhallaClient enforces Routes__EngineTimeoutSeconds per call
    c.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.EngineTimeoutSeconds) + 15);
});
builder.Services.AddHttpClient(RoutesBackendClient.HttpClientName, c =>
{
    c.BaseAddress = new Uri(options.BackendUrl.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(60);
    if (!string.IsNullOrEmpty(options.ServiceToken))
    {
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ServiceToken);
    }
});
builder.Services.AddSingleton<IValhallaClient, ValhallaClient>();
builder.Services.AddSingleton<IRoutesBackend, RoutesBackendClient>();
builder.Services.AddSingleton<GraphInfoSource>();
builder.Services.AddSingleton(sp =>
{
    // The tiles image's outlines; the bundled one until they are mounted
    var coverage = RegionCoverage.Load(options.RegionsPath);
    if (!coverage.Any)
    {
        sp.GetRequiredService<ILogger<RegionCoverage>>().LogWarning(
            "No region outlines at {Path}; using the bundled coarse outline", options.RegionsPath);
        coverage = RegionCoverage.Load(Path.Combine(AppContext.BaseDirectory, "Regions"));
    }

    return coverage;
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EngineBreaker>();
builder.Services.AddScoped<AssessRunner>();
builder.Services.AddScoped<MatchRunner>();
builder.Services.AddScoped<RebuildRunner>();
builder.Services.AddSingleton<WakeSignal>();
builder.Services.AddSingleton<IPause, TaskPause>();
builder.Services.AddSingleton<RoutesMigrationState>();
builder.Services.AddHostedService<RoutesScheduler>();

builder.Services.AddHealthChecks().AddCheck<RoutesReadyCheck>("ready", tags: new[] { "ready" });

var app = builder.Build();

app.UseBackendToken();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.MapInternalApi();

Log.Information("EchoRoutes.Worker starting up");
try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "EchoRoutes.Worker terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
