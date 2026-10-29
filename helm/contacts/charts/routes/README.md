# routes subchart: the route-quality service

`EchoRoutes.Worker` (route quality, Epic 12) with Valhalla in the same pod.
Architecture: `docs/planning/echo/architecture.md` §5A, AD-32 to AD-36.
It is off by default (`routes.enabled: false`). Switching it off has no effect on
Atlas beyond the original lines coming back.

## What the pod is

| Container | What | Listens |
| --- | --- | --- |
| `tiles` (init) | The tiles image (`contacts-routing-tiles`, FROM busybox) copies `valhalla_tiles.tar`, `valhalla.json`, `graph.json` and the `.poly` outlines into the `emptyDir` | nothing |
| `routes` | The .NET service: scheduler, stage A rule `routes.v1`, `/internal/v1/*`, `/health/live`, `/health/ready` | `:8080` |
| `valhalla` | The routing engine: `valhalla_service /data/valhalla.json 1`, with the config shipped in the tiles image (spike 12.1 round 2: tiles read from the tar, a 32 MiB LRU cache, reserved memory released, no tile URL, no elevation download, a 14 s server timeout, trace limits 200 km and 2,000 points). `files/valhalla.json` is a reference copy; the chart templates nothing over it | `127.0.0.1:8002` only |

`/health/ready` turns ready once `graph.json` has been read and Valhalla answers.

## Manual steps before `routes.enabled: true`

### 1. The `echo_routes` database role (operator, once)

The service writes only the `routes` schema, through its own role. Grants need the
Postgres superuser, so the service's migrations cannot do them. Run as `postgres`
in the app database (`contactsdb`):

```sql
CREATE ROLE echo_routes LOGIN PASSWORD '<a new random password>';
GRANT CONNECT ON DATABASE contactsdb TO echo_routes;
-- The schema belongs to the role: its migrations create the tables and the
-- history table routes.__ef_migrations inside it, and nothing else.
CREATE SCHEMA IF NOT EXISTS routes AUTHORIZATION echo_routes;
-- No grant on atlas, public or any other schema. On Postgres < 15, also:
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

-- The backend's Routes module (stories 12.6 to 12.8) reads routes and writes
-- only decisions and settings:
GRANT USAGE ON SCHEMA routes TO contactsapi;
ALTER DEFAULT PRIVILEGES FOR ROLE echo_routes IN SCHEMA routes
  GRANT SELECT ON TABLES TO contactsapi;
-- After the service's first start (when the tables exist):
GRANT SELECT ON ALL TABLES IN SCHEMA routes TO contactsapi;
GRANT INSERT, UPDATE, DELETE ON routes.route_decisions, routes.settings TO contactsapi;
```

Check: `SET ROLE echo_routes; SELECT 1 FROM atlas.activities LIMIT 1;` must fail
with "permission denied".

**Backups.** The nightly dump must cover the whole database (not `-n atlas`) so
that `routes` is included (AD-34 rule 9). Dump as `postgres`, or grant the
backup role `USAGE` on `routes` and `SELECT` on its tables.

### 2. Vault keys (`secret/data/contacts`)

| Key | Read by | Value |
| --- | --- | --- |
| `Routes__ServiceToken` | backend and routes | a new random token (32+ bytes). The routes service sends it; the backend accepts it only on `/internal/routes/*` |
| `Routes__BackendToken` | backend and routes | a different random token. The backend sends it; the service accepts nothing else on `/internal/v1/*` |
| `Routes__ConnectionString` | routes only | `Host=<release>-postgresql;Port=5432;Database=contactsdb;Username=echo_routes;Password=<the role's password>` |

The backend's Vault template exports every key, so it picks up the two tokens
with no chart change. The routes pod's template exports only these three keys, so
the backend's own `Database__ConnectionString` never enters it.

Create a Vault Kubernetes-auth role `contacts-routes` bound to the service
account `<release>-routes` in the release namespace, with a policy that reads
`secret/data/contacts`. Rotate both tokens with the other secrets (AD-21).

### 3. The tiles image

