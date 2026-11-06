using ContactsApi.Infrastructure.Routes;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Routes.Migrations
{
    /// <summary>
    /// AD-49 rule 1: ENABLE and FORCE row-level security and the owner_isolation policy on
    /// every routes table. Applied only when RowLevelSecurity__Policies is on, after
    /// AtlasRowLevelSecurity (rollout step 4); Down() is the kill switch. No model change.
    /// </summary>
    [DbContext(typeof(RoutesDbContext))]
    [Migration(Id)]
    public partial class RoutesRowLevelSecurity : Migration
    {
        /// <summary>The migration id (the migrator's policy gate)</summary>
        public const string Id = "20261106120300_RoutesRowLevelSecurity";

        /// <summary>The owner tables of routes (every one of them)</summary>
        public static readonly string[] OwnerTables =
        {
            "route_quality", "matched_routes", "route_decisions", "route_waypoints", "settings", "jobs", "owner_cursors"
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
                    EXECUTE format('ALTER TABLE routes.%I ENABLE ROW LEVEL SECURITY', t);
                    EXECUTE format('ALTER TABLE routes.%I FORCE ROW LEVEL SECURITY', t);
                    EXECUTE format('DROP POLICY IF EXISTS owner_isolation ON routes.%I', t);
                    EXECUTE format($p$CREATE POLICY owner_isolation ON routes.%I AS PERMISSIVE FOR ALL TO PUBLIC
                                     USING ("OwnerId" = NULLIF(current_setting('app.owner_id', true), ''))
                                     WITH CHECK ("OwnerId" = NULLIF(current_setting('app.owner_id', true), ''))$p$, t);
                  END LOOP;
                END
                $rls$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($$"""
                DO $rls$
                DECLARE t text;
                BEGIN
                  FOREACH t IN ARRAY ARRAY['{{string.Join("','", OwnerTables)}}']
                  LOOP
                    EXECUTE format('DROP POLICY IF EXISTS owner_isolation ON routes.%I', t);
                    EXECUTE format('ALTER TABLE routes.%I NO FORCE ROW LEVEL SECURITY', t);
                    EXECUTE format('ALTER TABLE routes.%I DISABLE ROW LEVEL SECURITY', t);
                  END LOOP;
                END
                $rls$;
                """);
        }
    }
}
