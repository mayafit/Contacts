---
title: Echo PRD — Addendum for the Architect
created: 2026-10-28
updated: 2026-10-28
---

# Echo PRD: Addendum

This addendum covers the technical context and options that sit behind the PRD ([prd.md](prd.md)). It does not set requirements: the PRD's FRs and NFRs govern, and this file only informs `bmad-architecture`. Every fact about the current system comes from the as-built inventories at `dev @ 1e05abc`, [contacts-as-built.md](contacts-as-built.md) and [atlas-as-built.md](atlas-as-built.md), checked against the code.

## A1. Current Shape the Architecture Starts From

- **Process and deployment.** There is one .NET 9 backend (`ContactsApi`) and one React frontend (the WebKit/BlueFiber template, running standalone).
  - They ship as one Helm umbrella, `helm/contacts` 0.4.2: an nginx frontend, the backend, PostgreSQL 18.6 from the Bitnami chart (chart `postgresql` 16.4.1, image pinned by digest; local-path, 5Gi) and Redis (no auth, 1Gi).
  - Flux deploys it from the separate `raspberry_deployment` repository.
  - The public URL is https://contacts.mayaffit.com, through a Cloudflare tunnel. `global.publicUrl` drives the OAuth redirects and CORS.
- **Stores.**
  - **Contacts** has no tables: Google People is the store. Per-owner state is kept in Redis (`refresh-token:`, `optins:`, `activity:`, `self-emails:`, session), encrypted with Data Protection where noted. The key ring is also in Redis.
  - **Atlas** uses the Postgres schema `atlas` (16 migrations), and every row has `OwnerId` = Google `sub`.
- **Assistant.**
  - The agent loop runs in the browser (`assistant/agent.ts`). The backend only proxies Gemini (`/api/assistant/generate`, `/search`).
  - Each Area passes its own `Toolset` to `AssistantPanel`: `AtlasHome.tsx` passes `atlasToolset(...)`, and Contacts passes the contact tools. Today no single conversation sees both.
- **MCP.** `/api/mcp` uses the official C# SDK and accepts `atl_` Bearer tokens (SHA-256 stored), plus Cloudflare Access for remote clients. It has Atlas tools only: no photos and no contacts.
- **Naming.** The repository, chart, images and Vault path are all named "contacts". Renaming them to Echo is optional (PRD FR-03 limits the requirement to the user-facing brand). If they are renamed, note that `global.publicUrl` must stay `contacts.mayaffit.com`. Also, the chart version must be bumped, or Flux ignores the change.

## A2. Phase 0 Remediation Notes (NFR-01 to NFR-06)

- **Secrets (NFR-01).**
  - Committed today:
    - `ContactsApi/src/ContactsApi.WebApi/appsettings.json` holds a Vault dev token, a placeholder DB connection string and the Google OAuth client id. The client id is public by nature, but it should still move to configuration.
    - `helm/contacts/values.yaml` holds a committed default Postgres password in `postgresql.auth.password`, and placeholder values in `backend.secrets.googleClientSecret` and `backend.secrets.dbPassword`. The placeholders are not credentials, but the keys should not carry defaults at all.
  - Options:
    - Remove the values and rotate them.
    - Rely on the Vault Agent Injector, which already renders `secret/data/contacts`. The chart's `vault.enabled` defaults to `false`, with a Kubernetes Secret as fallback.
    - Add `gitleaks` (or a similar scanner) to the PR CI.
  - Rewriting git history is optional once the values are rotated.
- **CI (NFR-02).**
  - The repository has no `.github/workflows`.
  - Options:
    - GitHub Actions with `docker buildx` under QEMU for arm64 (slow, but simple);
    - native arm64 GitHub runners;
    - a self-hosted runner on the cluster.
  - Images go to GHCR, tagged by short sha. The chart version bump can be automated, or Flux image automation can be used in `raspberry_deployment`.
  - Today's deploy is a hand build followed by `kubectl rollout restart`. Tests: xUnit, and Jest with jest-axe.
