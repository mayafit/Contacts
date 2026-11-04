# Epic 21 — Security hardening

> Vision: one compromised pod, one device on the home network or one stolen file no
> longer reaches every user's Google account and location history.

Requirements: PRD NFR-22 to NFR-25, with NFR-01, NFR-05, NFR-07, NFR-08, NFR-16, NFR-17
and FR-51's first increment (`echo/prd.md` §6, §4.1; the Owner's decisions are Q-15).
Architecture: AD-49 to AD-55 (`echo/architecture.md` §5J), which amend AD-12, AD-24,
AD-28 and AD-29.

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

- [ ] `redis.auth.enabled: true` as the chart default, the server reading
  `auth.existingSecret`, the backend's connection string rendered from Vault
  `Redis__Password` (AD-50 rule 1).
- [ ] The Redis NetworkPolicy admits 6379 only from the backend pod; a connection from
  another pod and from another namespace is refused.
- [ ] `bitnami/redis` pinned to a current digest with `pullPolicy: IfNotPresent`.

#### 21.5 The key ring wrapped, the exposed keys retired — Wave 1

- [ ] `ProtectKeysWithCertificate` with `DataProtection__Certificate` from Vault, rendered
  as a file; `UnprotectKeysWithAnyCertificate` ready for a roll-over (AD-50 rule 2).
- [ ] A new key created and activated; a one-shot job re-protects every protected value
  (refresh tokens, opt-ins, Activity Snapshot, own addresses, Other contacts dismissals,
  `source_connections.SecretProtected`, OneDrive tokens), with a count per kind and no
  failures.
- [ ] The old keys revoked and deleted; the ring in Redis holds only wrapped keys
  (checked by reading it).
- [ ] The Owner revokes and re-grants Google, and reconnects OneDrive and Sports Tracker.

#### 21.6 Postgres password rotated, chart defaults removed — Wave 1

- [ ] `contactsapi` has a random password held only in Vault; the backend restarts on it;
  `psql` with the old password fails (AD-49 rule 8 step 1, AD-52 rules 1 and 3).
- [ ] `postgresql.auth.password` removed from `helm/contacts/values.yaml` and from the
  release in `raspberry_deployment`; `auth.existingSecret` used; the `postgres`
  superuser password moved to Vault `secret/data/contacts-pg-admin`.
- [ ] The Postgres NetworkPolicy admits 5432 only from the backend, the route service and
  the backup job.
- [ ] A CI check fails when rendered values set a non-empty password, secret, token or
  key (AD-51 rule 8).

#### 21.7 Invite-only sign-in from Vault configuration — Wave 1

- [ ] The callback requires `email_verified` and checks the email against
  `Echo__AllowedEmails` (Vault) **before anything is stored**; a refused account has its
  grant revoked, nothing stored, and gets `NOT_INVITED` and the "invitation needed" page
  (AD-50 rule 6, AD-29).
- [ ] The first sign-in binds `sub`; later sign-ins match on it.
- [ ] Tests: an allowed account signs in; a refused one leaves no Redis or Postgres
  entry and its grant is revoked.
- [ ] The allowlist (the Owner and family members) is in Vault; no address is in the
  repository, values or a ConfigMap.

#### 21.8 Sessions re-keyed, browser hardened — Wave 1

- [ ] `EchoSession` is cookie authentication with a Redis ticket store (protected
  tickets, keys under the Owner prefix); the callback clears the pre-sign-in session,
  deletes its cookie and calls `SignInAsync`; sign-out deletes the ticket (AD-50 rule 4).
- [ ] Tests: the ticket key changes across the callback; a ticket replayed after sign-out
  is refused; the absolute lifetime of 30 days holds.
- [ ] Cookies `__Host-ContactsApi.Auth` and `__Host-ContactsApi.Session`.
- [ ] The Google access token is stored only protected (`ContactsApi.Session.GoogleAccessToken.v1`)
  (AD-50 rule 3).
- [ ] nginx sends HSTS; the CSP has no `unsafe-eval` and no script `unsafe-inline`;
  `img-src` and `connect-src` list only the hosts used; `X-Forwarded-Proto` is set by
  nginx; the built bundle loads under the policy (AD-50 rule 5).
- [ ] State-changing `/api/**` calls without `X-Echo-Request` or from another site get
  403 `REQUEST_REJECTED`; `/api/mcp` is exempt; no `GET` changes stored state (AD-50
  rule 7).

#### 21.9 Logs without codes, emails or coordinates — Wave 1

- [ ] `RequestLoggingMiddleware` logs the route template, never the query string or the
  raw path; the session id is no longer logged (AD-50 rule 8).
- [ ] A test sends `/api/auth/callback?code=X&state=Y` and finds neither value in any log
  event.
- [ ] Swagger UI only in Development; `/api/assistant/status` requires a session;
  development credentials moved to user-secrets and `.env.example` (AD-50 rule 10).

#### 21.10 MCP tokens expire and start read-only — Wave 1

- [ ] `access_tokens` gains `ExpiresAt` (default 90 days, at most 365) and a `write`
  scope; update and delete tools require it; expired tokens are refused (AD-50 rule 9,
  AD-18).
- [ ] A per-token rate limit; the token list shows the last use and the expiry.
- [ ] Existing tokens get an expiry 90 days from the migration, with a notice.

#### 21.11 Restricted pods and default-deny in `contacts` — Wave 1

