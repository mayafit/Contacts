# Epic 11 — Atlas fusion: one coherent picture from every source

> Vision: Atlas becomes a **Personal Self-Intelligence System**. Every source
> (Sports Tracker, Google Timeline, Google Fit, Fitbit, GPX/TCX files, and
> sources still to come) feeds one place. Atlas fuses records that describe
> the same thing into one entity, keeps where every value came from, shows
> conflicts, and builds the insight on top: where I was, what I did, how I
> felt, what I like, what I could improve.

Builds on Epic 10 (`epic-10-personal-atlas.md`): storage, import, map,
timeline, MCP.

## Principles

1. **Sources are never edited by fusion.** Every imported record stays as it
   came (its source, external id, raw values). Fusion adds a layer on top. It
   can be recomputed at any time, and nothing is lost when a rule changes.
2. **One entity per real-world thing.** A ride recorded by Sports Tracker,
   seen by Google Timeline as "cycling", and covered by Fitbit heart rate is
   one activity, with three contributors.
3. **Provenance on every value.** For each fused field: which source supplied
   it, and what the others said.
4. **Conflicts are shown, not hidden.** For example, type, distance or times
   that disagree beyond a tolerance.
5. **The user has the last word.** Manual merge and split, and pinning a
   field's source. User decisions survive re-fusion and re-import.
6. **Sources are pluggable.** A new source only maps its data into the
   common records (activity, visit, place, health sample). Fusion and the UI
   work unchanged.

## Data model

| Table | What | Notes |
| --- | --- | --- |
| `activities` (existing) | Source records | `ClusterId` added (nullable) |
| `activity_clusters` (new) | Fused activities | Fused fields, `PrimaryActivityId`, `FieldSources` (json), `Conflicts` (json), `Locked` (the user merged or split it) |
| `cluster_decisions` (new) | User merges, splits and field pins | Re-applied on every fusion run |
| `visits` (existing) | Source records | Linked to clusters via `activity_visit_links` (start, end, during) |
| `source_connections` (new) | Connected accounts (Sports Tracker) | Encrypted session key, last sync, status; never a password |

Search, map, timeline and MCP return **clusters** (one row per real-world
activity). A member row is shown only on drill-down.

## Fusion rules (activities)

- **Candidates:** activities of the same owner whose time ranges overlap.
- **Same thing when:**
  - the time overlap is at least 50% of the shorter one, **and**
  - their geography agrees: the bounding boxes intersect, or the start
    points are within 1 km (a record without a track matches on time alone).
- **Not the same:** two recordings from the same source. Two Sports Tracker
  workouts are never merged automatically.
- **Source priority per field** (highest first):
  - Track and distance: Sports Tracker / GPX / TCX, then Google Fit, then Google Timeline.
  - Heart rate: the recording (track HR), then Fitbit / Google Fit health samples.
  - Type: the recording's sport, then Fit, then Timeline (Timeline often says "driving" for a ride).
  - Name and notes: user edits, then the recording.
  - Times: the recording, then the widest span of the others.
- **Conflicts recorded:**
  - type differs (after normalisation)
  - distance differs by more than 10% and 500 m
  - start differs by more than 10 minutes
  - a member has a track far outside the others'
- **When fusion runs:** after every import batch, for the affected time
  window only, and also on demand ("Re-fuse everything").

## The fusion service (11.3b)

Fusion is a service of its own, not a step of the import:

- **Queue:** every upload (files, zips, Sports Tracker syncs; activities, visits, health)
  records the time window it touched in a persistent queue (`fusion_queue`).
- **Background service:** the service (`AtlasFusionWorker`) wakes within seconds. It merges
  nearby windows and fuses each one with the data already there:
  - A new record joins an existing fused activity (enhancing it), or forms a new one with
    records it matches.
  - Health data adds heart rate to activities that have none; the health source then counts
    as a contributor.
  - It also checks the queue every five minutes, so nothing queued before a restart is lost.
- **Status:** shown in the app (queued windows, running, last run).

### Accuracy tolerances and match confidence

Every source has a **time tolerance** (how far its start and end can be off) and a
**location tolerance** (how far its positions can be off):

