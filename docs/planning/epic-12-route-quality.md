# Epic 12 — Atlas route quality: trips drawn on the roads actually travelled

> Vision: a drive in Atlas looks like the drive it was. Echo notices by itself
> when a route was recorded too sparsely to draw, places it on the real roads,
> says how sure it is and which parts it inferred, and lets the Owner keep the
> original whenever they prefer it. All of this happens on the Owner's own
> cluster, and their location history never leaves it.

Builds on Epic 10 (`epic-10-personal-atlas.md`: storage, import, map) and
Epic 11 (`epic-11-fusion.md`: fusion, the fusion service, the drill-down).
Requirements: PRD FR-55 to FR-60, NFR-20 and NFR-21
(`echo/prd.md` §4.12). Architecture: AD-32 to AD-38 (`echo/architecture.md` §5A).

## The problem

On the Atlas map, car trips appear as straight grey lines across fields and
between roads. Google Timeline stores a drive as a few sparse points, and the map
joins them with straight lines. In production (2026-10-28):

| Source and type | Count | Points per Activity | Points per km |
| --- | --- | --- | --- |
| google-timeline drive | 15,420 | 13 | 0.94 |
| google-timeline walk | 7,311 | 6 | 4.4 |
| google-timeline ride | 1,043 | 23 | 2.0 |
| google-timeline transit | 473 | 10 | 0.62 |
| sports-tracker ride | 387 | | 91 |
| samsung-health (all) | | 0 | |

Google Timeline points are 60 s and 439 m apart at the median, and 2.8 km apart at
the 90th percentile. There are 65,797 gaps longer than 1 km.

## Words used here

- **Map matching:** working out which roads or paths a recorded track actually
  followed. For each GPS point, it finds the nearby roads. It then picks the most
  likely connected way through them, and fills the stretch between far-apart points
  with the most plausible route.
- **Routing graph (tiles):** the road and path network of a region, prepared from
  OpenStreetMap data so that a routing engine can search it quickly. Valhalla
  stores it as tiles, small files that each cover one square of the map. It holds
  no data about the Owner.
- **Routing engine:** open-source software that finds routes over a routing graph
  and can also do map matching. The three candidates were:
  - **Valhalla:** C++, MIT licence, community-maintained since 2018 (started by
    Mapzen). Its matcher is Meili.
  - **OSRM:** C++, BSD-2-Clause, maintained by the Project-OSRM community. Its
    matcher is `/match`.
  - **GraphHopper:** Java, Apache-2.0, maintained by GraphHopper GmbH. It has a
    map-matching module.

  All three match with a Hidden Markov Model. Atlas uses **Valhalla**, self-hosted
  (AD-32 explains the choice).
- **Route Quality:** a score from 0 to 100, a grade (good, fair, poor) and flags,
  computed from the recorded points.
- **Matched Route:** the derived line on the roads. Each piece of it is
  **observed** (recorded points lie along it), **inferred** (the road was chosen
  between sparse points) or a **gap** (nothing matched; the original leg stays).

## Principles

1. **Source tracks are never changed.** A Matched Route is a derived layer in its
   own schema. The recorded points, the analysis, the splits and photo placement
   all stay on the original (P-1, AD-34).
2. **Confidence before cosmetics.** A Matched Route is drawn only when its
   confidence is above the Owner's threshold. Otherwise it is only offered
   (AD-35).
3. **Show what was inferred.** Inferred road pieces are drawn dashed and fainter,
   and gaps stay as recorded.
4. **The Owner has the last word.** "Keep the original" survives re-matching, new
   map data, re-fusion and re-import (P-5). It is keyed by the record's natural
   key, as fusion decisions are.
5. **Nothing leaves home.** The route-quality pod can reach only the backend,
   Postgres and cluster DNS, and the routing engine's config makes no outbound
   calls. The map data is built in CI from a public extract (NFR-20).
6. **A separate service, a light touch on the Pi.** It runs in bounded batches,
   one engine request at a time, in the same round-robin across Owners as all
   other background work (AD-30, AD-33, AD-36).
7. **Every Owner's routes are their own.** Every row, job and cursor carries
   `OwnerId`, and the Owner comes only from the session or token (AD-28, AD-3).
   Only the routing graph, which holds no user data, is shared.

## Data model

| Table (schema `routes`) | What | Notes |
| --- | --- | --- |
| `route_quality` | Assessment of every source record with or without a track | Metrics, score, grade, flags, needs improvement, matchable reason (including `unknown` for transit with no source type), rule version, input hash, the record's `UpdatedAt` and point count as assessed |
| `matched_routes` | The Matched Route of a source record | Polyline (precision 5), pieces `{fromM, toM, kind}` in metres along the line (observed, inferred, gap), the roads it follows (`Roads`, `RoadNames`, story 12.16), confidence, road distance, engine, graph and rule version, input hash, and the record's `UpdatedAt`, point count and fused type at match time (the read-time check) |
| `route_decisions` | The Owner's `reject` and `accept` | Keyed by `source|externalId`, never by a derived id |
| `route_waypoints` | The points the Owner marked for a record (story 12.18) | Keyed like decisions; a decision, so backed up and exported |
| `settings` | Per Owner | Threshold (0.7), matched types, shown by default |
| `jobs`, `owner_cursors` | The service's per-Owner queue and its per-Owner pull cursors | Durable across restarts |
| `atlas.activities.SourceType` (new column) | The source's own type text, for example `in_bus` or `in_train` | Written only by the backend's ingest; tells a bus from a train |

