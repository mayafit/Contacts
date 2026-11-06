using ContactsApi.Infrastructure.Atlas;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <summary>
    /// AD-49 rule 5: the narrow SECURITY DEFINER functions through which work that spans
    /// Owners discovers them (owner ids, counts or flags only). EXECUTE goes to contactsapi
    /// alone. Once db-roles.sql has created atlas_system (rollout step 3) the functions
    /// belong to it, with column-level grants on what they read and write; before that they
    /// belong to whoever ran the migration, and work the same (no policy exists yet).
    /// No model change: no target model.
    /// </summary>
    [DbContext(typeof(AtlasDbContext))]
    [Migration("20261106120000_AtlasSystemFunctions")]
    public partial class AtlasSystemFunctions : Migration
    {
        /// <summary>The functions (CREATE OR REPLACE: re-runnable)</summary>
        internal const string Functions = """
            CREATE OR REPLACE FUNCTION atlas.sys_owners(p_kind text) RETURNS SETOF character varying
            LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            BEGIN
              IF p_kind = 'activities' THEN
                -- A loose index scan over IX_activities_OwnerId_StartTime: one probe per Owner
                RETURN QUERY WITH RECURSIVE o AS (
                  (SELECT a."OwnerId" AS v FROM atlas.activities a ORDER BY a."OwnerId" LIMIT 1)
                  UNION ALL
                  SELECT (SELECT a."OwnerId" FROM atlas.activities a WHERE a."OwnerId" > o.v ORDER BY a."OwnerId" LIMIT 1)
                  FROM o WHERE o.v IS NOT NULL)
                SELECT o.v FROM o WHERE o.v IS NOT NULL;
              ELSIF p_kind = 'fusion_queue' THEN
                RETURN QUERY SELECT DISTINCT q."OwnerId" FROM atlas.fusion_queue q;
              ELSIF p_kind = 'flights' THEN
                RETURN QUERY SELECT DISTINCT a."OwnerId" FROM atlas.activities a WHERE a."Type" = 'flight';
              ELSIF p_kind = 'unplaced_photos' THEN
                RETURN QUERY SELECT DISTINCT p."OwnerId" FROM atlas.photos p WHERE NOT p."Placed";
              ELSIF p_kind = 'places_to_identify' THEN
                RETURN QUERY SELECT DISTINCT p."OwnerId" FROM atlas.places p WHERE p."IdentifyState" IN ('pending', 'none');
              ELSIF p_kind = 'fusion_backfill' THEN
                -- Data imported before fusion existed: every Owner, once, while no fused row exists at all
                IF NOT EXISTS (SELECT 1 FROM atlas.activity_fusion f) THEN
                  RETURN QUERY SELECT DISTINCT a."OwnerId" FROM atlas.activities a;
                END IF;
              ELSE
                RAISE EXCEPTION 'sys_owners: unknown kind %', p_kind USING ERRCODE = '22023';
              END IF;
            END
            $fn$;

            CREATE OR REPLACE FUNCTION atlas.sys_owners_with_connection(p_provider text) RETURNS SETOF character varying
            LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
              SELECT DISTINCT c."OwnerId" FROM atlas.source_connections c
              WHERE c."Provider" = p_provider AND c."Status" <> 'expired'
            $fn$;

            CREATE OR REPLACE FUNCTION atlas.sys_fail_interrupted_syncs(p_provider text) RETURNS integer
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE n integer;
            BEGIN
              UPDATE atlas.source_connections c
                 SET "Status" = 'error',
                     "LastError" = 'The sync was interrupted by a restart. Sync again to continue.'
               WHERE c."Provider" = p_provider AND c."Status" = 'syncing';
              GET DIAGNOSTICS n = ROW_COUNT;
              RETURN n;
            END
            $fn$;

            CREATE OR REPLACE FUNCTION atlas.sys_resolve_access_token(p_hash text) RETURNS character varying
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE v_owner character varying; v_used timestamptz;
            BEGIN
              SELECT t."OwnerId", t."LastUsedAt" INTO v_owner, v_used
                FROM atlas.access_tokens t WHERE t."TokenHash" = p_hash;
              IF v_owner IS NULL THEN
                RETURN NULL;
              END IF;
              -- Stamped at most hourly
              IF v_used IS NULL OR v_used < now() - interval '1 hour' THEN
                UPDATE atlas.access_tokens t SET "LastUsedAt" = now() WHERE t."TokenHash" = p_hash;
              END IF;
              RETURN v_owner;
            END
            $fn$;
            """;

        /// <summary>
        /// EXECUTE only for contactsapi; ownership and column grants for atlas_system once it
        /// exists and the migrator may hand ownership to it (also run by db-roles.sql)
        /// </summary>
        internal const string Grants = """
            DO $grants$
            DECLARE f text;
            BEGIN
              FOREACH f IN ARRAY ARRAY['atlas.sys_owners(text)', 'atlas.sys_owners_with_connection(text)',
                                       'atlas.sys_fail_interrupted_syncs(text)', 'atlas.sys_resolve_access_token(text)']
              LOOP
                EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'contactsapi') THEN
                  EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO contactsapi', f);
                END IF;
              END LOOP;

              IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'atlas_system')
                 AND pg_has_role(current_user, 'atlas_system', 'MEMBER')
                 AND has_schema_privilege('atlas_system', 'atlas', 'CREATE') THEN
                GRANT USAGE ON SCHEMA atlas TO atlas_system;
                GRANT SELECT ("OwnerId", "Type") ON atlas.activities TO atlas_system;
                GRANT SELECT ("OwnerId") ON atlas.fusion_queue TO atlas_system;
                GRANT SELECT ("OwnerId") ON atlas.activity_fusion TO atlas_system;
                GRANT SELECT ("OwnerId", "Placed") ON atlas.photos TO atlas_system;
                GRANT SELECT ("OwnerId", "IdentifyState") ON atlas.places TO atlas_system;
                GRANT SELECT ("OwnerId", "Provider", "Status") ON atlas.source_connections TO atlas_system;
                GRANT UPDATE ("Status", "LastError") ON atlas.source_connections TO atlas_system;
                GRANT SELECT ("OwnerId", "TokenHash", "LastUsedAt") ON atlas.access_tokens TO atlas_system;
                GRANT UPDATE ("LastUsedAt") ON atlas.access_tokens TO atlas_system;
                FOREACH f IN ARRAY ARRAY['atlas.sys_owners(text)', 'atlas.sys_owners_with_connection(text)',
                                         'atlas.sys_fail_interrupted_syncs(text)', 'atlas.sys_resolve_access_token(text)']
                LOOP
                  EXECUTE format('ALTER FUNCTION %s OWNER TO atlas_system', f);
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
                DROP FUNCTION IF EXISTS atlas.sys_owners(text);
                DROP FUNCTION IF EXISTS atlas.sys_owners_with_connection(text);
                DROP FUNCTION IF EXISTS atlas.sys_fail_interrupted_syncs(text);
                DROP FUNCTION IF EXISTS atlas.sys_resolve_access_token(text);
                """);
        }
    }
}
