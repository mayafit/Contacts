# Epic 7 – Keeping in touch, and Epic 8 – Contacts assistant

Status: in implementation (2026-10-18). Follows Epic 6 in `epic-4-contacts-workspace.md`.

## Epic 7 – Keeping in touch (6.2 / 6.3 from the Epic 6 plan)

Goal: for every contact, when you were last in touch by email or in a meeting, and
who you are most / least in touch with.

### Sources and permissions

| Source | Scope | What is read | What is never read |
|---|---|---|---|
| Gmail | `gmail.metadata` | From, To, Cc, date, SENT label | subject, body, attachments |
| Google Calendar | `calendar.events.readonly` | start time, attendees, own response, title of the last/next meeting | descriptions, attachments |

- Opt-in: a "Connect Gmail & Calendar" button runs Google's consent once
  (`/api/auth/login?activity=true`, incremental, `include_granted_scopes`).
  Sign-in without it keeps asking for contacts only.
- Phone calls, SMS and WhatsApp are not reachable from a web app (no Google or
  Samsung API exposes them). See "Phone calls" below.

### Design

- A background job per user (`ActivityWorker`) builds an activity snapshot and
  keeps it encrypted in Redis (`activity:<google sub>`, Data Protection).
- Gmail: `messages.list` newest first, metadata fetched in batches of 50 under the
  per-user quota; stops at 12 months or 10,000 messages. Later runs read only
  new messages via `history.list`; a full rescan runs weekly or when history expired.
- Calendar: primary calendar, 12 months back to 60 days ahead, `singleEvents`;
  cancelled events and ones you declined are skipped.
- Per email address: last sent / received / any email, emails per month, last
  meeting (+ title), next meeting (+ title), meetings per month. Addresses kept:
  your contacts' addresses, plus anyone you have written to (for "people you
  email who are not in your contacts").
- API: `GET /api/activity/status`, `GET /api/activity`, `POST /api/activity/refresh`,
  `DELETE /api/activity`.
- Frontend: "Last in touch" table column, activity in the contact card, and an
  Insights section: most in touch (12 months), gone quiet (active before, nothing
  for 6 months), never in touch, and frequent correspondents not in contacts.

### Phone calls (not in scope)

Samsung Account / Samsung Cloud has no public API for call or message history.
The workable route is an Android backup app that writes call logs to Google
Drive (e.g. SMS Backup & Restore, XML), which this app could import with a
`drive.file` grant. Not planned until asked for.

## Epic 8 – Contacts assistant

Goal: a chat panel that answers questions about the contacts and proposes changes
that the user approves.

### Design

- Model: Google Gemini with the key GlutenGuardian uses, copied into the
  `secret/contacts` Vault path as `Gemini__ApiKey` (the contacts role stays
  read-only on its own path). Model is configurable, default `gemini-flash-latest`.
- The backend only proxies Gemini (`POST /api/assistant/generate`, signed-in only,
  rate limited, fixed model) and runs grounded web search
  (`POST /api/assistant/search`). The key never reaches the browser.
- The agent loop and its tools run in the browser over the contacts already loaded,
  so answers use the same data the user sees and writes reuse the existing
  etag-checked save paths.
- Read tools: search contacts (text, label, organization, domain, missing fields,
  script, last in touch), get contact, overview statistics, duplicates,
  organization suggestions, web search.
- Write tools only create proposals: update contacts (fields, emails, phones,
  urls, labels), create a contact, move to Trash, open the merge dialog. Each
  proposal shows a diff and is applied only when the user clicks Apply; it is
  written with the etag the proposal was made from, so a contact changed in the
  meantime is refused, not overwritten.
- UI tools: open a contact card, show a set of contacts in the table.
- New backend endpoints the tools need: `POST /api/contacts` (create) and
  `POST /api/contacts/labels` (add/remove label members, label created on demand).
- Privacy: the panel states that messages and the contact details the assistant
  looks up are sent to Google Gemini.

### Later

- Streaming replies; conversation history across sessions; LinkedIn CSV import
  as an assistant tool (Epic 6.5).
