-- =============================================================================
-- db-roles.sql: the role split of AD-49 rule 2 (story 21.16)
--
-- Run as the superuser `postgres`, connected to the app database (contactsdb):
--
--   psql -v ON_ERROR_STOP=1 -U postgres -d contactsdb -f db-roles.sql
--
-- Idempotent: safe to run again, and it must be run again after the first migration
-- of a fresh database (it grants on the tables that exist when it runs; default
-- privileges cover tables the migrators create later, and the second run takes back
-- the DML that default privileges give the runtime roles on __ef_migrations).
-- The Testcontainers fixture of the isolation tests runs this same file.
--
-- It sets NO passwords. Set them afterwards, interactively, so they never land in a
-- file or a shell history (then store them in Vault, AD-49 rule 3 / AD-52):
--   \password atlas_migrator      -> secret/data/contacts-migrate  Database__MigrationConnectionString
--   \password routes_migrator     -> secret/data/contacts-migrate  Routes__MigrationConnectionString
--   \password contacts_backup     -> secret/data/contacts-backup
-- (contactsapi and echo_routes keep the passwords they have; rotating contactsapi's is
-- rollout step 1, done before this script.)
--
-- Rollback: db-roles-rollback.sql (after the RLS migrations' Down(), if they were applied).
-- =============================================================================

-- ---- 1. Roles ---------------------------------------------------------------
DO $roles$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'contactsapi') THEN CREATE ROLE contactsapi LOGIN; END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'echo_routes') THEN CREATE ROLE echo_routes LOGIN; END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'atlas_migrator') THEN CREATE ROLE atlas_migrator LOGIN; END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'routes_migrator') THEN CREATE ROLE routes_migrator LOGIN; END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'atlas_system') THEN CREATE ROLE atlas_system NOLOGIN; END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'routes_system') THEN CREATE ROLE routes_system NOLOGIN; END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'contacts_backup') THEN CREATE ROLE contacts_backup LOGIN; END IF;
END
$roles$;

-- Runtime roles: own nothing, bypass nothing, DDL nothing
ALTER ROLE contactsapi     LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
ALTER ROLE echo_routes     LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
-- Migrators: own their schema; BYPASSRLS so data-fix migrations see every row
ALTER ROLE atlas_migrator  LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION BYPASSRLS;
ALTER ROLE routes_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION BYPASSRLS;
-- Definer-function owners: nobody logs in as them
ALTER ROLE atlas_system    NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION BYPASSRLS;
ALTER ROLE routes_system   NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION BYPASSRLS;
-- Backups (AD-24, AD-55): pg_dump refuses row-level security without BYPASSRLS
ALTER ROLE contacts_backup LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION BYPASSRLS;

-- Later migrations CREATE OR REPLACE the sys_* functions they no longer own
GRANT atlas_system TO atlas_migrator;
GRANT routes_system TO routes_migrator;

-- Per-role limits (AD-49 rule 2); the migrators keep 0 (long data-fix migrations)
ALTER ROLE contactsapi SET statement_timeout = '60s';
ALTER ROLE contactsapi SET idle_in_transaction_session_timeout = '60s';
ALTER ROLE echo_routes SET statement_timeout = '120s';
ALTER ROLE echo_routes SET idle_in_transaction_session_timeout = '60s';
ALTER ROLE atlas_migrator SET statement_timeout = 0;
ALTER ROLE routes_migrator SET statement_timeout = 0;

-- ---- 2. The database and the public schema belong to postgres ----------------
DO $db$
BEGIN
  EXECUTE format('ALTER DATABASE %I OWNER TO postgres', current_database());
  EXECUTE format('REVOKE CREATE, TEMPORARY ON DATABASE %I FROM PUBLIC', current_database());
  EXECUTE format('REVOKE ALL ON DATABASE %I FROM contactsapi, echo_routes', current_database());
  EXECUTE format('GRANT CONNECT ON DATABASE %I TO contactsapi, echo_routes, atlas_migrator, routes_migrator, contacts_backup',
                 current_database());
  -- CREATE SCHEMA for the next module schemas (people, faces, ...; AD-49 rule 7)
  EXECUTE format('GRANT CREATE ON DATABASE %I TO atlas_migrator, routes_migrator', current_database());
END
$db$;

ALTER SCHEMA public OWNER TO pg_database_owner;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON SCHEMA public FROM contactsapi, echo_routes;

-- ---- 3. The schemas and everything in them belong to the migrators -----------
CREATE SCHEMA IF NOT EXISTS atlas AUTHORIZATION atlas_migrator;
CREATE SCHEMA IF NOT EXISTS routes AUTHORIZATION routes_migrator;
ALTER SCHEMA atlas OWNER TO atlas_migrator;
ALTER SCHEMA routes OWNER TO routes_migrator;
-- The definer-function owners receive function ownership, which needs CREATE
GRANT USAGE, CREATE ON SCHEMA atlas TO atlas_system;
GRANT USAGE, CREATE ON SCHEMA routes TO routes_system;

