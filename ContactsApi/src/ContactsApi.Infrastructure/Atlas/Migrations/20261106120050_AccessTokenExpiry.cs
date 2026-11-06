using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <summary>
    /// MCP tokens expire and start read-only (AD-50 rule 9, AD-18): <c>ExpiresAt</c> and
    /// <c>Scopes</c> on <c>atlas.access_tokens</c>; the PAT resolver refuses an expired token;
    /// <c>sys_owners('source_connections')</c> lets the key-ring rotation find the Owners whose
    /// connection secrets it must re-protect (AD-50 rule 2, AD-49 rule 5).
    /// </summary>
    /// <remarks>
    /// <para>Ordered <b>before</b> <see cref="AtlasRowLevelSecurity"/> (120100) on purpose. While
    /// <c>RowLevelSecurity__Policies</c> is false, <see cref="Tenancy.EchoMigrator"/> applies
    /// everything up to the policy migration and stops there; a migration ordered after it would
    /// never apply (EchoMigrator throws). EF applies every pending migration whatever its place,
    /// so a database that already has the policy migration applies this one too; nothing in it
    /// depends on the policies.</para>
    /// <para>The function changes keep the signatures and the security attributes of
    /// <see cref="AtlasSystemFunctions"/> (<c>CREATE OR REPLACE</c> keeps the owner and the
    /// EXECUTE grants; <c>SECURITY DEFINER</c>, <c>search_path</c> pinned, schema-qualified), so the
    /// definer-function catalog test and <c>db-roles.sql</c> stay valid. The resolver now reads
    /// <c>ExpiresAt</c>, which its owner <c>atlas_system</c> is granted here and in db-roles.sql.</para>
    /// </remarks>
    public partial class AccessTokenExpiry : Migration
    {
        /// <summary>The two functions with the changes of this migration</summary>
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
              ELSIF p_kind = 'source_connections' THEN
                -- Every Owner with a connection, expired ones included (the key-ring rotation, AD-50 rule 2)
                RETURN QUERY SELECT DISTINCT c."OwnerId" FROM atlas.source_connections c;
              ELSE
                RAISE EXCEPTION 'sys_owners: unknown kind %', p_kind USING ERRCODE = '22023';
              END IF;
            END
            $fn$;

            CREATE OR REPLACE FUNCTION atlas.sys_resolve_access_token(p_hash text) RETURNS character varying
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $fn$
            DECLARE v_owner character varying; v_used timestamptz;
            BEGIN
              -- An expired token resolves to nobody, like a revoked one (AD-50 rule 9)
              SELECT t."OwnerId", t."LastUsedAt" INTO v_owner, v_used
                FROM atlas.access_tokens t WHERE t."TokenHash" = p_hash AND t."ExpiresAt" > now();
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

        /// <summary>The resolver's owner reads ExpiresAt (also in db-roles.sql)</summary>
        internal const string Grants = """
            DO $grants$
            BEGIN
              IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'atlas_system') THEN
                GRANT SELECT ("ExpiresAt") ON atlas.access_tokens TO atlas_system;
              END IF;
            END
            $grants$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing tokens: 90 days from now, and they keep write access until then ("legacy"
            // marks them for the notice in the token list). The defaults fill only those rows.
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                schema: "atlas",
                table: "access_tokens",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() + interval '90 days'");

            migrationBuilder.AddColumn<string>(
                name: "Scopes",
                schema: "atlas",
                table: "access_tokens",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "atlas write legacy");

            migrationBuilder.Sql("""ALTER TABLE atlas.access_tokens ALTER COLUMN "ExpiresAt" DROP DEFAULT;""");
            migrationBuilder.Sql("""ALTER TABLE atlas.access_tokens ALTER COLUMN "Scopes" DROP DEFAULT;""");
            migrationBuilder.Sql(Grants);
            migrationBuilder.Sql(Functions);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The functions as AtlasSystemFunctions made them, before the columns go
            migrationBuilder.Sql(AtlasSystemFunctions.Functions);

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                schema: "atlas",
                table: "access_tokens");

            migrationBuilder.DropColumn(
                name: "Scopes",
                schema: "atlas",
                table: "access_tokens");
        }
    }
}