Every `routes` row has `OwnerId` first in its indexes (AD-28). The whole schema
is in the nightly encrypted backup to Google Drive: decisions and settings
because they cannot be derived, and the matched routes because rebuilding them
would cost hours of Pi time after a restore (AD-34 rule 9).

## The service

- **Route-quality service** (`contacts-routes`): a .NET worker with Valhalla
  beside it in the same pod. It is switched on with `routes.enabled`.
- **Work arrives by pull.** The service reads changed records from the backend's
  `/internal/routes/changes`, once fusion has settled, using a per-Owner cursor.
  The fusion service sends a wake after each run. On-demand runs come through
  `/api/routes/*` (UI, and MCP from R2), which call the service's
  `/internal/v1/assess` and `/improve`.
- **Map data:** a monthly CI build of the Israel extract into a tiles image, which
  Flux rolls out. Regions abroad are added to the same build (Phase R3), on
  demand.

## Phases

| Phase | Content |
| --- | --- |
| R1 (MVP) | Engine and tiles in CI; the service and the backend's internal API; assessment of every type; matching of drive and bus or taxi transit in Israel; backfill and after-import runs; the panel toggle, revert and "Fix this route"; the map's dashed inferred pieces (stories 12.1 to 12.8, with 12.2b); roads travelled and the road filter (story 12.16); rebuild from similar drives and mark the way (stories 12.17, 12.18) |
| R2 | Foot and bike profiles (ride, walk, run, hike); route quality in Insights with the batch action; the settings dialog; photos on the matched line; the MCP and Assistant tools; "Use this route" for low-confidence matches; re-matching on a new graph version (stories 12.9, 12.10, 12.12 to 12.15) |
| R3 | Regions abroad: a coverage report, and added extracts (story 12.11) |

R1 ships as one release. It needs the `EchoService` scheme and CI image builds of
Phase 0, and builds them itself if Phase 0 has not (decided, architecture §12
item 11).

## Stories

### 12.1 Routing engine and monthly tiles build (spike first) — R1

As the Owner, I want a self-hosted routing engine with Israel's map data, so that
routes can be matched without sending my location history anywhere.

Acceptance criteria (status after the spike, 2026-10-29: [x] done, [ ] open):
- [x] `docker manifest inspect` shows that the pinned Valhalla 3.9.x digest lists
  `linux/arm64`. The digest is recorded in the chart and in architecture §7
  (`3.9.1@sha256:4fd114eb…`).
- [ ] `routing-tiles.yml` runs monthly and on manual dispatch. Geofabrik updates
  extracts daily, so each run downloads the latest `israel-and-palestine` with an
  MD5 check, and builds the tiles and admins with the **same** pinned image. It
  publishes `contacts-routing-tiles:il-<yyyymmdd>` (AD-32 rule 3: a short slug,
  ≤ 128 characters, no `+` or `@`), built `FROM busybox`, holding
  `valhalla_tiles.tar`, the region `.poly` outlines and `graph.json` (with the
  extract timestamp and the tar size). *The spike's image,
  `il-20261029-r2@sha256:791011ad…`, was built by hand and also holds the tuned
  `valhalla.json`; the workflow must ship that config too.*
- [x] The engine config sets `service_limits.trace.max_distance` (200 km in the
  spike's config; the service's per-call split now follows it) and `max_shape`
  to 2,000, and has no tile URL (AD-32 rules 2 and 7).
- [ ] The tiles image is rolled out by Flux image automation, or by a tag-bump PR
  while that is missing; the PR also updates the `emptyDir` size (AD-32 rule 4).
  No Pi node downloads map data.
- [x] **Spike on the Pi,** with 500 real Timeline drives (round 2): 82 tiles, a
  199 MB tar; one server thread with a 32 MiB LRU cache plateaus at about
  366 MiB anonymous RSS (about 477 MiB charged, peak 582 MiB); median 0.134 s,
  p95 0.73 s, max 5.0 s per drive; about 1.0 h for 15,420 drives. The results
  replace the estimates in AD-32 and AD-36 (AA-15), and tiles built on x64 load
  on arm64 (AA-16). The CI build's time and peak RAM were not measured.
  Write-up: the spike's round-2 `RESULTS.md`, summarised in AD-32 and AD-36.
