-- =============================================================================
-- db-roles-rollback.sql: undoes db-roles.sql (AD-49 rule 2), back to the layout
-- before the role split: contactsapi owns atlas, echo_routes owns routes, and both
-- apps migrate at startup again (set RowLevelSecurity__MigrateOnStartup=true and turn
-- the migrate init containers off BEFORE running this, or the next pod start fails).
--
-- If the RLS policy migrations were applied, run their Down() first, as the migrator
-- (the kill switch, AD-49 rule 8):
--   dotnet ContactsApi.WebApi.dll --migrate-only  with the previous image, or in psql as
--   atlas_migrator / routes_migrator: DROP POLICY owner_isolation ..., NO FORCE, DISABLE.
--
-- Run as postgres in the app database:
--   psql -v ON_ERROR_STOP=1 -U postgres -d contactsdb -f db-roles-rollback.sql
-- Idempotent. It keeps the runtime roles NOCREATEDB and the per-role timeouts (they
-- are safe either way); it drops the migrator, system and backup roles.
-- =============================================================================

DO $rollback$
DECLARE r record;
BEGIN
  -- Ownership back to the runtime roles (schemas, tables, sequences, functions)
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'atlas_migrator') THEN
    EXECUTE 'REASSIGN OWNED BY atlas_migrator TO contactsapi';
  END IF;
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'atlas_system') THEN
    EXECUTE 'REASSIGN OWNED BY atlas_system TO contactsapi';
  END IF;
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'routes_migrator') THEN
    EXECUTE 'REASSIGN OWNED BY routes_migrator TO echo_routes';
  END IF;
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'routes_system') THEN
    EXECUTE 'REASSIGN OWNED BY routes_system TO echo_routes';
  END IF;

  -- The backend reads routes and writes decisions and settings, as before the split
  IF to_regnamespace('routes') IS NOT NULL THEN
    GRANT USAGE ON SCHEMA routes TO contactsapi;
    GRANT SELECT ON ALL TABLES IN SCHEMA routes TO contactsapi;
    ALTER DEFAULT PRIVILEGES FOR ROLE echo_routes IN SCHEMA routes GRANT SELECT ON TABLES TO contactsapi;
    IF to_regclass('routes.route_decisions') IS NOT NULL THEN
      GRANT INSERT, UPDATE, DELETE ON routes.route_decisions TO contactsapi;
    END IF;
    IF to_regclass('routes.settings') IS NOT NULL THEN
      GRANT INSERT, UPDATE, DELETE ON routes.settings TO contactsapi;
    END IF;
  END IF;

  -- The sys_* functions stay executable by their runtime role (now also their owner)
  FOR r IN SELECT p.oid::regprocedure::text AS sig, n.nspname
             FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname IN ('atlas', 'routes') AND p.proname LIKE 'sys\_%'
  LOOP
    EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO %I', r.sig,
                   CASE r.nspname WHEN 'atlas' THEN 'contactsapi' ELSE 'echo_routes' END);
  END LOOP;

  -- Revoke what the dropped roles hold here, then drop them
  FOR r IN SELECT rolname FROM pg_roles
            WHERE rolname IN ('atlas_migrator', 'routes_migrator', 'atlas_system', 'routes_system', 'contacts_backup')
  LOOP
    EXECUTE format('DROP OWNED BY %I', r.rolname);
    EXECUTE format('DROP ROLE %I', r.rolname);
  END LOOP;
END
$rollback$;

-- The database owner as Bitnami created it (contactsapi)
DO $db$
BEGIN
  EXECUTE format('ALTER DATABASE %I OWNER TO contactsapi', current_database());
END
$db$;
