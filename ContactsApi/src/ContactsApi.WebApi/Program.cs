using ContactsApi.Infrastructure.Vault;
using ContactsApi.WebApi.Configuration;
using ContactsApi.WebApi.Middleware;
using ContactsApi.WebApi.Services;
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
builder.Services.AddSingleton<GoogleTokenService>();
builder.Services.AddSingleton<ActivityStore>();

// Assistant: Gemini key from Vault (Gemini__ApiKey); without it the assistant reports itself disabled
builder.Services.Configure<ContactsApi.WebApi.Controllers.GeminiOptions>(builder.Configuration.GetSection("Gemini"));
builder.Services.AddHttpClient("gemini", c =>
{
    c.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    c.Timeout = TimeSpan.FromSeconds(90);
});
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
var redisConnectionString = builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379";
var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
redisOptions.AbortOnConnectFail = false;
redisOptions.ConnectRetry = 5;
redisOptions.ConnectTimeout = 5000;

var redis = ConnectionMultiplexer.Connect(redisOptions);

// Configure Data Protection to persist keys to Redis
builder.Services.AddDataProtection()
    .PersistKeysToStackExchangeRedis(redis, "DataProtection-Keys")
    .SetApplicationName("ContactsApi");

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

builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromDays(7); // Session expires after 7 days
    options.Cookie.HttpOnly = true; // Prevent JavaScript access
    // Secure outside local development: the app is served over HTTPS (TLS ends at
    // Cloudflare/nginx, so the request scheme here is http and SameAsRequest
    // would never set it). Browsers treat http://localhost as secure anyway.
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax; // Allow OAuth redirects while maintaining CSRF protection
    options.Cookie.IsEssential = true; // Required for GDPR compliance
    options.Cookie.Name = ".ContactsApi.Session";
    // No Cookie.Domain: a host-only cookie works on whichever host serves the
    // app (cookies ignore ports, so localhost:3002 -> :5000 still works), while
    // a hardcoded "localhost" made browsers drop the cookie everywhere else.
    options.Cookie.Path = "/"; // Cookie available for all paths
    // Persistent, so closing the browser does not sign the user out; the
    // session itself still expires after 7 idle days.
    options.Cookie.MaxAge = TimeSpan.FromDays(7);
});

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

// Add API documentation
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add health checks
builder.Services.AddHealthChecks();

var app = builder.Build();

// Configure the HTTP request pipeline

// Exception handling middleware (must be first)
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Request logging middleware (should be early in pipeline)
app.UseMiddleware<RequestLoggingMiddleware>();

// Enable Swagger in all environments (can restrict to dev later)
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Contacts API V1");
    c.RoutePrefix = string.Empty; // Serve Swagger UI at root
});

// HTTPS redirection (disabled for development - will be handled by reverse proxy)
// app.UseHttpsRedirection();

// CORS must come before authentication
app.UseCors();

// Session management
app.UseSession();

// Authentication & Authorization
// app.UseAuthentication(); // TODO: Add when implementing proper auth
// app.UseAuthorization();

// Map controllers
app.MapControllers();

// Health check endpoint
app.MapHealthChecks("/health");

// Log application startup
Log.Information("ContactsApi starting up");

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
