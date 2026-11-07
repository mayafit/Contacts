# Epic 22 — Owner system-status bar, on-demand Grafana, and lighter monitoring

> Status: **Design approved, implementation in progress** (2026-11-07)
> Scope spans two repositories: **Contacts** (app: backend + frontend) and
> **raspberry_deployment** (cluster: monitoring stack, on-demand Grafana,
> reconcile agent).
> Owner-authorized on 2026-11-07. Follows Epic 21 (security hardening) and the
> 2026-11-07 penetration test; see `docs/planning/echo/` and the pentest findings.

## 1. Why

The home cluster is two Raspberry Pis (rasp01 control-plane + Vault, rasp02
Contacts Postgres/Redis — RAM- and disk-fragile). An **always-on Grafana**
(200–500 MiB) plus **15-second Prometheus scraping** is real, continuous
baseline load on exactly the node that is most likely to fall over. The owner
rarely needs Grafana — only "in case of a real need" — yet pays for it every
second.

At the same time there is no at-a-glance signal of whether the system is
healthy. The owner wants a quick traffic-light view inside the app they already
have open, and a way to summon Grafana only when they actually need to dig in.

This epic replaces continuous heavyweight observability with:

1. **Lightweight status lamps** (green/yellow/red) in the Contacts top bar.
2. **On-demand Grafana** — scaled to zero by default, woken on request, put
   back to sleep when idle.
3. **Grafana behind Cloudflare Access** (like the rest of the app) instead of
   LAN/VPN only.
4. **Slower monitoring collection** (15s → 60s) to cut steady-state load.

It also advances the DoS/resource-exhaustion goals from the pentest remediation:
less always-on consumption on the fragile node is directly a resilience win.

## 2. Non-negotiable constraint — do not widen the app's blast radius

Epic 21 and the pentest put the Contacts backend behind default-deny
NetworkPolicies, a restricted PSA, and least-privilege. **Giving the app
Kubernetes API credentials (to read health or to scale Grafana) would undo part
of that.** A compromised app pod could then manipulate cluster workloads.

**Decision (owner-approved, "CronJob-mediated" option):** the app gets **zero**
new Kubernetes access. All cluster privilege lives in a small cluster-side
component. The app and that component communicate only through **Redis**, which
the app already uses.

## 3. Architecture

### 3.1 Components

| Component | Repo | Runs in | New privilege |
|---|---|---|---|
| `status-agent` (reconcile loop) | raspberry_deployment | `monitoring` ns | read-only nodes/pods cluster-wide; scale **only** the `grafana` Deployment in `monitoring`; reach `contacts-redis` |
| Contacts backend `/api/system/*` | Contacts | `contacts` ns | **none** (reads/writes Redis it already owns) |
| Contacts frontend status bar | Contacts | browser | none |
| Grafana (scaled to zero) | raspberry_deployment | `monitoring` ns | unchanged |

### 3.2 The reconcile agent (`status-agent`)

A tiny always-on Deployment (1 replica, ~16–32 MiB, restricted securityContext)
in the `monitoring` namespace. It loops every ~10 seconds and does two jobs:

1. **Health collection.** Queries the Kubernetes API (not Prometheus — so the
   signal survives Prometheus itself being down) for:
   - Node `Ready` condition + `MemoryPressure`/`DiskPressure` on each node.
   - Readiness of the critical pods, grouped into lamps (see §3.4).
   It writes a compact JSON health snapshot to Redis key `ops:health` with a
   short TTL (e.g. 60 s) so a dead agent shows as stale, not falsely green.

2. **Grafana reconcile.** Reads Redis key `ops:grafana:wake_until` (a unix
   timestamp). If `now < wake_until`, it ensures the `grafana` Deployment is
   scaled to 1; otherwise it scales it to 0. It also records Grafana's current
   replica/ready state into `ops:health` so the frontend knows when the button's
   target is actually up.