- [x] The spike measured each node's free memory under its normal load (rasp02
  about 1.9 GiB, rasp01 about 1.1 GiB and overcommitted), and
  `routes.nodeSelector` is `kubernetes.io/hostname: rasp02` (AD-36 rule 2).
- [x] The median (0.134 s) is far below 3 s per drive, so the AD-36 rule 6
  fallback is not needed.

### 12.2 Route-quality service skeleton and the `routes` schema — R1

As the Owner, I want route quality to run as its own service, so that it cannot
slow down or break the rest of Echo.

Acceptance criteria:
- `EchoRoutes.Worker` and the `helm/contacts/charts/routes` subchart exist. The
  subchart has the service container, the Valhalla container (listening on
  `127.0.0.1` only), and the initContainer that runs the busybox-based tiles
  image and copies the tiles into an `emptyDir` sized for its regions. It uses the AD-36 requests and limits and the low `PriorityClass`.
  `routes.enabled` defaults to `false`, and the chart version is bumped.
- A NetworkPolicy on the `contacts-routes` pod (a policy applies to the whole
  pod, not to one container) allows egress only to the backend Service, Postgres
  and cluster DNS (`kube-dns`, port 53). The engine's config has no tile URL and
  makes no outbound calls. A test match with all other egress blocked passes
  (NFR-20).
- A NetworkPolicy on the backend pod admits the `contacts-routes` pod and no
  other new peer; nginx does not proxy `/internal/**`, and
  `Routes__ServiceToken` is accepted only on `/internal/routes/*` (AD-32 rule 7,
  AD-33 rule 2). A test shows that a call with that token to any other path is
  refused.
- The `routes` schema has its own DbContext and migration history. The DB role
  `echo_routes` can use only `routes`. The backup still includes `routes`.
- The `EchoService` scheme accepts `Routes__ServiceToken` on `/internal/routes/*`
  and nowhere else. The service accepts only `Routes__BackendToken`. Both tokens
  are in Vault. The AD-3 endpoint test covers the new routes.
- `/health/ready` turns ready only once Valhalla answers and `graph.json` has
  been read.
- The two-Owner isolation fixture (AD-3 rule 4, the NFR-07 hard gate) proves
  that a request with one `ownerId` cannot read or queue another Owner's
  records, in the backend and in the service. Every `routes` row, job and cursor
  carries `OwnerId`, and the `OwnerId` coverage test passes (AD-28).
- If Phase 0 has not delivered the `EchoService` scheme yet, this story adds it,
  to the AD-3 design (architecture §12 item 11).

### 12.2b The backend's internal API for the service — R1

As the Owner, I want the route-quality service to read my data only through a
narrow, Owner-checked API, so that it never touches the Atlas tables directly.
This story comes before 12.3 and 12.4, which need it.

Acceptance criteria:
- The backend serves `/internal/routes/owners`, `changes`, `tracks` and
  `exists` as specified in AD-33 rule 5. `changes` answers
  `409 DERIVATION_NOT_READY` while the Owner's `fusion_queue` is not empty. Atlas
  adds the `(OwnerId, UpdatedAt)` indexes.
- Each `changes` item carries the record's id, `UpdatedAt`, `PointCount` and
  fused type, so the service can skip records that only look changed after a
  re-fusion.
- Both `ClusterId` resets done with `ExecuteUpdate` (the fusion run's reset of
  its window, and import delete's reset of records whose primary was deleted)
  also set `UpdatedAt`; the fusion run sets it only on records whose `ClusterId`
  differs after the run (AD-33 rule 5).
- Fusion records `FieldSources.distance = "track"` when the distance is the
  record's own track length (AD-34 rule 5).
- `GET /internal/routes/owners` returns only Owners who are `active` on the
  allowlist.
- While an Owner's map or timeline read is in flight, the four endpoints answer
  `429` with `Retry-After` to batch calls. A call with
  `X-Echo-Priority: interactive` is never refused, and route job status polling
  never counts as load (AD-33 rule 5).
- Every endpoint checks that the requested records belong to the `ownerId`; the
  two-Owner isolation fixture covers them.

### 12.3 Assess every route (stage A) and keep the source's own type — R1

As the Owner, I want every Activity's route quality measured automatically, so
that I can see which trips are drawn badly and why.

Acceptance criteria:
- The `routes.v1` stage A metrics of AD-35 rule 1 are computed for every source
  record. This includes `no_track` for records with fewer than 2 points (for
  example Samsung Health) and `type_never` for flight, ski, swim and other.
- The score, the grade and "needs improvement" follow AD-35 rules 2 and 3. Unit
  tests pin the two worked examples: a Timeline drive at 0.94 points per km is
  **fair** and needs improvement; a Sports Tracker ride at 91 points per km is
  **good** and is not matched.
- Records outside the region outlines are marked `outside_coverage`.
- The backend's Atlas ingest stores the raw type it already receives (the value
  it passes to `NormalizeType`) in `activities.SourceType`; no importer changes.
  Re-importing an existing Timeline fills the column without creating
  duplicates.
