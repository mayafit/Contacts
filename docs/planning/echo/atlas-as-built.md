# Atlas — as-built inventory (repo D:\Code\Contacts, dev @ 1e05abc, 2026-10-28)

Built in PRs #56–#110 (from a91a5c7 "Personal site with Atlas" to 1e05abc photo marks).

## Facts from production and the owner (not in the repo)
- OneDrive (personal) is connected and synced: 157,021 photos and videos (6,890 videos), from 1984 to 2026-10-27; 40,724 have GPS; 30,159 placed on activities, 53,085 on visits, 74,061 shown on the map. PlacedBy: gps 40,724, track 6,641, visit 26,696, none 82,960. The clock check found the camera writes local (Israel) time, offset +120/+180 min for most photos.
- Google Takeout for Google Photos was not feasible for the owner, so photos come from OneDrive.
- Planned "Phase 2: people/faces in photos" was discussed with the owner. It is not started and needs the owner's explicit opt-in, because face recognition stores biometric data. Nothing about it is in the repo.
- The owner has Samsung Health data whose time alignment is still to be checked.

## 1. User-facing capabilities
- Site shell: a left rail with Contacts and Atlas; `/atlas` renders AtlasHome (src/entries/Contacts/components/SiteShell.tsx).
- Map (atlas/AtlasMap.tsx):
  - tracks coloured by type, visits as circles, flight great-circle arcs (geo.ts);
  - draw an area filter, fit to results, map styles (mapStyle.ts): OpenFreeMap vector in MapLibre with English labels, OSM raster fallback without WebGL, Esri imagery;
  - density mode: faint tracks plus a visit heat map (DensityLayer.tsx, densityGeo.ts);
  - photo layer toggle; thumbnail pins for the open item.
- Timeline (AtlasTimeline.tsx): histogram of activities, visits and health; drag a range, scroll to zoom; the start range is remembered (startRange.ts).
- Filters (AtlasFilterBar.tsx, filters.ts): time, area, types, text, "sources disagree". With types selected, visits are limited to those during such trips (±30 min, about 300 m).
- Results and Insights tabs (ResultsList.tsx, InsightsPanel.tsx, insights.ts):
  - totals vs the previous period, per-type statistics, trends, weekdays and hours;
  - records, streaks, top and new places, travel (flights, days more than 100 km from home);
  - data quality, rule-based proposals with actions;
  - weekly load, HR-zone time, sport trends;
  - "Explain with AI" hands the insights to the assistant.
- Activity panel (ActivityPanel.tsx, ActivityCharts.tsx, AnalysisSections.tsx, HrZonesBar.tsx):
  - stats and HR chart;
  - analysis: moving vs stopped, splits, HR zones, drift, best efforts with all-time rank, same-route comparison, percentiles;
  - fusion drill-down: sources, which source supplied each field, conflicts; split, merge, change type;
  - linked visits and photos.
- Place panel (PlacePanel.tsx, placeText.ts):
  - coordinates and copy, Google Maps and OSM links, visit statistics;
  - identify via OSM; associate with a found place or a typed name; confirm an automatic identity; choose among ambiguous candidates; apply to nearby places;
  - the place's photos.
- Photos (PhotoStrip.tsx, PhotoViewer.tsx, photos.ts): thumbnail grid, a viewer with arrows and an "open in OneDrive" link, and "has photos" marks in the list, map and legend (AtlasLegend.tsx).
- Import dialog (ImportDialog.tsx):
  - Files: files, zips or a folder; recognize automatically; list and delete imports;
  - Sports Tracker: connect, sync, disconnect;
  - OneDrive photos: connect, sync, disconnect.
- Fusion settings (FusionSettingsDialog.tsx): time and location tolerance per source, match threshold. Saving re-fuses everything.
- AI access (AiAccessDialog.tsx): personal access tokens for MCP clients.
- Assistant: the shared Gemini AssistantPanel with Atlas tools (atlasTools.ts): atlas_search, atlas_activity, atlas_health, atlas_near, atlas_identify_location, atlas_insights, atlas_summary, show_on_map, open_activity, and others.
- Health: HR and other samples in an activity's window, and a strip on the timeline. There is no separate health UI.

