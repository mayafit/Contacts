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
var redisConnectionString = builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379";
var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnectionString);

// Configure Data Protection to persist keys to Redis
builder.Services.AddDataProtection()
    .PersistKeysToStackExchangeRedis(redis, "DataProtection-Keys")
    .SetApplicationName("ContactsApi");

// Configure session management with Redis
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnectionString;
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
