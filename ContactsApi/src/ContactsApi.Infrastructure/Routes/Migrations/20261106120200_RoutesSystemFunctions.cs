using ContactsApi.Infrastructure.Routes;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Routes.Migrations
{
    /// <summary>
    /// AD-49 rule 5: the route service's cross-Owner discovery functions (owner ids and
    /// counts only), EXECUTE for echo_routes alone; owned by routes_system once
    /// db-roles.sql has created it. No model change: no target model.
    /// </summary>
    [DbContext(typeof(RoutesDbContext))]
    [Migration("20261106120200_RoutesSystemFunctions")]
    public partial class RoutesSystemFunctions : Migration
    {
        /// <summary>The functions (CREATE OR REPLACE: re-runnable)</summary>
        internal const string Functions = """
            CREATE OR REPLACE FUNCTION routes.sys_owners_with_open_interactive_jobs(p_now timestamptz) RETURNS SETOF character varying
            LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
              SELECT DISTINCT j."OwnerId" FROM routes.jobs j
              WHERE j."Priority" = 'interactive' AND j."State" IN ('queued', 'running')
                AND (j."NextAttemptAt" IS NULL OR j."NextAttemptAt" <= p_now)
            $fn$;

            CREATE OR REPLACE FUNCTION routes.sys_known_owners() RETURNS SETOF character varying
            LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
              SELECT c."OwnerId" FROM routes.owner_cursors c
              UNION
              SELECT j."OwnerId" FROM routes.jobs j
            $fn$;

            CREATE OR REPLACE FUNCTION routes.sys_prune_finished_jobs(p_before timestamptz) RETURNS integer
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE n integer;
            BEGIN
              DELETE FROM routes.jobs j
               WHERE j."State" IN ('done', 'failed') AND j."FinishedAt" IS NOT NULL AND j."FinishedAt" < p_before;
              GET DIAGNOSTICS n = ROW_COUNT;
              RETURN n;
            END
            $fn$;
            """;

        /// <summary>EXECUTE for echo_routes; ownership and column grants for routes_system (also run by db-roles.sql)</summary>
        internal const string Grants = """
            DO $grants$
            DECLARE f text;
            BEGIN
              FOREACH f IN ARRAY ARRAY['routes.sys_owners_with_open_interactive_jobs(timestamptz)', 'routes.sys_known_owners()',
                                       'routes.sys_prune_finished_jobs(timestamptz)']
              LOOP
                EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'echo_routes') THEN
                  EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO echo_routes', f);
                END IF;
              END LOOP;

              IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'routes_system')
                 AND pg_has_role(current_user, 'routes_system', 'MEMBER')
                 AND has_schema_privilege('routes_system', 'routes', 'CREATE') THEN
                GRANT USAGE ON SCHEMA routes TO routes_system;
                GRANT SELECT ("OwnerId", "Priority", "State", "NextAttemptAt", "FinishedAt") ON routes.jobs TO routes_system;
                GRANT DELETE ON routes.jobs TO routes_system;
                GRANT SELECT ("OwnerId") ON routes.owner_cursors TO routes_system;
                FOREACH f IN ARRAY ARRAY['routes.sys_owners_with_open_interactive_jobs(timestamptz)', 'routes.sys_known_owners()',
                                         'routes.sys_prune_finished_jobs(timestamptz)']
                LOOP
                  EXECUTE format('ALTER FUNCTION %s OWNER TO routes_system', f);
                END LOOP;
              END IF;
            END
            $grants$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Functions);
            migrationBuilder.Sql(Grants);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS routes.sys_owners_with_open_interactive_jobs(timestamptz);
                DROP FUNCTION IF EXISTS routes.sys_known_owners();
                DROP FUNCTION IF EXISTS routes.sys_prune_finished_jobs(timestamptz);
                """);
        }
    }
}