DO $own$
DECLARE r record; target text;
BEGIN
  -- Tables, views and free-standing sequences (identity sequences follow their table)
  FOR r IN
    SELECT n.nspname, c.relname, c.relkind
      FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE n.nspname IN ('atlas', 'routes')
       AND c.relkind IN ('r', 'p', 'v', 'm', 'S')
       AND NOT (c.relkind = 'S' AND EXISTS (
             SELECT 1 FROM pg_depend d WHERE d.objid = c.oid AND d.classid = 'pg_class'::regclass AND d.deptype IN ('a', 'i')))
  LOOP
    target := CASE r.nspname WHEN 'atlas' THEN 'atlas_migrator' ELSE 'routes_migrator' END;
    EXECUTE format('ALTER %s %I.%I OWNER TO %I',
      CASE r.relkind WHEN 'S' THEN 'SEQUENCE' WHEN 'v' THEN 'VIEW' WHEN 'm' THEN 'MATERIALIZED VIEW' ELSE 'TABLE' END,
      r.nspname, r.relname, target);
  END LOOP;

  -- Functions: the sys_* definer functions to the system roles, anything else to the migrator
  FOR r IN
    SELECT n.nspname, p.proname, p.oid::regprocedure::text AS sig
      FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
     WHERE n.nspname IN ('atlas', 'routes')
  LOOP
    target := CASE WHEN r.proname LIKE 'sys\_%' THEN r.nspname || '_system' ELSE r.nspname || '_migrator' END;
    EXECUTE format('ALTER FUNCTION %s OWNER TO %I', r.sig, target);
  END LOOP;
END
$own$;

-- ---- 4. Runtime grants: DML only (no TRUNCATE, TRIGGER, REFERENCES) ----------
REVOKE ALL ON SCHEMA atlas FROM PUBLIC, contactsapi, echo_routes;
REVOKE ALL ON SCHEMA routes FROM PUBLIC, contactsapi, echo_routes;
REVOKE ALL ON ALL TABLES IN SCHEMA atlas FROM PUBLIC, contactsapi, echo_routes;
REVOKE ALL ON ALL TABLES IN SCHEMA routes FROM PUBLIC, contactsapi, echo_routes;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA atlas FROM PUBLIC, contactsapi, echo_routes;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA routes FROM PUBLIC, contactsapi, echo_routes;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA atlas FROM PUBLIC, contactsapi, echo_routes;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA routes FROM PUBLIC, contactsapi, echo_routes;

-- atlas: the backend
GRANT USAGE ON SCHEMA atlas TO contactsapi;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA atlas TO contactsapi;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA atlas TO contactsapi;
ALTER DEFAULT PRIVILEGES FOR ROLE atlas_migrator IN SCHEMA atlas GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO contactsapi;
ALTER DEFAULT PRIVILEGES FOR ROLE atlas_migrator IN SCHEMA atlas GRANT USAGE, SELECT ON SEQUENCES TO contactsapi;

-- routes: the route service writes it; the backend reads it and writes decisions and settings
GRANT USAGE ON SCHEMA routes TO echo_routes, contactsapi;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA routes TO echo_routes;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA routes TO echo_routes;
GRANT SELECT ON ALL TABLES IN SCHEMA routes TO contactsapi;
ALTER DEFAULT PRIVILEGES FOR ROLE routes_migrator IN SCHEMA routes GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO echo_routes;
ALTER DEFAULT PRIVILEGES FOR ROLE routes_migrator IN SCHEMA routes GRANT USAGE, SELECT ON SEQUENCES TO echo_routes;
ALTER DEFAULT PRIVILEGES FOR ROLE routes_migrator IN SCHEMA routes GRANT SELECT ON TABLES TO contactsapi;
-- The default ACL echo_routes left while it owned the schema (charts/routes/README.md)
ALTER DEFAULT PRIVILEGES FOR ROLE echo_routes IN SCHEMA routes REVOKE SELECT ON TABLES FROM contactsapi;

-- Functions the migrators create are not executable by PUBLIC (the migrations grant EXECUTE)
ALTER DEFAULT PRIVILEGES FOR ROLE atlas_migrator REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE routes_migrator REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE atlas_system REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE routes_system REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;

