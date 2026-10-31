# Epic 19 — Scan period and a Contact's whole history

> Vision: the Owner decides how far back the activity looks, and can ask, for one
> person, everything their mailbox and calendar hold, without a date limit.

Requirements: PRD FR-90, FR-91 (`echo/prd.md` §4.20). Architecture: AD-47
(`echo/architecture.md` §5H).

## Why

On 2026-10-31 the Owner found a Contact they last wrote to in 2016 labelled "Never in
touch" (the activity reads 12 months), and asked to set the scan period in Settings and
to search a Contact's history from their card without a time limit.

## Stories

### 19.1 Scan period — Phase 1

- [x] `activity-window:{user}`, `PUT /api/activity/window`, `windowYears` in the status;
  the worker's period and cap (AD-47 rules 1, 2), with tests.
- [x] Settings → **Email and calendar**: the period, saved at once, the rescan explained.

### 19.2 A Contact's whole history — Phase 1

- [x] `ContactHistoryReader` (rules 4, 5), `/api/activity/history` and the store
  (rules 3, 6), with tests of the queries, the address filter and the meeting rules.
- [x] The dossier's **Search all history** (rule 7), with tests.

### 19.3 Deployment — Phase 1

- [ ] Merged, images built for arm64, rolled out; checked on the Owner's Contacts.