## 2. Sources and ingestion
- Google Timeline: on-device export and older Takeout (importers/google.ts), parsed in the browser. Upsert by (owner, source, externalId).
- Google Fit JSON and TCX, and Fitbit HR (google.ts, tracks.ts). Health is unique on (owner, type, start, source).
- Samsung Health export (importers/samsung.ts).
- Any GPX or TCX (importers/tracks.ts), optionally tagged as Sports Tracker.
- Sports Tracker: server sync through the unofficial API (Infrastructure/Atlas/SportsTracker.cs, WebApi/Services/AtlasConnections.cs, AtlasSyncWorker).
  - Incremental: already imported workouts are skipped.
  - The password is used once; the encrypted session key is kept.
  - No periodic sync. A sync cut short by a restart is marked as an error.
- OneDrive photos (Infrastructure/Atlas/OneDrive.cs, WebApi/Services/OneDrivePhotos.cs, PhotoSyncWorker):
  - Microsoft Graph delta; the delta link is stored in SourceConnection.Cursor;
  - a full re-list on ONEDRIVE_RESYNC, and missing items are deleted;
  - runs on connect, on Sync, at startup, and every 30 minutes;
  - unchanged items are skipped; a photo is placed again only if its time or GPS changed.
- Manual and MCP create calls (AtlasMcpTools.cs).
- Browser imports:
  - zips are streamed with fflate; entries over 256 MB are skipped; Records.json is ignored;
  - POST /api/atlas/ingest in batches of at most 4,000 items or 25,000 points; 20 MB per request;
  - one transaction per ingest, one at a time per owner; the time window touched goes to fusion_queue.
- Not built: Strava, Garmin, Google Photos, server-side file importers.

## 3. Data model (PostgreSQL schema `atlas`; Infrastructure/Atlas/AtlasDbContext.cs, AtlasEntities.cs; 16 migrations, 20261022164327_InitialAtlas … 20261027211113_Photos)
- Every row has OwnerId, the Google `sub`.
- activities: type, name, notes, start/end, distance, duration, climb, HR, bounding box (no PostGIS), encoded polyline, PointCount, ClusterId (the fused primary), TypeByUser. Every source record has Source, ExternalId and ImportId.
- track_points: time, lat/lon, elevation, HR, speed, cadence. Cascade-deleted with the activity.
- places: name, address, lat/lon, category, GooglePlaceId, LinkedRef (osm:node/123), website, phone, hours, Details JSON, UserEdited, IdentifyState (pending/auto/ambiguous/none/user), IdentifyCandidates (≤6), IdentifiedFrom, IdentifyCheckedAt.
- visits (PlaceVisit): place (set to null when the place is deleted), start/end, lat/lon, confidence.
- health_samples: type, start/end, value, unit, source.
- activity_fusion: 1:1 with the primary. Fused fields, segments, Sources, FieldSources, Conflicts, Evidence, MatchConfidence.
- fusion_decisions: a pair, kind together or apart.
- fusion_queue: time windows, a Full flag, attempts with backoff.
- fusion_settings: JSON per owner.
- activity_efforts: best efforts.
- imports (ImportRun): counts per run; deleting an import deletes its rows.
- access_tokens: SHA-256 and a prefix.
- source_connections: provider (sports-tracker/onedrive), account, SecretProtected, status, progress, Cursor. Unique on (owner, provider).
- photos (AtlasPhoto): external id, name, MIME type, video flag, size, dimensions, camera, WebUrl, Folder, TakenAtRaw, ClockOffsetMin, TakenAt (UTC), camera lat/lon/altitude, MapLat/MapLon, PlacedBy (gps/track/visit), ActivityId, VisitId, Placed. Metadata only; the files stay in OneDrive.
- Relationships:
  - photo → activity and visit are plain Guid columns, not EF foreign keys;
  - visit links on an activity are computed when it is read (no activity_visit_links table, unlike the epic doc);
  - the epic's activity_clusters became activity_fusion.