| Source | Time ± | Location ± |
| --- | --- | --- |
| Sports Tracker, GPX, TCX | 30 s | 25 m |
| Google Fit | 2 min | 100 m |
| Fitbit | 1 min | 100 m |
| Google Timeline | 10 min | 300 m |
| Manual | 15 min | 500 m |

Two items are compared with the **combined** tolerance of their sources:

- **Time:** start and end within the combined time tolerance give full time agreement.
  Otherwise they must overlap by at least half of the shorter and a quarter of the longer
  item, and agreement is the overlap (plus the tolerance) as a share of the longer one.
- **Location:** the tracks are compared **at the same moments**: about 60 samples of one,
  each against the other track interpolated at that time. The median distance within the
  combined tolerance gives full agreement, which falls to zero at four times the tolerance.
  Without two tracks, only the areas (widened by the tolerance) must touch, and the
  agreement is 0.85.
- **Confidence** = time × location agreement. An automatic match needs the **threshold**
  (default 50%).

Every member of a fused activity keeps its **evidence** against the primary: start/end
difference, track distance, the tolerances used, and the confidence. Differences beyond
tolerance become **inconsistencies** (time or location conflicts). Tolerances and threshold
are per user (Fusion settings in Atlas); saving them re-fuses everything.

## Context links

- **Visits:** a visit linked to an activity is where it started, ended or
  stopped (within 300 m of the track and close in time). Drill-down shows the
  Google Maps places of the ride.
- **Health:** heart rate, steps and calories in the activity's time window;
  this exists already and is now listed as a contributor.
- **Later:**
  - calendar events in the window, and the people from Contacts who were there
  - photos (EXIF time and place)

## Sports Tracker connector

- The import dialog gets a **Sports Tracker** tab with username and password.
- **Server side:**
  - Log in to the Sports Tracker API: `POST /apiserver/v1/login`, which returns a session key.
  - List the workouts.
  - Download each workout's track (GPX export), with heart rate when recorded.
  - Ingest through the same pipeline as file imports (source `sports-tracker`, external id = workout key), so re-syncing updates rather than duplicates.
- **Credentials:**
  - The password is used once and never stored.
  - The session key is stored encrypted (ASP.NET Data Protection, like Google refresh tokens), so **Sync again** works without the password until it expires.
  - **Disconnect** deletes it.
- **Caveat:** the API is unofficial (it is what Sports Tracker's own web app
  uses), so it can change. Failures are reported per step: login, list,
  download.

## Drill-down UI

The activity panel shows:

- **Sources:** one row per contributor (icon, source, its own type, distance,
  times, HR), and which fields it supplied.
- **Conflicts:** what disagrees and which value won.
- **Actions:**
  - *Split this source off*
  - *Use this source for…* (pin a field)
  - *Merge with…* (pick another activity from the list or map)
- **Places:** the linked visits (start, end, stops) with Google Timeline place names.

## Insights v1 (11.4, built)

An **Insights** tab next to Results, computed for the current query (time, area, types, text,
"sources disagree"), and the MCP / assistant tool `atlas_insights`:

- **Statistics:**
  - totals with the change against the period before
  - per type: count, km, hours, climb, speed, heart rate, longest
  - a trend by day, week or month
  - weekdays and hours
- **What merging reveals:**
  - **Records:** longest, by time, climb, fastest, highest heart rate
  - **Streaks:** active days in a row, days since the last activity
  - **Places:** top places by time spent, new places, unnamed places
  - **Travel:** flights, days more than 100 km from Home
  - **Data quality:** heart-rate coverage, recorded vs only inferred, fused, conflicts, sources
- **Proposals** (rule-based, each with an action):
  - **Rhythm:** a sport's gap is twice its usual one.
  - **Training load:** last 7 days against the 4 weeks before: above 1.5× or below 0.5×.
  - **Review conflicts.**
  - **Add heart-rate data.**
  - **Name a frequently visited unnamed place.**
  - **New places.**
  - **An all-time best** in the period.
  - **Days away.**
- **"Explain with AI"** hands the insights to the assistant for a written account.

## Activity analysis and deeper insights (11.4b, built)

Computed on the server from the **full** track (the app receives a thinned one), returned as
`analysis` on the activity and by `atlas_get_activity`:

