# Epic 17 — Favourites, labels and segments

> Vision: the Owner's favourites are one list everywhere (Echo, Google Contacts, their
> phones), easy to find and to keep close; their Contacts divide into labels Google
> knows, rules Echo keeps current, and groups Echo finds for them.

Requirements: PRD FR-79 to FR-84 (`echo/prd.md` §4.17, §4.18). Architecture: AD-44,
AD-45 (`echo/architecture.md` §5F). No language model is used (the Owner's decision,
2026-10-31).

## Why

On 2026-10-31 the Owner asked for favourites synced with Google Contacts' favourites
(mark, filter, "any other functionality"), and for group segmentation of their
Contacts, with traditional methods, not Gemini.

What Echo had:
- Google's Starred group was accepted by the backend as a label to add or remove, but
  nothing in the UI showed or set it.
- Labels could be filtered (one at a time) and set by the Assistant; not created,
  renamed or deleted; not edited in the card or for selected rows.
- No sync with Google after loading: a change in Google showed after a reload only.

## Stories

### 17.1 Favourites — Phase 1

- [x] `changeMembership` and `refreshContacts` (`labelThunks.ts`), `membershipChanged`
  (AD-44 rule 2), with tests (optimistic change, undo on failure).
- [x] The star in the table's Name cell, the phone list and the card's header; the
  selection toolbar's Add to / Remove from favourites; the Favourites chip.
- [x] `useLabelSync` and `GET /api/contacts/groups/members` (rule 3), with tests of
  the reconcile (changed, new Contact, untouched system labels).
- [x] The Favourites card: gone quiet and suggested (rule 6), with tests.
- [x] The Assistant's `favourite` search filter.

### 17.2 Labels — Phase 1

- [x] Create, rename, delete (`/api/contacts/groups`, AD-45 rule 1), with tests of
  the guards (system, Trash, duplicate, length).
- [x] The card's label editor and the toolbar's **Label…** (rule 2); **Manage labels…**.

### 17.3 Segments — Phase 1

- [x] `model/segments.ts`: rules and evaluation, tiers, organization normalization,
  calling codes (rules 3 to 6), with tests.
- [x] `OwnerSettings` kind `segments`; the segment menu, the editor with its live
  count, the linked label's difference and **Update label** (rules 7, 8).
- [x] Suggested segments in Insights (rule 9), with tests.

### 17.4 Deployment — Phase 1

- [ ] Merged, images built for arm64, rolled out; checked on the Owner's Contacts.
