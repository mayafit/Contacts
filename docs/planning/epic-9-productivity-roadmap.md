# Epic 9 – Roadmap: from address book to relationship assistant

Status: proposal (2026-10-19). Builds on Epic 7 (keeping in touch) and Epic 8 (assistant).
Effort: S ≈ a day, M ≈ a few days, L ≈ a week or more.

## 1. What the app gathers today, and why some data is missing

| Gap | Cause | Fix |
|---|---|---|
| Hebrew sender names unreadable or missing | Gmail returns non-ASCII names as RFC 2047 encoded-words (`=?UTF-8?B?…?=`, `=?windows-1255?Q?…?=`) and the scan stored them undecoded | **Fixed in this release** (decoder + tests); the next full scan rebuilds names |
| Most contacts show no "last in touch" | Mail is linked to a contact only by email address; most contacts have only a phone | G1 below: match by name, suggest the address |
| No phone numbers, titles or companies from mail | `gmail.metadata` gives headers only, never the body or signature | G2 below (opt-in, needs `gmail.readonly`) |
| Older relationships invisible | Scan window is 1 year / 10,000 messages | G8: configurable window (e.g. 3 years) |
| Shared and secondary calendars ignored | Only the primary calendar is read | G6 |
| Work mail in another system (Outlook) or a second Google account | One Google account per user | S2, S3 |

Each scan now logs counts (messages, addresses, names, undecoded names, messages without To/Cc), so coverage can be checked without reading anyone's data.

## 2. Gmail and Calendar – recommended enhancements

| # | Enhancement | Value | Scope needed | Effort |
|---|---|---|---|---|
| G1 | **Suggested emails**: for contacts without an email, find directory addresses whose display name matches the contact's name or phonetic name (Hebrew ↔ English); review list → Apply. Activity then flows to those contacts | Very high | none new | S |
| G2 | **Signature enrichment**: for each contact, read the 1–3 most recent messages *from* them, extract phone, title, company, website, LinkedIn with Gemini, and propose updates. Bodies are processed in memory and never stored | Very high | `gmail.readonly` (restricted) | M |
| G3 | **Awaiting reply**: threads where the last message is theirs and older than N days ("you owe a reply"), and threads where you wrote last and got no answer ("follow up") | High | none new (thread id + direction from metadata) | M |
| G4 | **Cadence**: learn each relationship's usual interval and flag overdue ones ("usually every 3 weeks, now 9") | High | none | S |
| G5 | **Meeting prep**: before each meeting, a card per guest – last emails, open threads, notes, how you know them; optional morning digest | High | none new | M |
| G6 | All calendars (calendarList), recurring 1:1 detection, meeting hours as a personal/professional signal | Medium | none new | S |
| G7 | **Relationship graph**: who is copied together → who knows whom, suggested introductions, team clusters per organization | Medium | none | M |
| G8 | Longer, configurable scan window; per-label exclusions (Promotions, Social, mailing lists) to cut noise | Medium | none | S |
| G9 | Personal vs professional: free-mail vs company domain, Gmail category, meeting hours, labels → two views and two "keep in touch" targets | Medium | none | S |

## 3. Other services and accounts

| # | Source | How | Effort |
|---|---|---|---|
| S1 | LinkedIn | Connections.csv export import: company, title, profile URL; flag job changes on re-import | M |
| S2 | Second Google account (work + personal) | Link several Google accounts to one app user; merged directory and activity | L (identity model change) |
| S3 | Microsoft 365 / Outlook | Microsoft Graph: contacts, mail headers, calendar – same pipeline as Gmail | L |
| S4 | Phone calls and SMS | Android "SMS Backup & Restore" to Google Drive (XML); import with `drive.file`; match by phone number | M |
| S5 | WhatsApp | No API; import an exported chat (.txt) per person for last-contact dates | M |
| S6 | Facebook | Export has no friends' contact details – not worth it | – |

## 4. Insights and advice – "who should I contact"

- **Reach out list** (daily/weekly): score = relationship strength × overdue against cadence, plus upcoming birthdays, job changes (S1), unanswered threads (G3), and meetings in the next days (G5). Each item says *why* and offers a one-click draft.
- **Targets**: "keep in touch with my 20 key people monthly", separate for personal and professional (G9), with progress on Insights.
- **Birthday and anniversary reminders** with a drafted greeting.
- **Assistant actions** beyond contacts: draft an email in Gmail (`gmail.compose`), create a reminder or event (`calendar.events`), add a follow-up task (Google Tasks). All stay propose → Apply.
- **Per-contact notes / interaction log** (where you met, family names, topics) – feeds meeting prep and the assistant.

## 5. Productivity features

- Command palette (Ctrl+K) and assistant shortcuts; saved views; bulk edit for any field.
- Undo for applied proposals and merges (keep the previous version for 24 h).
- Export (CSV / vCard) of any view; scheduled cleanup report.
- Mobile layout for the card and the assistant; Hebrew UI.

## 6. Platform: many users

Already per-account: sessions, refresh tokens, activity snapshots and assistant limits are all keyed by the Google account; scans of different users run in parallel (3 at a time); the shared Gemini key has per-user and global daily caps.

What limits the number of users is **Google's OAuth verification**, not the code:

| Stage | Who can sign in | Notes |
|---|---|---|
| Testing (today) | Up to 100 test users added by hand | Sign-in expires every 7 days |
| Production, unverified | Anyone, with Google's "unverified app" warning | Lifetime cap of 100 users for unapproved sensitive/restricted scopes; no 7-day expiry |
| Verified | Anyone | Contacts and Calendar are *sensitive*: brand verification, privacy policy, demo video. Gmail scopes are *restricted*: also a yearly CASA security assessment |

Recommendation: move to **Production now** (ends the weekly re-consent); add a privacy policy and terms page; submit verification for contacts + calendar; keep Gmail features opt-in and plan the CASA assessment only if the Gmail features (G2 especially) prove worth it.

Also needed for real multi-user operation: durable per-user storage in Postgres (Redis holds everything today), per-user data export and delete, a usage dashboard (scans, assistant calls and cost), and CI that builds the images on merge.

## 7. Suggested order

1. G1 suggested emails, G4 cadence, reach-out list (no new permissions, biggest visible gain)
2. G3 awaiting reply, G5 meeting prep
3. Production publishing + privacy policy; Postgres for user data
4. S1 LinkedIn import, G9 personal/professional
5. G2 signature enrichment (decide on `gmail.readonly` / CASA)
6. S2 multiple accounts, S3 Outlook
