# Epic 18 — Public profiles and the dossier

> Vision: each Contact's card opens with a dossier, their public profiles, what they
> say about themselves, the relationship and what the Owner shares with them, built
> from exact matches and public, keyed sources, never from guesses.

Requirements: PRD FR-85 to FR-89 (`echo/prd.md` §4.19). Architecture: AD-46
(`echo/architecture.md` §5G). No language model is used.

## Why

On 2026-10-31 the Owner asked for automatic mapping and syncing of public social
profiles, shared context, or digital dossiers, as in Nimble, with traditional methods.

Checked on 2026-10-31:
- **Gravatar's** public API (v3) answers by an email's SHA-256 hash, without a key at
  100 requests an hour (`X-RateLimit-Limit: 100`), with the person's verified accounts
  (X, LinkedIn, GitHub, Instagram…), job title, company and location.
- **GitHub's** user search by email found nothing for an address whose Gravatar lists
  GitHub (only public emails match), at 10 searches a minute: not used.
- LinkedIn and Facebook offer no lookup by email or name to third parties, and forbid
  scraping: Echo links to their search pages (FR-08) and recognises their profile links.

## Stories

### 18.1 Profiles on file and the dossier — Phase 1

- [x] `classifyUrl` and `profilesOf` (AD-46 rules 1, 2), with tests of every network
  and the exclusions.
- [x] `model/dossier.ts` (rule 6): colleagues, segments, relationship, with tests.
- [x] `Dossier.tsx` at the top of the card; **Add to contact** into the draft.

### 18.2 Gravatar — Phase 1

- [x] `GravatarClient` (the call, the profile's fields, the rate-limit headers),
  `ProfileStore`, `ProfileQueue`, `ProfileWorker` (rule 3), with tests of the parse,
  the queue (30 days, no duplicates) and the pacing.
- [x] `ProfilesController`: enable, hashes, read, turn off and forget.
- [x] The Profiles card and **Review links** (rules 5, 7).

### 18.3 Signatures — Phase 1

- [x] `POST /api/profiles/signatures` and `SignatureLinks` (rule 4), with tests of
  the extraction and of the website filter.

### 18.4 Deployment — Phase 1

- [ ] Merged, images built for arm64, rolled out; checked on the Owner's Contacts.
