using Microsoft.EntityFrameworkCore;
using ContactsApi.Infrastructure.Vault;
using ContactsApi.WebApi.Configuration;
using ContactsApi.WebApi.Middleware;
using ContactsApi.WebApi.Services;
using ContactsApi.WebApi.Services.KeyRing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Serilog;
using Serilog.Formatting.Compact;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter())
    .WriteTo.File(
        new CompactJsonFormatter(),
        path: "logs/contacts-api-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .CreateLogger();

builder.Host.UseSerilog();

// The migrate-only init container (AD-49 rule 3): apply the Atlas migrations and exit
if (ContactsApi.Infrastructure.Tenancy.EchoMigrator.MigrateOnlyRequested(args))
{
    Environment.ExitCode = await RowLevelSecurity.MigrateOnlyAsync(builder.Configuration);
    Log.CloseAndFlush();
    return;
}

// Add services to the container
builder.Services.AddControllers();

// Register Vault client for secret management
builder.Services.AddSingleton<IVaultClient, VaultClient>();

// Configure Google OAuth options
builder.Services.Configure<GoogleOAuthOptions>(options =>
{
    options.ClientId = builder.Configuration["GoogleOAuth:ClientId"] ?? string.Empty;
    options.RedirectUri = builder.Configuration["GoogleOAuth:RedirectUri"] ?? string.Empty;
    options.Scopes = builder.Configuration.GetSection("GoogleOAuth:Scopes").Get<string[]>() ?? Array.Empty<string>();
    // In the cluster the Vault Agent sidecar delivers the secret as the
    // GoogleOAuth__ClientSecret env var, so read it from configuration first.
    // VaultConfigurationService only fills it in (local dev) when it is empty.
    options.ClientSecret = builder.Configuration["GoogleOAuth:ClientSecret"] ?? string.Empty;
});

// Register hosted service to load secrets from Vault at startup
builder.Services.AddHostedService<VaultConfigurationService>();
// Google's OAuth endpoints (code exchange, ID token, refresh, revoke)
builder.Services.AddHttpClient(GoogleSignIn.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<IGoogleSignIn, GoogleSignIn>();
builder.Services.AddSingleton<GoogleTokenService>();
builder.Services.AddSingleton<ActivityStore>();
// Public profiles by email hash (AD-46 rule 3); Gravatar__ApiKey is optional
builder.Services.Configure<GravatarOptions>(builder.Configuration.GetSection("Gravatar"));
builder.Services.AddSingleton<ProfileStore>();
builder.Services.AddSingleton<ProfileQueue>();
builder.Services.AddHostedService<ProfileWorker>();
builder.Services.AddSingleton<ScopePreferences>();
// Dismissed suggestions from Other contacts (AD-48 rule 7)
builder.Services.AddSingleton<OtherContactsStore>();

// Assistant: Gemini key from Vault (Gemini__ApiKey); without it the assistant reports itself disabled
builder.Services.Configure<ContactsApi.WebApi.Controllers.GeminiOptions>(builder.Configuration.GetSection("Gemini"));
builder.Services.AddSingleton<GeminiService>();
// The phonetic fill's Google Translate (AD-43 rule 4); off until GoogleTranslate__ApiKey is set
builder.Services.Configure<GoogleTranslateOptions>(builder.Configuration.GetSection("GoogleTranslate"));
builder.Services.AddSingleton<NameTranslator>();
builder.Services.AddHttpClient("gemini", c =>
{
    c.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    c.Timeout = TimeSpan.FromSeconds(90);
});
// Atlas: PostgreSQL (Database__ConnectionString from Vault); without it Atlas
// stays off and the rest of the app works as before
// Row-level security (AD-49): the owner scope, its interceptor on every DbContext, and the
// connection string guards (no multiplexing, reset on close, application name, pool size)
builder.Services.AddEchoRowLevelSecurity(builder.Configuration);
var atlasConnection = ContactsApi.Infrastructure.Tenancy.OwnerConnectionStrings.Prepare(
    builder.Configuration["Database:ConnectionString"], RowLevelSecurity.ApplicationName, RowLevelSecurity.MaxPoolSize);
builder.Services.AddSingleton(new AtlasReadiness { Configured = !string.IsNullOrWhiteSpace(atlasConnection) });
builder.Services.AddDbContext<ContactsApi.Infrastructure.Atlas.AtlasDbContext>((sp, o) =>
    ContactsApi.Infrastructure.Tenancy.OwnerScopeServices.UseOwnerRls(
        o.UseNpgsql(string.IsNullOrWhiteSpace(atlasConnection) ? "Host=unset" : atlasConnection,
            npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", "atlas")), sp));
builder.Services.AddScoped<ContactsApi.Infrastructure.Atlas.AtlasService>();
// Routes module (AD-34): reads routes (matched lines, quality) and writes decisions only,
// with the backend's own role; the schema's migrations belong to the route-quality service
builder.Services.AddDbContext<ContactsApi.Infrastructure.Routes.RoutesDbContext>((sp, o) =>
    ContactsApi.Infrastructure.Tenancy.OwnerScopeServices.UseOwnerRls(
        o.UseNpgsql(string.IsNullOrWhiteSpace(atlasConnection) ? "Host=unset" : atlasConnection), sp));
builder.Services.AddSingleton(new ContactsApi.Infrastructure.Routes.RouteGeometryOptions
{
    Enabled = !string.IsNullOrWhiteSpace(atlasConnection) && !string.IsNullOrWhiteSpace(builder.Configuration["Routes:ServiceUrl"])
});
builder.Services.AddScoped<ContactsApi.Core.Interfaces.IRouteGeometryReader, ContactsApi.Infrastructure.Routes.RouteGeometryReader>();
builder.Services.AddScoped<ContactsApi.Infrastructure.Routes.RouteOwnerData>();
// The route-quality service reads Atlas only through /internal/routes/* (AD-33)
builder.Services.AddScoped<ContactsApi.Infrastructure.Atlas.AtlasRoutesFeed>();
builder.Services.AddSingleton<InteractiveLoad>();
// The backend calls the route-quality service's /internal/v1 with Routes__BackendToken
RoutesServiceClient.Configure(builder.Services, builder.Configuration);
builder.Services.AddSingleton<RoutesServiceClient>();
// Edits of a type, merges and splits wake the service (imports wake it after fusion)
builder.Services.AddSingleton<ContactsApi.Core.Interfaces.IRouteChangeSignal>(sp => sp.GetRequiredService<RoutesServiceClient>());
// The fusion service: imports queue their time windows, it fuses them in the background
builder.Services.AddSingleton<FusionSignal>();
builder.Services.AddSingleton<ContactsApi.Infrastructure.Atlas.IFusionSignal>(sp => sp.GetRequiredService<FusionSignal>());
builder.Services.AddSingleton<FusionStatusBoard>();
builder.Services.AddHostedService<AtlasFusionWorker>();
builder.Services.AddScoped<AccessTokenService>();
// A rate limit per MCP token (AD-50 rule 9): Mcp__RateLimit__PerMinute / __PerDay
builder.Services.AddSingleton(builder.Configuration.GetSection("Mcp:RateLimit").Get<PatRateLimitOptions>() ?? new PatRateLimitOptions());
builder.Services.AddSingleton<PatRateLimiter>();
builder.Services.AddHostedService<AtlasMigrator>();
// Connected sources (Sports Tracker): connect, background sync
builder.Services.AddHttpClient(ContactsApi.Infrastructure.Atlas.SportsTrackerClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<AtlasSyncQueue>();
// What is at a point (OpenStreetMap Nominatim and Overpass)
// Each request has its own deadline (a busy Overpass queues for up to a minute)
builder.Services.AddHttpClient<ContactsApi.Infrastructure.Atlas.GeoLookupClient>(c => c.Timeout = TimeSpan.FromSeconds(90));
builder.Services.AddScoped<AtlasConnectionService>();
builder.Services.AddHostedService<AtlasSyncWorker>();
// OneDrive photos: the Entra app's id and secret come from Vault (MicrosoftOAuth__*),
// the redirect from the config map; without them OneDrive reports itself not set up
builder.Services.Configure<OneDriveOptions>(builder.Configuration.GetSection("MicrosoftOAuth"));
builder.Services.AddHttpClient(ContactsApi.Infrastructure.Atlas.OneDriveClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<OneDriveTokenCache>();
builder.Services.AddSingleton<PhotoSyncQueue>();
builder.Services.AddScoped<OneDrivePhotoService>();
builder.Services.AddHostedService<PhotoSyncWorker>();
// Reviews from a scheduled Takeout on Drive (AD-40): the Owner's Google sign-in, with the Drive opt-in
builder.Services.Configure<TakeoutOptions>(builder.Configuration.GetSection("Takeout"));
builder.Services.AddHttpClient(ContactsApi.Infrastructure.Atlas.GoogleDriveTakeout.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(15));
// The Owner's settings documents (AD-41)
builder.Services.AddScoped<ContactsApi.Infrastructure.Atlas.OwnerSettingsService>();
builder.Services.AddSingleton<TakeoutDriveQueue>();
builder.Services.AddSingleton<ITakeoutTokens, GoogleTakeoutTokens>();
builder.Services.AddScoped<TakeoutDriveService>();
builder.Services.AddHostedService<TakeoutDriveWorker>();
// Flights (AD-42): the Owner's mail (T2), The OpenSky Network and adsbdb
builder.Services.Configure<ContactsApi.Infrastructure.Atlas.Flights.OpenSkyOptions>(builder.Configuration.GetSection("OpenSky"));
builder.Services.AddHttpClient(ContactsApi.Infrastructure.Atlas.Flights.OpenSkyClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(90));
builder.Services.AddHttpClient(ContactsApi.Infrastructure.Atlas.Flights.AdsbdbClient.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Echo/1.0 (personal atlas)");
});
builder.Services.AddSingleton<ContactsApi.Infrastructure.Atlas.Flights.IOpenSky>(sp => new ContactsApi.Infrastructure.Atlas.Flights.OpenSkyClient(
    () => sp.GetRequiredService<IHttpClientFactory>().CreateClient(ContactsApi.Infrastructure.Atlas.Flights.OpenSkyClient.HttpClientName),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ContactsApi.Infrastructure.Atlas.Flights.OpenSkyOptions>>(),
    sp.GetRequiredService<ILogger<ContactsApi.Infrastructure.Atlas.Flights.OpenSkyClient>>()));
builder.Services.AddSingleton<ContactsApi.Infrastructure.Atlas.Flights.IAdsbdb>(sp => new ContactsApi.Infrastructure.Atlas.Flights.AdsbdbClient(
    () => sp.GetRequiredService<IHttpClientFactory>().CreateClient(ContactsApi.Infrastructure.Atlas.Flights.AdsbdbClient.HttpClientName)));
builder.Services.AddSingleton<IFlightMail, GmailFlightMail>();
builder.Services.AddSingleton<FlightQueue>();
builder.Services.AddScoped<FlightService>();
builder.Services.AddHostedService<FlightWorker>();
builder.Services.AddHttpContextAccessor();

// MCP server over the Atlas service (tools in AtlasMcpTools), at /api/mcp
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<AtlasMcpTools>();

builder.Services.AddSingleton<ActivityWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ActivityWorker>());

// Configure Redis connection
//
// AbortOnConnectFail=false is the whole point of this block, and it is the
// difference between a pod that rides out a slow Redis and a pod that needs
// hundreds of restarts to get lucky.
//
// StackExchange.Redis defaults that flag to TRUE, which makes the synchronous
// Connect() below throw if Redis is not already accepting connections at the
// instant this process starts. Nothing catches it, and this runs before the
// host is even built, so the throw takes the container down -- straight into
// CrashLoopBackOff, where the only thing that resolves it is Redis happening
// to win the next startup race.
//
// That is not hypothetical. In the 2026-08-31 power outage every workload came
// back at once; contacts-redis-master-0 was still starting, and this line had
// restarted contacts-backend 755 times by the time anyone looked. The
// surfaced error was a RedisConnectionException, which pointed here and hid
// the actual trigger (a sealed Vault upstream) completely.
//
// With the flag off, Connect() returns immediately with a multiplexer that
// reconnects in the background. A cold or briefly absent Redis then costs a
// few seconds of degraded sessions instead of an outage.
//
// Redis requires a password (AD-50 rule 1): Redis__Password from Vault is added to the
// connection string the ConfigMap renders without it.
var redisOptions = RedisSettings.Options(builder.Configuration);

var redis = ConnectionMultiplexer.Connect(redisOptions);
builder.Services.AddSingleton<IConnectionMultiplexer>(redis);

// Data Protection (AD-12, AD-50 rule 2): the ring in Redis (DataProtection-Keys, as before),
// wrapped with the Vault certificate (DataProtection__Certificate) when it is configured;
// without it the ring stays unwrapped and a warning is logged. DataProtection__Rotation runs
// the one-shot re-protection (dry-run, reprotect, retire).
builder.Services.AddEchoDataProtection(builder.Configuration, redis);

// Configure session management with Redis
builder.Services.AddStackExchangeRedisCache(options =>
{
    // Built from the same options, NOT from the raw string. The cache opens
    // its own multiplexer, and passing the string here would parse it fresh
    // with AbortOnConnectFail back at its default -- reintroducing the exact
    // failure this block exists to remove, one connection over.
    // Clone() because a multiplexer takes ownership of the instance it is
    // given, and these are two separate connections.
    options.ConfigurationOptions = redisOptions.Clone();
    options.InstanceName = "ContactsApi_";
});

// EchoSession (AD-3 rule 3, AD-50 rule 4): cookie authentication with the ticket in Redis,
// re-keyed at sign-in, under __Host- cookies; the ASP.NET session keeps only non-identity
// state (the pre-sign-in OAuth flow). The allowlist (AD-50 rule 6) comes from Echo__AllowedEmails.
builder.Services.AddEchoSession(builder.Configuration);

// Configure CORS
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials(); // Required for cookies
    });
});

// EchoService: internal services' tokens (Routes__ServiceToken from Vault), each
// accepted only on its own /internal path (AD-3 rule 3, AD-33 rule 2); never the default scheme
builder.Services.AddEchoServiceAuth(builder.Configuration["Routes:ServiceToken"]);

// CSRF (AD-50 rule 7): the public origin and, in Development, the CORS origins
builder.Services.AddSingleton(new ContactsApi.WebApi.Middleware.CsrfOptions
{
    PublicOrigin = builder.Configuration["Frontend:BaseUrl"],
    DevelopmentOrigins = allowedOrigins,
    IsDevelopment = builder.Environment.IsDevelopment()
});

// Add API documentation
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add health checks
builder.Services.AddHealthChecks()
    // Unready while a migration waits for the migrate init container (AD-49 rule 3)
    .AddCheck<AtlasMigrationsCheck>("atlas-migrations", tags: new[] { AtlasMigrationsCheck.ReadyTag });

var app = builder.Build();

// Configure the HTTP request pipeline

// Exception handling middleware (must be first)
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Request logging middleware (should be early in pipeline)
app.UseMiddleware<RequestLoggingMiddleware>();

// Swagger only in Development (AD-50 rule 10)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Contacts API V1");
        c.RoutePrefix = string.Empty; // Serve Swagger UI at root
    });
}