- [ ] Every Contacts container has the restricted securityContext, Valhalla included
  (`runAsUser` 1001), and the tiles, migrate and backup containers (AD-51 rule 5).
- [ ] `automountServiceAccountToken: false` everywhere; the frontend has its own
  ServiceAccount; the Vault Agent gets a projected token with audience `vault` (AD-51
  rule 6).
- [ ] Requests and limits on every container; a `LimitRange` in `contacts` (AD-51
  rule 7).
- [ ] The chart ships the default-deny policy and the allows of AD-51 rule 2, with
  `routes.backendIngressPolicy` on; every pod's probes stay green; the backend reaches
  Google over 443 and cannot reach a home-network address.
- [ ] The namespace enforces `restricted` and every pod is admitted.

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

- [ ] `OwnerScope` and `OwnerRlsInterceptor` on every DbContext, in log-only mode; the
  configuration guards (no multiplexing, reset on close, application name, pool size)
  (AD-49 rule 4).
- [ ] Every entry point sets the Owner: the `/api/**` middleware, the `EchoPat` handler,
  `InternalRoutesController`, the route service's `/internal/v1/*`, every hosted worker
  per item in a fresh scope.
- [ ] The `sys_*` functions created by `AtlasSystemFunctions` and `RoutesSystemFunctions`;
  workers refactored to them; `PlacePendingPhotosAsync` runs one transaction per Owner;
  `DeleteGoneOwnersAsync` deletes under each gone Owner's scope (AD-49 rule 5).
- [ ] A few days of live logs with zero "owner scope missing" warnings.

#### 21.15 Database isolation tests — Wave 2

- [ ] A Testcontainers fixture (`postgres:18`) that runs `db-roles.sql`, migrates as the
  migrators and seeds Owners A and B in every table (AD-49 rule 9).
- [ ] The catalog, unset, cross-tenant read and write, pooling, definer-function,
  role-boundary and destructive-path tests of AD-49 rule 9 pass, and run on every PR as
  part of the NFR-07 gate.
- [ ] The two-Owner fixture of AD-3 rule 4 passes over every `/api/**`, `/internal/routes/*`
  and `/internal/v1/*` route with RLS on.

#### 21.16 Roles split, migrations out of the app — Wave 2

- [ ] `helm/contacts/files/db-roles.sql` run once as `postgres`; ownership of `atlas`
  and `routes` moved to the migrators; runtime roles DML only, with their timeouts
  (AD-49 rule 2).
- [ ] `--migrate-only` init containers in the backend and route pods, with
  `secret/data/contacts-migrate` injected into the init container only (AD-49 rule 3).
- [ ] The catalog test passes against the live database; both pods migrate through their
  init container; the `sys_*` functions are re-owned to the system roles.

#### 21.17 Policies on, then throw — Wave 2

- [ ] `AtlasRowLevelSecurity` deployed, then `RoutesRowLevelSecurity` (AD-49 rule 8).
- [ ] `EXPLAIN` of the five heaviest reads compared before and after; no regression past
  NFR-09.
- [ ] Live smoke test: the map, the timeline, the routes panel, an MCP tool, a Sports
  Tracker sync.
- [ ] The interceptor switched to throwing; 24 h without `OwnerScopeMissingException`.

#### 21.18 Data-model follow-up — Wave 2

- [ ] `track_points.OwnerId` backfilled in batches and `NOT NULL`, with the plain policy
  (AD-49 rule 10).
- [ ] Composite foreign keys for `flights`, `activity_fusion`, `activity_efforts`,
  `track_points`, `visits`, `place_reviews` and `review_reminders`.
- [ ] `pgaudit.log = 'ddl, role'`, connection logging and the log prefix on.

### Wave 3 — Platform (`raspberry_deployment`)

#### 21.19 Default-deny networks and Pod Security Admission everywhere — Wave 3

- [ ] Default-deny with explicit allows in `vault`, `cloudflared`, `monitoring`,
  `glutguard`, `headscale`, `logging` and `flux-system` (AD-51 rule 3); cloudflared
  egress on 7844 and 443 only.
- [ ] PSA labels per AD-51 rule 4; node-exporter moved to `monitoring-host`; fluent-bit's
  `/var/log` mount read-only; the subnet router in userspace mode.
- [ ] A probe pod in another namespace cannot reach Contacts' Postgres, Redis or the
  backend.

#### 21.20 Backups automated — Wave 3

- [ ] The `contactsdb` CronJob as `contacts_backup`: `pg_dump -Fc`, `age`, rclone to
  Drive with `drive.file`, 14 daily and 8 weekly (AD-55 rule 2, AD-24).
- [ ] The same for GlutenGuardian's database, Vault and the k3s datastore (with its
  `cred` and `tls` directories); Headscale's copy moved off-node.
- [ ] A restore of each into a scratch target succeeds and is recorded.
- [ ] The `age` identity is in Vault and in the password manager.

#### 21.21 Vault hardened — Wave 3

- [ ] TLS on the listener; `ClusterIP` only; the Vault Agent trusts the CA (AD-52 rule 6).
- [ ] An audit device to stdout, shipped to Seq.
- [ ] `audience` on the Kubernetes roles; the `openclaw` role and policy deleted; the
  backend template renders only the keys it uses.
- [ ] Upgraded to a supported release, its storage moved to Raft (AD-54 rule 3).

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

- [ ] The OAuth app stays in Testing with explicit test users until 21.7 is live, then
  moves to Production, unverified (AD-26).
- [ ] The family members' addresses are on the allowlist in Vault.
