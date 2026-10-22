# Epic 10 – Personal site and Atlas

Status: in implementation – phase 1 (2026-10-22).

## Goal

Turn the Contacts app into a personal site with areas. Contacts stays one area. A new
area, **Atlas**, brings together everything that happened *where* and *when*: Google
Timeline (places visited, trips), Sports Tracker workouts (rides, hikes, runs), and
health data (heart rate and more from Google Fit / Fitbit). Everything is shown on a
world map with a timeline under it, can be searched by time and place together, and is
available to AI through MCP.

Example questions it must answer:
- What was my heart rate during the ride on 3 Sep?
- When was I last in Haifa, and did I ride there?
- Which hikes did I do in the north in 2024, and how long were they?

The site keeps its address (contacts.mayaffit.com) and the same Google OAuth client and
Cloudflare setup.

## Site structure

- **Shell**: a left rail (bottom bar on phones) to switch areas: Contacts, Atlas. The
  account menu and sign-in/out are shared. Routes: `/contacts`, `/atlas`; `/` opens the
  last area used.
- **Contacts**: unchanged, moved under `/contacts`.
- **Atlas**: world map (top) + timeline (bottom) + results / details panel + search +
  assistant + import.

## Data model (PostgreSQL, the chart's existing `contacts-postgresql`)

All rows belong to one Google account (`owner_id` = Google `sub`). Every imported row
records its `source`, `external_id` (stable id from the source, used to upsert, so
re-importing the same file never duplicates) and `import_id` (the batch, so an import can
be undone).

| Table | What | Key fields |
|---|---|---|
| `activities` | A movement with a track: ride, hike, run, walk, drive, flight, … | type, name, start/end, distance, duration, elevation gain, avg/max heart rate, bounding box, simplified polyline |
| `track_points` | The track of an activity | activity, time, lat, lon, elevation, heart rate, speed, cadence |
| `places` | A named place | name, address, lat/lon, category, Google place id |
| `visits` | Being at a place for a while | place, start/end, lat/lon |
| `health_samples` | A health measurement over time | type (heart_rate, steps, …), start/end, value, unit |
| `imports` | One import run | source, file name, counts, created |
| `access_tokens` | Personal access tokens for MCP clients | hashed token, name, created, last used |

Geography without PostGIS (not available for the chart's image on arm64): each activity
keeps a bounding box, so "within this area" is an indexed box test, refined on points when
needed. Time is indexed everywhere. At personal scale (years of data, millions of heart
rate samples) this is enough; PostGIS can replace it later without changing the API.

## Import

Parsing runs **in the browser**: Google exports are large (a Timeline file can be hundreds
of MB, a Takeout zip several GB), above Cloudflare's 100 MB request limit and the Pi's
memory. The browser reads the zip or files, turns them into the model above, and sends
them in batches of a few thousand items to `POST /api/atlas/ingest`.

| Source | Files | Becomes |
|---|---|---|
| Google Timeline (on-device export, 2024+) | `Timeline.json` (`semanticSegments`) | visits + places, activities with `timelinePath` tracks |
| Google Timeline (older Takeout) | `Semantic Location History/YYYY/*.json` (`timelineObjects`) | visits + places, activities with waypoints |
| Sports Tracker | workout `.gpx` (with `gpxtpx:hr`) or `.tcx` exports | activities with full tracks and heart rate |
| Google Fit (Takeout) | `Fit/All Data/*heart_rate*.json`, `Fit/Activities/*.tcx` | heart-rate samples, activities |
| Fitbit (Takeout) | `Global Export Data/heart_rate-*.json` | heart-rate samples |
| Any GPX / TCX | `.gpx`, `.tcx` | activities |

## API and MCP

One service layer (`AtlasService`) behind two doors:

- **REST** (`/api/atlas/...`) for the web app, with the Google session.
- **MCP** (`/mcp`, streamable HTTP, official C# SDK) for AI clients, authorized with a
  **personal access token** created in the app (Account → AI access). Tools cover search
  (time, area, text, type), get activity with its track and heart rate, health in a time
  range, visits near a point, summary, and create / update / delete for activities,
  places, visits and imports.
- Remote MCP clients reach the site through Cloudflare Access with a service token (sent as
  headers) plus the personal access token; no Cloudflare change is needed for that.

## UI

- **Map**: Leaflet with dark CARTO tiles; activities as coloured tracks by type, visits as
  dots; draw a rectangle to search an area; fit to results.
- **Timeline**: a histogram of activity, visits and health data over time; drag to select a
  range, scroll to zoom. The selection drives the map and the results.
- **Results / details**: the list of matches; an activity shows distance, duration,
  elevation and a heart-rate chart along the ride (from its own track, or from health
  samples in the same minutes).
- **Search**: text (names, places) combined with the map and timeline selections.
- **Assistant**: the same panel as Contacts, with Atlas tools (search, details, health,
  show on map, set the time range).

## Phases

1. **Foundation (this release)**: shell with areas; Postgres schema and migrations; ingest,
   query and CRUD API; MCP server with personal access tokens; importers for GPX, TCX,
   Google Timeline (both formats), Google Fit and Fitbit heart rate; Atlas page with map,
   timeline, results, activity details with heart rate, import dialog, assistant.
2. Places enrichment (reverse geocoding), photos from Google Photos at a place and time,
   elevation profiles, comparisons between rides.
3. Health beyond heart rate (sleep, steps, weight), daily summaries, trends.
4. Links between areas: who you met where (Calendar + Timeline + Contacts).
