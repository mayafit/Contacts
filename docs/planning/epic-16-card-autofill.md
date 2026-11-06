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

### 16.4 The same fills in the table — Phase 1

On 2026-10-30, after 16.1 and 16.2 were live, the Owner asked for the fills in the
table's cells too, when a cell is edited and its text box appears.

- [x] `EditableCell` `assist`: the button inside an empty text box, the menu, focus
  kept so the click does not save (AD-43 rule 10).
- [x] `cellAssist.tsx`: the Phonetic name and Email columns, through the card's
  `spellPhonetic` and `lookUpEmail`; a new address typed by its domain.

### 16.5 Google Translate before Gemini — Phase 1

On 2026-10-30 the Owner preferred a translation service to Gemini (faster, no model
tokens). Google Translate translates a name alone as words ("dawn dew" for טל שחר);
framed as "שמי …" ("My name is …") it spelled 10 hard names right.

- [x] `NameTranslator`: Cloud Translation v2 with the frame, the alignment
  (as many words as the name, Latin only), the key in a header (AD-43 rule 4).
- [x] `POST /api/assistant/transliterate {words, name}`: Translate first, Gemini for
  the words left; sources in the reply; the caption names Google Translate.
- [ ] The operator enables the Cloud Translation API on a Google Cloud project and
  puts its key in Vault (`secret/contacts`, `GoogleTranslate__ApiKey`). Until then
  the fill uses Gemini.

### 16.3 Deployment — Phase 1

- [ ] Merged, images built for arm64, rolled out; checked on the Owner's Contacts.
