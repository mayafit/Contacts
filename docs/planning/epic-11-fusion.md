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

## Insights (later phases)

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
