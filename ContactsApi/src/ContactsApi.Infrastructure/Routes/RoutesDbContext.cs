/**
 * @fileoverview EF Core context for the routes schema (its own migration history)
 * @module ContactsApi.Infrastructure.Routes
 */
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ContactsApi.Infrastructure.Routes;

/// <summary>
/// The routes schema. The route-quality service writes it through the echo_routes
/// role; the backend's Routes module reads it and writes decisions and settings only.
/// Every index starts with OwnerId.
/// </summary>
public class RoutesDbContext : DbContext
{
    /// <summary>The schema</summary>
    public const string Schema = "routes";

    /// <summary>The migration history table (in the schema, so echo_routes needs no other grant)</summary>
    public const string HistoryTable = "__ef_migrations";

    /// <summary>Creates the context</summary>
    public RoutesDbContext(DbContextOptions<RoutesDbContext> options) : base(options)
    {
    }

    public DbSet<RouteQualityRow> Quality => Set<RouteQualityRow>();
    public DbSet<MatchedRouteRow> MatchedRoutes => Set<MatchedRouteRow>();
    public DbSet<RouteDecision> Decisions => Set<RouteDecision>();
    public DbSet<RouteSetting> Settings => Set<RouteSetting>();
    public DbSet<RouteJob> Jobs => Set<RouteJob>();
    public DbSet<OwnerCursor> Cursors => Set<OwnerCursor>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<RouteQualityRow>(e =>
        {
            e.ToTable("route_quality");
            e.HasIndex(x => new { x.OwnerId, x.ActivityId }).IsUnique();
            // Stage B's waiting records, newest change first
            e.HasIndex(x => new { x.OwnerId, x.NeedsImprovement, x.SourceUpdatedAt });
            e.Property(x => x.OwnerId).HasMaxLength(64);
            e.Property(x => x.SourceKey).HasMaxLength(256);
            e.Property(x => x.Type).HasMaxLength(32);
            e.Property(x => x.SourceType).HasMaxLength(64);
            e.Property(x => x.InputHash).HasMaxLength(64);
            e.Property(x => x.Metrics).HasColumnType("jsonb");
            e.Property(x => x.Grade).HasMaxLength(8);
            e.Property(x => x.Flags).HasMaxLength(256);
            e.Property(x => x.Matchable).HasMaxLength(24);
            e.Property(x => x.RuleVersion).HasMaxLength(16);
        });

        b.Entity<MatchedRouteRow>(e =>
        {
            e.ToTable("matched_routes");
            e.HasIndex(x => new { x.OwnerId, x.ActivityId }).IsUnique();
            // Due retries (per-record backoff)
            e.HasIndex(x => new { x.OwnerId, x.NextAttemptAt });
            e.Property(x => x.OwnerId).HasMaxLength(64);
            e.Property(x => x.SourceKey).HasMaxLength(256);
            e.Property(x => x.InputHash).HasMaxLength(64);
            e.Property(x => x.FusedType).HasMaxLength(32);
            e.Property(x => x.Costing).HasMaxLength(16);
            e.Property(x => x.Engine).HasMaxLength(16);
            e.Property(x => x.EngineVersion).HasMaxLength(32);
            e.Property(x => x.GraphVersion).HasMaxLength(256);
            e.Property(x => x.RuleVersion).HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Pieces).HasColumnType("jsonb");
            e.Property(x => x.MetricsAfter).HasColumnType("jsonb");
            e.Property(x => x.Roads).HasColumnType("jsonb");
            e.Property(x => x.RoadNames).HasColumnType("text[]");
            e.Property(x => x.Reason).HasMaxLength(500);
        });

        b.Entity<RouteDecision>(e =>
        {
            e.ToTable("route_decisions");
            e.HasIndex(x => new { x.OwnerId, x.SourceKey }).IsUnique();
            e.Property(x => x.OwnerId).HasMaxLength(64);
            e.Property(x => x.SourceKey).HasMaxLength(256);
            e.Property(x => x.Kind).HasMaxLength(8);
        });

        b.Entity<RouteSetting>(e =>
        {
            e.ToTable("settings");
            e.HasKey(x => x.OwnerId);
            e.Property(x => x.OwnerId).HasMaxLength(64);
            e.Property(x => x.Json).HasColumnType("jsonb");
        });

        b.Entity<RouteJob>(e =>
        {
            e.ToTable("jobs");
            e.HasIndex(x => new { x.OwnerId, x.State, x.CreatedAt });
            // The interactive pass looks across Owners for open interactive jobs: a partial index
            // holding only those (a handful of rows), so the scan never reads the job history
            e.HasIndex(x => new { x.OwnerId, x.NextAttemptAt })
                .HasDatabaseName("IX_jobs_open_interactive")
                .HasFilter($"\"Priority\" = '{RouteJobPriorities.Interactive}' AND \"State\" IN ('{RouteJobStates.Queued}', '{RouteJobStates.Running}')");
            e.Property(x => x.OwnerId).HasMaxLength(64);
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Priority).HasMaxLength(16);
            e.Property(x => x.Scope).HasColumnType("jsonb");
            e.Property(x => x.State).HasMaxLength(16);
            e.Property(x => x.Counts).HasColumnType("jsonb");
            e.Property(x => x.LastError).HasMaxLength(500);
            e.Property(x => x.Cursor).HasMaxLength(128);
        });

        b.Entity<OwnerCursor>(e =>
        {
            e.ToTable("owner_cursors");
            e.HasKey(x => x.OwnerId);
            e.Property(x => x.OwnerId).HasMaxLength(64);
            e.Property(x => x.Cursor).HasMaxLength(128);
            e.Property(x => x.LastError).HasMaxLength(500);
        });
    }
}

/// <summary>For `dotnet ef migrations` (no running app needed)</summary>
public class RoutesDbContextFactory : IDesignTimeDbContextFactory<RoutesDbContext>
{
    /// <inheritdoc />
    public RoutesDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RoutesDbContext>()
            .UseNpgsql("Host=localhost;Database=design;Username=design;Password=design",
                o => o.MigrationsHistoryTable(RoutesDbContext.HistoryTable, RoutesDbContext.Schema))
            .Options);
}