// HTTPS redirection (disabled for development - will be handled by reverse proxy)
// app.UseHttpsRedirection();

// CORS must come before authentication
app.UseCors();

// State-changing /api calls need X-Echo-Request and same-origin fetch metadata (AD-50 rule 7)
app.UseMiddleware<ContactsApi.WebApi.Middleware.CsrfProtectionMiddleware>();

// Map and timeline reads in flight: the route-quality service's batch calls wait
app.UseMiddleware<InteractiveLoadMiddleware>();

// Nothing under /internal/** is anonymous: only an EchoService token valid for the path passes
app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/internal"), internalApi => internalApi.Use(async (ctx, next) =>
{
    var result = await ctx.AuthenticateAsync(EchoServiceDefaults.Scheme);
    if (!result.Succeeded)
    {
        await ctx.ChallengeAsync(EchoServiceDefaults.Scheme);
        return;
    }

    await next();
}));

// Session management: the ASP.NET session (non-identity state), loaded up front, then the
// EchoSession ticket for /api (not /api/mcp, and not the OAuth callback, AD-50 rule 4)
app.UseSession();
app.UseMiddleware<EchoSessionMiddleware>();

// The request's Owner from the session (AD-49 rule 4); /api/mcp sets it from its token below
app.UseMiddleware<OwnerScopeMiddleware>();