**Why a Deployment, not a literal CronJob:** a CronJob cannot fire more often
than every 60 s, which would make the "Open Grafana" button feel dead for up to
a minute. A ~16–32 MiB loop pod costs a rounding error next to the 200–500 MiB
of always-on Grafana it lets us remove, and makes wake latency ~10 s. The
security property is identical (all k8s privilege is cluster-side). Named
trade-off: one small always-on pod vs. sub-minute responsiveness.

### 3.3 Redis contract (the only coupling between app and cluster)

All keys are namespaced under `ops:` in the Contacts Redis (same instance the
app uses; a distinct key prefix, no new DB).

| Key | Writer | Reader | Shape |
|---|---|---|---|
| `ops:health` | status-agent | backend | JSON snapshot (see §3.4), TTL ~60 s |
| `ops:grafana:wake_until` | backend | status-agent | unix seconds (string); absent/past ⇒ asleep |

The app **never** scales anything and **never** calls the Kubernetes API. It
writes a timestamp to request wake, and reads a JSON blob to render lamps. If
Redis is unreachable the backend returns an explicit `unknown` state (grey
lamp) rather than guessing.

### 3.4 Health snapshot shape (`ops:health`)

```jsonc
{
  "generatedAt": "2026-11-07T20:15:04Z",   // for staleness detection
  "overall": "green",                        // worst of the lamps below
  "lamps": [
    { "id": "nodes",  "label": "Nodes",       "state": "green",  "detail": "rasp01, rasp02 Ready" },
    { "id": "data",   "label": "Data",        "state": "green",  "detail": "Postgres, Redis Ready" },
    { "id": "app",    "label": "App",         "state": "green",  "detail": "backend 1/1" },
    { "id": "vault",  "label": "Vault",       "state": "green",  "detail": "unsealed" },
    { "id": "monitor","label": "Monitoring",  "state": "yellow", "detail": "Grafana asleep (on demand)" }
  ],
  "grafana": { "awake": false, "ready": false, "wakeUntil": null }
}
```

Lamp state rules:
- **green** — all tracked objects Ready / node conditions healthy.
- **yellow** — degraded but serving (e.g. one of N replicas down, a node under
  memory pressure, Grafana intentionally asleep).
- **red** — a critical object is down (node NotReady, Postgres/Redis/backend not
  Ready, Vault sealed).
- **grey/unknown** — snapshot missing or stale (agent down, Redis unreachable).

"Grafana asleep" is reported as **yellow with an explanatory detail**, not red —
it is the intended steady state, and the UI copy makes that clear so it does not
read as a fault.

### 3.5 On-demand Grafana lifecycle

1. Grafana Deployment default **`replicas: 0`** (helm value) — no baseline cost.
2. Owner clicks **Open Grafana** → frontend calls `POST /api/system/grafana/wake`.
3. Backend (admin-gated) sets `ops:grafana:wake_until = now + 30 min` in Redis.
4. status-agent (next ~10 s tick) scales grafana to 1; reports readiness into
   `ops:health`.
5. Frontend polls `/api/system/status`; when `grafana.ready` is true it opens
   `https://grafana.mayaffit.com` (behind Cloudflare Access) in a new tab.
6. Each additional "Open Grafana" (or an explicit "keep awake") pushes
   `wake_until` forward. When it lapses, the agent scales grafana back to 0.

**Cold-start fix:** Grafana currently has persistence **off**, so every boot
rebuilds its SQLite schema from scratch — the exact migration storm that caused
the restart loop on 2026-11-07. This epic adds a small persistent volume
(local-path, ReadWriteOnce) so a scaled-up Grafana reuses its migrated DB and
starts in seconds. Named trade-off: a local-path PV pins Grafana to one node and
does not survive that node's loss — acceptable for a non-critical, on-demand
dashboard. Grafana is pinned to **rasp01** (control-plane; keeps it off the
data-fragile rasp02).

### 3.6 Owner-only gating

