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
    public DbSet<ActivityFusion> Fusions => Set<ActivityFusion>();
    public DbSet<FusionDecision> FusionDecisions => Set<FusionDecision>();
    public DbSet<SourceConnection> Connections => Set<SourceConnection>();
    public DbSet<FusionQueueItem> FusionQueue => Set<FusionQueueItem>();
    public DbSet<FusionSetting> FusionSettings => Set<FusionSetting>();
    public DbSet<ActivityEffort> Efforts => Set<ActivityEffort>();
    public DbSet<AtlasPhoto> Photos => Set<AtlasPhoto>();
    public DbSet<OwnerSetting> OwnerSettings => Set<OwnerSetting>();
    public DbSet<PlaceReview> Reviews => Set<PlaceReview>();
    public DbSet<ReviewReminder> ReviewReminders => Set<ReviewReminder>();

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
            e.Property(x => x.SourceType).HasMaxLength(64);
            e.Property(x => x.Source).HasMaxLength(64);
            e.Property(x => x.OwnerId).HasMaxLength(64);
            // The route-quality change feed pages by (UpdatedAt, Id)
            e.HasIndex(x => new { x.OwnerId, x.UpdatedAt, x.Id });
            e.HasMany(x => x.Points).WithOne().HasForeignKey(p => p.ActivityId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OwnerId, x.ClusterId });
            e.HasOne(x => x.Fusion).WithOne().HasForeignKey<ActivityFusion>(f => f.ActivityId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ActivityFusion>(e =>
        {
            e.ToTable("activity_fusion");
            e.HasKey(x => x.ActivityId);
            e.HasIndex(x => new { x.OwnerId, x.UpdatedAt });
            e.Property(x => x.OwnerId).HasMaxLength(64);
            e.Property(x => x.Type).HasMaxLength(32);
        });

        b.Entity<FusionDecision>(e =>
        {
            e.ToTable("fusion_decisions");
            e.HasIndex(x => new { x.OwnerId, x.ActivityA, x.ActivityB }).IsUnique();
            e.Property(x => x.OwnerId).HasMaxLength(64);
            e.Property(x => x.Kind).HasMaxLength(16);
        });

        b.Entity<FusionQueueItem>(e =>
        {
            e.ToTable("fusion_queue");
            e.HasIndex(x => new { x.OwnerId, x.Id });
            e.Property(x => x.OwnerId).HasMaxLength(64);
        });

        b.Entity<OwnerSetting>(e =>
        {
            e.ToTable("owner_settings");
            e.HasKey(x => new { x.OwnerId, x.Kind });
            e.Property(x => x.OwnerId).HasMaxLength(64);
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.Json).HasColumnType("jsonb");
        });

        b.Entity<FusionSetting>(e =>
        {
            e.ToTable("fusion_settings");
            e.HasKey(x => x.OwnerId);
            e.Property(x => x.OwnerId).HasMaxLength(64);
        });

        b.Entity<ActivityEffort>(e =>
        {
            e.ToTable("activity_efforts");
            e.HasKey(x => x.ActivityId);
            e.HasIndex(x => new { x.OwnerId, x.Type });
            e.Property(x => x.OwnerId).HasMaxLength(64);
            e.Property(x => x.Type).HasMaxLength(32);
            e.HasOne<AtlasActivity>().WithMany().HasForeignKey(x => x.ActivityId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SourceConnection>(e =>
        {
            e.ToTable("source_connections");
            e.HasIndex(x => new { x.OwnerId, x.Provider }).IsUnique();
            e.Property(x => x.OwnerId).HasMaxLength(64);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(16);
        });

        b.Entity<AtlasPhoto>(e =>
        {
            e.ToTable("photos");
            e.HasIndex(x => new { x.OwnerId, x.Provider, x.ExternalId }).IsUnique();
            e.HasIndex(x => new { x.OwnerId, x.TakenAt });
            e.HasIndex(x => new { x.OwnerId, x.ActivityId }).HasFilter("\"ActivityId\" IS NOT NULL");
            e.HasIndex(x => new { x.OwnerId, x.VisitId }).HasFilter("\"VisitId\" IS NOT NULL");
            e.HasIndex(x => new { x.OwnerId, x.Placed }).HasFilter("NOT \"Placed\"");
            e.Property(x => x.OwnerId).HasMaxLength(64);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.ExternalId).HasMaxLength(128);
            e.Property(x => x.PlacedBy).HasMaxLength(16);
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

        b.Entity<PlaceReview>(e =>
        {
            e.ToTable("place_reviews");
            e.HasIndex(x => new { x.OwnerId, x.Source, x.ExternalId }).IsUnique().HasFilter("\"ExternalId\" IS NOT NULL");
            e.HasIndex(x => new { x.OwnerId, x.PlaceId });
            e.HasIndex(x => new { x.OwnerId, x.ReviewedAt });
            e.HasIndex(x => x.ImportId);
            e.HasOne<AtlasPlace>().WithMany().HasForeignKey(x => x.PlaceId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.OwnerId).HasMaxLength(64);
            e.Property(x => x.Source).HasMaxLength(64);
            e.Property(x => x.ExternalId).HasMaxLength(512);
            e.Property(x => x.LinkedBy).HasMaxLength(16);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.CountryCode).HasMaxLength(8);
            e.Property(x => x.Text).HasMaxLength(10_000);
            e.Property(x => x.MapsUrl).HasMaxLength(1000);
            e.Property(x => x.Answers).HasColumnType("jsonb");
        });

        b.Entity<ReviewReminder>(e =>
        {
            e.ToTable("review_reminders");
            e.HasIndex(x => new { x.OwnerId, x.PlaceId }).IsUnique().HasFilter("\"PlaceId\" IS NOT NULL");
            e.HasIndex(x => new { x.OwnerId, x.Category }).IsUnique().HasFilter("\"Category\" IS NOT NULL");
            e.HasOne<AtlasPlace>().WithMany().HasForeignKey(x => x.PlaceId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("CK_review_reminders_place_or_category",
                "(\"PlaceId\" IS NULL) <> (\"Category\" IS NULL)"));
            e.Property(x => x.OwnerId).HasMaxLength(64);
            e.Property(x => x.Category).HasMaxLength(100);
            e.Property(x => x.Kind).HasMaxLength(16);
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