DO $runtime$
BEGIN
  -- History tables: SELECT only (the readiness check reads them)
  IF to_regclass('atlas.__ef_migrations') IS NOT NULL THEN
    REVOKE INSERT, UPDATE, DELETE ON atlas.__ef_migrations FROM contactsapi;
  END IF;
  IF to_regclass('routes.__ef_migrations') IS NOT NULL THEN
    REVOKE INSERT, UPDATE, DELETE ON routes.__ef_migrations FROM echo_routes;
    REVOKE ALL ON routes.__ef_migrations FROM contactsapi;
  END IF;
  -- The backend's Routes module writes decisions and settings (AD-34)
  IF to_regclass('routes.route_decisions') IS NOT NULL THEN
    GRANT INSERT, UPDATE, DELETE ON routes.route_decisions TO contactsapi;
  END IF;
  IF to_regclass('routes.settings') IS NOT NULL THEN
    GRANT INSERT, UPDATE, DELETE ON routes.settings TO contactsapi;
  END IF;
END
$runtime$;

-- ---- 5. The sys_* functions: EXECUTE for one runtime role, column grants for their owner
DO $sys$
DECLARE f text; g record;
BEGIN
  FOREACH f IN ARRAY ARRAY['atlas.sys_owners(text)', 'atlas.sys_owners_with_connection(text)',
                           'atlas.sys_fail_interrupted_syncs(text)', 'atlas.sys_resolve_access_token(text)']
  LOOP
    IF to_regprocedure(f) IS NOT NULL THEN
      EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
      EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO contactsapi', f);
    END IF;
  END LOOP;
  FOREACH f IN ARRAY ARRAY['routes.sys_owners_with_open_interactive_jobs(timestamptz)', 'routes.sys_known_owners()',
                           'routes.sys_prune_finished_jobs(timestamptz)']
  LOOP
    IF to_regprocedure(f) IS NOT NULL THEN
      EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
      EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO echo_routes', f);
    END IF;
  END LOOP;

  -- What each function reads and writes, and nothing else (each grant where its table exists)
  FOR g IN SELECT * FROM (VALUES
      ('atlas.activities',         'SELECT ("OwnerId", "Type")',                                    'atlas_system'),
      ('atlas.fusion_queue',       'SELECT ("OwnerId")',                                            'atlas_system'),
      ('atlas.activity_fusion',    'SELECT ("OwnerId")',                                            'atlas_system'),
      ('atlas.photos',             'SELECT ("OwnerId", "Placed")',                                  'atlas_system'),
      ('atlas.places',             'SELECT ("OwnerId", "IdentifyState")',                           'atlas_system'),
      ('atlas.source_connections', 'SELECT ("OwnerId", "Provider", "Status")',                      'atlas_system'),
      ('atlas.source_connections', 'UPDATE ("Status", "LastError")',                                'atlas_system'),
      ('atlas.access_tokens',      'SELECT ("OwnerId", "TokenHash", "LastUsedAt")',                 'atlas_system'),
      ('atlas.access_tokens',      'UPDATE ("LastUsedAt")',                                         'atlas_system'),
      ('routes.jobs',              'SELECT ("OwnerId", "Priority", "State", "NextAttemptAt", "FinishedAt")', 'routes_system'),
      ('routes.jobs',              'DELETE',                                                        'routes_system'),
      ('routes.owner_cursors',     'SELECT ("OwnerId")',                                            'routes_system')
    ) AS v(tbl, priv, grantee)
  LOOP
    IF to_regclass(g.tbl) IS NOT NULL THEN
      EXECUTE format('GRANT %s ON %s TO %I', g.priv, g.tbl, g.grantee);
    END IF;
  END LOOP;
END
$sys$;

-- ---- 6. The backup role: SELECT only, on everything pg_dump reads (faces excluded, AD-24)
GRANT USAGE ON SCHEMA atlas, routes TO contacts_backup;
GRANT SELECT ON ALL TABLES IN SCHEMA atlas, routes TO contacts_backup;
GRANT SELECT ON ALL SEQUENCES IN SCHEMA atlas, routes TO contacts_backup;
ALTER DEFAULT PRIVILEGES FOR ROLE atlas_migrator IN SCHEMA atlas GRANT SELECT ON TABLES TO contacts_backup;
ALTER DEFAULT PRIVILEGES FOR ROLE atlas_migrator IN SCHEMA atlas GRANT SELECT ON SEQUENCES TO contacts_backup;
ALTER DEFAULT PRIVILEGES FOR ROLE routes_migrator IN SCHEMA routes GRANT SELECT ON TABLES TO contacts_backup;
ALTER DEFAULT PRIVILEGES FOR ROLE routes_migrator IN SCHEMA routes GRANT SELECT ON SEQUENCES TO contacts_backup;

-- ---- 7. Check (prints one row per role; every column after the name must read f) -----
SELECT r.rolname,
       r.rolsuper, r.rolcreatedb, r.rolbypassrls,
       EXISTS (SELECT 1 FROM pg_class c WHERE c.relowner = r.oid
                 AND c.relnamespace IN ('atlas'::regnamespace, 'routes'::regnamespace)) AS owns_tables
  FROM pg_roles r
 WHERE r.rolname IN ('contactsapi', 'echo_routes')
 ORDER BY r.rolname;