There is no admin/owner distinction in the app today — every allowed email is a
peer family member who owns their own data. This epic adds a **fail-closed**
`Echo:AdminEmails` configuration key (in the cluster, Vault `Echo__AdminEmails`;
comma/space separated; empty ⇒ nobody is admin ⇒ the whole feature is hidden).
No address is stored in the repository, mirroring `Echo:AllowedEmails`.

- `GET /api/auth/status` gains `isAdmin: bool` (true iff the signed-in verified
  email is in `AdminEmails`).
- `GET /api/system/status`, `POST /api/system/grafana/wake` require admin;
  non-admins get 403, and the frontend never renders the bar for them.

Family members (Yael et al.) see the normal app with no ops controls.

### 3.7 Lighter monitoring

In `apps/monitoring/release.yaml`:
- Prometheus `scrapeInterval` and `evaluationInterval`: default (15 s) → **60 s**
  (global). ~4× fewer samples ⇒ less CPU, less WAL, less disk churn on rasp02.
- Keep the existing `retention: 2d` / `retentionSize: 2GB` guards (they bind disk).
- Grafana `replicas: 0` + persistence + rasp01 pin as above.

Named trade-off: 60 s resolution is coarser for incident forensics. Acceptable
for a home system; the owner can widen `wake_until` and still get 60 s dashboards
when investigating, and can lower the interval temporarily if ever needed.

## 4. Security review checklist (self-review before each PR)

- [ ] App gains no Kubernetes RBAC, no new secrets beyond the admin-email config.
- [ ] status-agent SA is least-privilege: read-only nodes/pods (ClusterRole),
      scale **only** `grafana` (Role + `resourceNames: [grafana]`), nothing else.
- [ ] NetworkPolicy: status-agent may egress to the kube API and to
      `contacts-redis:6379` only; default-deny otherwise.
- [ ] Redis credentials for the agent come from Vault / an existing secret,
      never Git.
- [ ] Admin-email list is fail-closed and absent from Git.
- [ ] `/api/system/*` endpoints are `[Authorize]` + admin-checked; no owner data
      is exposed through them.
- [ ] Grafana remains ClusterIP (no LAN NodePort); public reach only via the
      Cloudflare tunnel + Access.

## 5. Owner (Cloudflare dashboard) tasks — cannot be done from Git

1. Tunnel → **Public Hostname**: `grafana.mayaffit.com` →
   `http://kube-prometheus-stack-grafana.monitoring.svc.cluster.local:80`.
2. Zero Trust → **Access → Applications**: add `grafana.mayaffit.com`, policy =
   the owner's email (same pattern as the Contacts app).
3. Vault: set `Echo__AdminEmails` (owner's sign-in email) under the Contacts
   secret path, and `grafana`/redis creds for the agent as noted in the infra PR.

## 6. Delivery plan (PRs)

1. **Docs PR (this file)** — Contacts repo, `docs/epic-22-ops-status`.
2. **Infra PR** — raspberry_deployment: monitoring interval 60 s; Grafana
   `replicas: 0` + PV + rasp01 pin; `status-agent` Deployment + SA/RBAC +
   NetworkPolicy + image/script; Redis-access wiring.
3. **App PR** — Contacts: `AdminEmails` + `isAdmin`; `/api/system/status` and
   `/api/system/grafana/wake`; `SystemStatusBar` React component in the Atlas
   top bar (and shared shell), owner-only, polling + wake flow.

Each PR is built in its own worktree on a feature branch, self-reviewed with the
code-review skill, and merged only via `gh` PR.

## 7. Open follow-ups (tracked here, not in GitHub issues)

- Optional "keep awake" / "sleep now" controls on the bar.
- Optional: surface a couple of headline numbers (node mem %, disk %) in the
  expanded lamp popover, sourced from the same k8s API the agent already reads.
- Revisit whether the status-agent should also publish to the GlutenGuardian
  namespace lamps once that app is back in rotation.