// Authorization: only the EchoService policy of /internal/** so far; the session and
// PAT schemes of AD-3 rule 3 are still per controller and per path
app.UseAuthorization();

// Map controllers
// MCP clients authenticate with a personal access token (Bearer), never the
// browser session; the token's owner is passed to the tools
app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/api/mcp"), mcp => mcp.Use(async (ctx, next) =>
{
    var readiness = ctx.RequestServices.GetRequiredService<AtlasReadiness>();
    var header = ctx.Request.Headers.Authorization.ToString();
    var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : string.Empty;
    // Unknown, revoked and expired tokens are all refused the same way (AD-50 rule 9). The
    // token's Owner is not known yet: CheckAsync resolves it through the definer function
    // (AD-49 rule 5), then sets the request's Owner and reads the token's rights under it.
    var checkedToken = readiness.Ready && token.Length > 0
        ? await ctx.RequestServices.GetRequiredService<AccessTokenService>().CheckAsync(token)
        : null;
    if (checkedToken == null)
    {
        ctx.Response.StatusCode = readiness.Ready ? StatusCodes.Status401Unauthorized : StatusCodes.Status503ServiceUnavailable;
        ctx.Response.Headers.WWWAuthenticate = "Bearer";
        await ctx.Response.WriteAsync(readiness.Ready ? "A personal access token is required (Atlas → AI access)." : "Atlas is starting.");
        return;
    }

    // In process memory: no database work, so no Owner scope is involved
    if (!ctx.RequestServices.GetRequiredService<PatRateLimiter>().TryAcquire(checkedToken.Id, out var retryAfter))
    {
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await ctx.Response.WriteAsync("This access token has made too many calls. Try again later.");
        return;
    }

    ctx.Items[AtlasMcpTools.OwnerItem] = checkedToken.OwnerId;
    ctx.Items[AtlasMcpTools.CanWriteItem] = checkedToken.CanWrite;
    await next();
}));

