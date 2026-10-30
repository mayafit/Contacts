# Epic 13 — Atlas place reviews: what I wrote about the places I went to

> Vision: every place in Atlas says whether the Owner reviewed it on Google Maps,
> with the stars and the words they wrote, and Atlas reminds them, briefly, of the
> places worth a review that they have not reviewed yet.

Builds on Epic 10 (`epic-10-personal-atlas.md`: import, places, the place panel,
Insights). Requirements: PRD FR-64 to FR-67 (`echo/prd.md` §4.13). Architecture:
AD-39 and AD-40 (`echo/architecture.md` §5B).

## The data (first Owner, 2026-10-30)

| | Count |
| --- | --- |
| Places | 4,403 (all from the Timeline, all with a Google place id) |
| With a real identity (auto or by the Owner) | 393 |
| Of those, with a visit of 10 minutes or more in the last 180 days | 43 |
| Visited on more than 30 days (routine) | 18 |

Google has no API for a person's own reviews; the Takeout export
`Maps (your places)/Reviews.json` is the source. Its fields follow Google's Data
Portability schema `maps.reviews`; it holds no photos.

## Stories

### 13.1 Import my reviews — Phase 1

As the Owner, I want my Google Maps reviews imported from Takeout, so that Echo
knows what I wrote about the places I went to.

Acceptance criteria (FR-64, AD-39 rules 1 and 2):
- [x] `parseGoogleReviews` reads `Reviews.json` (by name, or a `FeatureCollection`
  with review fields), alone or in the Takeout zip: published values first, then
  drafts; the answers flattened; features with neither stars nor text skipped.
- [x] `atlas.place_reviews` (migration `PlaceReviews`), keyed by the `cid` of the
  review's Google Maps link; a newer import updates, never deletes; deleting the
  import deletes its reviews.
- [x] The import reports the number of reviews.

### 13.2 My review on each place — Phase 1

As the Owner, I want to see on each place whether I reviewed it and what I wrote.

Acceptance criteria (FR-65, AD-39 rules 3, 4 and 7):
- [x] Linking: a shared word within 75 m, else the only place within 30 m, else a
  clear nearest within 10 m (no other within 25 m), else unlinked; one review per
  place; run after imports of reviews and of places; links kept until the place goes.
- [x] A review linked by position names an unnamed place the Owner did not name.
- [x] The place panel shows the review (stars, date, text, answers, draft, Open on
  Google Maps).
- [x] `GET /api/atlas/reviews` and the Insights **Reviews** section (written, average,
  linked, latest).

### 13.3 Reminders to review — Phase 1

As the Owner, I want to be reminded of the places I went to and have not reviewed,
and to set reminders aside.

Acceptance criteria (FR-66, AD-39 rules 5 and 6):
- [x] Due: identified and named, not an excluded category, a visit of 10 minutes or
  more in the last 180 days, 30 visit days or fewer, no review, no decision; the most
  recent first, 30 shown with the total.
- [x] `atlas.review_reminders`: Later (30 days), Not this one, Not this kind, I
  reviewed it, and Remind me again.
- [x] The Insights list and the place panel show the reminder with **Write a review**
  (the Maps URLs link with the Google place id).

### 13.5 Review fixes — Phase 1

- [x] Links by name before links by position (two passes); a taken place still counts
  as there and is never taken again; home and work are never linked.
- [x] Only http and https links kept (browser and server); a missing date stays none;
  `[0, 0]` positions and non-text addresses skipped; localized Maps folders read.
- [x] A concurrent choice stored once; a check constraint on the decisions; the
  overview reads counts from small columns and shows a linked review at its place.

### 13.6 Reviews from a scheduled Takeout on Drive — Phase 1

As the Owner, I want my new reviews to arrive by themselves, so I schedule Takeout to
Drive once and Echo picks up each export.

Acceptance criteria (FR-67, AD-40):
- [x] The opt-in `takeout` adds `drive.readonly` to the Google sign-in, like the Gmail
  and Calendar opt-ins; declining leaves the sign-in as it was.
- [x] `atlas.connections` row `google-drive-takeout`: connect (only with the scope
  granted), status, check now, disconnect.
- [x] The daily check: Takeout zips only, new ones oldest first, over 500 MB skipped,
  streamed to `/tmp` and deleted, only the reviews file read (at most 20 MB), imported
  as its own import, each export once.
- [x] Lost access marks the connection expired; a renewal note after 70 days.
- [x] The import dialog's Google Drive tab, with the steps to schedule Takeout.
- [x] Tests: the parser (C#) on a Takeout-shaped zip; the check against a fake Drive
  (new, seen, oversized, no reviews, lost access); the opt-in scopes; the tab (Jest).

### 13.7 Takeout pickup review fixes — Phase 1

- [x] Only the Owner's own files; `.zip` by name, no MIME filter.
- [x] A check claims its connection (one at a time, also across pods) and always ends
  with a status; dead claims reset at start.
- [x] Refused sign-in (expired) told from Google unreachable (error); a 403 on one
  file is that file's note; passing failures retried, lasting ones seen.
- [x] The first check takes only the newest export; new reviews counted, not all.
- [x] Download time limit, bounded zip entry read, `/tmp` capped at 1 GiB.
- [x] The tab polls after Check now and connect, and connects once after the sign-in;
  the opt-in is refused while signed out.

### 13.4 Tests — Phase 1

- [x] Pure: the parser (published, draft, answers, the `location` array form, skips,
  unsafe links, no date, `[0, 0]`, localized folders), linking (each rule, the mall
  case, naming, home and work, taken places, the name pass), eligibility (each rule),
  the keys, safe links.
- [x] Postgres: the upsert and its key, linking after reviews and after places, the
  place read, the overview, the decisions, a deleted place, the two-Owner isolation.
- [x] Jest: the parser on a Takeout-shaped file, the panel block and its choices, the
  Insights section.

## Later

- Review photos, if the Owner's export holds them as files (FR-64).
- The Assistant and MCP tools reading reviews ("what did I think of that place?").
- Review text in the Atlas search.
- A star on reviewed places on the map.

## Coverage

| Requirement | Stories |
| --- | --- |
| FR-64 Import my reviews | 13.1, 13.4 |
| FR-65 My review on each place | 13.2, 13.4 |
| FR-66 Reminders to review | 13.3, 13.4 |
| FR-67 Reviews from a scheduled Takeout on Drive | 13.6 |