- **OAuth (NFR-03).**
  - The app is in Google "Testing" mode, so refresh tokens expire after 7 days.
  - Moving to Production while unverified ends that expiry. It shows an "unverified app" screen and has a lifetime cap of 100 users for sensitive or restricted scopes, which is fine for one owner.
  - Contacts and Calendar scopes are *sensitive*, and Gmail scopes are *restricted*. Full verification, with a CASA assessment for Gmail, is only needed if Q-1 (multi-user) becomes yes.
  - The privacy and terms pages (FR-50) are needed either way.
  - `include_granted_scopes` is not used, **on purpose**. Each sign-in asks for exactly the scopes of the tiers the Owner holds, because Gmail refuses content reads when a token also carries `gmail.metadata` (`ScopePreferences.cs:25-28`, `AuthController.cs:74-75`). Ending the 7-day expiry does not need it, and adopting it would break T2.
- **Durable Contacts state (NFR-04).**
  - Move `optins`, `refresh-token`, `activity`, `self-emails` and the dismissals (currently in localStorage) to Postgres. This could be a new schema such as `echo` or `people`, owner-keyed and encrypted with the existing protectors.
  - Redis would then stay a session and cache store.
  - Consider moving the Data Protection key ring out of Redis too. Losing it makes every encrypted value unreadable.
  - Mind NFR-10: the backend needs the key ring at start to read the session and OAuth cookies. If the ring lives only in Postgres, a backend that starts while Postgres is down cannot sign anyone in, and Contacts goes down with the database. Whatever holds the ring must keep a copy that Contacts can read without Postgres (the architecture's AD-12 keeps Postgres as the record and a mirror in Redis).
- **Backups (NFR-05).**
  - Postgres runs on `local-path` storage, which ties it to one node. A node loss strands it.
  - Options: a `pg_dump` CronJob to off-node storage (see Q-8), encrypted, with a restore drill per phase. The backup must include the Data Protection keys, or encrypted columns cannot be restored.
  - Vault stays sealed after a reboot (a known operational fact). That is the one manual step allowed by NFR-10.
- **Stale docs (NFR-06).** Three documents are wrong today:
  - `DEPLOYMENT.md` describes a frontend-only setup.
  - `project-context.md` lists a `PATCH /api/contacts/{rn}/fields` endpoint that does not exist, and axios retry interceptors.
  - `docs/index.md` (2026-01) describes only the template.
- The epics also disagree with the code:
  - Epic 11's tolerance table: Google Fit is 100 m in the epic and 300 m in the code. Samsung Health and "other" are missing from the epic.
  - Epic 11's table names: `activity_clusters` and `cluster_decisions` became `activity_fusion` and `fusion_decisions`, and `activity_visit_links` was never built.
  - Epic 7 claims `include_granted_scopes` is used. It is not, deliberately (see OAuth above). The claim is superseded and must not be "made true".
  - Epic 6.2 names `calendar.readonly`. The code uses `calendar.events.readonly`.
- **Code TODOs worth closing in Phase 0:**
  - the token-encryption TODOs in `AuthController`;
  - `app.UseAuthentication()` is commented out;
  - unused `ITokenRepository`, `User`, `UserToken` and `Core/Services/GooglePeopleService.cs`;
  - OTEL environment variables with no OpenTelemetry package.

## A3. Connect Layer: Mechanism Options (FR-33 to FR-42)

- **Where Person Links and Encounters live.**
  - They need Postgres tables that are owner-keyed and reference a Contact by `resourceName`. They must survive a Contact merge: follow the kept contact, re-point the deleted contacts' links, and record this in the merge flow.
  - Suggested entities:
    - `person_links` (owner, resourceName, kind = email | attendee | face_cluster, value, status = confirmed | proposed | rejected, evidence JSON, decidedAt);
    - `encounters` (owner, resourceName, start/end, placeId?, activityId?, visitId?, calendarEventId?, confidence, origin, status, evidence JSON). **Origin** is one of `derived` (made by the rule) or `owner` (added by the Owner, FR-35). What an Encounter rests on is not its origin but its evidence kinds: `calendar_event`, `visit`, `activity`, `address_match` and, with T4, `photo_face`. A "face-only" Encounter is a derived Encounter whose only evidence kind is `photo_face`. The architecture uses the same lists (§6 there);
    - `encounter_decisions`, to persist confirm and reject across re-derivation, by the same pattern as `fusion_decisions`.
- **Inputs available today.**
  - The Activity Snapshot keeps per-address counts and last and next meeting. It does **not** keep the event list. It holds only the last and next meeting per address, plus monthly counts.
  - Encounters need the calendar events themselves: time, attendees, location and status. This means either a new calendar-events store, or extending the Activity Snapshot.
  - Only the primary calendar is read today (`GoogleActivityClient.cs:257`). Reading all calendars (Planned G6) raises coverage.
- **Join rule (a starting point).**
  - An event that overlaps a Visit or a Fused Activity by at least 50% of the shorter of the two, and that is not declined or cancelled.
  - If the event has a location, geocode it once (cached, within OSM etiquette) and require it to be within some distance of the Visit. Without a location, use time only, at lower confidence.
  - Geocoding sends free text (event locations, and for FR-36 postal addresses) to Nominatim. That is a new data kind for OSM, so it is listed in PRD §5.2 and runs only behind the Owner's address-lookup switch (PRD FR-47), off by default. With the switch off, treat the event as having no location.
  - Reuse the fusion style "time agreement × location agreement" confidence for consistency.
- **Address ↔ Place (FR-36).** Geocode contact postal addresses with Nominatim (1 request per second, cached), only while address lookup is on, and match them to Places within about 100 m. Exclude the Owner's own long-stay Places from automatic assignment.
- **Unified Timeline (FR-40).** This is a read model over `atlas.*`, encounters, calendar events, daily email aggregates and photos. It could be a UNION query with paging by time range, or a materialized per-day digest table rebuilt by the fusion worker when idle. NFR-09 (p95 ≤ 2 s on the Pi) favours the per-day digest.
- **Recompute (NFR-12).** Derivation runs as another idle task of the existing fusion worker, triggered by ingest windows and by snapshot refreshes. Owner decisions are re-applied on each run.

## A4. One Assistant (FR-31, FR-32)

- Simplest route: merge the Toolsets in the browser (contact tools, `atlasToolset` and new Connect tools), with a router so that links open the right Area.
- Concerns:
  - The Contacts tools work over the contacts loaded in Redux. In Atlas, those may not be loaded. Either load them lazily, or add server-side contact tools.
  - The function declarations grow, and so does the token cost per call (NFR-11 caps the increase at 2×).
  - Q-5 (sending combined data to Gemini) may push Connect answers to be computed on the server, with only the summarized result sent to the model.
- MCP for people (FR-32): add read tools (contacts search and get, person links, encounters, timeline day) to `AtlasMcpTools`, and add a per-token scope field to `access_tokens`. MCP writes to Contacts stay excluded.

## A5. People in Photos (FR-43 to FR-45)

- **Inputs.** There are 157,021 Photos and videos as OneDrive metadata. Thumbnails come through Graph and are proxied today. Face detection needs pixels, so fetch a medium thumbnail per photo on demand and do not store the image.
- **Compute.**
  - On-cluster only (the PRD forbids cloud face APIs). Candidates are CPU or ONNX models on arm64: SCRFD or RetinaFace for detection, ArcFace-class embeddings, and HDBSCAN or DBSCAN for clustering.
  - On Raspberry Pi CPUs, plan the first pass in batches over days, with resumable progress (NFR-14), at a lower priority than interactive requests (NFR-09). An optional worker on an off-Pi machine owned by the Owner would still meet "the Owner's infrastructure". Confirm A-4.
- **Storage.** Store embeddings encrypted (NFR-08) in an owner-keyed table, and keep face boxes with the photo id. A "never recognize" list stores only what is needed to suppress re-clustering. Prefer a negative-centroid approach, and decide whether that is itself biometric (likely yes; keep it minimal and inside T4).
  - Use a key that only face data uses. A face worker that can read the main key ring could decrypt every token and snapshot in Echo.
  - Keep the face worker away from other modules' tables: it should get photo ids and thumbnails from the backend, not by reading `atlas.photos`.
- **Revocation.** Delete everything face-derived in one transaction: detections, templates, clusters, `face_cluster` Person Links, Encounters with `photo_face` evidence, and the Owner's decisions about them. Meet the 24 h window by running the deletion synchronously. Keep all of it out of backups (PRD FR-43), which is simplest if it all lives in one schema that the backup excludes.

## A6. Privacy Center Data (FR-47)

Showing "what each third party received recently" needs counters per provider (Gemini calls and tokens, OSM lookups by kind, Graph calls, Sports Tracker calls) kept per owner per day. There are existing hook points:
- `GeminiService` (it already enforces limits);
- `GeoLookup`;
- `OneDrive`;
- `SportsTracker`.

The switch for background geocoding gates the place-identification idle task of `AtlasFusionWorker` (in `AtlasFusionService.cs`). The address-lookup switch gates the geocoding of event locations and postal addresses (A3).

## A7. Export (FR-48)

- **Atlas tables:** JSON per table, plus GPX per Activity track.
- **Connect tables:** JSON.
- **Activity Snapshot:** decrypt and write as JSON.
- **Manifest:** with counts.

Large exports should be streamed as a zip; they may exceed the 20 MB request limits, which apply only to ingest. Exclude secrets and token hashes. Include faces only if they are explicitly requested.

## A8. Scale Facts (for sizing)

- 157,021 Photos (6,890 videos), from 1984 to 2026. 40,724 have GPS, and 74,061 are placed: by GPS 40,724, by track 6,641, by visit 26,696. 82,960 are unplaced.
- The current caps:

  | What | Limit |
  |---|---|
  | Search | 2,000 per kind |
  | Density | 6,000 tracks and 30,000 visits |
  | Map photos | 3,000 |
  | Panel photos | 500 |
  | Activity detail | 2,000 points |
  | Health series | 1,500 points |
  | Ingest | 20 MB, 4,000 items or 25,000 points per batch |

- Contacts paging is ≤ 1,000 per page. The activity scan covers 365 days and 10,000 messages, 3 concurrent scans.
- Gemini limits: per user, 40 generate and 10 search per minute and 600 per day; 3,000 per day globally.

## A9. Rejected or Deferred Alternatives (with reasons)

- **Writing Person Links into Google Contacts (as custom fields):** rejected. It violates P-1 and P-2 (Echo-derived data in the Owner's source of truth), and it syncs to the phone.
- **Inferring Encounters from email:** rejected for Phase 1. An email exchange is not a meeting, and inferring one would lower precision (SM-C1).
- **Cloud face APIs (Google Vision, Azure Face):** rejected. Biometric data would leave the Owner's infrastructure.
- **Google Photos as the photo source:** not feasible for the Owner (Takeout did not work out). OneDrive is the source.

## A10. Implementation Details of Built Behaviour (moved from the PRD)

The PRD states these FRs as observable behaviour. The mechanisms behind them are recorded here, from the as-built inventories.

| PRD item | Mechanism today |
|---|---|
| FR-05 stale write | Google People returns 412 on a stale etag; the API maps it to `CONTACT_CONFLICT` (412) in the `ApiResponse` envelope, and the SPA opens the conflict dialog. |
| FR-12 T1 permissions | Scopes `gmail.metadata` and `calendar.events.readonly`. T2 uses `gmail.readonly`, which replaces `gmail.metadata` on the token (see A2, OAuth). |
| FR-17 no duplicates on re-import | Every source record is upserted by (OwnerId, Source, ExternalId) and carries its ImportId, so deleting an import deletes its rows. |
| FR-24 automatic naming | Exactly one relevant place within 40 m and no other within 80 m; otherwise up to 6 candidates within 100 m are kept as ambiguous. Places with typical stays of 6 h or more are excluded. |
| FR-27 photo clock | `PhotoClock.cs`: GPS photos are tested as UTC and as local time of their zone against Visits and Activities (within 1 km agrees, beyond 5 km contradicts). A local clock needs at least 5 local votes and more than 3× the UTC votes. Photos without GPS take the nearest offset within 3 days, else the most common one. Re-run after a sync that changed photos, else at most daily. |
| FR-27 photo position | Own GPS, else the track interpolated within 10 min, else the Visit's location. `PlacedBy` (gps, track, visit) records the method. |
| FR-28 rate limits | Counted in memory in the backend process (per user per minute 40 generate and 10 search; 600 per user per day; 3,000 per day globally), so a pod restart resets them. The architecture moves them to Redis (AD-17). |
| FR-31 Connect answers | Echo computes the when/where/with-whom joins on the server and passes only the matched rows and citations to Gemini (architecture AD-17). |
| P-3 exception | The MCP tools `atlas_update_activity`, `atlas_update_place`, `atlas_delete_activity`, `atlas_delete_place` and `atlas_delete_visit` change or delete rows in place, with no history. Only `atlas_delete_import` (and re-import) works through provenance. |