app.MapControllers();
app.MapMcp("/api/mcp");

// Health check endpoint
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = c => !c.Tags.Contains(AtlasMigrationsCheck.ReadyTag)
});
app.MapHealthChecks("/health/ready");

// Log application startup
Log.Information("ContactsApi starting up");
var allowlist = app.Services.GetRequiredService<AllowedEmails>();
if (allowlist.IsConfigured)
{
    Log.Information("Sign-in allowlist: {Count} addresses", allowlist.Count);
}
else
{
    Log.Warning("Sign-in allowlist ({Key}) is empty or missing: nobody can sign in, and existing sessions are refused", "Echo__AllowedEmails");
}

// Admin allowlist (Epic 22): operator-only features (system status bar, on-demand
// Grafana). Fail-closed — when empty the feature is simply hidden for everyone.
var admins = app.Services.GetRequiredService<AdminEmails>();
if (admins.IsConfigured)
{
    Log.Information("Admin allowlist: {Count} addresses (ops status bar enabled)", admins.Count);
}
else
{
    Log.Information("Admin allowlist ({Key}) is empty or missing: the ops status bar is hidden for everyone", "Echo__AdminEmails");
}

try
{
    app.Run();
    Log.Information("ContactsApi shut down gracefully");
}
catch (Exception ex)
{
    Log.Fatal(ex, "ContactsApi terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
