/**
 * @fileoverview The backend's row-level security plumbing (AD-49): options, the /api
 * owner middleware and the migrate-only entry point
 * @module ContactsApi.WebApi.Services
 */
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Serilog.Extensions.Logging;

namespace ContactsApi.WebApi.Services;

/// <summary>Registration and the migrate-only run of the backend</summary>
public static class RowLevelSecurity
{
    /// <summary>The backend's Application Name (pg_stat_activity, the Postgres logs)</summary>
    public const string ApplicationName = "contacts-backend";

    /// <summary>The backend's pool (AD-49 rule 4)</summary>
    public const int MaxPoolSize = 20;

    /// <summary>Reads RowLevelSecurity__* and registers the owner scope and its interceptor</summary>
    public static RowLevelSecurityOptions AddEchoRowLevelSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(RowLevelSecurityOptions.Section).Get<RowLevelSecurityOptions>() ?? new RowLevelSecurityOptions();
        services.AddOwnerScoping(options);
        return options;
    }

    /// <summary>
    /// --migrate-only (AD-49 rule 3): applies the Atlas migrations with
    /// Database__MigrationConnectionString (the atlas_migrator role) and exits
    /// </summary>
    public static Task<int> MigrateOnlyAsync(IConfiguration configuration)
    {
        var options = configuration.GetSection(RowLevelSecurityOptions.Section).Get<RowLevelSecurityOptions>() ?? new RowLevelSecurityOptions();
        var logger = new SerilogLoggerFactory(Serilog.Log.Logger).CreateLogger("EchoMigrator");
        return EchoMigrator.RunMigrateOnlyAsync<AtlasDbContext>(configuration["Database:MigrationConnectionString"],
            cs => new DbContextOptionsBuilder<AtlasDbContext>()
                .UseNpgsql(cs, n => n.MigrationsHistoryTable("__ef_migrations", "atlas")).Options,
            Infrastructure.Atlas.Migrations.AtlasRowLevelSecurity.Id, options.Policies, logger);
    }
}

/// <summary>
/// Sets the request's Owner from the EchoSession ticket for /api/** (AD-49 rule 4, AD-50
/// rule 4: the <c>sub</c> claim, never the ASP.NET session); /api/mcp gets it from its token
/// instead. Runs after <see cref="EchoSessionMiddleware"/>.
/// </summary>
public class OwnerScopeMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>Creates the middleware</summary>
    public OwnerScopeMiddleware(RequestDelegate next) => _next = next;

    /// <summary>Sets the Owner when the request is signed in with a valid one</summary>
    public Task InvokeAsync(HttpContext context, OwnerScope owners)
    {
        if (context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/api/mcp")
            && context.OwnerId() is { } owner && OwnerScope.IsValid(owner))
        {
            owners.Set(owner);
        }

        return _next(context);
    }
}
