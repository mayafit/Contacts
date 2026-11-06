# Epic 15 — Flights: each flight a real flight

> Vision: every flight in Atlas shows which flight it was (number, airline, aircraft),
> its airports, its scheduled and actual times, and the route it really flew, found
> in the Owner's mail or typed by them, and checked against public flight data.

Requirements: PRD FR-72 to FR-76 (`echo/prd.md` §4.15). Architecture: AD-42
(`echo/architecture.md` §5D).

## Why

On 2026-10-30 the Owner asked Echo to find their flights' details in their mail, to let
them type a flight number, and to show the actual flight from public sources. Atlas
holds about 100 flights (2014 to 2026, all from the Timeline). Each is drawn as an arc
between the phone's last point before take-off and its first point after landing, with
the phone's guessed times.

A check on the first Owner's data on 2026-10-30:
- **OpenSky** (the operator's account) returns, for a departure airport and a few
  hours, every flight with its call sign, aircraft, first and last seen and estimated
  arrival airport, back to at least 2020 (thinner further back; nothing at some
  airports).
- **Public data alone is ambiguous.** On 27 Aug 2026 two Etihad flights left AUH for TLV
  within 20 minutes of the recorded take-off, and BKK to CNX had three airlines.
- **The mail is decisive.** An airline's booking confirmation lists every flight of the
  trip with its number, both airport codes, the local date and times, and the aircraft.
- **Call signs are not always flight numbers.** EY593 flew as `ETD5SM`, so matching
  falls back to the route and the scheduled time.

## Stories

### 15.1 Flights, airports and flight numbers — Phase 1

- [x] `atlas.flights` (migration `Flights`), one row per primary flight activity,
  moved with fusion's primary; statuses of AD-42 rule 1.
- [x] `airports.csv` (3,697 airports with scheduled service, with time zones) and
  `Airports.Near` (50 km, 3 nearest); "probably not a flight" and "no airport".
- [x] `FlightNumbers.Parse` (IATA and ICAO forms) and the call sign match.

### 15.2 Find the flight in the mail — Phase 1

- [x] `GmailFlightReader`: the search by the airports' codes over a year, 20 newest,
  each message parsed once per round.
- [x] `FlightMail.Parse` (schema.org `FlightReservation`, then flight tokens with their
  windows) and `FlightMail.Match` (airports in order, date, time window, score).
- [x] Without email content: `not_allowed`, and the T2 opt-in returning to Atlas
  (`back=atlas`); searched again once allowed.

### 15.3 Public flight data — Phase 1

- [x] `OpenSkyClient` (client credentials from Vault, departures, tracks, the allowance
  kept above 300 credits) and `AdsbdbClient` (airline, call sign, aircraft).
- [x] `FlightMatch`: by call sign, by adsbdb's call sign, by route and time; suggestions
  without a number; operated-by for codeshares.
- [x] The flown path for flights that landed within 29 days, simplified to 500 points.

### 15.4 The worker and the API — Phase 1

- [x] `FlightWorker`: hourly rounds, rows inserted and moved, 25 per Owner per round,
  newest first; claims; retries of passing failures (5).
- [x] `/api/atlas/flights/{activityId}`: GET, PUT (type a number), DELETE `/number`,
  POST `/search`, POST `/confirm`.

### 15.5 The flight panel and the map — Phase 1

- [x] `FlightSection.tsx`: identity and source, the input, route, scheduled and actual
  times in local time, aircraft, call sign, operator, distance, path note, suggestions,
  status lines, sources; polling while pending.
- [x] `AtlasMap` draws the selected flight along its track, or the great circle between
  its airports.

### 15.6 Deployment — Phase 1

- [ ] The OpenSky credentials in Vault (`OpenSky__ClientId`, `OpenSky__ClientSecret`).
- [ ] The first Owner's flights looked up; a sample checked against their mail.
