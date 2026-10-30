# Epic 16 — Auto-fill in the contact card

> Vision: a Hebrew Contact's phonetic name and a Contact's missing email are one
> click away in the contact card, spelled the way the Owner already spells names
> and found in the Owner's own mail.

Requirements: PRD FR-77 and FR-78 (`echo/prd.md` §4.16). Architecture: AD-43
(`echo/architecture.md` §5E).

## Why

On 2026-10-30 the Owner asked for an auto-fill symbol in an empty phonetic name field
that translates the Hebrew name into English, and for a button in an empty email field
that finds the address by searching the mailbox for mail from the person.

What Echo already has:
- **Phonetic names** are filled only by a merge with an English twin (FR-10) or by
  the Assistant, one Contact at a time, by asking.
- **Suggested emails** (FR-14) match the activity scan's names for many Contacts at
  once, from Insights; nothing in the card.
- The Owner's Contacts already spell most Hebrew names in their phonetic fields, so a
  dictionary built from them follows the Owner's own conventions; Gemini (already
  used by the Assistant) spells the rest.

## Stories

### 16.1 Phonetic name auto-fill — Phase 1

- [x] `model/phonetic.ts`: words (rule 1), the twin (rule 2), the dictionary
  (rule 3) and the fill plan (rule 5), with unit tests (twin agreement, word pairing,
  hyphens, ties, half-spelled parts).
- [x] `POST /api/assistant/transliterate`: the word check, the limits, the prompt
  and schema, the answer's check (rule 4), with unit tests of the check.
- [x] `ContactForm`: the symbol in empty phonetic fields with a Hebrew name part,
  the spinner and the caption; the session cache.

### 16.2 Find the email in the mail — Phase 1

- [x] `MailSenders`: the Gmail query (stripped names), the `From` header parse and
  the grouping (rule 7), with unit tests.
- [x] `POST /api/activity/find-email`: directory and mailbox, own addresses removed,
  merge and order, the `mail` state, with unit tests of the merge.
- [x] `model/emailFind.ts` and `ContactForm`: the symbol in empty email fields,
  **Find in mail**, fill at once or the menu, "already on", the address type, the
  captions and the T2 link (rule 8).

### 16.3 Deployment — Phase 1

- [ ] Merged, images built for arm64, rolled out; checked on the Owner's Contacts.
