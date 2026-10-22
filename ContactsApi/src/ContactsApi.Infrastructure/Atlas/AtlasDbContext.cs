/**
 * @fileoverview EF Core context for the Atlas tables (PostgreSQL)
 * @module ContactsApi.Infrastructure.Atlas
 */
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ContactsApi.Infrastructure.Atlas;

/// <summary>Atlas tables. Every query filters by owner; the indexes start with it.</summary>
public class AtlasDbContext : DbContext
{
    /// <summary>Creates the context</summary>
    public AtlasDbContext(DbContextOptions<AtlasDbContext> options) : base(options)
    {
    }

    public DbSet<AtlasActivity> Activities => Set<AtlasActivity>();
    public DbSet<TrackPoint> TrackPoints => Set<TrackPoint>();
    public DbSet<AtlasPlace> Places => Set<AtlasPlace>();
    public DbSet<PlaceVisit> Visits => Set<PlaceVisit>();
    public DbSet<HealthSample> HealthSamples => Set<HealthSample>();
    public DbSet<ImportRun> Imports => Set<ImportRun>();
    public DbSet<AccessToken> AccessTokens => Set<AccessToken>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("atlas");

        b.Entity<AtlasActivity>(e =>
        {
            e.ToTable("activities");
            e.HasIndex(x => new { x.OwnerId, x.StartTime });
            e.HasIndex(x => new { x.OwnerId, x.Source, x.ExternalId }).IsUnique().HasFilter("\"ExternalId\" IS NOT NULL");
            e.HasIndex(x => x.ImportId);
            e.Property(x => x.Type).HasMaxLength(32);
            e.Property(x => x.Source).HasMaxLength(64);
            e.Property(x => x.OwnerId).HasMaxLength(64);
            e.HasMany(x => x.Points).WithOne().HasForeignKey(p => p.ActivityId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TrackPoint>(e =>
        {
            e.ToTable("track_points");
            e.HasIndex(x => new { x.ActivityId, x.Time });
        });

        b.Entity<AtlasPlace>(e =>
        {
            e.ToTable("places");
            e.HasIndex(x => new { x.OwnerId, x.Source, x.ExternalId }).IsUnique().HasFilter("\"ExternalId\" IS NOT NULL");
            e.HasIndex(x => new { x.OwnerId, x.Lat, x.Lon });
            e.HasIndex(x => x.ImportId);
            e.Property(x => x.OwnerId).HasMaxLength(64);
            e.Property(x => x.Source).HasMaxLength(64);
        });

        b.Entity<PlaceVisit>(e =>
        {
            e.ToTable("visits");
            e.HasIndex(x => new { x.OwnerId, x.StartTime });
            e.HasIndex(x => new { x.OwnerId, x.Source, x.ExternalId }).IsUnique().HasFilter("\"ExternalId\" IS NOT NULL");
            e.HasIndex(x => x.ImportId);
            e.HasOne(x => x.Place).WithMany().HasForeignKey(x => x.PlaceId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.OwnerId).HasMaxLength(64);
            e.Property(x => x.Source).HasMaxLength(64);
        });

        b.Entity<HealthSample>(e =>
        {
            e.ToTable("health_samples");
            e.HasIndex(x => new { x.OwnerId, x.Type, x.StartTime, x.Source }).IsUnique();
            e.HasIndex(x => x.ImportId);
            e.Property(x => x.Type).HasMaxLength(48);
            e.Property(x => x.OwnerId).HasMaxLength(64);
            e.Property(x => x.Source).HasMaxLength(64);
        });

        b.Entity<ImportRun>(e =>
        {
            e.ToTable("imports");
            e.HasIndex(x => new { x.OwnerId, x.CreatedAt });
            e.Property(x => x.OwnerId).HasMaxLength(64);
        });

        b.Entity<AccessToken>(e =>
        {
            e.ToTable("access_tokens");
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.OwnerId);
            e.Property(x => x.OwnerId).HasMaxLength(64);
        });
    }
}

/// <summary>For `dotnet ef migrations` (no running app or Vault needed)</summary>
public class AtlasDbContextFactory : IDesignTimeDbContextFactory<AtlasDbContext>
{
    /// <inheritdoc />
    public AtlasDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AtlasDbContext>()
            .UseNpgsql("Host=localhost;Database=design;Username=design;Password=design")
            .Options);
}