- Transit with no `SourceType` is marked `Matchable = unknown` and is not
  matched in R1.
- The assessment of about 25,000 records finishes within 15 minutes on the Pi,
  in batches of 500 with 400 ms pauses.

### 12.4 Match drives and road transit (stage B) — R1

As the Owner, I want my drives and bus trips placed on the roads I most likely
took, so that the map shows real routes instead of straight lines.

Acceptance criteria:
- Records that need improvement, of type drive or road transit, are sent to
  `trace_attributes`, with the AD-35 costing (`auto`, `bus`, or `auto` for a
  taxi), `search_radius`, the per-source `gps_accuracy` (Timeline 75 m, dense
  GPS 15 m), `breakage_distance` of 10 km and timestamps. Gaps longer than 10 km
  or 15 min are cut first and become `gap` pieces. A trace longer than 200 km is
  split into calls of at most 200 km and 2,000 points (AD-32 rule 2).
- Valhalla's polyline6 shape is converted to precision 5 before it is stored.
- Pieces follow AD-35 rule 7: `matched` and `interpolated` points are observed
  anchors, placed on the shape with `edge_index` and `distance_along_edge`; legs
  between anchors are observed or inferred by the straight-jump length; a leg
  touching an `unmatched` point is a `gap`; breaks come from `edge_index` and
  `begin_route_discontinuity` / `end_route_discontinuity`. Pieces are stored as
  `{fromM, toM, kind}` in metres along the line. Unit tests cover each label case
  with recorded Valhalla responses.
- The confidence follows AD-35 rule 6: unmatched points lower only
  `matchedShare`, and continuity counts only the engine's breaks, not gaps cut
  before the call. The confidence, the status (`matched`, `low_confidence` or
  `failed`), the road distance, the engine, graph and rule versions, the input
  hash, and the record's `UpdatedAt`, point count and fused type are stored in
  `matched_routes`.
- Transit is sent to the engine only with a bus or taxi `SourceType`. Rail is
  never sent, and transit with no `SourceType` stays `unknown` (AD-35 rule 5).
- A fixture of at least 30 Israeli drives is kept, each with a reference line
  (the road actually driven, drawn once from OpenStreetMap ways). An automated
  test passes when, for at least 90% of the drives the matcher marks `matched`,
  at least 90% of the matched line's length lies within 30 m of the reference
  line.
- Engine timeouts and errors back off per record (1 min, 10 min, 1 h). After 3
  attempts the record is `failed` with its reason.

### 12.4a Gap filling and confidence v2 — R1

As the Owner, I want a drive that is almost on roads drawn on roads, and judged
by whether the drawn line is believable, not by how sparse the recording was.

Acceptance criteria:
- Every unbridged stretch between two anchors (pre-split cut gap, engine break,
  leg skipping an unmatched point across a break) gets one Valhalla `/route`
  call, longest first and at most `GapFillMaxCallsPerDrive` (20) per anchored
  drive, with the matched shape's heading at each anchor
  (`GapFillHeadingTolerance` 45°); the route is kept when it fits the elapsed
  time (at least elapsed × 0.1) and the straight line (slack min(500 m,
  max(100 m, 2 × straight))) and does not back-track near either end (AD-35 rule
  7), and is stored as `inferred`; otherwise the straight `gap` stays. A
  motorway loop ramp is not a back-track; a headed route rejected for its length
  or a back-track is retried once without headings, within the cap
  (`routes.v3`). A leg
  skipping an unmatched point on a continuous matched shape keeps that shape
  (no call). A drive with no matched point is routed point to point at a lower
  weight. Every number is `Routes__GapFill*` configuration, and
  `Routes__GapFill=false` turns it off.
- The confidence is `min(coverage, anchorQuality, plausibility, continuity)`
  (AD-35 rule 6, `routes.v2`); a bridged break is not penalised; Timeline points
  are off-road only beyond 100 m; accepted routes lift `anchorQuality` to
  at least 0.6 × their share of the stretches (one anchor never scores below
  none); the length ratio leaves out filled stretches. `MetricsAfter` stores
  the factors and the fills (`fillSkipped`, `fillDeferred` included).
- A synthetic replica of the live Route 4 drive (17 points, one 5.1 km pre-split
  gap, one engine break) scores at least 0.8 when filled; with a jump no road
  explains, or a route too slow for the elapsed time, it stays below 0.7.
- A `/route` call has its own timeout (`GapFillTimeoutSeconds`, 5 s). A timeout
  leaves the gap and does not fail the record; the record is re-matched after
  the first backoff step at most `GapFillRetries` (3) times. Only
  `GapFillTimeoutsInARow` (3) timeouts in a row count for the breaker.
  Interactive work stops a record's filling between calls; it is re-matched
  soon.
