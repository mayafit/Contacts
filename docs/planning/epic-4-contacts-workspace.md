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

---

# Epic 5 — Clean-up: delete and merge

| Version | Date       | Author | Change |
|---------|------------|--------|--------|
| 1.0     | 2026-10-18 | Alon (with Claude) | Requested after first use of Epic 4 |

### 5.1 Delete contacts
- Checkbox on every row; header checkbox selects all visible (filtered)
  rows; shift-click selects a range.
- Selection bar: "N selected · Merge · Delete · Clear".
- Delete from the selection bar, from the contact card and from a
  duplicate group; always behind a confirmation listing who is deleted.
- API: `DELETE /api/contacts/{rn}`, `POST /api/contacts/batch-delete`.

### 5.2 Merge contacts
- From a duplicate group or from 2+ selected rows. The user picks the
  contact to keep and can leave members out; a preview shows the result.
- Merge rules: keep the main contact's name, fill its empty name and
  phonetic parts from the others; union phones (same number in any
  format counts once), emails, addresses, websites, relations, custom
  fields, nicknames, organizations and labels; keep the main birthday or
  take another's; join distinct notes.
- API: `POST /api/contacts/merge` updates the main contact (etag-checked)
  first, adds labels, and only then deletes the others.
- **Data-loss guard:** before deleting, the API re-reads the contacts to
  delete including fields the app does not merge (photo, events, IM,
  SIP, locations, occupations, interests, skills, keywords, external IDs,
  calendar URLs, gender). If any is present it refuses with a list, and
  the user must confirm the loss explicitly.

**AC:** a merge never deletes a contact unless the merged contact was
saved; nothing is lost silently.

---

# Epic 6 — Relationship insights and enrichment

| Version | Date       | Author | Change |
|---------|------------|--------|--------|
| 1.0     | 2026-10-18 | Alon (with Claude) | Requested after Epic 4; feasibility checked per item |

### 6.1 Organization from work email — feasible now
- Group contacts by email domain, ignoring free-mail providers (gmail,
  outlook/hotmail/live, yahoo, icloud, walla, bezeqint, netvision, 012,
  protonmail, ...).
- Per corporate domain: its contacts, the organization each has, and a
  suggested organization name (the most common name already used for that
  domain, else one derived from the domain, e.g. elta.co.il → "Elta").
- Flag contacts whose organization is missing or different; apply the
  suggestion one by one or for the whole domain (writes only the
  organizations group).

### 6.2 Last contact and activity — feasible for email and meetings only
- Email: Gmail API with the `gmail.metadata` scope (headers only, never
  bodies): last message to/from each contact address, and message count
  in the last 12 months. This is a Google "restricted" scope: it works for
  the account owner while the OAuth app is in Testing mode; publishing it
  for other users needs Google's security assessment.
- Meetings: Calendar API (`calendar.readonly`): last and next event with
  the contact as attendee.
- **Not possible from a web app:** phone calls, SMS and WhatsApp. Google
  exposes no API for the Android call log or messages, and WhatsApp has no
  API for personal chats. That would need a companion Android app reading
  the device call log (separate epic, if wanted).

### 6.3 Most and least active contacts — depends on 6.2
- Insights: top contacts by recent activity; contacts not in touch for
  1y / 3y / never; filter chips linking to them.

### 6.4 Look someone up — feasible now
- Contact card: one-click searches (Google, LinkedIn people search,
  Facebook) prefilled with name and organization, opening in a new tab.
- Optional later: automatic web suggestions through Google Programmable
  Search (needs an API key; 100 free queries/day), shown as suggestions,
  never applied automatically (common names give wrong matches).

### 6.5 LinkedIn / Facebook cross-reference — via data export, not API
- LinkedIn's API only returns the signed-in member's own profile;
  connections' profiles are limited to approved partners. Facebook's
  Graph API has not exposed friends' data since 2018. Scraping either
  violates their terms, so the app will not do it.
- Feasible route: the user downloads their own data (LinkedIn → Settings →
  Data privacy → "Get a copy of your data" → Connections.csv with name,
  company, position, connected-on and, when shared, email). Upload it;
  the app matches connections to contacts by email, then name, and
  suggests organization/title updates for review.