- **Points** carry the distance from the start (`d`), speed (recorded, or the ±10 s average) and cadence.
- **Effort:**
  - moving vs stopped time (moving: faster than 1 km/h on foot, 0.5 km/h swimming, 3 km/h otherwise)
  - average moving speed, max speed (best 30–90 s average, impossible speeds ignored), cadence
- **Splits:** per km (run, walk, hike), 5 km (ride, ski), 100 m (swim), 10 km (drive, transit), with moving time, pace, heart rate and climb (3 m dead band).
- **Heart-rate zones:** from the user's own max (98th percentile of their activities' max heart rates, at least 5; else this activity's). Zones start at 60/70/80/90 % of it. Heart rate from the track, another source's track, or health samples, each value counting up to its series' spacing.
- **Drift (aerobic decoupling):** speed per heartbeat, second half of the moving time against the first. Needs 20 minutes of moving time. Under 5 % is steady, 5–10 % drifting, over 10 % fading.
- **Best efforts:**
  - distances: 1, 5, 10 km and the half marathon on foot; 5, 10, 20 and 40 km on a ride
  - ranked against all the user's other efforts of the type
  - efforts are kept in `activity_efforts`, computed when an activity is opened and by the fusion service in the background
- **Compared with you:**
  - **same route:** start and end within 300 m, distance ±10 %, and 80 % of the track within 200 m of the other one
  - rank by speed on the route
  - percentiles of speed and distance among the same type over the last 365 days

Insights add:

- **weekly load:** hours, km, and minutes × intensity, where intensity is average HR ÷ max HR, or 0.6 without heart rate
- **time in zones:** each activity counted in the zone of its average
- **per-sport trends of speed and heart rate:** the last third of the period against the first

## Place details and location lookup (11.4c, built)

- **Coordinates:** a selected visit shows them, with a copy button and links to Google Maps and OpenStreetMap.
- **Identify this location:** `GET /api/atlas/lookup?lat&lon&radius` (MCP `atlas_identify_location`). It asks OpenStreetMap, with no API key needed:
  - **Nominatim:** the street address and the object at the point
  - **Overpass:** named places within 50–250 m (shops, restaurants, sites, offices, parks, bus stops, buildings), with category, distance, address, website, phone, opening hours, brand, cuisine, operator and Wikipedia
  - **Usage policy:** one Nominatim request per second with an identifying User-Agent, answers cached for a day, and partial results when one service fails. The results show "© OpenStreetMap contributors".
- **Associate:** `POST /api/atlas/visits/{id}/place` gives the visit's place the chosen name, address, category, website, phone, opening hours and other details. It also stores a link to the OSM entry (`LinkedRef`, e.g. `osm:node/123`).
  - Every visit to that place shows the new name.
  - A visit without a place gets a new place at the visit's position.
  - The place keeps its own coordinates (where the user was).
- **Place details:** `GET /api/atlas/places/{id}` (MCP `atlas_get_place`) returns the place's fields plus its visit count, first and last visit, and hours spent there.

## Insights, next (later phases)

- **Places:** most visited, new places, routines (weekday and hour), time at home, work and elsewhere.
- **Movement:**
  - per sport: volume, distance and climb trends, personal records
  - favourite routes (track similarity)
- **Body:**
  - heart rate by sport and over time; resting HR trend (when available)
  - HR zones per activity
- **"What could I improve":** consistency, load spikes, recovery, compared
  with my own history (not generic norms).
- **"What I like":**
  - place categories (food, nature, culture); needs Places categories (Google Places API key) or OSM tags
  - repeat visits and dwell time
- **Assistant and MCP:** insight tools (`atlas_insights_places`,
  `atlas_insights_activity`, …) so the assistant and external AI clients can
  answer "where do I eat most on weekends?" over the fused data.

## Phases

| Phase | Content |
| --- | --- |
| 11.1 | Sports Tracker connector (login, list, tracks, sync again, disconnect) |
| 11.2 | Fusion engine: clusters, source priority, conflicts, provenance; runs after imports; search/map/timeline/MCP on clusters |
| 11.3 | Drill-down: sources, conflicts, split/merge/pin; visit links |
| 11.4 | Insights v1: places, movement, body; assistant and MCP tools |
| 11.5 | More sources: Strava / Garmin / Samsung Health exports, calendar and Contacts links, photos |

Phases 11.1–11.3 ship together as the first fusion release.