## 4. Pipelines
- Fusion (Infrastructure/Atlas/AtlasFusion.cs, about 1,355 lines; worker WebApi/Services/AtlasFusionService.cs):
  - default tolerances per source (time / location):

    | Source | Time | Location |
    |---|---|---|
    | sports-tracker, gpx, tcx | 30 s | 25 m |
    | manual | 900 s | 500 m |
    | google-fit | 120 s | 300 m |
    | samsung-health | 60 s | 300 m |
    | fitbit | 60 s | 100 m |
    | google-timeline | 600 s | 300 m |
    | other | 300 s | 200 m |

  - Threshold 0.5. Confidence = time agreement × location agreement (tracks compared at about 60 moments).
  - Never merges two recordings from one source, consecutive items, or a short activity into a long one around it.
  - Pieces of one outing are joined when the gap is ≤15 min.
  - Fields come by source priority; conflicts recorded: type, distance, start, location.
  - User merge/split decisions are re-applied on every run.
- Worker: wakes on an ingest signal or every 5 minutes, waits 2 s, then drains the queue per owner. When idle it:
  1. places photos, 500 per batch with 400 ms pauses;
  2. computes best efforts, 20 per batch;
  3. identifies places, 10 per batch.
- AtlasMigrator: applies migrations at startup (20-minute timeout) and a one-time backfill; Atlas answers 503 until the database is ready.
- Track analysis (TrackAnalysis.cs, AtlasAnalysis.cs): on the full-resolution server track. HR zones use the 98th-percentile max HR.
- Place identification (AtlasPlaceIdentity.cs, GeoLookup.cs):
  - services: Nominatim (≤1 request/s, User-Agent `Atlas/1.0 (...contacts.mayaffit.com)`), Photon (≤2 at a time), Overpass (3 instances, 8-second budget); answers cached in memory for a day; partial results allowed;
  - automatic identity: exactly one relevant place within 40 m and none other within 80 m; otherwise ≤6 candidates within 100 m;
  - places with typical stays of 6 hours or more (home, work) are never named automatically; bus stops, parking and benches don't count;
  - "none" is rechecked after 30 days; most-visited places go first;
  - a user identity spreads to unnamed places within 100 m when there is no other identity within 150 m.
- Photo clock calibration (PhotoClock.cs):
  - GPS photos are tested as UTC and as local time of their zone (GeoTimeZone), against visits and activities;
  - within 1 km agrees, beyond 5 km contradicts;
  - local clock needs ≥5 local votes and more than 3× the UTC votes; photos and videos are decided separately;
  - photos without GPS take the nearest offset within 3 days, else the most common one;
  - runs after a sync that changed photos, else at most once a day.
- Photo placement (AtlasPhotos.PlacePendingPhotosAsync):
  - activity: the shortest fused activity covering the time ±2 min; visit: the shortest covering the time;
  - position: own GPS, else the track interpolated within 10 min, else the visit's location;
  - placed again on a time or GPS change, on new data within ±1 h, or on an offset change.

## 5. API and frontend
- REST /api/atlas (WebApi/Controllers/AtlasController.cs):
  - status, ingest, search, histogram, insights, summary, density, near, health;
  - activities/{id} (GET maxPoints, PATCH, DELETE), /split, /merge, /photos;
  - fuse, fusion/status, fusion/settings;
  - connections, connections/sports-tracker (+/sync), connections/onedrive (/start, /callback, /sync);
  - photos/map, photos/{id}/thumbnail;
  - places (POST), places/{id} (GET, PATCH, DELETE, /confirm, /apply-to, /photos);
  - lookup, visits/{id}/place, visits/{id}/photos, visits/{id} (DELETE);
  - imports/{id} (DELETE), tokens.
