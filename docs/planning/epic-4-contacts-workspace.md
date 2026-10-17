# Epic 4 — Contacts Workspace

| Version | Date       | Author | Change |
|---------|------------|--------|--------|
| 1.0     | 2026-10-18 | Alon (with Claude) | Initial epic, from first hands-on use of the deployed app |

> The original PRD, architecture and epics 1–3 lived in the gitignored
> `_bmad-output/` and were lost. Planning docs now live here, in git.

## Why

The first real login (2026-10-17) worked, but the app showed only name, phone
and email, in an oversized, plain table. The core goal of the product — seeing
and fixing **phonetic names** — was not visible at all, edits could not be
saved (the backend had no write path and only a read-only scope), and there was
no way to understand or clean up the address book.

## Stories

### 4.1 Full contact model, safe writes (backend)
- Fetch names (incl. phonetic given/middle/family), nicknames, phones, emails,
  addresses, organizations, birthdays, URLs, notes, relations, custom fields,
  photos, label memberships, update time and **etag**.
- `PUT /api/contacts/{resourceName}` takes the etag plus only the field groups
  being changed; `updatePersonFields` is exactly those groups, so untouched
  groups can never be cleared.
- Stale etag → **412** `CONTACT_CONFLICT`; missing scope → **403**
  `INSUFFICIENT_SCOPE`.
- `GET /api/contacts/groups` returns label names.
- OAuth scope `contacts` (read/write). If the user unticks it on Google's
  consent screen, login ends at `/login?error=missing_scope` with an explanation.

**AC:** editing one field of a contact leaves every other field unchanged in
Google Contacts; a concurrent edit yields 412, not a silent overwrite.

### 4.2 App shell and visual redesign
- Dedicated Contacts theme (compact type scale, dark surfaces, one accent),
  independent of the host template's 160%/25px font settings.
- Top bar: app name, global search, refresh, account menu with sign-out.
- Views: **Contacts**, **Insights**, **Duplicates** (with a count badge).

### 4.3 Contacts table
- Default columns: Name (avatar + name), **Phonetic name**, Phone, Email,
  Organization, Labels. Column config still available; existing saved layouts
  gain the Phonetic name column once.
- Search across name, phonetic, nickname, phone (digits-only match), email, org.
- Filter chips: All · Missing phonetic · No phone · No email · Has duplicates;
  label filter.
- Click a header to sort; Hebrew and Latin names sort with locale collation.
- **Alphabet rail** (A–Z, א–ת, #) jumps to the first contact for that letter
  when sorted by name.

### 4.4 Inline editing that fits the table
- Single click edits the cell in place at the same font, size and row height.
- Enter saves and moves down, Tab saves and moves right, Esc cancels.
- Editable inline: Name, Phonetic name, primary Phone, primary Email,
  Organization, Job title.

### 4.5 Contact card
- Double-click any row opens a side card with every field, editable: name parts,
  phonetic parts, nickname, phones/emails/addresses/URLs (add/remove, type),
  organization, birthday, notes, relations, custom fields; labels and photo
  shown read-only.
- Save writes only the changed groups; conflict shows a reload option.

### 4.6 Insights
- Tiles: total contacts, % with phonetic name, % with phone, % with email,
  duplicate groups.
- Field completeness bars; phone origin (Israeli mobile, Israeli landline,
  international, unparseable); name script (Hebrew, Latin, mixed, none);
  top labels; birthdays by month.
- Clicking a "missing" bar opens the Contacts view with that filter applied.

### 4.7 Duplicate finder
- Group contacts that share a normalized phone number (last 9 digits), an email
  (case-insensitive) or an identical normalized name.
- Each group shows why it matched and the key fields side by side; open any
  member's card to fix it.
- Out of scope here: automatic merge and delete (Google has no merge API; it
  needs a field-by-field merge design — candidate for Epic 5).
