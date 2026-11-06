# Epic 21 — Security hardening

> Vision: one compromised pod, one device on the home network or one stolen file no
> longer reaches every user's Google account and location history.

Requirements: PRD NFR-22 to NFR-25, with NFR-01, NFR-05, NFR-07, NFR-08, NFR-16, NFR-17
and FR-51's first increment (`echo/prd.md` §6, §4.1; the Owner's decisions are Q-15).
Architecture: AD-49 to AD-55 (`echo/architecture.md` §5J), which amend AD-3, AD-10,
AD-12, AD-18, AD-21, AD-22, AD-23, AD-24, AD-26, AD-27, AD-28, AD-29 and AD-32.

## Why

On 2026-11-03 five security assessments looked at Echo and the home-rasps cluster: the
attack surface and threat model, the database (with a row-level security design), a
static code review, an SBOM and dependency scan, and the platform. The application code
held up (no IDOR, injection, SSRF or XSS sink was found). The platform did not:

- Redis has no password, admits any pod and any tailnet device, and holds the
  unencrypted Data Protection key ring, every user's refresh token and live Google
  access tokens. Postgres runs on the chart's public default password and admits any pod.
- Cluster-admin is reachable from the internet behind a shared service token and a
  ServiceAccount token valid until 2036, both in one plaintext file; nothing is audited.
- Nothing is backed up. Both nodes are unpatched since install and accept SSH passwords.
- .NET 9 leaves support on 2026-11-10; the frontend image runs an end-of-life Alpine.
- The Flux controllers crash-loop, so a fix merged in git may never reach the cluster.

On 2026-11-04 the Owner decided the programme recorded in AD-49 to AD-55 (PRD Q-15),
before any of it is built. The stories run in five waves. A story whose change goes
through GitOps is done only when the **live object** shows it, not when its PR merges
(AD-55 rule 4).

## Stories

### Wave 0 — Prerequisites

#### 21.1 Off-node backup before any change — Wave 0

