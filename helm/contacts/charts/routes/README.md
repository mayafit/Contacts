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
Valhalla has its own liveness probe (`GET /status` on the loopback, a 20 s timeout,
restarted only after 5 failures 30 s apart, `valhalla.livenessProbe`), so a hung
engine is restarted rather than left to trip the breaker forever.

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

### 2. Vault: the pod's own path (`secret/data/contacts-routes`)

The routes pod reads **only** `secret/data/contacts-routes` (`vault.kvPath`), and its
role can read nothing else, so none of the backend's secrets
(`secret/data/contacts`: Google OAuth, the backend's database credentials, Gemini)
can reach it, whatever its template says (least privilege, AD-32 rule 7).

| Key | Path | Value |
| --- | --- | --- |
| `Routes__ServiceToken` | `contacts-routes` **and** `contacts` | a new random token (32+ bytes). The routes service sends it; the backend accepts it only on `/internal/routes/*` |
| `Routes__BackendToken` | `contacts-routes` **and** `contacts` | a different random token. The backend sends it; the service accepts nothing else on `/internal/v1/*` |
| `Routes__ConnectionString` | `contacts-routes` only | `Host=<release>-postgresql;Port=5432;Database=contactsdb;Username=echo_routes;Password=<the role's password>` |

The backend keeps its copy of the two tokens in its own path, `secret/data/contacts`
(its template already exports them when present), so the backend's role is not
widened and a missing `contacts-routes` path can never stop the backend's agent from
rendering. The cost is one rule: **the two tokens must be equal in both paths**;
write and rotate them together (AD-21).

```sh
# Once (as a Vault admin). The tokens: openssl rand -base64 48
vault kv put secret/contacts-routes \
  Routes__ServiceToken="$SVC" Routes__BackendToken="$BE" \
  Routes__ConnectionString="Host=<release>-postgresql;Port=5432;Database=contactsdb;Username=echo_routes;Password=<pw>"
vault kv patch secret/contacts Routes__ServiceToken="$SVC" Routes__BackendToken="$BE"

vault policy write contacts-routes - <<'HCL'
path "secret/data/contacts-routes" { capabilities = ["read"] }
HCL
vault write auth/kubernetes/role/contacts-routes \
  bound_service_account_names=<release>-routes \
  bound_service_account_namespaces=<namespace> \
  policies=contacts-routes ttl=1h
```

Check: `vault policy read contacts-routes` lists the one path. Rotation: generate new
values, `kv patch` both paths with them, then restart the backend and the routes pod
(the agents render the files at start).

### 3. The tiles image

`tiles.tag` is `il-20261029-r2@sha256:791011ad…` (spike 12.1 round 2: 82 tiles,
a 199 MB tar, so `tiles.sizeLimit` is the 1Gi floor). `valhalla.image` is pinned to
`3.9.1@sha256:4fd114eb…`, and `nodeSelector` is `kubernetes.io/hostname: rasp02`
(about 1.9 GiB free; rasp01 is overcommitted). A new tiles image must keep
`valhalla.json` beside the tar, with its paths under `/data`; a config change is
a tiles-image change. Results: AD-32 and AD-36 in the architecture, and story
12.1 in the epic.

### 4. The worker image

There is no CI build yet. Tag the image with the **short sha** of the commit it is
built from, never `latest`: the chart refuses an empty or `latest` `image.tag`, and
with a fixed tag `IfNotPresent` always runs the image the values name (a moved
`latest` would never be pulled again). From `ContactsApi/`, on the merged commit:

```sh
TAG=$(git rev-parse --short HEAD)
docker buildx build --platform linux/arm64 -f src/EchoRoutes.Worker/Dockerfile \
  -t ghcr.io/mayafit/contacts-routes:$TAG --push .
```

Then set `routes.image.tag: <that sha>` in the release values (the chart's default
is the sha the chart was last tested with). A new worker build is a values change.

## Network policies

- **The routes pod** (both containers) may reach only the backend Service port,
  Postgres (pods labelled `networkPolicy.postgres.podLabels`, port 5432), cluster
  DNS (`kube-dns`, 53 UDP and TCP) and, when `vault.enabled`, Vault
  (`vault.namespace`, port 8200). The Vault agent sidecar runs in the pod, so the
  policy must let it reach Vault. Ingress is from the backend only.
- **The backend pod** gets a policy only when `backendIngressPolicy.enabled` is
  true (default **off**). Once on, it admits the frontend's nginx, the routes pod
  and the peers in `backendIngressPolicy.extraBackendIngressFrom`, and **cuts every
  other peer off**. nginx proxies only `/api/`, so `/internal/**` is never reachable
  through the frontend either way; the routes token is accepted only on
  `/internal/routes/*`. Before switching it on, list the live peers and add each one
  that is not the frontend:

  ```sh
  # Who reaches the backend today? Its Service, the Ingress / IngressRoutes, the tunnel, scrapes
  kubectl -n <ns> get svc <release>-backend -o wide
  kubectl get ingress,ingressroute -A | grep -i <release>
  kubectl get pods -A -l app.kubernetes.io/name=cloudflared     # an in-cluster Cloudflare tunnel
  kubectl get servicemonitor,podmonitor -A 2>/dev/null | grep -i <release>
  ```

  Typical peers: the ingress controller (Traefik in `kube-system`) if any route
  targets the backend directly, an in-cluster `cloudflared` if its config points at
  the backend, and the metrics scraper (Prometheus). For example:

  ```yaml
  routes:
    backendIngressPolicy:
      enabled: true
      extraBackendIngressFrom:
        - namespaceSelector: { matchLabels: { kubernetes.io/metadata.name: kube-system } }
          podSelector: { matchLabels: { app.kubernetes.io/name: traefik } }
        - namespaceSelector: { matchLabels: { kubernetes.io/metadata.name: monitoring } }
  ```

  Then check from each peer (`kubectl exec … -- wget -qO- http://<release>-backend:5000/health`)
  and keep the old revision at hand to roll back.

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
| Switch on | `routes.enabled: true` (this pod) **and** `backend.routes.enabled: true` (the backend sets `Routes__ServiceUrl`; that also turns on the backend's matched-route reads, `RouteGeometryOptions.Enabled`), with `routes.vault.enabled: true` and `routes.image.tag: <short sha>` |
| Vault | `secret/data/contacts-routes`: `Routes__ServiceToken`, `Routes__BackendToken`, `Routes__ConnectionString`, read only by role `contacts-routes`; the two tokens also in `secret/data/contacts` for the backend, equal in both; see step 2 |
| Backend ingress policy | Off (`routes.backendIngressPolicy.enabled: false`); list the live peers first (Network policies) |
| DB roles | `echo_routes` owns schema `routes`; the backend role (`contactsapi`) needs `USAGE` on `routes`, `SELECT` on its tables, and `INSERT, UPDATE, DELETE` on `route_decisions` and `settings` (step 1). Without the grants the map silently draws the recorded lines and decisions answer 503 `ROUTES_UNAVAILABLE` |
| Tiles image | `tiles.tag` = `il-20261029-r2@sha256:791011ad…` (step 3); it carries the engine's `valhalla.json` |
| Spike results | Story 12.1 (round 2, 2026-10-29): median 0.134 s, p95 0.73 s per drive, about 1.0 h for 15,420 drives, about 366 MiB anonymous RSS with one thread; details in AD-32 and AD-36 and the epic's story 12.1; `scripts/routes/accuracy-check.py` reproduces the accuracy figures |
| What the Owner sees | `GET /api/routes/status` (one line in the import dialog), `GET /api/routes/export` (JSON with GeoJSON matched routes) |

Switching either flag off returns every map, list and panel to the recorded lines;
nothing in `atlas` was changed by matching, so there is nothing to undo.

If the backend's grants (step 1) are missing, the backend logs it once and draws the
recorded lines for 5 minutes before it looks again; it never fails a read for it.

## Enabling, in order

1. The `echo_routes` role, the schema and the backend's grants (step 1).
2. The Vault path, policy and role, and the two tokens copied into
   `secret/data/contacts` (step 2).
3. Build and push the worker image; note its short sha (step 4).
4. Release with `routes.enabled: true`, `routes.vault.enabled: true`,
   `routes.image.tag: <sha>` and `backend.routes.enabled: true`. The service migrates
   the schema at start; run the `GRANT SELECT ON ALL TABLES` of step 1 again after
   its first start (and after a migration adds a table).
5. Check: the pod's both containers ready; `GET /api/routes/status` answers;
   the backend logs no "routes schema or the backend's grants are missing".
6. Optional, later: the backend ingress policy, after listing its live peers.