- MCP at /api/mcp (official C# MCP SDK; Bearer `atl_` personal tokens; AtlasMcpTools.cs): search, get activity, health, near, identify, get place, insights, summary, merge, split, fuse, fusion settings, create/update/delete activities and places, delete visit, add health, delete import. No photo tools.
- Frontend:
  - React, MUI, react-leaflet 4.2.1, Leaflet 1.9.4, maplibre-gl 5.24 (loaded lazily), fflate;
  - state in useState in AtlasHome.tsx (977 lines), not Redux; localStorage for preferences;
  - REST through src/services/api/BackendApiClient.ts; types in atlas/types.ts;
  - pure logic in analysis.ts, insights.ts, fusionText.ts, geo.ts, filters.ts.

## 6. Scale and performance
- Raspberry Pi arm64 (hence no PostGIS, and pauses in the workers).
- Caps:

  | What | Limit |
  |---|---|
  | Search | 1–2,000 per kind |
  | Density | 6,000 tracks and 30,000 visits (tracks at about 30 m) |
  | Map photos | 3,000 |
  | Panel photos | 500 |
  | Activity detail | 2,000 points |
  | Health series | 1,500 points |
  | Ingest | 20 MB |
  | Clock-offset updates | chunks of 2,000 |

- Rendering: canvas (preferCanvas); track photo dots are on the canvas; the density view uses WebGL.
- Indexes start with OwnerId, plus partial indexes for photos.

## 5B. Place reviews (Epic 13)
Architecture §5B, AD-39; plan `docs/planning/epic-13-place-reviews.md`.
- **Import.** `parseGoogleReviews` (`importers/google.ts`) reads Takeout's `Maps (your places)/Reviews.json` (by name, or by a `FeatureCollection` carrying review fields) into `ParsedData.reviews`; the import dialog sends them in batches with the source `google-maps`, and `IngestRequest.Reviews` stores them in `atlas.place_reviews`, keyed by the cid of the review's Google Maps link. Review photos are not in Google's export.
- **Linking.** `PlaceReviews.Link` (`AtlasPlaceReviews.cs`) links each review to one place (a shared word within 75 m, the only place within 30 m, or a clear nearest within 10 m); `AtlasService.LinkReviewsAsync` runs after every batch with reviews or places, and a review linked by position names an unnamed place (`IdentifiedFrom = review`).
- **Shown.** `GET /api/atlas/places/{id}` carries `review` and `reviewState`; `PlaceReview.tsx` shows the review or the reminder in the place panel, and `ReviewsSection.tsx` the Insights **Reviews** section from `GET /api/atlas/reviews`.
- **From Drive (AD-40).** The `takeout` opt-in adds `drive.readonly` to the Google sign-in; `/api/atlas/connections/google-drive` keeps an `atlas.connections` row (`google-drive-takeout`); `TakeoutDriveWorker` checks connected Drives daily for new `takeout-*.zip` exports (500 MB at most), reads their reviews file on the server (`GoogleReviewsParser`) and imports it like the browser does. The import dialog's **Google Drive** tab explains the Takeout schedule.
- **Reminders.** `PlaceReviews.Assess` decides at read time; `atlas.review_reminders` keeps Later, Not this one, Not this kind and I reviewed it (`POST /api/atlas/reviews/reminders`).

## 6A. Routes (route quality and road matching, Epic 12 R1)
Architecture §5A, AD-32 to AD-37; plan `docs/planning/epic-12-route-quality.md`.
- **Roads travelled (AD-37, rule `routes.v4`).**
  - **Stored.** Every matched line stores the roads it follows: `matched_routes.Roads` holds `[{name, ref, m, fromM}]` in travel order, and `RoadNames` holds the names alone for the filter. The names come from Valhalla's `edge.names` and from the gap fills' maneuver `street_names`. Only observed and inferred metres count, and a road needs 100 m.
  - **Matched.** Every matchable drive is matched, not only the sparse ones.
  - **Shown.** The panel's "Via" line (`RouteRoads.tsx`) lists the roads.
  - **Searched.** `AtlasQuery.Road` filters search, density, the histogram and Insights through `IRouteGeometryReader.OnRoadAsync`. It is set from the panel, by "road 431" or "כביש 431" in the search box, or by the Assistant and MCP `road` argument.
- **Rebuild on demand (AD-38, stories 12.17 to 12.19).**
  - **Similar drives.** "Rebuild: from similar drives" in the panel (`RouteRebuild.tsx`) queues a `rebuild` job. `RebuildRunner.PoolAsync` finds the Owner's confident `auto` drives whose `matched_routes.Cells` (the 0.01° corridor cells within 200 m of the line, `RouteCells`) cover at least 60% of the drive's non-stray points, or, for rows without cells yet, both ends within 500 m; `SimilarRoutes` scores each line both ways round (fit in travel order, pace, time and kind of day, detour, ends), groups the best 30 into ways, and `Rebuilder` tries the best of up to three ways at 200 m and 600 m. The suggestion waits on `jobs.Result` until the Owner uses or discards it.
  - **Mark the way.** The map's marking mode sends the Owner's points; `Waypoints` places them on the recorded legs. A used route stores them in `route_waypoints`, and every later match goes through them.
  - **Cells** are written with every line and filled at the worker's start for older rows (no re-match).
- **Service.** `EchoRoutes.Worker` (with Valhalla in the same pod, `helm/contacts/charts/routes`) pulls each Owner's change feed through `/internal/routes/*`, assesses every record (rule `routes.v1`, stage A: grade, flags, matchability) and matches drives and bus or taxi transit in Israel (stage B). It writes only the `routes` schema: `route_quality`, `matched_routes` (polyline at precision 5, pieces `[{fromM, toM, kind}]` in metres, status, confidence, the record's `UpdatedAt`, point count and fused type at match time), `route_decisions`, `settings`, `jobs`, `owner_cursors`.
- **Read-time choice (AD-34 rule 3).** Atlas reads the geometry only through `IRouteGeometryReader` (`Core`), implemented by `Infrastructure/Routes/RouteGeometryReader.cs`:
  - one set-based query per read, by record ids: `matched_routes` by `(OwnerId, ActivityId)`, left-joined to `route_decisions` by `(OwnerId, SourceKey)`, the Owner's settings as a scalar subquery. No points are read and nothing is hashed; existing unique indexes serve it, so no index was added;
  - a match is drawn only when its stored `UpdatedAt`, point count and fused type equal the record's, there is no `reject`, and it is `matched` with a confidence at least the threshold (0.7) while "shown by default" is on (or, R2, `accept` and at least 0.3);
  - measured (`RouteGeometryDbTests`): search sends 3 Atlas commands with or without routes and exactly 1 routes command, also for 40 records; density sends 1 routes command;
  - the reader is on only when `Routes:ServiceUrl` is set; a missing `routes` schema or a lost connection logs a warning and draws the recorded lines. Stored matches are still drawn while the service itself is down (they are in Postgres).