`tiles.tag` is `il-20261029-r2@sha256:791011ad…` (spike 12.1 round 2: 82 tiles,
a 199 MB tar, so `tiles.sizeLimit` is the 1Gi floor). `valhalla.image` is pinned to
`3.9.1@sha256:4fd114eb…`, and `nodeSelector` is `kubernetes.io/hostname: rasp02`
(about 1.9 GiB free; rasp01 is overcommitted). A new tiles image must keep
`valhalla.json` beside the tar, with its paths under `/data`; a config change is
a tiles-image change. Results: AD-32 and AD-36 in the architecture, and story
12.1 in the epic.

### 4. The worker image

There is no CI build yet. From `ContactsApi/`:

```sh
docker buildx build --platform linux/arm64 -f src/EchoRoutes.Worker/Dockerfile \
  -t ghcr.io/mayafit/contacts-routes:<tag> --push .
```

## Network policies

- **The routes pod** (both containers) may reach only the backend Service port,
  Postgres (pods labelled `networkPolicy.postgres.podLabels`, port 5432), cluster
  DNS (`kube-dns`, 53 UDP and TCP) and, when `vault.enabled`, Vault
  (`vault.namespace`, port 8200). The Vault agent sidecar runs in the pod, so the
  policy must let it reach Vault. Ingress is from the backend only.
- **The backend pod** admits the frontend's nginx and the routes pod on its port.
  Add any other peer that must reach the backend directly to
  `networkPolicy.extraBackendIngressFrom`. This policy exists only while
  `routes.enabled` is true. nginx proxies only `/api/`, so `/internal/**` is
  never reachable through the frontend.

## Tuning matching on the Pi (stories 12.4, 12.5)

Every engine-facing number is a `config` value (`Routes__*`), so a finding of the
Pi spike is a values change, not a release: `EngineConcurrency` (1),
`EngineTimeoutSeconds` (15), `BreakageDistanceM` (5000), `SearchRadiusM` (100),
`GpsAccuracyTimelineM` / `GpsAccuracyDenseM` (75 / 15), `SplitGapM` /
`SplitGapMinutes` (5000 / 15), `MaxPointsPerCall` / `MaxKmPerCall` (2000 / 600),
`BackoffMinutes` (`1,10,60`), `BreakerFailures` / `BreakerPauseMinutes` (5 / 10),
`MatchBatch` / `MatchPauseMs` (25 / 1000), `AssessBatch` / `PauseMs` (500 / 400),
`PollSeconds` (120) and `SweepDays` (7).

When the engine fails `BreakerFailures` times in a row, all matching pauses for
`BreakerPauseMinutes`; `GET /api/routes/status` shows it under `engine.breaker`.

To let the backend reach the service, set `backend.routes.enabled: true` together
with `routes.enabled: true`. The backend then needs `Routes__BackendToken` (and
`Routes__ServiceToken`) in Vault; its agent template exports them when present.

The accuracy check (`scripts/routes/accuracy-check.py`) runs against the live
database and engine through port-forwards and prints aggregates only.

## Operations summary (R1)

| What | Where |
| --- | --- |
| Switch on | `routes.enabled: true` (this pod) **and** `backend.routes.enabled: true` (the backend sets `Routes__ServiceUrl`; that also turns on the backend's matched-route reads, `RouteGeometryOptions.Enabled`) |
| Vault keys (`secret/data/contacts`) | `Routes__ServiceToken`, `Routes__BackendToken` (backend and routes), `Routes__ConnectionString` (routes only); see step 2 |
| DB roles | `echo_routes` owns schema `routes`; the backend role (`contactsapi`) needs `USAGE` on `routes`, `SELECT` on its tables, and `INSERT, UPDATE, DELETE` on `route_decisions` and `settings` (step 1). Without the grants the map silently draws the recorded lines and decisions answer 503 `ROUTES_UNAVAILABLE` |
| Tiles image | `tiles.tag` = `il-20261029-r2@sha256:791011ad…` (step 3); it carries the engine's `valhalla.json` |
| Spike results | Story 12.1 (round 2, 2026-10-29): median 0.134 s, p95 0.73 s per drive, about 1.0 h for 15,420 drives, about 366 MiB anonymous RSS with one thread; details in AD-32 and AD-36 and the epic's story 12.1; `scripts/routes/accuracy-check.py` reproduces the accuracy figures |
| What the Owner sees | `GET /api/routes/status` (one line in the import dialog), `GET /api/routes/export` (JSON with GeoJSON matched routes) |

Switching either flag off returns every map, list and panel to the recorded lines;
nothing in `atlas` was changed by matching, so there is nothing to undo.