- Bumping `RuleVersion` re-matches older rows in the normal queue,
  `low_confidence` and `failed` first; the old line is served until replaced,
  and no failure (a spent backoff or a bug) ever clears a stored line for the
  same input;
  `reject` decisions win; status shows `rematchRemaining`.
- When "Fix this route" still lands below the threshold, the panel shows the
  confidence and the main reason.
- Fix (`routes.v3`, after the `routes.v2` rollout): two live Timeline drives
  were rejected on obviously correct roads. A 5.5 km Route 4 jump was routed
  through a loop ramp (5,984 m, 242 s, inside every limit) and rejected as a
  U-turn, because the check was a bearing change of more than 150° near an end;
  it now needs a true back-track (back the opposite way within 50 m of the way
  out), counted as `fillRejectedReversal`, and the drive scores 0.882 instead
  of 0.686. On a 42 km drive the backend returned points with the same
  timestamp (Timeline segment boundaries) in a different order than recorded,
  so two engine breaks were routed backwards (12 km and 5.7 km for 850 m and
  920 m) and rejected; the feed now orders by time, then id, and the drive
  scores 0.957 instead of 0.7 with both breaks bridged.

### 12.5 Runs after import, the backfill, and round-robin scheduling — R1

As the Owner, I want new trips improved automatically after every import, and my
history improved once, without the Pi slowing down.

Acceptance criteria:
- It uses the backend's internal API of story 12.2b.
- Tests show that every update path for a track or a fused type moves `UpdatedAt`
  (AA-17), including two change-feed cases:
  - a re-fusion (and a fusion settings save, which queues a full re-fusion)
    returns the whole window from `changes`, and the service skips every record
    whose `UpdatedAt`, point count and fused type equal its stored values, so no
    engine call is made and no match is hidden;
  - a record whose cluster dissolves gets a new `UpdatedAt`, appears in
    `changes`, and is assessed and, if it needs it, matched.
- The service polls `changes` every 2 minutes with a durable per-Owner cursor.
  The fusion service sends `POST /internal/v1/wake` after draining an Owner. Work
  queued before a restart is still done after it.
- The first run backfills the whole history. For R1, about 15,900 drives and
  transit trips are done within 24 h, with interactive p95 within NFR-09 while it
  runs.