- [x] An off-node backup of `contactsdb` taken 2026-11-04 (`pg_dump -Fc` to the
  operator's laptop), before any database, password or RLS change (AD-24, AD-55 rule 2).
- [ ] The dump is stored encrypted, and a restore into a scratch database succeeds.
- [ ] It is deleted once the automated job (21.20) has produced two backups that
  restored successfully.

#### 21.2 A cluster that converges — Wave 0

- [ ] Memory limits on every container without one: the Grafana sidecars,
  kube-state-metrics, the prometheus-operator, Headlamp, Jaeger, local-path-provisioner,
  Traefik (`HelmChartConfig`) (AD-55 rule 4).
- [ ] kube-prometheus-stack pinned to an exact chart version; the `ArtifactFailed`
  HelmReleases (`kube-prometheus-stack`, `ollama`) reconcile.
- [ ] Ollama moved off the control-plane node or removed.
- [ ] The k3s datastore's size is investigated and compacted.
- [ ] Every Flux Kustomization and HelmRelease is Ready, and controller restart counts
  stay flat for 24 h.

### Wave 1 — Application and chart (Contacts repository)

#### 21.3 .NET 10 before 2026-11-10 — Wave 1, deadline

- [ ] `net10.0` in all five projects; EF Core, Npgsql and `Microsoft.*` on 10.0;
  `ModelContextProtocol.AspNetCore` 2.3.1 and the `Google.Apis.*` minors (AD-54 rules 1
  and 3, AD-23).
- [ ] Both Dockerfiles on `sdk:10.0-alpine` / `aspnet:10.0-alpine` pinned by digest.
- [ ] The test suite passes; both EF migration histories apply on a copy of the live
  database; backend and route service deployed together and running before 2026-11-10.
- [ ] `dotnet list package --vulnerable --include-transitive` reports nothing.

#### 21.4 Redis locked down — Wave 1

- [x] `redis.auth.enabled: true` as the chart default, the server reading
  `auth.existingSecret`, the backend's connection string rendered from Vault
  `Redis__Password` (AD-50 rule 1). *(Chart, `fix/chart-hardening`: rendering fails
  without `redis.auth.existingSecret`, with auth off, or with a password in values; the
  Vault Agent template builds `Redis__ConnectionString` from `Redis__Password`, so the
  backend needs no code change. The Vault key and the Secret
  (`helm/scripts/secrets-from-vault.sh`, with the rollout order in its header) are
  operator steps, done before the release upgrade. The backend reads
  `Redis:ConnectionString` and, when `Redis__Password` is also present, sets it on the
  connection options (`RedisSettings`, `fix/auth-session-hardening`); harmless when both
  carry the same value.)*
- [ ] The Redis NetworkPolicy admits 6379 only from the backend pod; a connection from
  another pod and from another namespace is refused. *(Chart: `allowExternal: false`,
  `allowExternalEgress: false`, `extraIngress` from the backend only; rendering fails on
  `allowExternal: true`. The refusal is checked live after the rollout.)*
- [x] `bitnami/redis` pinned to a current digest with `pullPolicy: IfNotPresent`.
  *(The chart default is the digest the live pod ran on 2026-11-03.)*

#### 21.5 The key ring wrapped, the exposed keys retired — Wave 1

- [x] `ProtectKeysWithCertificate` with `DataProtection__Certificate` from Vault, rendered
  as a file; `UnprotectKeysWithAnyCertificate` ready for a roll-over (AD-50 rule 2).
  *Code: `Services/KeyRing/EchoDataProtection.cs`. Accepts the PEM (certificate and
  private key) or base64 PKCS#12 inline in `DataProtection__Certificate`, or a rendered file
  in `DataProtection__CertificatePath`; `DataProtection__PreviousCertificate[Path]` for a
  roll-over. Without a certificate the app runs exactly as before (warning at startup). A
  startup guard, run before every other hosted service, refuses to start when the ring
  holds wrapped keys and the certificate is missing or wrong.*
- [ ] A new key created and activated; a one-shot job re-protects every protected value
  (refresh tokens, opt-ins, Activity Snapshot, own addresses, Other contacts dismissals,
  `source_connections.SecretProtected`, OneDrive tokens), with a count per kind and no
  failures. *Code ready (`DataProtection__Rotation`, runbook below); the run is the
  operator's. Opt-ins are stored in clear (no Data Protection), so there is nothing to
  re-protect for them; the job also covers the Contacts' histories and the public profiles.
  Sports Tracker and OneDrive secrets are both `source_connections.SecretProtected`.*
- [ ] The old keys revoked and deleted; the ring in Redis holds only wrapped keys
  (checked by reading it). *Code ready (`retire`); operator run pending.*
- [ ] The Owner revokes and re-grants Google, and reconnects OneDrive and Sports Tracker.

**Runbook: wrap the key ring and retire the exposed keys (operator).** Every step is a
Vault write plus a backend restart; the backend logs counts only, never values. Run it
before the RLS policies are on (Wave 2 step 4): the job reads `source_connections` across
Owners.

0. *Before:* the backend runs this branch's image with `Echo__AllowedEmails` set. Take a
   copy of the ring for rollback, kept encrypted off the cluster and deleted after step 6:
   `kubectl -n contacts exec contacts-redis-master-0 -- sh -c 'redis-cli --no-auth-warning -a "$REDIS_PASSWORD" LRANGE DataProtection-Keys 0 -1' | age -r <recipient> > dp-ring.age`.
1. *Certificate:* create an RSA 3072 key and a self-signed certificate (10 years; it is
   only a key-wrapping key) and store both PEM blocks in one Vault value:
   `openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -subj "/CN=contacts-dataprotection" -keyout dp.key -out dp.crt`,
   then `vault kv patch secret/contacts DataProtection__Certificate="$(cat dp.crt dp.key)"`
   and `shred -u dp.key`. Keep a copy of the PEM in the password manager: without it the
   wrapped ring cannot be read (the backend then refuses to start, by design). If the chart
   renders it as a file instead, set `DataProtection__CertificatePath` to that file.
2. *Wrapping on:* restart the backend (`kubectl -n contacts rollout restart deploy/contacts-backend`).
   Expect `Data Protection key ring wrapped with the Vault certificate: N keys, N not wrapped`.
3. *Dry run:* `vault kv patch secret/contacts DataProtection__Rotation=dry-run`, restart.
   Expect one `Key ring rotation (DryRun) <kind>: ...` line per kind; `re-protected` is what
   would move, `unreadable` must be 0.
4. *Re-protect:* `DataProtection__Rotation=reprotect`, restart. Expect `new key created` and
   per-kind counts with `changed meanwhile 0` and `unreadable 0`. Safe to repeat: a second
   run re-protects 0 and counts everything as `current`.
5. *Retire:* `DataProtection__Rotation=retire`, restart. Expect `N old keys revoked and
   deleted; every session ends`. If it logs `old keys kept: ...`, run it again (values that
   changed during the run move on the next run); `unreadable` above 0 means a value under a
   purpose the job does not know: stop and investigate, never delete keys by hand.
6. *Off:* `vault kv patch secret/contacts DataProtection__Rotation=off`, restart. Check the
   ring: every `<key ...>` entry of `LRANGE DataProtection-Keys 0 -1` contains
   `encryptedSecret`. Delete `dp-ring.age`.
7. *Owner:* sign in again; revoke Echo at https://myaccount.google.com/permissions and sign
   in again (a new Google grant); reconnect OneDrive and Sports Tracker in Atlas.

*Rollback.* Before step 5 nothing is lost: the old keys are still in the ring and every
value is readable as long as the certificate stays in Vault. Never remove
`DataProtection__Certificate` once a wrapped key exists (the guard then stops the backend
rather than let tokens become unreadable; restore the value to recover). Up to step 3 the
previous image still works (no wrapped key exists yet); from step 4 on it cannot unwrap the
new key, so fix forward.
After step 5 the old keys are gone; `RPUSH DataProtection-Keys` of the entries in
`dp-ring.age` brings them back if a value turns out to need one. A later certificate
roll-over: the new PEM in `DataProtection__Certificate`, the old one in
`DataProtection__PreviousCertificate`, restart, steps 4 to 6, then remove the previous one.

#### 21.6 Postgres password rotated, chart defaults removed — Wave 1

- [ ] `contactsapi` has a random password held only in Vault; the backend restarts on it;
  `psql` with the old password fails (AD-49 rule 8 step 1, AD-52 rules 1 and 3).
- [ ] `postgresql.auth.password` removed from `helm/contacts/values.yaml` and from the
  release in `raspberry_deployment`; `auth.existingSecret` used; the `postgres`
  superuser password moved to Vault `secret/data/contacts-pg-admin`. *(Chart: done. The
  password is gone, `auth.existingSecret` is required, and rendering fails when
  `password` or `postgresPassword` is set, so the release must drop it in the same
  change. The Vault path and the Secret are operator steps; the live superuser password
  sits only in the Bitnami Secret `contacts-postgresql`, which the upgrade deletes, so it
  is copied to Vault first.)*
- [x] The Postgres NetworkPolicy admits 5432 only from the backend, the route service and
  the backup job. *(Chart: `allowExternal: false`, no egress but DNS, `extraIngress` for
  the `backend`, `routes` and `backup` pod labels.)*
- [x] Postgres re-pinned to a current digest of major 18 (AD-54 rule 3). *(The live
  18.6.0 digest is now the chart default, not only the release's.)*
- [ ] A CI check fails when rendered values set a non-empty password, secret, token or
  key (AD-51 rule 8). *(The check exists, `helm/scripts/conformance.py --values`; wiring
  it into CI waits for AD-22.)*

#### 21.7 Invite-only sign-in from Vault configuration — Wave 1

- [x] The callback requires `email_verified` and checks the email against
  `Echo__AllowedEmails` (Vault) **before anything is stored**; a refused account has its
  grant revoked, nothing stored, and gets `NOT_INVITED` and the "invitation needed" page
  (AD-50 rule 6, AD-29).
- [x] Matching is on the normalised, verified email only; binding `sub` waits for AD-29's
  `platform.allowlist` table. *Comma, semicolon or space separated, case-insensitive. An
  empty or missing list admits nobody (fail closed) and existing sessions are refused too;
  startup logs a warning.*
- [x] Tests: an allowed account signs in; an account with `email_verified` false is
  refused; a refused one leaves no Redis or Postgres entry and its grant is revoked.
  *(`AuthSessionTests`; the callback writes nothing to Postgres.)*
- [x] The allowlist (the Owner and family members) is in Vault; no address is in the
  repository, values or a ConfigMap. *(Set in Vault, 6 addresses.)*

#### 21.8 Sessions re-keyed, browser hardened — Wave 1

- [x] `EchoSession` is cookie authentication with a Redis ticket store (protected
  tickets, keys under the Owner prefix); the callback clears the pre-sign-in session,
  deletes its cookie and calls `SignInAsync`; sign-out deletes the ticket (AD-50 rule 4).
- [x] Tests: the ticket key changes across the callback; a ticket replayed after sign-out
  is refused; the absolute lifetime of 30 days holds.
- [x] Cookies `__Host-ContactsApi.Auth` and `__Host-ContactsApi.Session`, `Secure` in
  every environment; local sign-in still works in Development. *(Attributes tested; a
  Development sign-in through a real browser on http://localhost is not yet tried.)*
- [x] The Google access token is stored only protected (`ContactsApi.Session.GoogleAccessToken.v1`)
  (AD-50 rule 3).
- [ ] nginx sends HSTS; the CSP has no `unsafe-eval` and no script `unsafe-inline`;
  `img-src` and `connect-src` list only the hosts used; `X-Forwarded-Proto` is set by
  nginx; the built bundle loads under the policy (AD-50 rule 5). *Chart work
  (`fix/chart-hardening`), not in the application branch.*
- [x] State-changing `/api/**` calls without `X-Echo-Request`, from another site (a
  sibling subdomain included) or with neither `Sec-Fetch-Site` nor `Origin` get 403
  `REQUEST_REJECTED`; `/api/mcp` is exempt; no `GET` changes stored state (AD-50 rule 7).

#### 21.9 Logs without codes, emails or coordinates — Wave 1

- [x] `RequestLoggingMiddleware` logs the route template, never the query string or the
  raw path; the session id is no longer logged (AD-50 rule 8). *Numeric values of
  paging and count keys are kept.*
- [x] A test sends `/api/auth/callback?code=X&state=Y` and finds neither value in any log
  event.
- [x] Swagger UI only in Development; `/api/assistant/status` requires a session;
  development credentials moved to user-secrets and `.env.example` (AD-50 rule 10).
  *The EF design-time factories keep their placeholder string (`design`/`design`, never
  used to connect), left alone to avoid a conflict with `feat/postgres-rls`.*

#### 21.10 MCP tokens expire and start read-only — Wave 1

- [x] `access_tokens` gains `ExpiresAt` (default 90 days, at most 365) and a `write`
  scope; update and delete tools require it; expired tokens are refused (AD-50 rule 9,
  AD-18). *Migration `AccessTokenExpiry`; all twelve tools that change data need `write`.*
- [x] A per-token rate limit; the token list shows the last use and the expiry.
  *`Mcp__RateLimit__PerMinute` (60) and `__PerDay` (5000), in process memory (one replica).*
- [x] Existing tokens get an expiry 90 days from the migration, with a notice.

#### 21.11 Restricted pods and default-deny in `contacts` — Wave 1

- [ ] Every Contacts container has the restricted securityContext, Valhalla included
  (`runAsUser` 1001), and the tiles, migrate and backup containers (AD-51 rule 5).
  *(Chart: done for every container that exists, Valhalla and tiles included; Valhalla
  as 1001 was checked against the pinned image and tiles (AA-19). The migrate init
  container (21.15) and the backup job (21.20) must use the charts'
  `containerSecurityContext` when they are added.)*
- [x] `automountServiceAccountToken: false` everywhere; the frontend has its own
  ServiceAccount; the Vault Agent gets a projected token with audience `vault` (AD-51
  rule 6). *(Chart. The Vault roles' `audience` (21.21) is set after the new pods run:
  a role without one checks the token against its own audience, so the projected token
  logs in; a role with one rejects the old pods' default tokens. Rollout order:
  `helm/scripts/secrets-from-vault.sh`.)*
- [x] Requests and limits on every container; a `LimitRange` in `contacts` (AD-51
  rule 7). *(The Vault Agent containers get the injector's defaults.)*
- [ ] The chart ships the default-deny policy and the allows of AD-51 rule 2, with
  `routes.backendIngressPolicy` on; every pod's probes stay green; the backend reaches
  Google over 443 and cannot reach a home-network address. *(Chart: done, with the
  backup job's policy and `global.networkPolicy.nodeAddresses` for AA-18. The probe,
  Google and home-network checks are live checks after the rollout.)*
- [ ] The namespace enforces `restricted` and every pod is admitted. *(The label belongs
  in `raspberry_deployment` `apps/contacts/namespace.yaml`; the rendered chart passes
  the restricted checks of `helm/scripts/conformance.py`.)*
- [ ] The conformance check of AD-51 rule 8 runs on the rendered manifests in CI: no
  `:latest`, no `NodePort`, the rule 5 securityContext on every container, a
  NetworkPolicy selecting every pod, no `allowExternal: true`. *(The check is
  `helm/scripts/conformance.py`; CI waits for AD-22. With the live values it still
  fails on the frontend and backend `:latest` images, which 21.24 pins.)*

#### 21.12 Frontend image and dependencies — Wave 1

- [ ] The frontend image on a current nginx stable Alpine and a `node:24-alpine`
  builder, both pinned by digest; Trivy shows no Critical or High OS finding (AD-54
  rule 2).
- [ ] Runtime npm upgrades of AD-54 rule 3 in one PR; `npm audit --omit=dev` is clean;
  jest passes; sign-in, the contacts grid and Atlas smoke-tested.
- [ ] Dev-tool upgrades of AD-54 rule 3 in a second PR.
- [ ] The accepted findings of AD-54 rule 3 are recorded where the scan reads them.

#### 21.13 maplibre-gl 6 — Wave 1, within 30 days

- [ ] `maplibre-gl` 6.x; the Atlas base map, the density layer and route drawing
  re-tested; `npm audit --omit=dev` reports no Critical (AD-54 rule 3).

### Wave 2 — Database row-level security (AD-49)

#### 21.14 Shadow mode — Wave 2

- [x] `OwnerScope` and `OwnerRlsInterceptor` on every DbContext, in log-only mode; the
  configuration guards (no multiplexing, reset on close, application name, pool size)
  (AD-49 rule 4). `RowLevelSecurity__Mode` = `Off` / `Shadow` (default) / `Enforce`;
  code in `ContactsApi.Infrastructure/Tenancy/`.
- [x] Every entry point sets the Owner: the `/api/**` middleware (`OwnerScopeMiddleware`),
  the MCP token (the `/api/mcp` PAT middleware until AD-3 rule 3's `EchoPat` handler
  exists; it resolves through `sys_resolve_access_token`), `InternalRoutesController`,
  the route service's `/internal/v1/*` (an endpoint filter), every hosted worker per item
  in a fresh scope (`CreateOwnerScope`).
- [x] The `sys_*` functions created by `AtlasSystemFunctions` and `RoutesSystemFunctions`;
  workers refactored to them; `PlacePendingPhotosAsync` runs one transaction per Owner;
  `DeleteGoneOwnersAsync` deletes under each gone Owner's scope (AD-49 rule 5). Also
  converted, beyond the AD's list: best efforts and place identification (new
  `sys_owners` kinds `places_to_identify`; efforts use `activities`), the Takeout claim
  reset and due check, and the route service's line fill (`sys_known_owners`).
- [ ] A few days of live logs with zero "owner scope missing" warnings. *Operator: deploy
  the image with the chart defaults (`Shadow`, policies off), then count
  `RLS: owner scope missing` in Seq.*

#### 21.15 Database isolation tests — Wave 2

- [x] A Testcontainers fixture (`postgres:18`) that runs `db-roles.sql`, migrates as the
  migrators and seeds Owners A and B in every table (AD-49 rule 9). (`tests/.../Rls/`; it
  seeds a third Owner for the destructive tests, and skips with the reason when Docker is
  not available.)
- [ ] The catalog, unset, cross-tenant read and write, pooling, definer-function,
  role-boundary and destructive-path tests of AD-49 rule 9 pass, and run on every PR as
  part of the NFR-07 gate. *All 22 pass locally (`dotnet test --filter
  FullyQualifiedName~RowLevelSecurityDbTests`), plus a no-Docker model test; "on every PR"
  waits for CI (AD-22).*
- [ ] The two-Owner fixture of AD-3 rule 4 passes over every `/api/**`, `/internal/routes/*`
  and `/internal/v1/*` route with RLS on.

#### 21.16 Roles split, migrations out of the app — Wave 2

- [ ] `helm/contacts/files/db-roles.sql` run once as `postgres`; ownership of `atlas`
  and `routes` moved to the migrators; runtime roles DML only, with their timeouts
  (AD-49 rule 2).
- [ ] `--migrate-only` init containers in the backend and route pods, with
  `secret/data/contacts-migrate` rendered only by the Vault Agent's init container
  (`agent-configmap`, `agent-init-first: "true"`) and deleted by the migrate container
  when it finishes; a check in the running main container finds no migrate credential
  under `/vault/secrets` (AD-49 rule 3).
- [ ] The catalog test passes against the live database; both pods migrate through their
  init container; the `sys_*` functions are re-owned to the system roles.

*Built (not run against the cluster):* `helm/contacts/files/db-roles.sql` and
`db-roles-rollback.sql` (idempotent, no passwords in them; a split and rollback round trip
was run twice on `postgres:18`); `--migrate-only` in both executables
(`Database__MigrationConnectionString`, `Routes__MigrationConnectionString`); chart
`migrations.initContainer` (off by default) with the agent configmap, init-first and the
delete; `/health/ready` unready while migrations are pending.

#### 21.17 Policies on, then throw — Wave 2

- [ ] `AtlasRowLevelSecurity` deployed, then `RoutesRowLevelSecurity` (AD-49 rule 8).
- [ ] `EXPLAIN` of the five heaviest reads compared before and after; no regression past
  NFR-09.
- [ ] Live smoke test: the map, the timeline, the routes panel, an MCP tool, a Sports
  Tracker sync.
- [ ] The interceptor switched to throwing; 24 h without `OwnerScopeMissingException`.

*Built behind configuration:* the policy migrations apply only with
`RowLevelSecurity__Policies=true` (the migrator stops before them otherwise, and readiness
does not wait for them); throwing is `RowLevelSecurity__Mode=Enforce`.

**Runbook for 21.14 to 21.17 (operator, in order; each step has its gate):**

1. *Backup* (AD-55): a fresh off-node `pg_dump -Fc` of `contactsdb` as `postgres`, restored
   into a scratch database. Rotate `contactsapi`'s password first if not done (rollout
   step 1, AD-52).
2. *Shadow* (21.14): deploy the new backend and route images with chart 0.7.0 defaults
   (`rowLevelSecurity.mode: Shadow`, `policies: false`, `migrations.initContainer: false`).
   The apps still migrate at startup and create the `sys_*` functions. Gate: a few days of
   Seq with zero `RLS: owner scope missing`.
3. *Roles* (21.16): `psql -v ON_ERROR_STOP=1 -U postgres -d contactsdb -f db-roles.sql`;
   `\password atlas_migrator`, `\password routes_migrator`, `\password contacts_backup`;
   `vault kv put secret/contacts-migrate Database__MigrationConnectionString=... Routes__MigrationConnectionString=...`
   (`Username=atlas_migrator` / `routes_migrator`); add
   `path "secret/data/contacts-migrate" { capabilities = ["read"] }` to the `contacts` and
   `contacts-routes` Vault policies; check `backend.vault.address`; set
   `backend.migrations.initContainer: true`, `routes.migrations.initContainer: true` and
   `backend.probes.readiness.path: /health/ready`. Gate: both pods pass Init; `ls
   /vault/secrets` in the main containers shows no `migrate`; the catalog queries of
   `RowLevelSecurityDbTests` pass against live (read-only, as `postgres`); `sys_*` owned by
   `atlas_system` / `routes_system`.
4. *Policies* (21.17): `EXPLAIN (ANALYZE, BUFFERS)` of the five heavy reads; then
   `backend.rowLevelSecurity.policies: true` (AtlasRowLevelSecurity), and once the backend
   is Ready `routes.rowLevelSecurity.policies: true` (RoutesRowLevelSecurity). Gate: the
   same `EXPLAIN`s, the smoke test above.
5. *Enforce*: `rowLevelSecurity.mode: Enforce` in both. Gate: 24 h without
   `OwnerScopeMissingException`.

*Rollback:* step 5 by `Shadow`; step 4 by running the policy migrations' `Down()` as the
migrator (`dotnet ef database update AtlasSystemFunctions` / `RoutesSystemFunctions` with
the migrator's string, or the `DROP POLICY` / `NO FORCE` / `DISABLE` statements in psql)
and `policies: false`; step 3 by `initContainer: false` and `db-roles-rollback.sql`.

#### 21.18 Data-model follow-up — Wave 2

- [ ] `track_points.OwnerId` backfilled in batches and `NOT NULL`, with the plain policy
  (AD-49 rule 10).
- [ ] Composite foreign keys for `flights`, `activity_fusion`, `activity_efforts`,
  `track_points`, `visits`, `place_reviews` and `review_reminders`.
- [ ] `pgaudit.log = 'ddl, role'`, connection logging and the log prefix on.

### Wave 3 — Platform (`raspberry_deployment`)

#### 21.19 Default-deny networks and Pod Security Admission everywhere — Wave 3

- [ ] Default-deny with explicit allows in `vault`, `cloudflared`, `monitoring`,
  `monitoring-host`, `glutguard`, `headscale`, `logging`, `flux-system`, `ollama` and
  `headlamp` (AD-51 rules 1 and 3); cloudflared egress on 7844 and 443 only; API-server
  clients reach the nodes on 6443; the injector webhook is admitted from the nodes, and
  a restarted Contacts pod still has its `vault-agent` container.
- [ ] PSA labels per AD-51 rule 4, `enforce` raised only after `warn` is clean; the
  third-party workloads in `restricted` namespaces on the rule 5 securityContext; Ollama
  and Headlamp moved to their own namespaces; node-exporter moved to `monitoring-host`; fluent-bit's
  `/var/log` mount read-only; the subnet router in userspace mode.
- [ ] A probe pod in another namespace cannot reach Contacts' Postgres, Redis or the
  backend.

#### 21.20 Backups automated — Wave 3

- [ ] The `contactsdb` CronJob as `contacts_backup`: `pg_dump -Fc`, `age`, rclone to
  Drive with `drive.file`, 14 daily and 8 weekly (AD-55 rule 2, AD-24); its Vault Agent
  is pre-populate-only, so the Job completes.
- [ ] The same for GlutenGuardian's database, Vault and the k3s datastore (with its
  `cred` and `tls` directories); Headscale's copy moved off-node.
- [ ] A restore of each into a scratch target succeeds and is recorded.
- [ ] The `age` identity is in Vault and in the password manager.

#### 21.21 Vault hardened — Wave 3

- [ ] TLS on the listener; `ClusterIP` only; the Vault Agent trusts the CA (AD-52 rule 6).
- [ ] An audit device to stdout, shipped to Seq.
- [ ] `audience` on the Kubernetes roles; the `openclaw` role and policy deleted; the
  backend template renders only the keys it uses.
- [ ] Upgraded to a supported release, its storage moved to Raft (AD-52 rule 6, AD-54
  rule 3).

#### 21.22 Secrets encrypted, fewer readers — Wave 3

- [ ] k3s `secrets-encryption` on and every Secret re-encrypted; the reviewed
  `config.yaml` kept in the repository (AD-52 rule 4).
- [ ] Grafana `rbac.namespaced`, sidecars on ConfigMaps in `monitoring` only (AD-52
  rule 5).
- [ ] Grafana on a random admin password from Vault, anonymous off, Access JWT login,
  Infinity removed or restricted (AD-52 rule 7).
- [ ] The stale `apps/*/secret.yaml` Secrets removed; Contacts gets its own Gemini key
  (AD-52 rules 2 and 10).

#### 21.23 Exposure closed — Wave 3

- [ ] Grafana, Vault, Ollama and Headlamp are `ClusterIP`; no NodePort remains (AD-53
  rule 4).
- [ ] The Traefik catch-all, `contacts.lan` and `headlamp.local` routes are gone;
  `/api/mcp` is unreachable from the LAN.
- [ ] Every Access-protected hostname sets `originRequest.access.required`; the API hop
  verifies the cluster CA (AD-53 rule 5).
- [ ] Grafana, Headlamp and Seq limited to the operator's identity; Seq requires an
  ingestion key; the orphan service token revoked (AD-53 rule 6).
- [ ] The subnet router advertises only the two node host routes; the Headscale ACL
  allows only ports 22 and 6443 to tagged nodes; pre-auth keys are single-use and
  short-lived (AD-53 rule 7).

#### 21.24 Supply chain — Wave 3

- [ ] Branch protection on Contacts `main` and `dev`; secret scanning and push protection
  on (AD-54 rule 6).
- [ ] The `contacts` HelmRelease runs as `contacts-deployer`; the controllers run with
  `--default-service-account` (AD-54 rule 7).
- [ ] CI builds images with SBOM and provenance, Trivy-scanned and cosign-signed; PR CI
  runs the npm and NuGet vulnerability gates with NuGet lock files (AD-54 rule 8, AD-22).
- [ ] Every third-party image pinned by tag and digest; no `:latest` deployed anywhere
  (AD-54 rule 5).
- [ ] Renovate on both repositories (AD-54 rule 9).
- [ ] Platform minors: cloudflared, Traefik, Headscale 0.29.x, Flux 2.10, vault-k8s 1.7.x
  (AD-54 rule 10).
- [ ] Scheduled majors of AD-54 rule 3: kube-prometheus-stack to a current chart (its
  Grafana provisioning checked first), k3s one minor at a time to a supported line.

#### 21.25 Security monitoring — Wave 3

- [ ] fluent-bit ships the API audit log, the Vault audit log and the Postgres audit log
  to Seq, kept 30 days (AD-55 rule 3).
- [ ] The Grafana alerts and Seq alerts of AD-55 rule 3 exist and email the operator.
- [ ] Each alert fired once by a test event and received within 15 min.

### Wave 4 — Operator actions

#### 21.26 Node OS hardening — Wave 4 (*in progress, 2026-11-04*)

- [ ] Both nodes patched one at a time (drain, upgrade, reboot, unseal Vault, uncordon)
  (AD-55 rule 1).
- [ ] Desktop services disabled on both nodes.
- [ ] `unattended-upgrades` installed, security updates only, automatic reboot off.
- [ ] SSH key-only, no root login, no X11 forwarding, on both nodes; the operator's key
  installed first.
- [ ] Follow-ups: the nftables INPUT allow-list, `sudo` with a password, k3s on its latest
  supported patch, desktop packages removed.

#### 21.27 Remote administration behind the Owner's identity — Wave 4

- [ ] The `k8s.mayaffit.com` Access application allows only the Owner's personal login;
  the shared service token deleted (AD-53 rule 1).
- [ ] The `headlamp` ServiceAccount recreated; the legacy token Secret and the cluster-admin
  bindings not in git deleted (AD-53 rule 2).
- [ ] `remote-reader` and `remote-operator` ServiceAccounts with tokens minted on demand;
  the API server caps token lifetimes at 24 h.
- [ ] API audit logging on (AD-53 rule 3).

#### 21.28 Credentials rotated, Vault rekeyed — Wave 4

- [ ] Every credential in the operator's credentials file rotated and moved to a
  password manager; the file deleted (AD-52 rule 8).
- [ ] The Cloudflare API token replaced by a read-only token and a break-glass edit token;
  GitHub tokens fine-grained with an expiry.
- [ ] The Vault root token revoked; Vault rekeyed to 3 of 5 offline shares; the unsealer
  and its Secret deleted (AD-52 rule 6).
- [ ] The GlutenGuardian database password rotated.

#### 21.29 Real keys purged from the deployment history — Wave 4

- [ ] OpenAI, Gemini, the Google Drive service account and the session secret rotated
  first (AD-52 rule 9).
- [ ] The commit holding them purged with `git filter-repo` and force-pushed; Flux
  reconciles afterwards.

#### 21.30 Google OAuth app — Wave 4

- [ ] The OAuth app stays in Testing with explicit test users until 21.7 is live and the
  FR-50 legal pages exist, then moves to Production, unverified (AD-26).
- [ ] The family members' addresses are on the allowlist in Vault.
