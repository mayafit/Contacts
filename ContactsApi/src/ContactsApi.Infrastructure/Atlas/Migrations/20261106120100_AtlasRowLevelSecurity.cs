using ContactsApi.Infrastructure.Atlas;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <summary>
    /// AD-49 rule 1: ENABLE and FORCE row-level security and one owner_isolation policy on
    /// every owner table of atlas; track_points through its activity. Applied only when
    /// RowLevelSecurity__Policies is on (rollout step 4); its Down() is the kill switch,
    /// which only a migrator can run. No model change: no target model.
    /// </summary>
    [DbContext(typeof(AtlasDbContext))]
    [Migration(Id)]
    public partial class AtlasRowLevelSecurity : Migration
    {
        /// <summary>The migration id (the migrator's policy gate)</summary>
        public const string Id = "20261106120100_AtlasRowLevelSecurity";

        /// <summary>The owner tables with an OwnerId column (every one of them, AD-49 rule 1)</summary>
        public static readonly string[] OwnerTables =
        {
            "activities", "activity_fusion", "activity_efforts", "fusion_decisions", "fusion_queue", "fusion_settings",
            "health_samples", "imports", "owner_settings", "photos", "places", "visits", "place_reviews",
            "review_reminders", "flights", "source_connections", "access_tokens"
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($$"""
                DO $rls$
                DECLARE t text;
                BEGIN
                  FOREACH t IN ARRAY ARRAY['{{string.Join("','", OwnerTables)}}']
                  LOOP
                    EXECUTE format('ALTER TABLE atlas.%I ENABLE ROW LEVEL SECURITY', t);
                    EXECUTE format('ALTER TABLE atlas.%I FORCE ROW LEVEL SECURITY', t);
                    EXECUTE format('DROP POLICY IF EXISTS owner_isolation ON atlas.%I', t);
                    EXECUTE format($p$CREATE POLICY owner_isolation ON atlas.%I AS PERMISSIVE FOR ALL TO PUBLIC
                                     USING ("OwnerId" = NULLIF(current_setting('app.owner_id', true), ''))
                                     WITH CHECK ("OwnerId" = NULLIF(current_setting('app.owner_id', true), ''))$p$, t);
                  END LOOP;
                END
                $rls$;

                -- track_points has no OwnerId yet (AD-49 rule 10): scoped through its activity,
                -- whose own policy filters the subquery
                ALTER TABLE atlas.track_points ENABLE ROW LEVEL SECURITY;
                ALTER TABLE atlas.track_points FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS owner_isolation ON atlas.track_points;
                CREATE POLICY owner_isolation ON atlas.track_points AS PERMISSIVE FOR ALL TO PUBLIC
                  USING (EXISTS (SELECT 1 FROM atlas.activities a WHERE a."Id" = track_points."ActivityId"))
                  WITH CHECK (EXISTS (SELECT 1 FROM atlas.activities a WHERE a."Id" = track_points."ActivityId"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($$"""
                DO $rls$
                DECLARE t text;
                BEGIN
                  FOREACH t IN ARRAY ARRAY['{{string.Join("','", OwnerTables)}}', 'track_points']
                  LOOP
                    EXECUTE format('DROP POLICY IF EXISTS owner_isolation ON atlas.%I', t);
                    EXECUTE format('ALTER TABLE atlas.%I NO FORCE ROW LEVEL SECURITY', t);
                    EXECUTE format('ALTER TABLE atlas.%I DISABLE ROW LEVEL SECURITY', t);
                  END LOOP;
                END
                $rls$;
                """);
        }
    }
}
