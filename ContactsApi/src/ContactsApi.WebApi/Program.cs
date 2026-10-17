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

// Configure Google OAuth options (secret loaded from Vault at startup)
builder.Services.Configure<GoogleOAuthOptions>(options =>
{
    options.ClientId = builder.Configuration["GoogleOAuth:ClientId"] ?? string.Empty;
    options.RedirectUri = builder.Configuration["GoogleOAuth:RedirectUri"] ?? string.Empty;
    options.Scopes = builder.Configuration.GetSection("GoogleOAuth:Scopes").Get<string[]>() ?? Array.Empty<string>();
    // ClientSecret will be loaded from Vault by VaultConfigurationService
});

// Register hosted service to load secrets from Vault at startup
builder.Services.AddHostedService<VaultConfigurationService>();

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
    options.Cookie.SecurePolicy = CookieSecurePolicy.None; // Allow HTTP for development (use Always in production with HTTPS)
    options.Cookie.SameSite = SameSiteMode.Lax; // Allow OAuth redirects while maintaining CSRF protection
    options.Cookie.IsEssential = true; // Required for GDPR compliance
    options.Cookie.Name = ".ContactsApi.Session";
    options.Cookie.Domain = "localhost"; // Allow cookie to work across different ports on localhost
    options.Cookie.Path = "/"; // Cookie available for all paths
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