- **API shape.** `ActivitySummary.geometry` is `matched` or `recorded`; `polyline` is then the matched line. `pieces` and `onRoadsM` are sent only in the detail view (`GET /api/atlas/activities/{id}`, which also returns the source points unchanged and `route`: record ids, quality, stored match, decision, observed and inferred shares). Search and density carry the chosen polyline only; density re-simplifies it as before (about 30 m). An outing of several recordings joins each recording's chosen line in time order (the jump between recordings is a gap piece); that needs one more Atlas query, only when an outing is in the list. `activity_fusion.Polyline` is never changed.
- **Distance (rule 5).** When fusion took the distance from the straight legs (`FieldSources.distance = "track"`), the matched length replaces it in the list and the panel while the match is drawn; otherwise the panel shows "· X km on roads" beside it. Insights totals still use the fused distance (not done in R1).
- **What stays on the source points:** the analysis, splits, HR zones, best efforts, charts, photo placement and the chart's hover marker on the map.
- **REST** (`RoutesController`, Owner from the session only): `POST /api/routes/improve` and `/assess` (ids, a range or all; ≤ 50 ids is interactive), `GET /api/routes/jobs/{id}`, `GET /api/routes/status` (`enabled: false` when switched off, `available: false` when the service is down), `PUT` and `DELETE /api/routes/activities/{id}/decision` (reject or accept for each drawn record's `source|externalId`; 503 `ROUTES_UNAVAILABLE` when off), `GET /api/routes/export`.
- **Export.** There is no per-user export elsewhere yet (no `IOwnerDataContributor`), so `GET /api/routes/export` streams the Owner's routes data as one JSON document: settings, decisions, quality rows with their metrics, and `matchedRoutes` as a GeoJSON FeatureCollection (LineString, longitude first; properties include status, confidence, graph version and the pieces). Erase of `routes` rows is not built yet (it belongs with the contributor).
- **Map.** List and density lines are drawn as before, on the canvas, with the chosen polyline. The selected activity, when matched and on "Roads", is drawn from its pieces: observed solid in the type's colour, inferred dashed and fainter, gaps as faint grey dotted straight legs; its source points show as small dots from zoom 14. Pieces are decoded once per route (`routeGeometry.ts`, memoised). The legend adds "Inferred road (no GPS)" with a dashed sample only while an inferred piece is drawn. Lists show nothing new.
- **Panel** (`RouteLine.tsx`): one caption line under the header: "Route: matched to roads · 92%" with a Roads / Recorded toggle and "Revert to recorded"; "Route quality: fair" with the flags on hover and "Fix this route"; "Route: kept as recorded" with "Try again" (deletes the reject first). Fix queues an interactive improve and polls the job every 2 s. Errors say "Route matching unavailable". Hidden when route quality is off.
- **Status.** The import dialog shows one line from `/api/routes/status`: waiting and queued counts, an ETA, a paused breaker or a down engine; last run, last error and graph version on hover. Nothing when switched off.

## 7. Planned but not built (docs/planning/epic-10-personal-atlas.md, epic-11-fusion.md)
- Elevation profiles, comparisons between rides.
- Health beyond HR: sleep, steps, weight, daily summaries, trends.
- "Who you met where": Calendar + Timeline + Contacts.
- Strava and Garmin; Calendar and Contacts links; people from Contacts on an activity.
- Routines, time at home and work, favourite routes, resting-HR trend, "what could I improve" and "what I like"; insight MCP tools; pin a field's source.

## 8. Privacy and security
- Every REST call needs a Google session (GoogleTokenService); the owner is the session's user id and every query filters on it. MCP gets the owner from the token (Program.cs, around lines 260–283).
- Access tokens: atl_ + 32 bytes; only the SHA-256 is stored; revocable; remote clients come through Cloudflare Access.
- Secrets are encrypted with Data Protection (purpose Atlas.SourceSecrets.v1): the Sports Tracker session key and the OneDrive refresh token. A refused renewal marks the connection expired.
- OneDrive: scopes offline_access Files.Read User.Read; /consumers; state protects against CSRF; the client secret is in Vault; thumbnails are proxied; disconnecting deletes the photo metadata.
- Third parties that receive data:
  - Nominatim, Photon and Overpass get coordinates, including automatic background lookups;
  - Microsoft Graph and Sports Tracker;
  - tile servers;
  - Google Gemini gets assistant messages and tool results.
- Imports can be undone; fusion never edits source records.

## 9. Touchpoints with Contacts
- Shared: the Google session and identity, the site shell, one backend process (Atlas has its own AtlasDbContext and schema), the Gemini assistant panel, the theme.
- No data links today.
- Natural joins (none built):
  - contacts and calendar attendees ↔ visits in the same time window;
  - people in photos;
  - contact addresses ↔ places;
  - home and work detection ↔ Contacts addresses.

## Tests
- Backend xUnit (ContactsApi/tests/ContactsApi.Tests): AtlasTests, AtlasFusionTests, FusionOutingTests, AtlasInsightsTests, TrackAnalysisTests, PhotoTests, PlaceIdentityTests, GeoLookupTests.
- Routes: RouteQualityTests, RoutesMatchingTests, RoutesMatchDbTests, RoutesSchedulingTests, RoutesWorkerTests, RoutesFeedDbTests, InternalRoutesAuthTests, RoutesServiceClientTests, RouteGeometryDbTests (choice, query counts, decisions, export).
- Frontend Jest (atlas/__tests__, importers/__tests__), including routeGeometry.test.ts and RouteLine.test.tsx (with jest-axe).
