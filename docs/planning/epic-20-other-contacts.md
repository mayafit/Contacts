# Epic 20 — Suggestions from Other contacts

> Vision: the addresses Gmail collected on its own complete the Owner's Contacts,
> one reviewed suggestion at a time.

Requirements: PRD FR-92 (`echo/prd.md` §4.21). Architecture: AD-48
(`echo/architecture.md` §5I).

## Why

On 2026-10-31 the Owner pointed at Google Contacts' **Other contacts** (people Gmail
saves when the Owner writes to someone not in their Contacts) and asked to use it to fill
in missing details. Its names are often reversed, partial or addresses, and many entries
are relays and no-reply senders, so nothing is merged without the Owner's review.

## Stories

### 20.1 Reading Other contacts — Phase 1

- [x] The opt-in scope `contacts.other.readonly` (`?other=true`, AD-48 rule 1), with
  tests of the scope list.
- [x] `OtherContactsReader` and `GET /api/other-contacts` (rules 2, 3), with tests of
  the automated-address filter and the entry mapping.
- [x] `OtherContactsStore` and `/api/other-contacts/dismissed` (rule 7), with tests.

### 20.2 The suggestions — Phase 1

- [x] `buildOtherSuggestions` (rules 4, 5), with tests of every match step, the
  ambiguity stop, Trash, and the save list.
- [x] Insights → **Other contacts** → the review dialog: allow, tick, apply, dismiss,
  show dismissed (rule 6).

### 20.3 Deployment — Phase 1

- [ ] Merged, images built for arm64, rolled out; checked on the Owner's Contacts.