- Scheduling follows AD-36: one engine request at a time, 25 records per Owner
  turn, 1 s pauses, and pauses on a 429 from the backend. Interactive jobs go
  first, and the scheduler checks for them **between records**, not only
  between batches; their backend calls carry `X-Echo-Priority: interactive`. Fairness is the AD-30 round-robin, not a scheme of its own: every
  Owner's backfill, after-import runs and re-matches share it, and the AD-30
  fairness test passes with the service included (with one Owner's backfill
  queued, another Owner's new import is assessed within 5 min).
- An Owner who drops out of `GET /internal/routes/owners` (removed or erased)
  has their cursor deleted.
- A new graph version is used for new matches only; existing rows keep their
  graph version (re-matching them is story 12.15, R2).
- The weekly orphan sweep removes the derived rows of deleted records.

### 12.6 Draw the chosen geometry on the map — R1

As the Owner, I want the map to show confident matched routes, with inferred
stretches visibly marked, so that I see real routes without being misled.

Acceptance criteria:
- Atlas reads geometry only through `IRouteGeometryReader` (declared in `Core`),
  and chooses as in AD-34 rule 3. `activity_fusion.Polyline` is unchanged.
- `ActivitySummary` (C# and `types.ts`) gains `geometry: 'matched' | 'recorded'`
  and an optional `pieces: [{fromM, toM, kind}]`.
- An outing of several recordings joins each segment's chosen line in time order.
- Observed pieces are solid in the type's colour, inferred pieces are dashed at
  lower opacity, and gaps are thin grey. The legend explains the dashes. The
  density view uses the chosen line.
- **The selected Activity.** The detail endpoint (`GET /api/atlas/activities/{id}`)
  returns the chosen geometry (the matched line and its pieces) **and** the
  source points. `AtlasMap` draws the selected Activity from the chosen pieces,
  not from the source points as it does today, and shows the source points as
  small markers. The chart's hover marker stays on the source points, which
  carry the heart rate and the time. A test opens a matched drive and checks
  that it is not drawn as straight lines.
- A match whose stored `UpdatedAt`, point count or fused type differs from the
  record's is never drawn; the check reads no points.
- When the fused distance is the source's, it is unchanged and the panel shows
  "on roads: X km" beside it. When it came from the straight-line track length
  (`FieldSources.distance = "track"`), the matched length replaces it in the
  panel, lists and Insights, and reverting restores it (AD-34 rule 5).
- Analysis, splits, HR zones, best efforts and photo placement still use the
  source points.
- Map and timeline p95 stay within NFR-09 with the Owner's real data.

### 12.7 Activity panel: quality badge, toggle, revert and "Fix this route" — R1

As the Owner, I want to see a route's quality, compare the original with the
matched route, and keep whichever I trust, with my choice remembered.

Acceptance criteria:
- The panel shows:
  - the quality badge (the grade, with the flags on hover);
  - for a drawn match, a "Route improved" chip with an *Original* or *On roads*
    toggle, the confidence, and the observed and inferred shares.
- "Keep the original" writes a `reject` decision keyed by `source|externalId`.
  The original is drawn again everywhere. The decision survives a re-match, a new
  graph version, a re-fusion and a re-import.
- "Fix this route" (and "Try again" after a revert, which deletes the `reject`)
  queues an interactive job. The panel shows its progress, and the result appears
  within 60 s, even during the backfill and while the Owner is using the map
  (NFR-21). Polling the job's progress never triggers a 429.
- Changing the Activity's type re-matches with the new costing.
- The view passes jest-axe with no serious violations (NFR-15).

### 12.8 Status, export and docs — R1

As the Owner, I want to see what the route service is doing with my data, and I
want route data covered by export and erase.

Acceptance criteria:
- `GET /api/routes/status` and the job board show queued, running, last run, last
  error, engine and graph version, and regions. When the service is down, they
  show "route-quality service unavailable", and writes answer
  `ROUTES_UNAVAILABLE`.
- The Routes `IOwnerDataContributor` exports decisions, settings, metrics and
  GeoJSON matched routes with their piece kinds, and erases every `routes` row.
  The contributor coverage test passes.
- `atlas-as-built.md` and the PRD and architecture statuses are updated in the
  same PR (NFR-06).

### 12.9 Foot and bike profiles — R2

As the Owner, I want sparse walks, runs, hikes and rides placed on paths and
roads too, without spoiling the routes I recorded well.

Acceptance criteria:
- ride (`bicycle`, Hybrid), walk and run (`pedestrian`) and hike (`pedestrian`,
  with tracks preferred) are matched with the AD-35 parameters, including a
  `breakage_distance` of 5 km or 3 km.
- Only records that need improvement are matched. A dense recording (for example
  Sports Tracker at 91 points per km) is never matched.
- A fixture of at least 20 foot and bike Activities, including hikes on trails,
  is kept, each with a reference line. An automated test passes when, for at
  least 90% of the Activities the matcher marks `matched`, at least 90% of the
  matched line's length lies within 30 m of the reference (15 m on foot).
- If Valhalla's trail matching fails that test, the hike type stays disabled,
  and GraphHopper is re-evaluated (AD-32).

### 12.10 Route quality in Insights, with batch improve and settings — R2

As the Owner, I want a data-quality view of my routes and one action to improve
many at once.

Acceptance criteria:
- The Insights data-quality section shows, for the current query:
  - counts by grade;
  - improved, waiting, and unmatchable, with the reason (no track, outside
    coverage, type never matched, rail, transit type unknown);
  - the worst sources.

  It comes from `GET /api/routes/summary`.
- "Improve N routes" queues a batch job for the query (≤ 2,000 ids), and shows
  its progress.
- A proposal appears when the period has more than 20 drawn-as-straight Activities
  that need improvement. Its action is the batch.
- A "Route quality" dialog next to Fusion settings sets the threshold, the matched
  types and "shown by default". Saving re-selects at the next read, with no
  re-match. Enabling a type queues its records.

### 12.11 Routes abroad — R3

As the Owner, I want my trips abroad on the roads too, for the countries I choose.

Acceptance criteria:
- A coverage report lists the regions that hold the most `outside_coverage`
  Activities, ranked by activity count, from the Geofabrik region index shipped
  in the tiles image. Each Owner sees their own counts; the operator sees only
  per-region counts summed over all Owners (AD-32 rule 6). No Activity data
  leaves the cluster to build it.
- Adding a region is a change to `routing/regions.txt`. CI merges the
  overlapping extracts with `osmium merge` into one `.osm.pbf`, builds one tile
  set, and the graph version lists the regions. The image tag uses the short
  slug of AD-32 rule 3 (`r` and 10 hex digits of the region list's hash), never
  the region names.
- The `emptyDir` `sizeLimit` and the ephemeral-storage limit are set from the
  new tar's size (AD-32 rule 4) in the same tag-bump PR.
- After the rollout, the Activities in the added region are assessed and matched.
  The engine's RSS stays within its 512Mi limit.
- The PRD §5.2 Geofabrik row lists the regions downloaded.

### 12.12 Photos on the matched line — R2

As the Owner, I want photos taken on an inferred stretch to sit on the drawn
road, not off it on the old straight line.

Acceptance criteria:
- Until this story, a photo on an inferred leg stays at its position
  interpolated on the straight leg (AD-34 rule 4).
- When a matched route is chosen (AD-34 rule 3), a photo whose time falls on an
  inferred piece is placed on the matched line by time, interpolated between
  the two anchors' positions along the line (`fromM`, `toM`). On observed pieces
  and gaps, and when the original is shown, placement is unchanged.
- The stored photo position is not changed; the placement is computed at read
  time, so a revert puts the photo back.

### 12.13 MCP and Assistant tools — R2

As the Owner, I want my AI clients and the Assistant to see route quality and ask
for improvements.

Acceptance criteria:
- The MCP tools `atlas_route_quality`, `atlas_improve_routes` (ids ≤ 500, or a
  query) and `atlas_route_status` exist, with scope `atlas` (AD-33 rule 8). The
  same tools are in the Assistant's Atlas toolset. Owner ids come only from the
  token or the session.
- The two-Owner isolation fixture covers the three tools.

### 12.14 "Use this route" for low-confidence matches — R2

As the Owner, I want to look at a match that was not confident enough to show,
and use it if it is right.

Acceptance criteria:
- For a `low_confidence` match, the panel offers to view it, and "Use this
  route" writes an `accept` decision keyed by `source|externalId`. It works only
  at a confidence of 0.3 or more.
- An accepted match is drawn everywhere (AD-34 rule 3) and survives a re-match,
  a re-fusion and a re-import, as `reject` does.

### 12.15 Re-match on a new graph version — R2

As the Owner, I want my routes improved when the map data improves, without the
map flickering.

Acceptance criteria:
- A new graph version re-matches existing `matched` and `low_confidence` rows at
  the lowest priority, oldest first, in the AD-30 round-robin (AD-34 rule 7).
- The old row is served until its replacement is written.
- `reject` decisions are never overridden.

### 12.16 Roads travelled and the road filter — R1

As the Owner, I want to see which roads a matched route took, and find every route
that passed through a road ("all routes through road 431").

Acceptance criteria (FR-61, AD-37):
- [x] Stage B reads `edge.names` from `trace_attributes` and the maneuvers' `street_names`
  from gap-filling `/route` calls (`directions_type: maneuvers`). No extra engine call.
- [x] Each road gets the metres of the `observed` and `inferred` segments on it; gaps
  count for no road. A road needs 100 m, and at most 40 are kept per line, in travel
  order with `fromM`. Route numbers (1 to 4 digits, an optional letter) are marked `ref`.
- [x] `matched_routes.Roads` (jsonb) and `RoadNames` (text[]) are written with the line
  and cleared with it (migration `RoutesRoads`), and the routes export includes them.
- [x] Rule `routes.v4`: every matchable drive and bus or taxi trip is matched, not only
  those that need improvement (1,392 more drives for the first Owner). Every row is
  re-matched once in the normal queue.
- [x] `AtlasQuery.Road` filters search, density, the timeline and Insights through
  `IRouteGeometryReader.OnRoadAsync`. A route number matches exactly and a name in
  part. Only confident or accepted, unrejected and current matches count, and visits
  are left out. With route quality off, the filter finds nothing.
- [x] The panel shows "Via 4 › 431 › street" under the route line while the matched
  line is drawn. A click sets the filter, shown as a "Road 431" chip; "road 431" or
  "כביש 431" typed into the search box sets it too.
- [x] The Assistant tools and the MCP `atlas_search` and `atlas_insights` take `road`.
- [x] Tests:
  - pure: names, metres, gaps and fills, the limits, the parsing;
  - Postgres: the filter, its exclusions, the detail and coverage;
  - the local Valhalla 3.9.1 with the Israel tiles: Ramla to Modi'in lists 423, then 431, and a filled stretch counts for 431;
  - Jest: the filter helpers and the panel line.

### 12.17 Rebuild a route from similar drives — R1

As the Owner, I want a drive that cannot be placed on the roads from its own points
rebuilt from my other drives between the same places (or the same places reversed),
when I ask, and shown to me before it is used.

Acceptance criteria (FR-62, AD-38):
- [x] `RoutePoint.Synthetic`: legs touching a synthetic anchor are `inferred`, and
  `anchorQuality` counts real points only.
- [x] `matched_routes` gains `Method`, `Basis`, `StartedAt`, `StartLat`, `StartLon`,
  `EndLat` and `EndLon`, written at match time (rule `routes.v5` re-matches every row
  once to fill them). `jobs` gains `Result` (migration `RoutesRebuild`).
- [x] `SimilarRoutes` (pure):
  - the pool: matched, at least 0.8, `auto`, not rejected, the same type, both ends within 500 m, same direction or reversed, the 200 nearest;
  - the fit: the share of real points along the line, projected in travel order (a round trip or a place passed twice reads in order), at least 0.6;
  - the rank: fit, then time of day, then recency;
  - borrowing: on legs longer than 300 m whose two ends both lie along the basis, never on the basis's gap pieces, timed by distance, never faster than the type's maximum.
- [x] `Rebuilder`: the best 3 bases at 200 m and 600 m, keeping the most confident
  (stopping at 0.95); the off-road tolerance is 150 m; the confidence is `min(factors, fit)`.
- [x] The `rebuild` job runs interactively and stores the suggestion (line, pieces,
  roads, confidence, the current confidence, basis) on the job. With no basis, the
  outcome is `no_similar`, with why.
- [x] `/api/routes/activities/{id}/rebuild` and `/apply`; apply answers 409 when the
  record changed, writes the row (`Method = similar`, `Basis`) and an `accept` decision.
- [x] A `similar` row is not re-matched for a new rule version, and is re-matched
  `auto` when its points or type change.
- [x] The panel offers "Rebuild: from similar drives". The suggestion is drawn on the
  map as the selected route, and the panel shows the basis ("From 14 drives between
  the same places (6 follow this one), the closest on 3 Sep at 08:12, the other way"),
  the confidence against the current one, Use this route and Discard.
- [x] Tests:
  - pure: kinds and the confidence, fit, the other direction, round trips, rank, borrowing, timing, the speed limit and gaps, marked points;
  - Postgres: the pool and its exclusions, a rebuild both ways, `no_similar`, apply, `not_found`, 409, lasting across rule versions, re-matched on change;
  - the local engine: Ramla to Modi'in rebuilt both ways from a twin;
  - Jest: the texts, the panel's flows and the marking bar.

  Measured on 60 sampled weak drives (AD-38 rule 9): 38 matched after a rebuild, against 6 by the plain match.

### 12.18 Mark the way — R1

As the Owner, I want to click on the map where I drove, so that a drive with no
similar drives can still be rebuilt, and I want those points kept.

Acceptance criteria (FR-63, AD-38):
- [x] The map's marking mode: a click adds a numbered point, a click on a point
  removes it, at most 50; selecting other activities pauses while marking; the panel's
  bar shows the count, Rebuild and Cancel. Points marked before are offered again.
- [x] Marked points are placed on the nearest leg of the recorded track, in the order
  marked, timed by distance; points over 5 km from the track are refused.
- [x] Using the result stores `route_waypoints`, and every later automatic match of
  that record goes through them (`Method = waypoints`), including after a rule change
  and a forced "Fix this route". Marked points that no longer fit a changed track are
  left out.
- [x] `route_waypoints` is exported with the Owner's routes data and deleted with an
  Owner who is gone. "Clear them" deletes a record's marked points and re-matches it.
- [x] Review fixes: round trips scored in both directions; apply in one transaction
  against the drive's current track; the pool reads lines for the 200 nearest only;
  a re-match that gives up keeps the start and ends; rebuilds run before other
  interactive jobs; marking ends with an unrebuildable reload and is exclusive with
  the Area tool.

## Coverage

| Requirement | Stories |
| --- | --- |
| FR-55 Route quality assessment | 12.3, 12.2b (inputs), 12.5 (runs) |
| FR-56 Road-matched routes | 12.1, 12.4, 12.6 (R1); 12.9, 12.12 (R2) |
| FR-57 Automatic and on-demand runs | 12.2, 12.2b, 12.5, 12.7, 12.8 (R1); 12.13, 12.15 (R2) |
| FR-58 Original or matched; the Owner decides | 12.6, 12.7 (R1); 12.14 (R2) |
| FR-59 Route quality in Insights | 12.10 |
| FR-60 Routes abroad | 12.11 |
| FR-61 Roads travelled | 12.16 |
| FR-62 Rebuild from similar drives | 12.17 |
| FR-63 Mark the way | 12.18 |
| NFR-20 Route matching stays home | 12.1, 12.2 |
| NFR-21 Route quality fits the Pi | 12.1, 12.5, 12.7 |

## Decisions

The Owner asked for these to be decided rather than asked (2026-10-28). The
reasons are in PRD §11 and architecture §12.

- **Activities with no track** (Samsung Health): no route is synthesised. They
  are flagged "no track" and not drawn (PRD Q-11, AD-35 rule 1).
- **Rail transit:** left as recorded; only buses and taxis are matched on roads
  (PRD Q-12, AD-35 rule 5). Transit with no source type is `unknown` and not
  matched in R1.
- **Regions abroad:** none at MVP. In R3 they are added on demand from the
  "outside coverage" list, ranked by activity count (PRD Q-13, AD-32 rule 6).
- **Shown by default:** a Matched Route is shown by default at a confidence of
  0.7 or more, with no acceptance needed (PRD Q-14, AD-34 rule 3).
- **R1 scope:** the MCP and Assistant tools, "Use this route" for
  low-confidence matches, re-matching on a new graph version and photos on the
  matched line move to R2 (stories 12.12 to 12.15).
- **Node:** the pod runs on the node with the most free RAM, chosen by the spike
  in story 12.1 (architecture §12 item 10, AD-36 rule 2).
- **Phase 0 ordering:** R1 builds the internal service auth and the CI image
  build itself if Phase 0 has not delivered them (architecture §12 item 11).

No open questions remain.
