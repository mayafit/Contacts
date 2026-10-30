---
title: Echo
status: draft-for-owner-review
created: 2026-10-28
updated: 2026-10-28
---

# PRD: Echo

*Working title chosen by the owner: "Echo, an Echo of Me". It is a brownfield PRD over the code at `dev @ 1e05abc` (2026-10-28).*

## 0. Document Purpose

This PRD defines **Echo**, a personal intelligence system. Each of its users is the **Owner** of their own data, and no user sees another's (§2.2). Echo began with one Owner and, since the Owner's answer to Q-1 (2026-10-28), serves a small invited circle. It is written for the first Owner, who is also the admin and decides scope, and for the downstream `bmad-architecture` and story workflows.

Echo is not greenfield. Two Areas already run in production at https://contacts.mayaffit.com: **Contacts** (Epics 4–8) and **Atlas** (Epics 10–11). The PRD therefore describes Echo as one product, and it gives every functional requirement a **Status**:

| Status | Meaning |
|---|---|
| **Built** | Runs in production today. Verified against the as-built inventories [contacts-as-built.md](contacts-as-built.md) and [atlas-as-built.md](atlas-as-built.md) (in this folder). |
| **Planned** | Already in an epic document under `docs/planning/` (Epics 4–11), or scheduled by an Owner decision recorded in §11, but not built. |
| **Proposed** | New in this PRD, or only a one-line intent in an epic: mostly the Echo **Connect** layer, which joins people, places, time and photos, plus the platform realignment. Where an epic already named the intent, the FR cites it as its origin. |

The rest of this document works as follows:
- The Glossary (§3) fixes the vocabulary.
- Features (§4) nest globally numbered requirements, FR-01 to FR-76. FR-51 to FR-54 were added after the Owner's answers of 2026-10-28, so they sit in the section they belong to, not in id order. FR-55 to FR-60 (route quality, §4.12) follow them. FR-61 (roads travelled), FR-62 (rebuild from similar drives) and FR-63 (mark the way) were added to §4.12 on 2026-10-29. FR-64 to FR-67 (place reviews, §4.13), FR-68 to FR-71 (settings and appearance, §4.14) and FR-72 to FR-76 (flights, §4.15) were added on 2026-10-30.
- Cross-cutting quality requirements are NFR-01 to NFR-21 (§6).
- Inferences are tagged `[ASSUMPTION]` and indexed in §13.
- Technical "how" (mechanisms, storage, pipelines, remediation steps, and implementation details of Built behaviour such as error codes, scope names and thresholds) is in [prd-addendum.md](prd-addendum.md) for the architect.
- The as-built inventories are the record of Built behaviour. The epics hold the original intent and detail, but in places they disagree with the code (NFR-06), so where they conflict, the as-built inventories win.

## 1. Vision

Echo is an **echo of the Owner**: one private place that knows the people in the Owner's life, the places they went, what they did there and when, and can answer for it. A good test is a question like *"When did I last see Dana, and where were we?"* Today the Owner cannot answer it without opening four apps. Echo should answer it in seconds, with the evidence attached.

Echo already holds much of the Owner's life:
- **Contacts:** every person in the Owner's Google Contacts, with email and meeting history.
- **Atlas:** years of Google Timeline visits, rides and hikes fused from six kinds of source, heart rate, and **157,021 photos and videos** going back to 1984, of which 74,061 are placed on the map.

These two halves do not know about each other yet. Contacts knows *who* and roughly *when*. Atlas knows *where* and *what*. The only thing they share is the Owner's login. **The thesis of Echo is that the value is in the joins.** A person becomes far more meaningful once they are tied to the places and moments shared with the Owner. A place or a ride becomes far more meaningful once the people who were there are attached to it.

Echo makes those joins **privately and explainably**:
- The data stays the Owner's.
- Every derived fact shows its evidence.
- Nothing about a real person is written silently.

### 1.1 Product Principles

These are binding on every requirement. Downstream artifacts should cite them as P-n.

- **P-1. The Owner's data is the source of truth.**
  - Source records (a Google contact, a Sports Tracker workout, a photo's EXIF) are never edited by Echo's derivations.
  - Derived layers (fusion, Person Links, Encounters, Face Clusters) sit on top and can be recomputed.
- **P-2. Nothing is overwritten silently.**
  - Every write to an external store is checked against the version the Owner saw (etag).
  - A conflict is shown to the Owner, never resolved by a last write.
- **P-3. Proposals, not silent writes.**
  - The Assistant, enrichment and the Connect layer may only *propose* changes to the Owner's data.
  - The Owner applies them, and they show as a diff.
  - **Exception (Built):** an external AI client using the Owner's Personal Access Token acts *as the Owner* on Atlas data. Its writes there are direct, with no Proposal step. It has no write access to Contacts or to Connect data (FR-32).
  - Not every such write can be undone. Deleting an import removes what that import brought in, but the MCP tools that edit or delete Activities, Places and Visits change records in place: a deleted record comes back only if its source is imported again, and an edited Place or Activity keeps the new value until the Owner changes it back. The client is asked to confirm with the Owner before deleting. `[NOTE FOR PM]` Whether these tools need an undo or a Proposal step is open.
- **P-4. Explainable insights.**
  - Every insight, link, Encounter and assistant answer can show the records it came from, and its confidence where one exists.
  - "No evidence" is a valid answer, and a guess is not.
- **P-5. The Owner has the last word.**
  - Manual merges, splits, confirmations and rejections persist across re-imports and re-computation.
  - A rejected link stays rejected.
- **P-6. Private by default, opt-in by tier.** Each step that widens what Echo reads, or what leaves Echo's infrastructure, is a separate, revocable opt-in (§5). Each Owner makes these choices for their own data only.
- **P-7. Sources are pluggable.** A new source maps into the common records (contact, activity, visit, place, health sample, photo). The rest of Echo works unchanged.
- **P-8. One entity per real-world thing** (from Epic 11).
  - One ride recorded by three sources is one Fused Activity.
  - In the same way, one human is one Person, even as a Hebrew/English pair of Contacts, or as several email addresses and a Face Cluster.
- **P-9. Conflicts are shown, not hidden** (from Epic 11). Evidence that disagrees is displayed with the winning value, whether it is about an Activity's type or about whether an Encounter took place.

### 1.2 Why Now

- Both Areas have reached a level where they are useful on their own: Contacts covers Epics 4–8, and Atlas covers fusion, insights and photos up to PR #110.
- The next unit of value is the connection between them, which the Owner has asked for ("soon they will also connect").
- The scans also found platform debt that compounds with every new feature:
  - committed secrets;
  - no CI;
  - a sign-in that expires weekly;
  - Contacts state that exists only in Redis.
- That debt is cheapest to pay before the Connect layer adds new durable data.

## 2. Target User

### 2.1 The Owner

Every user of Echo is the **Owner** of their own data. The **first Owner** is also the **admin** and the **operator**:
- They are bilingual in Hebrew and English and live in Israel.
- They ride bikes, hike and travel, and have decades of photos.
- They self-host Echo on a 2-node Raspberry Pi k3s cluster, run its backups (NFR-05) and manage the allowlist of invited users (FR-51).
- Their identity is their Google account.

**Invited users** are a handful of friends and family whom the admin invites. Each one signs in with their own Google account, connects their own sources, and owns what Echo holds about them. Their jobs to be done are the same as the first Owner's.

**Jobs to be done**
- *Remember:* "When and where did I last see this person? Who was on that trip? What did we do that weekend?"
- *Keep in touch:* know who has gone quiet, who is waiting for a reply, and whom to reach out to today.
- *Keep my records clean:* phonetic names, duplicates, Hebrew/English twins, and organizations, all in the contact list the phone uses.
- *Understand myself:* training load, routines, places and trends, compared with the Owner's own history rather than generic norms.
- *Own it:* know exactly what each third party sees, and be able to switch it off, export it or delete it.

### 2.2 Multi-User Stance (decision, Q-1 answered 2026-10-28)

**Echo supports more than one user. Each user owns their data, and no user ever sees another user's data.**
- **Ownership.** Every record, setting, grant, consent, token, file and derived fact belongs to exactly one Owner. Nothing is shared, pooled or matched across Owners: not Contacts, not Places, not Encounters, not face data (FR-44). There are no family accounts, shared albums or shared views.
- **Sign-up policy (FR-51).** **Default (logged 2026-10-28): invite-only.** Only a Google account on the allowlist can sign in. The allowlist is managed by the **admin**, who is the first Owner. There is no public sign-up. The admin role manages the allowlist only: it gives no access to any other user's data.
- **Scale.** Echo is sized for **a handful of users** (A-10) on the operator's Raspberry Pi cluster. Pi capacity is a real limit (NFR-18), and the allowlist has a configurable maximum that stays well below Google's limit below.
- **Per-user everything.** Each user has their own cost and rate caps (Gemini, geocoders; NFR-11, NFR-18), their own consent ledger and tiers (FR-46, FR-47), their own Google, OneDrive and Sports Tracker grants (FR-18, FR-46), their own export and erase (FR-48, FR-49), and isolation tests are a hard gate (NFR-07).
- **The Google OAuth reality.** Echo signs users in with Google, and the Touchpoint tiers use Gmail. Plainly:
  - Phase 0 moves the Google app to **Production, unverified** (Q-3). Every user sees Google's "unverified app" warning at sign-in, and Google allows such an app **at most 100 users over its lifetime** (users who are removed still count).
  - The Gmail scopes that T1 and T2 use (`gmail.metadata`, `gmail.readonly`) are **restricted** scopes. Beyond test use, Google requires app verification **plus a yearly CASA security assessment** for them.
  - **Plan:** while Echo serves the Owner and a small invited circle (at most 100 users in all), it stays Production, unverified. The Gmail-based tiers (T1, T2) stay available to invited users under the unverified warning. Full verification and CASA are a **gate before Echo opens beyond an invited circle** (NFR-19).
- **People in other users' data.** Each invited user's contacts, photos and calendars describe people who never signed up. Each user is responsible for their own data, as the first Owner is for theirs; Echo's protections (tiers, T4 consent, "never recognize", erase) apply per user.

### 2.3 Non-Users

- Other people in an Owner's life who are not invited. They appear as **data**, not as users. They have no login, no view and no notification. Being invited does not change this: a user who also appears in another user's data never sees that data.
- Anyone not on the allowlist.
- Anyone wanting a hosted SaaS, a social network or a shared family album.

### 2.4 Key User Journeys

- **UJ-1. The Owner cleans up a Hebrew/English twin.** *(Built)*
  - The Owner notices "דנה כהן" and "Dana Cohen" in Duplicates.
  - They open the group, keep the Hebrew contact, and fill the phonetic name from the English one.
  - They merge. Echo refuses until they confirm that a photo on the deleted twin will be lost.
  - The phone shows one contact. Realizes FR-05, FR-10.
- **UJ-2. The Owner checks a ride.** *(Built)*
  - In Atlas, the Owner asks the assistant: "What was my heart rate on the 3 Sep ride, and where did I stop?"
  - The answer cites the fused activity (Sports Tracker track, Fitbit HR) and its linked visits. A link opens the ride on the map.
  - Realizes FR-19, FR-23, FR-28.
- **UJ-3. The Owner remembers when they last saw a friend.** *(Proposed)*
  - From anywhere in Echo, the Owner asks: "When did I last see Dana, and where?"
  - The assistant finds the most recent **Encounter**, and the answer shows the evidence for it: a calendar dinner with Dana as an attendee, a visit to a café in Haifa at the same time, and four photos.
  - It also names the place. It offers "Open that day" (the Unified Timeline) and "Open Dana" (the contact card, which now lists their recent Encounters).
  - **Edge case:** if only an email exchange exists, the assistant says that it has no evidence of a meeting, gives the last email date, and does not guess a place.
  - Realizes FR-31, FR-33, FR-34, FR-38, FR-41, FR-42.
- **UJ-4. The Owner replays a day.** *(Proposed)*
  - The Owner opens 14 Jun 2025 in the Unified Timeline. In time order they see a morning ride with its HR, a meeting with two colleagues (linked to Contacts), a visit to a restaurant, and the evening's photos.
  - They filter the timeline to "with Yoni" and see every day the two shared.
  - Realizes FR-39, FR-40.
- **UJ-5. The Owner turns on People in Photos.** *(Proposed, Phase 2)*
  - The Owner opens Privacy, reads the biometric notice, and turns on face recognition.
  - Echo works through the Owner's photos (not videos) on Echo's own cluster. It then presents unnamed face clusters in a review session, and the Owner links clusters to contacts: their kids, Dana, Yoni.
  - They mark one acquaintance as "never recognize". Their cluster is deleted and is not rebuilt.
  - When the review session ends, the clusters the Owner did not name are discarded. Only named people are kept.
  - **Edge case:** they later turn the opt-in off. All face data is deleted, and Encounters that relied only on faces disappear.
  - Realizes FR-43 to FR-45.
- **UJ-6. The Owner audits what leaves home.** *(Proposed)*
  - The Owner opens the Privacy Center. It shows each opt-in tier, its state, and the third parties that receive data and what they receive.
  - They turn off automatic background place lookup, then export all of their Echo data as an archive.
  - Realizes FR-47, FR-48.
- **UJ-7. The admin invites a friend.** *(Proposed, Phase 0)*
  - The admin adds Yoni's Google address to the allowlist.
  - Yoni signs in with Google, sees Google's "unverified app" warning, continues, and lands in an empty Echo of their own: their own Contacts, no Atlas data yet, every tier off.
  - Yoni connects their own sources and opts into tiers. Nothing of the admin's data is visible to Yoni, and nothing of Yoni's is visible to the admin.
  - **Edge case:** someone not on the allowlist signs in. Echo refuses, stores nothing about them, and withdraws the Google permission they just gave.
  - Realizes FR-01, FR-51.

## 3. Glossary

*Downstream documents use these terms exactly.*

- **Owner:** the user who owns the data. Identified by a Google account `sub`. Echo has several users, and each one is the Owner of their own data. Every record belongs to exactly one Owner, and no Owner can see another Owner's records.
- **Admin:** the first Owner, who manages the allowlist (FR-51). The role gives no access to any other Owner's data.
- **Allowlist:** the list of Google accounts that may sign in to Echo (invite-only, FR-51).
- **Operator:** whoever runs Echo's cluster and its system-level backups (NFR-05). Today this is the admin.
- **Area:** a top-level section of Echo in the shell. Today there are two, **Contacts** and **Atlas**. The Unified Timeline is proposed as a third.
- **Contact:** a person in the Owner's Google Contacts. Google People API is its store (P-1), and Echo holds no copy of its fields.
- **Activity Snapshot:** Echo's per-Owner summary of Touchpoints per email address. It is metadata only and needs Tier T1. Despite the historical name, it has nothing to do with an Activity.
- **Touchpoint:** an email or a meeting between the Owner and an email address, from the Activity Snapshot. A Touchpoint is evidence of contact, not of being together, so it is not an Encounter.
- **Own Addresses:** the email addresses the Owner marked as "this is me". They are used to tell sent mail from received mail.
- **Source Record:** an imported item exactly as a source gave it (source, external id, raw values). It is never edited by derivations.
- **Activity:** a movement with time (and usually a track): a ride, hike, run, drive, flight and so on. A **Fused Activity** is one real-world Activity built from one or more Source Records.
- **Visit:** a stay at a location over a time window, from Google Timeline.
- **Place:** a named location that Visits point to. It has an identity state (pending, auto, ambiguous, none, user).
- **Photo:** metadata of a photo or video in the Owner's OneDrive: time, camera position and placement. The file stays in OneDrive.
- **Person:** a Contact as seen across Echo, meaning the Contact together with all its Person Links.
- **Person Link:** an Echo-held link from a Contact to an identifier in other data: an email address, a calendar attendee or a Face Cluster. A link is *confirmed*, *proposed* or *rejected*, and it carries its evidence. It is never written into Google Contacts.
- **Encounter:** a fact that a Person was with the Owner at a time and (usually) a Place. It cites its evidence: a calendar event, a Visit, an Activity or a Photo, and has a confidence. Its status is *derived* (inferred by Echo), *confirmed* (by the Owner, or added by the Owner) or *rejected*.
- **Unified Timeline:** a single chronological view of the Owner's Activities, Visits, Encounters, meetings, email touchpoints, Photos and health, across Areas.
- **Face Cluster:** a group of face detections believed to be the same person, held only with Tier T4. It is named by linking it to a Contact.
- **Proposal:** a change Echo suggests (shown as a diff) that takes effect only when the Owner applies it.
- **Opt-in Tier:** a revocable permission level that widens what Echo reads or shares (§5).
- **Assistant:** Echo's chat agent (Google Gemini), which answers with links to records and writes only through Proposals.
- **Personal Access Token:** an Owner-created bearer token (`atl_…`) that lets an external AI client use Echo through MCP.
- **Route Quality:** Echo's assessment of how well an Activity's recorded track describes the way actually travelled: point density, gaps, straight jumps, implausible speeds. It has a score, a grade and flags.
- **Map matching:** working out which roads or paths a recorded track actually followed. It takes the recorded GPS points, finds the roads near each one, and picks the most likely connected way through them. Where points are far apart, it fills the stretch between them with the most plausible road route.
- **Routing graph (tiles):** the road and path network of a region, prepared from OpenStreetMap data so that a routing engine can search it quickly. Valhalla stores it as **tiles**, small files that each cover one square of the map. Echo builds it from a public map extract, and FR-60 adds more regions. It is shared by every Owner and holds no data about any of them.
- **Routing engine:** open-source software that finds routes over a routing graph and can also do map matching. Echo uses one routing engine, self-hosted on the operator's cluster (NFR-20). The architecture compares Valhalla, OSRM and GraphHopper (AD-32).
- **Matched Route:** a derived geometry that places an Activity's track on the real road or path network. Each piece of it is *observed* (recorded points lie along it), *inferred* (the road was chosen between sparse points) or a *gap* (nothing was matched). The source track is never changed (P-1). Like every derived fact, it belongs to the Owner of the Activity.

## 4. Features

*FR numbering is global and stable. "Status" follows §0. Built FRs are stated briefly; their detailed behaviour is in the cited epic.*

### 4.1 Echo Shell and Identity

The Owner signs in once with Google and moves between Areas in one shell. Echo presents itself as one product at the same URL.

#### FR-01: Google sign-in as the Owner — Built
The Owner can sign in with Google and reach every Area with one session.
- The identity is the Google `sub`. The session lasts 7 days, and when it expires the Owner is sent to sign in again, not shown an error.
- Every record Echo stores is scoped to that `sub` (see NFR-07).
- *Proposed (Phase 0):* sign-in also requires the account to be on the allowlist (FR-51), and the Google app runs in Production, unverified (NFR-03, §2.2).

#### FR-02: Area shell — Built
The Owner can switch between Areas from a left rail (a bottom bar on phones). `/` opens the last Area used.
- Each Area has a stable route (`/contacts`, `/atlas`). A deep link opens the right Area.

#### FR-03: Echo branding — Proposed
The Owner sees the product as **Echo**: in the shell, the signed-out home, the browser title and the assistant's self-description. The Areas keep their names.
- The URL https://contacts.mayaffit.com is unchanged, and so are existing deep links.
- **Out of scope (Owner decision, Q-4):** renaming repositories, charts, images or the domain. Echo is the in-app name only, and contacts.mayaffit.com stays.

#### FR-51: Invite-only sign-up with an admin-managed allowlist — Proposed (Phase 0)
Only Google accounts on the allowlist can sign in. The admin (the first Owner) can add and remove allowlist entries from an admin page. Realizes UJ-7.
- An account that is not on the allowlist is refused at sign-in. Echo stores nothing about it and withdraws the Google permission it just received.
- A new user starts with an empty Echo: every tier off, no sources connected.
- The admin page shows each user's allowlist entry, last sign-in and usage against their caps (NFR-18). It never shows any user's data.
- Removing a user blocks their next sign-in and ends their sessions and tokens. The admin can then run that user's erase (FR-49) without seeing the data.
- The allowlist has a configurable maximum (default 10, A-10). Because Google counts every user an unverified app has ever had, toward a lifetime cap of 100 (§2.2), the admin page shows that count too.
- The admin role cannot be granted from the UI. The first admin is set in the deployment configuration.

#### FR-04: Hebrew interface — Planned (Epic 9 §5)
The Owner can switch the interface to Hebrew with right-to-left layout.
- Every label, date and number format in the shell, Contacts and Atlas follows the chosen language. The data handling of Hebrew (NFR-13) is unchanged.

### 4.2 Contacts: Workspace (Epics 4, 5)

The Owner works on Google Contacts directly, in a fast spreadsheet-like workspace built for phonetic names and Hebrew/English data. Google remains the store.

#### FR-05: View and edit every contact field safely — Built
The Owner can view and edit every field group of a Contact: names with phonetic parts, nicknames, phones, emails, addresses, organizations, birthdays, URLs, notes, relations, custom fields and labels. They can do this inline in the table or in the contact card. Realizes UJ-1.
- Only the field groups that changed are written. Every other field in Google is unchanged.
- If the Contact changed in Google since the Owner loaded it, the write is refused and the Owner sees a conflict dialog. It is never overwritten (P-2).
- Saves are optimistic, retry on failure, and show their sync state.

#### FR-06: Find and organize — Built
The Owner can:
- search across name, phonetic name, nickname, phone digits, email and organization;
- filter by chips and labels;
- sort with Hebrew and Latin collation, and jump with an A–Z / א–ת rail;
- configure columns.

On a phone the table becomes a list.

#### FR-07: Contact insights — Built
The Owner can see completeness, phone origin, name script, labels and birthdays by month. Clicking a chart filters the list. Echo proposes organization names from work-email domains, one at a time or per domain.

#### FR-08: Device actions — Built
From a Contact, the Owner can:
- call, SMS, WhatsApp or email (Israeli numbers converted to international format);
- export a vCard 3.0;
- compare the phone's contacts with Google and add the missing ones;
- open look-up searches for the Contact on Google, LinkedIn and Facebook (in a new tab, filled in with the name and organization).

#### FR-09: Clean-up: Trash, restore, delete — Built
The Owner can soft-delete to Echo's Trash label, restore, and permanently delete in batches of up to 500.
- Every delete is behind a confirmation that lists who is affected.

#### FR-10: Duplicates and merge without loss — Built
The Owner can find duplicate groups (last 9 phone digits, email, normalized name) and merge them, including Hebrew/English versions of one person. Realizes UJ-1.
- A merge deletes a contact only after the merged contact was saved.
- If a deleted contact holds fields the merge does not carry, the merge is refused until the Owner confirms the loss explicitly.
- A contact that changed since it was loaded is refused.

#### FR-11: Workspace productivity — Planned (Epic 9 §5)
The Owner can use a command palette, saved views, bulk edit of any field, undo of applied Proposals and merges for 24 h, CSV/vCard export of any view, a scheduled clean-up report, and a phone layout for the contact card and the Assistant.

### 4.3 Contacts: Keeping in Touch (Epics 6, 7, 9)

With Tier T1, Echo learns from Gmail headers and the Calendar when the Owner was last in touch with each person and whom to reach out to.

#### FR-12: Last in touch — Built
After opting into T1, the Owner sees for every Contact:
- the last email sent and received, and the last and next meeting;
- most in touch, gone quiet and never in touch;
- people the Owner emails who are not Contacts.

They can mark their Own Addresses, which are excluded from counts and used to tell sent mail from received mail.
- At T1, Echo asks Google only for read access to email headers and calendar events. Subjects and bodies are never read at T1.
- The scan covers 365 days and 10,000 messages, on the primary calendar only.
- "Forget my activity data" deletes the Activity Snapshot.

#### FR-13: Reach out and awaiting reply — Built
The Owner sees:
- "Reach out today", based on each person's usual rhythm, with dismissals;
- threads awaiting the Owner's reply;
- threads where the Owner is waiting for an answer.

#### FR-14: Suggested emails — Built
For Contacts without an email, Echo proposes addresses whose display names match the Contact's name or phonetic name across Hebrew and English. The Owner applies or dismisses each one.

#### FR-15: Signature enrichment — Built (Tier T2)
After opting into T2, the Owner can choose up to 10 Contacts. Echo reads each one's last 3 emails, extracts phone, title, company and links with Gemini, and shows the results as Proposals.
- Email bodies are processed in memory and never stored. Email text is treated as data, not instructions.

#### FR-16: Relationship extras — Planned (Epic 9 G5–G9, §4)
- Meeting prep and a morning digest.
- All calendars and recurring 1:1 detection.
- A configurable scan window and label exclusions.
- Personal vs professional views.
- Keep-in-touch targets.
- Birthday reminders with a drafted greeting.
- Per-contact notes or an interaction log.
- A relationship graph (who is copied together).
- Reach-out scoring that also weighs birthdays, job changes and upcoming meetings, with a one-click draft.
- Assistant actions in Gmail, Calendar and Tasks: draft an email, create an event, add a task. These are always Proposals.
- `[NOTE FOR PM]` Assistant actions need new *write* permissions from Google (`gmail.compose`, `calendar.events`, Tasks). That would add a tier above T2 (§5.1).

### 4.4 Atlas: Sources and Fusion (Epics 10, 11)

Atlas brings every movement, stay, health sample and photo into one owner-scoped store, and fuses duplicates of the same real-world Activity while keeping provenance.

#### FR-17: File import — Built
The Owner can import Google Timeline (on-device export and older Takeout), Google Fit, Fitbit heart rate, Samsung Health, and any GPX/TCX. They can import files, zips or a folder; parsing happens in the browser.
- Re-importing the same data never creates duplicates: an item the source already gave is updated, not added again.
- Every import is listed with its counts and can be deleted, which removes all of its rows.

#### FR-18: Connected sources — Built
The Owner can connect, sync and disconnect **Sports Tracker** (incremental; the password is used once and never stored) and **OneDrive photos** (Microsoft Graph delta, every 30 minutes and on demand).
- Disconnecting OneDrive deletes all of its Photo metadata.
- A refused token renewal marks the connection as expired and visible to the Owner.
- Each Owner connects their own OneDrive and Sports Tracker accounts. A connection, its stored secret and its synced data belong to that Owner only.
- Today Sports Tracker syncs only on demand, and a sync cut short by a restart ends as an error. FR-52 adds a schedule (Q-9).

#### FR-19: Fusion with provenance — Built
Echo fuses Source Records that describe one real-world Activity into one Fused Activity. It uses per-source time and location tolerances and a match threshold, and the Owner can change both (saving them re-fuses everything). Realizes UJ-2.
- Source Records are never edited by fusion (P-1).
- For every fused field, the Owner can see which source supplied it and what the others said. Disagreements on type, distance, start or location are recorded as conflicts.
- Two recordings from the same source are never merged automatically.

#### FR-20: Owner overrides survive — Built
The Owner can merge, split or change the type of an Activity. These decisions are re-applied on every fusion run and import (P-5).

#### FR-21: Pin a field's source — Planned (Epic 11 Principle 5)
The Owner can choose which source supplies a given field of a Fused Activity, and the choice survives re-fusion.

#### FR-52: Scheduled Sports Tracker sync — Planned (Q-9, Phase 1)
Once an Owner has connected Sports Tracker, Echo syncs it on a schedule, incrementally, as it does OneDrive: every few hours (default every 6 hours), plus on demand, at a polite request rate.
- The Sports Tracker API is unofficial and may change or break without notice. A failed or interrupted sync is shown as the connection's status (for example "sync failed at 14:05, will retry", or "Sports Tracker changed; file import still works"), with backoff. It never crashes a worker or affects other sources or other Owners.
- A sync cut short by a restart resumes on the next run instead of ending as an error.
- File import (FR-17) stays available as the fallback.

#### FR-53: Samsung Health time alignment is verified and fixed — Proposed (Q-10, Phase 0)
Echo checks whether Samsung Health times are shifted and corrects them, so that Samsung data can be trusted in fusion, health trends and Encounters. Samsung Health is not excluded.
- **Detect.** Fusion already pairs Samsung exercise sessions with the same outings from Sports Tracker or GPX tracks and records their start-time conflicts (FR-19). Echo uses these pairs to find any systematic offset, for example local time stored as UTC, or a one-hour DST shift, per period.
- **Correct.** The offset found is applied to Samsung data on import and to the existing rows, as the photo clock calibration does for photos (FR-27). The raw source value is kept (P-1); the corrected time is what fusion, insights and Connect use. The Owner can see the correction applied to each record.
- **Done when:** over the Owner's matched pairs, the median absolute start-time difference between Samsung Health and the matched GPS source is **under 60 s**. Echo reports this figure as a data-quality measure.

### 4.5 Atlas: Explore and Understand

#### FR-22: Map and time exploration — Built
The Owner can explore on a map and a time histogram together:
- On the map: tracks coloured by type, Visits, flight arcs and Photos, an area filter, map styles and a density mode.
- On the histogram: drag to select a range and scroll to zoom.
- Filters: text, types and "sources disagree".

With types selected, Visits are limited to those during such trips.

#### FR-23: Activity analysis — Built
For an Activity, the Owner sees:
- moving vs stopped time, splits and heart-rate zones (from the Owner's own maximum), and aerobic drift;
- best efforts with all-time rank, same-route comparison, and percentiles over the last 365 days;
- the fusion drill-down, linked Visits and Photos. Realizes UJ-2.

#### FR-24: Place identification — Built
The Owner can identify a location through OpenStreetMap, associate it with a found or typed place, confirm an automatic identity, pick among ambiguous candidates, and apply an identity to nearby Places.
- Places where the Owner typically stays 6 hours or more (home, work) are never named automatically.
- A Place is named automatically only when exactly one relevant place is very close and no competing place is nearby. Otherwise it is left for the Owner as ambiguous. (The distances are in the addendum, §A10.)

#### FR-25: Atlas insights and proposals — Built
For the current query, the Owner sees totals against the previous period, per-type statistics, trends, records, streaks, top and new Places, travel, weekly load, HR-zone time and data quality. They also get rule-based proposals, each with an action, and can hand the insights to the Assistant ("Explain with AI").

#### FR-26: Deeper self-insight — Planned (Epic 10 phases 2–3, Epic 11 "Insights, next")
- Elevation profiles and ride comparisons.
- Health beyond heart rate (sleep, steps, weight), with daily summaries and trends.
- Routines, time at home and work, and favourite routes.
- Resting-HR trend.
- "What could I improve" and "what I like", each compared with the Owner's own history.
- Insight tools for the Assistant and MCP (places, activity).
- Health trends that use Samsung Health data rely on its corrected times (FR-53, Phase 0).

### 4.6 Atlas: Photos

#### FR-27: Photos in time and place — Built
The Owner sees Photos placed on Activities and Visits and on the map, and can open them in a viewer and in OneDrive. Items with photos are marked in the list, the map and the panels.
- A Photo whose camera clock was set to the wrong time zone is still placed at the right moment, and placement is redone when new movement data arrives.
- A Photo is placed from its own GPS where it has one, and otherwise from the Owner's track or Visit at that time. The Owner can see how each Photo was placed. (The method is in the addendum, §A10.)
- Only metadata and proxied thumbnails pass through Echo, and the files stay in OneDrive.

### 4.7 Assistant and AI Access

Today each Area has its own Assistant toolset. In Contacts, the Assistant sees contact tools. In Atlas, it sees Atlas tools. Echo makes it one Assistant.

#### FR-28: Assistant with Proposals — Built (per Area)
In each Area, the Owner can chat with the Assistant in Hebrew or English. Answers link to records (contact cards, Activities, map views).
- Every Assistant write is a Proposal with a diff (P-3). For Contacts, it is applied with the etag it was made from.
- The Assistant can run a web search (Gemini with Google Search grounding).
- The panel states that messages and the records looked up go to Google Gemini.
- Usage is rate-limited per Owner per minute and per day, with a global daily cap. `[NOTE FOR PM]` Today a restart of the backend resets the counts, so the caps can be exceeded across restarts (addendum §A10).

#### FR-29: MCP access with Personal Access Tokens — Built (Atlas only)
The Owner can create and revoke Personal Access Tokens. External AI clients (MCP) can then search, read and edit Atlas data as that Owner. Their edits are direct writes, under the P-3 exception. A token reaches only its own Owner's data.
- Echo stores only a hash of each token. Remote clients also pass through Cloudflare Access. `[ASSUMPTION A-9: the Cloudflare Access policy on remote MCP is configured outside this repository and was not checked here.]`
- Today MCP exposes no Contacts, Photo or Connect data.

#### FR-30: Conversation history and streaming — Planned (Epic 8 "Later")
The Owner's conversations stream as they are generated and persist across sessions. They can delete any conversation or all of them.

#### FR-31: One Assistant across Echo — Proposed
From any Area, the Owner can ask one question that needs Contacts, Atlas and Connect data together. The Assistant combines their tools in one conversation. Realizes UJ-3.
- Links in an answer open the right Area: a contact card, an Activity, a Place or a day in the Unified Timeline.
- The proposal rule (P-3) and the Gemini disclosure apply unchanged.
- **Decision (Owner, Q-5 answered 2026-10-28):** for a when, where or with-whom question, Echo computes the joins itself, and Gemini receives only the matched results and their citations, never bulk lists of people with locations. Gemini then writes the reply in natural language, as the Assistant already does for Contacts and Atlas. The disclosure says that results may name people together with places and times.
- **Per-user switch.** Each Owner has a switch in the Privacy Center (FR-47), "Let Gemini word answers about people and places", **on by default**. With it off, the matched results are shown as plain cards with their evidence, and nothing about them is sent to Gemini.
- If the Owner has not granted a tier the answer needs, the Assistant says so and does not work around it.
- `[ASSUMPTION A-3: Gemini remains the model; this FR extends the existing disclosure rather than adding a provider.]`

#### FR-32: MCP for people and Connect — Proposed
With a Personal Access Token, an external AI client can *read* Contacts, Person Links, Encounters and the Unified Timeline.
- Writes to Contacts or Connect data through MCP are out of scope. Contacts sync straight to the Owner's phone, and MCP has no Proposal review step.
- Each token states which data it may read (Atlas only, or Atlas plus people).

### 4.8 Connect: People, Places and Time (Proposed, Phase 1)

This is the heart of Echo. The Connect layer holds **Person Links** and derives **Encounters**, so that a Contact can be placed in time and space and a moment can be tied to people. Everything here is derived and explainable (P-4), it is held by Echo and not written into Google (P-1), and the Owner has the last word on it (P-5).

#### FR-33: Person Links — Proposed
Echo maintains Person Links between each Contact and the identifiers that refer to them elsewhere:
- email addresses in the Activity Snapshot;
- calendar attendees;
- with T4, Face Clusters.

Links that follow from the Contact's own fields are *confirmed* automatically. A name-based match is only *proposed*, and the Owner confirms or rejects it.
- Each Person Link shows its evidence and status. A rejected link is never proposed again for the same pair.
- When the Owner merges Contacts (FR-10), the Person Links, Encounters and decisions of the removed Contacts move to the kept Contact. No confirmation or rejection is lost (P-5, P-8).
- When a Contact is deleted outside Echo, its Person Links are removed on the next sync.
- A Hebrew/English pair of Contacts that is not merged can be linked as one Person (P-8).

#### FR-34: Encounters from meetings and stays — Proposed
Echo derives an Encounter when a calendar event that has a linked Person as an attendee overlaps a Visit or Activity of the Owner in time. Where the event has a location, that location must also agree with the Visit or Activity. Realizes UJ-3. *Origin: Epic 10 phase 4, "who you met where".*
- **Default rule:**
  - *Overlap:* at least 50% of the shorter of the event and the Visit or Activity.
  - *Location:* if the event has a location, it must be within 500 m of the Visit or the track. Without a location, the confidence is capped at 0.6.
  - Checking an event's location means looking up its text with OpenStreetMap, which is a disclosure (§5.2). It happens only while the Owner's **address lookup** switch is on (FR-47). While it is off, an event's location is treated as absent (confidence capped at 0.6).
  - *Threshold:* no Encounter is created below a confidence of 0.5.
  - The Owner can tune the defaults, as with fusion settings. `[ASSUMPTION A-8: these defaults are proposed starting values.]`
- Each Encounter cites its calendar event, its Visit or Activity, and its Place, and has a confidence from 0 to 1. Evidence that disagrees is shown, for example when the event location and the Visit are far apart (P-9).
- The Owner can confirm or reject an Encounter, and the decision survives re-derivation.
- Answers (FR-41) may cite *confirmed* Encounters, and *derived* Encounters labelled as "likely". They never cite *rejected* ones.
- Events the Owner declined or that were cancelled never produce an Encounter.
- Out of scope: Encounters inferred from email alone. Email is a touchpoint, not a meeting.
- `[ASSUMPTION A-7: calendar data plus Atlas Visits and Activities give enough evidence for Phase 1. Calls and WhatsApp are not available until their imports exist.]`

#### FR-35: Owner-created Encounters — Proposed
The Owner can attach one or more Contacts to an Activity, a Visit or a day ("Yoni was on this ride"). The result is a confirmed Encounter. *Origin: Epic 11 "people from Contacts on an activity".*
- The attachment is shown with the source "added by you" and is never removed by re-derivation.

#### FR-36: Contact addresses and Places — Proposed
Echo matches the postal addresses of Contacts to Atlas Places, and proposes the match ("This Place may be Dana's home") for the Owner to confirm. A confirmed match makes Visits there count as evidence for an Encounter.
- Matches are always Proposals. Home and work Places of the Owner are never assigned to another person automatically.
- Matching sends the Contact's postal address text to OpenStreetMap (§5.2), so it runs only while the Owner's **address lookup** switch is on (FR-47). The switch is off by default (P-6). A Place identified by its coordinates alone is not affected.

#### FR-37: Owner's home and work from Atlas — Proposed
Echo proposes the Owner's own home and work addresses from long stays in Atlas. If the Owner accepts, Echo writes them to their own Contact through the normal Proposal path.

#### FR-38: People in the contact card — Proposed
In the contact card, the Owner sees the Person's Encounters (last met, where, how often per year) and shared Photos. Each item opens the matching Atlas or Unified Timeline view. Realizes UJ-3.

#### FR-39: People in Atlas — Proposed
In Atlas, the panels for an Activity, Visit and Place show the Persons met there. The Owner can filter Atlas by Person ("rides with Yoni", "places I met Dana"). *Origin: Epic 11 "Calendar and Contacts links".* Realizes UJ-4.
- Selecting a Person in the filter shows only Activities and Visits that have a confirmed or derived Encounter with that Person.

#### FR-40: Unified Timeline — Proposed
The Owner can open a Unified Timeline: one chronological view, by day and by range, of:
- Activities and Visits;
- Encounters and meetings;
- email touchpoints, as a count per Person per day (Owner decision, Q-6). Individual messages are not shown, and content never is;
- Photos;
- health highlights.

Realizes UJ-4.
- The Owner can filter the timeline by Person, Place, type and Area, and jump from any item to its Area detail.
- Every item shows its source. Items from tiers the Owner has not granted are absent, not greyed out.

#### FR-41: When/where/with-whom answers — Proposed
The Assistant and MCP can answer "when did I last see X", "who was at Y" and "where was I with X in year Z". They answer from Encounters, Visits and Photos, and cite them. Realizes UJ-3.
- An answer without supporting records says so, and gives the nearest touchpoint (for example, the last email) instead of guessing.

#### FR-42: Explain a link — Proposed
For any Person Link or Encounter, the Owner can open "Why?" to see the exact records, rules and confidence behind it, including any evidence that disagrees (P-9). They can act on it there: confirm, reject or edit. Realizes UJ-3.

### 4.9 People in Photos (Proposed, Phase 2, Tier T4)

Face recognition stores biometric data about the Owner and about people who never consented. The first Owner has accepted storing face data of non-consenting friends and family (Q-2), on these terms: it is off by default, gated by its own explicit opt-in per Owner, confined to Echo's own infrastructure, kept only for people the Owner names, and computed from photos only, never videos. Each Owner's face data is their own: it is never shared with, shown to or matched against another Owner's.

#### FR-43: Biometric opt-in — Proposed
The Owner can turn on People in Photos only through a dedicated consent screen. The screen states:
- what is computed (face detections and face templates);
- where it is stored (only on Echo's own cluster, run by the operator);
- that it covers other people;
- that only people the Owner names are kept, and that only photos are scanned, not videos;
- how to remove it.

Realizes UJ-5.
- Until the Owner gives this consent, no face detection runs on any Photo.
- Turning the opt-in off deletes, within 24 hours, every face detection, template and Face Cluster, every Person Link to a Face Cluster, every Encounter that relied on faces, and the Owner's decisions about them. The Privacy Center confirms the deletion.
- Face data and everything derived from it are never written to backups, so the deletion is complete when the Privacy Center confirms it. No backup copy has to expire.
- `[ASSUMPTION A-4: detection and clustering can run in batch on Echo's cluster; a first pass taking hours to days is acceptable.]`

#### FR-44: Face Clusters named by the Owner — Proposed
Echo groups faces in the Owner's Photos into Face Clusters. The Owner names a cluster by linking it to a Contact, which creates a Person Link. No cluster is ever linked to a Contact without the Owner's action.
- The Owner can split or merge clusters, remove a face from a cluster, and mark a cluster "never recognize". A cluster marked this way has its data deleted, and that person is not clustered again.
- **Named people only (Q-2).** Unnamed clusters are presented in a review session. When the session ends (or 30 days after it opened, if the Owner leaves it open), every cluster the Owner did not name is discarded with its detections and templates. Only named clusters are kept. A later scan of new photos can present new unnamed clusters in the next session.
- **Photos only (Q-2).** Videos are never scanned.
- **Per Owner.** Clustering runs over one Owner's photos at a time. Face templates and clusters are never compared, matched or merged across Owners, and no Owner can see another's face data.
- Face templates never leave Echo's infrastructure, and no face data goes to Gemini or any other third party (§5).

#### FR-45: People in Photos as evidence — Proposed
Named Face Clusters make Photos evidence for Encounters (FR-34) and populate "Photos with X" in the contact card, Atlas and the Unified Timeline.
- The Owner can filter Photos by one or more Persons.

### 4.10 Privacy and Data Control

#### FR-46: Per-feature opt-ins — Built
- The Owner grants Gmail and Calendar metadata (T1) and email content (T2) through separate Google consents.
- They connect each Atlas source separately.
- Every grant (Google, OneDrive, Sports Tracker) is per Owner: one Owner's grant never gives Echo access to another Owner's accounts or data.
- Each one can be undone in Echo: "forget my activity data" deletes the Activity Snapshot, disconnecting a source deletes its stored secret (and, for OneDrive, its Photo metadata), and deleting an import deletes its rows.
- `[NOTE FOR PM]` Today Echo does not revoke the Google permissions themselves. That is done in the Google account, and the stored refresh token lasts up to 180 days (sliding). In-app revocation is part of FR-47.

#### FR-47: Privacy Center — Proposed
The Owner can open one Privacy page, which covers their own data only. Realizes UJ-6. Each Owner has their own consent ledger: every grant, revocation and switch change they make is recorded for them, and never affects another Owner. The page shows:
- each Opt-in Tier and its state;
- each connected source;
- each Personal Access Token;
- the Third-Party Disclosure Register (§5.2), with what each party received recently (counts, not content).
- From the Privacy Center, the Owner can revoke any tier, token or source. Revoking a Google tier deletes that tier's data and withdraws Echo's whole Google permission, because Google cannot withdraw one part of it. Echo then asks the Owner to sign in again and consent to the tiers they still want, so the new permission covers exactly those.
- They can also turn off automatic background place lookups. Manual "Identify" still works.
- They can turn **address lookup** on or off. It sends the text of calendar event locations and Contacts' postal addresses to OpenStreetMap (FR-34, FR-36). It is off by default.
- They can turn "Let Gemini word answers about people and places" on or off (FR-31). It is on by default.

#### FR-48: Export everything — Planned (Epic 9 §6)
The Owner can export all data Echo holds for them as an archive of open formats (JSON, GPX, CSV), with a manifest. The archive holds that Owner's data only, never another Owner's. It covers:
- Atlas records and their provenance;
- import history;
- Person Links and Encounters, with their decisions;
- the Activity Snapshot;
- opt-ins and settings.

Realizes UJ-6.
- Face templates are included only if the Owner asks for them explicitly.
- Contact fields are not in the archive, because Google holds them. The manifest says so and points to Google Takeout.
- Secrets and tokens are never exported.

#### FR-49: Delete everything — Planned (Epic 9 §6; Phase 1)
The Owner can delete all Echo-held data about themselves in one action, after a confirmation, and then gets a completion receipt.
- Afterwards, no row with the Owner's `sub` exists in Echo's stores, and their Google and Microsoft grants and Sports Tracker connection are revoked. Other Owners' data is untouched.
- The admin can run the same erase for a user removed from the allowlist (FR-51), without seeing that user's data.
- Data in Google Contacts and OneDrive is not touched.
- Older system backups still hold the erased data, encrypted, until they expire, at most about 8 weeks later (NFR-05). A restore re-applies the erase.
- **Moved to Phase 1 (2026-10-28):** with invited users (Q-1), every user needs a way to leave Echo with their data deleted, and the admin needs one to remove a user cleanly.

#### FR-50: Legal pages — Planned (Epic 9 §6)
Echo publishes a privacy policy and terms page at stable URLs, as Google requires for an OAuth app in Production. They describe the tiers and the third parties in §5, that Echo is invite-only and run by the operator, that each user's data is theirs and is not shared with other users, and that system backups hold every user's data, encrypted.

### 4.11 Later Sources — Planned

All of these are in Epics 9–11 and are deferred past Phase 2. Each one maps into the common records (P-7). The Owner chose LinkedIn as the first one (Q-7), so it has an FR; the others get one when they are scheduled.

#### FR-54: LinkedIn connections import — Planned (Epic 9 S1; first source after Phase 2, Q-7)
The Owner can import `Connections.csv` from their LinkedIn data export, and Echo links each connection to a Contact.
- LinkedIn has no personal API, so this is an export import, like Google Timeline. Echo never scrapes LinkedIn or signs in to it (§7).
- A connection whose email matches a Contact is linked automatically. A name or company match is only proposed, and the Owner confirms or rejects it (as for Person Links, FR-33). Contact fields are changed only through Proposals (P-3).
- Re-importing a newer export updates connections without duplicates, flags job changes (company or title) as Proposals, and the import can be deleted like any other (FR-17).

The remaining sources, in no fixed order yet:
- a second Google account;
- Microsoft 365 mail, calendar and contacts;
- phone calls and SMS via Drive backups;
- WhatsApp chat exports;
- Strava and Garmin;
- automatic web suggestions for Contacts (Programmable Search).

### 4.12 Atlas: Route Quality and Road Matching (Proposed)

On the Atlas map, most car trips are drawn as straight grey lines that cut across fields and between roads. Google Timeline records a drive as a few sparse points: the first Owner's production data has 15,420 drives with about 13 points each, which is 0.94 points per km, and the median gap between points is 60 s and 439 m. Echo detects these quality problems automatically and draws such trips on the roads that were most likely driven. A **separate route-quality service** does this on the operator's own cluster. It runs automatically on imported data, and on demand for a batch of routes or a single route.

- Everything here is a **derived layer** (P-1, NFR-12). Source tracks are never changed, and the Owner can always see the original.
- No location data leaves Echo's infrastructure for this feature (NFR-20), apart from the encrypted system backups that already hold every Owner's Atlas data (NFR-05).
- **Per Owner.** Each Owner's Route Quality, Matched Routes, decisions and settings are their own, like the rest of their Atlas data (§2.2, NFR-07). Only the routing graph, which holds no user data, is shared.

#### FR-55: Route quality assessment — Proposed
Echo assesses every Activity's track and shows its **Route Quality**: a score from 0 to 100, a grade (good, fair or poor) and the flags that explain it. The flags are:
- sparse points (points per km below what the type needs);
- long gaps in time or distance (the largest gap and the 90th percentile);
- straight jumps (legs longer than a per-type length with no point between);
- implausible speed for the type;
- no track at all (for example every Samsung Health activity). Such an Activity is never given a route (decision Q-11);
- start or end far from the linked Visits;
- after matching: points far from any road, and a low matching confidence.

Details:
- The assessment runs automatically for new and changed Activities, and for all existing ones once (a backfill).
- An Activity whose type can be matched (FR-56) and whose score is below the "needs improvement" threshold is marked **needs improvement**. The thresholds are in the architecture (AD-35) and are starting values (A-11).
- Assessment never needs a third party.

#### FR-56: Road-matched routes — Proposed
For an Activity marked "needs improvement", Echo computes a **Matched Route** that follows the real road network, and shows it instead of the straight lines when the match is confident enough. From 2026-10-29, every matchable Activity of a matched type is matched, including a well-recorded one that does not need improvement, so that its roads are known (FR-61). A well-recorded drive's Matched Route is shown by the same confidence rule, and the Owner can still choose the recorded line.
- **Types.** Phase R1: drive, and transit on roads (buses). Phase R2: ride, walk, run and hike, each on a bike or foot network. Flight, ski, swim and "other" are **never** matched. A transit trip that turns out to be rail is left as recorded (decision Q-12).
- **Provenance.** Every piece of a Matched Route is *observed*, *inferred* or a *gap* (§3). On the map, inferred pieces are drawn more faintly (dashed), and gaps stay as recorded.
- **Confidence.** Each Matched Route has a confidence from 0 to 1. It is shown by default, with no acceptance needed, when its confidence is at or above the Owner's threshold, which starts at **0.7** (decision Q-14). Below it, the original stays in place; from Phase R2 the Owner can view the Matched Route and choose to use it (FR-58).
- **Distance.** When a source gave the Activity's distance (FR-19), it stays, and a Matched Route's road distance is shown beside it ("13.5 km recorded, 15.1 km on roads"), not in place of it. When no source gave a usable distance and Echo measured it along the recorded straight lines (as it does for most Google Timeline drives), the shown Matched Route's road distance **replaces** that measurement, in the panel, the lists and Insights; reverting to the original restores it.
- **Photos.** Photos taken on an inferred stretch stay where they are placed today, on the straight line between recorded points, until a Phase R2 change places them on the Matched Route by time.
- Each Matched Route records the engine, the map data version and the rule version that produced it (P-4).

#### FR-57: Automatic and on-demand runs — Proposed
- **Automatic.** After every import or sync, once fusion has settled, the route-quality service assesses the changed Activities and matches those that need it. A one-time backfill covers each Owner's existing history. From Phase R2, a new monthly map data version also re-matches existing routes in the background; in R1 it is used only for new matches.
- **On demand.** The Owner can:
  - run "Fix this route" on one Activity (the result appears within about a minute);
  - run "Improve routes" on a batch: the Activities of the current Atlas query, or a list from the data-quality insights (FR-59);
  - see the service's status for their own data: queued, running, last run, last error, map data version and coverage (NFR-14).
- **MCP and Assistant (Phase R2).** An external AI client (with a Personal Access Token) and the Assistant can read an Activity's Route Quality and request improvement of a list of Activities. That only adds derived data, so it falls within the P-3 exception and changes no source record.
- **Per Owner and fair.** Every run belongs to one Owner and touches only that Owner's Activities (NFR-07). Runs, including each Owner's backfill, share the same round-robin across Owners as all other background work, so one Owner's backlog never starves another Owner's runs (NFR-18, NFR-21).

#### FR-58: Original or matched; the Owner decides — Proposed
In the Activity panel, the Owner sees:
- a **quality badge**, the grade with its flags on hover;
- a **"Route improved"** indicator when a Matched Route is shown, with a toggle between *original* and *matched*;
- the confidence, and the shares of observed and inferred pieces.

They can also:
- **Revert** to the original for good. This is a decision that survives re-matching, re-fusion, new map data and re-import (P-5), as fusion decisions do;
- **Accept** a Matched Route that is below the threshold (Phase R2: "Use this route", at a confidence of 0.3 or more);
- run **"Fix this route"** again after changing the type (for example from drive to ride), which re-matches with the right network.

The map and the density view draw what the panel shows: the Matched Route when it is shown by default (confidence at or above the threshold, with no acceptance needed) or, from R2, accepted; otherwise the original. This includes the selected Activity: it is drawn on the Matched Route, with its recorded points as small markers, and the chart's hover marker stays on the recorded points, which carry the heart rate and time.

#### FR-59: Route quality in Insights — Proposed (Phase R2)
The data-quality section of Insights (FR-25) shows, for the current query:
- the count of Activities by grade;
- the count improved, the count waiting, the count that cannot be matched (no track, outside map coverage, type not matchable, rail, transit type unknown);
- the worst sources.

It also offers the batch action "Improve N routes". A rule-based proposal ("62 drives in this period are drawn as straight lines: improve them") links to it. The Owner can set the confidence threshold, the matched types, and whether Matched Routes are shown by default.

#### FR-61: Roads travelled — Proposed (Phase R1)
When a route is matched to roads, Echo records which roads it followed: route numbers (for example 431) and street names as the map data spells them (Hebrew in Israel), in travel order, with the distance on each. The Owner asked for this on 2026-10-29: "so I could look for things like all routes that passed through road 431".
- **Shown.** In the Activity panel, under the route line: "Via 4 › 431 › איילון צפון". Route numbers look like small road signs. It is shown only while the Matched Route is drawn. Each road can be clicked.
- **Searchable.** A **road filter** finds every Activity whose route followed a road. The Owner sets it by:
  - clicking a road in the panel;
  - typing "road 431" or "כביש 431" in the search box and pressing Enter;
  - asking the Assistant, or through MCP (`road`).

  It shows as a chip ("Road 431") beside the other filters and combines with them (time, area, types). The timeline, the density map and Insights count what it finds; visits are left out. A route number matches exactly (431 is not 4313); a street name matches in part ("גבירול" finds "אבן גבירול").
- **What counts.** Only the observed and inferred pieces of the line: the straight leg of a gap is on no road. A road followed for less than 100 m is left out (a junction crossed, or a slip of the matching). Only matches the Owner sees as right count: confident or accepted, never rejected ("Keep the original"), and never stale.
- **Coverage.** Every matchable drive and bus or taxi trip is matched for its roads (FR-56). Activities that are not matched (walks and rides until Phase R2, flights, records outside the map data) have no roads, so the filter does not find them.
- **Derived.** Roads belong to the Matched Route: exported, erased and recomputed with it.
- English names are not offered in R1: the map data has none for most streets.

#### FR-62: Rebuild a route from similar drives — Proposed (Phase R1)
Some drives cannot be placed on the roads from their own points: they are too sparse, or the engine breaks on them. In the first Owner's data on 2026-10-29 this was 2,713 of 14,369 matchable drives (2,081 low confidence, 632 failed). But the Owner drives the same trips again and again: **88%** of those drives (2,394) have at least one confidently matched drive whose start and end are both within about 500 m of theirs; 2,290 have three or more, and 2,315 have one that started within two hours of the same time of day. A drive home from work is the **reverse** of the drives to work (37 drives match only reversed).

**On demand,** for one drive the Owner chooses ("Rebuild from similar drives" in the Activity panel), Echo:
1. **Finds similar drives.** These are the Owner's own confidently matched drives of the same type that run along this drive's own recorded points, in either direction. The **way** decides, not the ends: a recording often starts late or stops early (the phone found its fix only on the road, or the last point is a stray one), so a drive is similar when most of this drive's points lie along it, wherever the two start and end. Among them, Echo prefers:
   - the way that most of the similar drives take (a common commute beats a one-off detour that happens to pass the same few points);
   - the same time of day and the same kind of day (a weekday morning commute for a weekday morning drive);
   - a way that could be driven in this drive's real time, without a long detour;
   - drives whose start and end are also near this one's.

   Echo tries the best drive of up to three different ways, so a rare way still gets its chance when the common one does not fit.
2. **Borrows points.** Between two of this drive's real points that both lie along the most similar drive, Echo adds synthetic points taken from that drive's road line. Each synthetic point is timed between the two real points, in proportion to the distance along the line, so the drive never moves faster than its type allows. Where this drive left the similar drive's way, nothing is borrowed.
3. **Matches again.** The real and borrowed points are placed on the roads as usual (FR-56).
4. **Suggests.** The result is drawn on the map as a **suggestion**, with what it is based on: "From 14 similar drives (6 agree), the closest on 3 Sep at 08:12, reversed", and its confidence. The Owner chooses **Use this route** or **Discard**. Nothing changes until they choose.

Details:
- **Results.** Measured on 60 of the first Owner's weak drives, picked at random: a rebuild placed **38** on the roads with confidence, against 6 by the normal matching. 9 had no similar drive that follows them; those are for marking the way.
- **Why the way, not the ends** (2026-10-30). A morning commute recorded with 11 points over 27 minutes started at home, but its last point lay about 2 km from each of the Owner's usual work ends (the recording stopped early or ended on a poor fix). Only **1** of the Owner's 9,143 confident drives had both ends within 500 m of it, so a search by the ends found nothing. Yet dozens of commutes had 9 of its 11 points within 200 m of their way.
- **The rules, in the Owner's terms** (the exact algorithm and every number are in architecture AD-38 rule 3):
  - *Similar* means: the Owner's own drive of the same type, matched on its own with a confidence of at least 0.8 and not reverted, along which at least **60%** of this drive's points lie, each within **150 m** and in the order driven. Either direction counts, and a round trip is read in order.
  - *Possible* means: the similar drive's way between this drive's first and last points along it can be driven in the time this drive took, at the type's top speed or less.
  - *Preferred*, in this order of weight: more of this drive's points along it; the same time of day (up to three hours apart); not a long detour (a way more than 1.3 times the drive's own straight legs costs, up to 1.8 times); the same kind of day (Friday, Saturday, or a working day); both ends near this drive's ends (within 500 m). Among the 30 best, drives that take the same way are grouped, and a way more drives take wins a close call.
  - Echo tries the best drive of up to **three** different ways, each with points borrowed every 200 m and every 600 m, and keeps the most confident result.
  - The panel says how many drives are similar, and how many of them take the chosen way: "From 14 similar drives (6 take this way), the closest on 3 Sep at 08:12, the other way". When they all take it, the count in brackets is left out.
  - When no drive is similar, the panel says why ("No confidently matched drive runs along this drive's points", or that none of those nearby follows them closely enough), and, while Echo is still preparing older drives for this search after an update, how many and to try again in a few minutes.
- **Partial overlap.** Points are borrowed only between this drive's own first and last points along the similar drive: a route is never extended to where the recording did not go (the place it "probably" ended is a guess the Owner can make by marking the way).
- **Stray points.** A point (other than the first or last) that the drive could only have reached, and left, at more than one and a half times its type's top speed, even allowing a minute's slack on each side (Timeline times are rounded to the minute), is left out of the search; so is a first or last point that could only be reached that fast from the nearest point that is not stray. It stays in the recording and in the match. This catches teleports of kilometres, not a merely poor last fix: that one point costs the resemblance little, as most of the points still follow the way.
- **Provenance.** Borrowed stretches are *inferred* pieces (dashed), never *observed*. The confidence counts only the drive's real points. It also counts the share of them that lie along the similar drive, so a weak resemblance gives a weak suggestion.
- **Lasting.** A route the Owner used is kept as it is when Echo's matching rule changes. It is rebuilt automatically only when the drive's own points change, and "Revert to recorded" still applies.
- **No similar drives.** When no similar drive is found, or the resemblance is too weak, the panel says so and offers marking the way (FR-63).
- Only the Owner's own drives are ever borrowed from (§2.2, NFR-07). Nothing leaves Echo's infrastructure (NFR-20).

#### FR-63: Mark the way — Proposed (Phase R1)
When even similar drives are not enough, the Owner can tell Echo where they drove. They choose **Mark the way** in the Activity panel, click points on the map in the order they drove through them, and choose **Rebuild**.
- **Placement.** Each marked point is placed in time between the drive's real points, by where it lies along the drive and in the order the Owner marked it. Echo then matches the drive through the real and marked points (FR-56), and the result is offered as a suggestion, as in FR-62. Up to 50 points can be marked; a point can be removed by clicking it.
- **Owner's input.** The marked points are a **decision**, like "Keep the original" (P-5). They are stored when the Owner uses the route, and every later match of that drive goes through them, including after a rule change or a re-import. The Owner can clear them ("clear them" in the panel), and the drive is then matched as usual again. They are exported and erased with the Owner's route data, and backed up.
- **Provenance.** Stretches through marked points are *inferred* pieces. A route the Owner used is shown whatever its confidence, as long as it is at least 0.3.

#### FR-60: Routes abroad — Proposed (Phase R3)
Activities outside the map data that Echo holds are flagged "outside map coverage" and left as recorded. At MVP, Echo holds Israel only (decision Q-13).
- Each Owner sees which regions hold the most of their own such Activities.
- Regions are added **on demand**: the operator adds regions from the "outside coverage" list, ranked by activity count. The admin sees only per-region counts summed over all Owners, never whose Activities they are, like the usage counters (FR-51).
- Map data for a region is added as a published artifact (AD-32), never fetched by the cluster at run time, and Activities there are then matched like the rest.

### 4.13 Atlas: Place Reviews (Proposed)

The Owner writes reviews of places on Google Maps. Echo shows, for each place they have been to, whether they reviewed it, what they wrote and how many stars they gave, and it reminds them of the places they went to and have not reviewed yet. Google offers no API for a person's own reviews, so the reviews come from the Owner's **Google Takeout** export ("Maps (your places)" → `Reviews.json`), imported like the Timeline (FR-17).

- The reviews are the Owner's own **source data** (P-1): kept as imported, never edited by Echo, removed with the import that brought them, and erased with the Owner (FR-49).
- Nothing leaves Echo: no Google API is called for a review. The links Echo shows open Google Maps in the Owner's browser.
- **Per Owner**, like the rest of Atlas (§2.2, NFR-07).

#### FR-64: Import my reviews — Proposed
The Owner imports the Takeout export as they import the Timeline: the zip, or the `Reviews.json` file on its own.
- **What is kept**, for each review: the place's name, address, country and position as Google gives them; the stars (1 to 5); the text; the answers to Google's questions about the place ("Price per person", "Service: dine in"); the date the review was written or last changed (when the export has one); whether it is published or only a draft; and the review's link on Google Maps (only a web link is kept). A review with neither stars nor text, or without a position, is skipped.
- **Any language.** Takeout names its files in the account's language; Echo reads the reviews file from the Maps folder whatever it is called, and recognises it by its content.
- **Again and again.** Importing a newer export updates each review (matched by its Google Maps link) and adds new ones; a review missing from a newer export is kept (an import never deletes), and deleting an import removes what it brought (FR-17).
- **Photos.** Google's documented export of reviews holds no photos (the Data Portability schema `maps.reviews` lists none). Echo therefore shows the Owner's **own photos taken at the place** (FR-27), and the review's link opens it on Google Maps, where the photos added to the review are. If the Owner's export turns out to include the review photos as files, a later story imports them.

#### FR-65: My review on each place — Proposed
- **Linked to the place.** Each review is linked to the place in Atlas that it is about, when one is clear:
  1. a place within 75 m whose name shares a word with the review's place name (a word of 3 letters or more, in any script, ignoring case and punctuation): the nearest such;
  2. otherwise the only place within 30 m;
  3. otherwise the nearest place within 10 m, when no other place is within 25 m;
  4. otherwise the review stays unlinked; it is still listed with the Owner's reviews.

  A place takes one review (Google keeps one review per place per person). Linking runs after every import of reviews, and again after every import of places, for the reviews not linked yet. A link stays until the place is deleted.
- **Never home or work.** A review is never linked to the Owner's home or work, so the café downstairs does not become the home.
- **A review names its place.** Many Timeline places have no name ("Place"). When a review is linked to such a place by position (rules 2 and 3), the place takes the review's name, and its address when it has none, unless the Owner named the place themselves.
- **Names first.** Reviews that a place's name matches are linked before those linked by position, so a review merely near a place never takes the place another review names.
- **In the place panel.** A reviewed place shows the review: the stars, the date, the text, the answers, "Draft" when it was never published, and **Open on Google Maps** (the review's link). The Owner's own photos at the place are shown as before.
- **In Insights.** A **Reviews** section shows how many reviews the Owner has written, their average stars, how many are linked to visited places, and the latest reviews; a click opens the place, or Google Maps for an unlinked review. It covers all reviews, whatever the current filters.

#### FR-66: Reminders to review — Proposed
Echo reminds the Owner of places they went to and have not reviewed.
- **Which places.** A place is *to review* when all of these hold:
  - it has a real identity: a name that is not generic ("Place", "Home", "Work", "Saved place", "Searched place"), identified automatically or by the Owner (FR-24);
  - its category is not an errand or a private place: fuel stations, buildings, offices and companies, works and industry, banks, post offices, public offices (town hall, court, police, embassy), stops and stations, health care (clinics, doctors, hospitals, pharmacies), schools, places of worship, car repair, homes and workplaces (the full list is in the architecture, AD-39);
  - the Owner stayed there **at least 10 minutes** on a visit that ended within the last **180 days**;
  - it is not a routine place: visited on **30 days or fewer** in all;
  - no review is linked to it;
  - the Owner has not set it aside (below).
- **Where.** The Insights **Reviews** section lists the places to review, the most recently visited first (up to 30, with the total), each with the last visit, the number of visits, and **Write a review**, which opens the place on Google Maps, where the review is written. The place panel of a place to review says "You have not reviewed this place" with the same link.
- **Setting aside.** For each reminder, the Owner can choose:
  - **Later**: hidden for 30 days;
  - **Not this one**: never again for this place;
  - **Not this kind**: never again for places of this category;
  - **I reviewed it**: hidden; the next import brings the review itself.

  Each choice is a decision of the Owner (P-5), kept across imports and undone from the place panel ("Remind me again").
- In the first Owner's data on 2026-10-30, 393 of 4,403 places have a real identity, and 43 of them had a visit of 10 minutes or more in the last 180 days; the reminders are meant to be a short list.

#### FR-67: Reviews from a scheduled Takeout on Drive — Proposed
Importing a Takeout by hand every time is a chore. The Owner can instead schedule Takeout to export "Maps (your places)" **every 2 months for a year** and **add it to Google Drive**, and connect Echo to their Drive once. Echo then picks up each new export by itself and imports its reviews (FR-64), so new reviews appear within two months without the Owner doing anything.
- **Why Drive.** Google offers no API for a person's own reviews: the Business Profile API serves the reviews a business receives, the Places API a few public reviews of a place, and the Data Portability API (the programmatic Takeout) is offered only in the EU, the EEA, Switzerland and the UK, not to an account in Israel (checked 2026-10-30). A scheduled Takeout delivered to Drive is the automatic route left.
- **Connecting.** In the import dialog, a **Google Drive** tab explains how to schedule the export and has **Connect Google Drive**. It asks Google for one more permission on the Owner's existing Echo sign-in: reading files in their Drive (read only). Echo is the Owner's own app and not reviewed by Google for this permission, so Google shows its "unverified app" warning once; the Owner continues past it. Declining leaves everything as it was.
- **What Echo reads.** Only the Owner's own files named like Takeout exports (`takeout-….zip`), never anything else in the Drive, and never a file someone else shared with them. From each it reads only the reviews file; the rest of the zip is not opened. An export larger than 500 MB is skipped with a note: the Owner should choose only "Maps (your places)" for this schedule (a Timeline or Photos export goes in a separate, manual Takeout).
- **When.** Once a day, and on **Check now**. Each export is imported once; a review already imported is updated, never duplicated (FR-64). The first check after connecting takes only the newest export (each export holds all the reviews). An export that failed for a passing reason (Google busy, the connection broke) is tried again at the next check; one that can never be read is noted and left.
- **Status.** The tab shows when Drive was last checked, how many new reviews the pickup has brought in all, the last export found and how many reviews it held, and any problem of the last check in words (for example "Sign in to Google again", "takeout-….zip is larger than 500 MB").
- **Disconnecting** stops the checks and forgets which exports were seen; the imported reviews stay (they are deleted with their import). The Drive permission itself is removed at the Google Account's "Third-party connections" page, which the tab links to.
- **Renewal.** Google ends a Takeout schedule after a year (6 exports); the tab reminds the Owner when no new export has arrived for 70 days.
- **Privacy.** The zip is downloaded to the backend's temporary disk only for as long as the reviews file is read, then deleted. Nothing leaves Echo; only Google's Drive API is called, with the Owner's own permission.

### 4.14 Settings and Appearance (Proposed)

The Owner finds some of Echo's text too small, the points on the map too small to grab, and the pointer too small to follow. Enlarging everything would only move the problem. Instead, a **Settings** page lets the Owner set each kind of thing on its own: the size of each kind of text, the size of each kind of symbol on the map and how far from it a click still takes it, the pointer, the colour of each kind of thing, and the style of each kind of route. Every change shows at once, everywhere, so the Owner sees the result before moving on, and every setting can be put back to its default.

- **Per Owner, on every device.** Settings are kept with the Owner's account, so another browser or the phone shows the same Echo. The browser also remembers the last settings, so Echo opens with them without a flash of the defaults; settings made in a browser before the account had any become the account's.
- **Defaults are today's Echo.** Nothing changes until the Owner changes it.
- **Bounded.** Every number has a sensible range (text from 8 to 28 px by kind, map symbols up to between 2.4 and 5 times their default), so a slip cannot make Echo unusable; **Reset** puts a section, or everything, back.

#### FR-68: The Settings page — Proposed
- A **Settings** entry (a gear) at the bottom of the navigation rail opens `/settings`. It is not an Area: Echo never opens on it by itself.
- Sections: **Text**, **Pointer**, **Map symbols**, **Colours**, **Routes**, **Charts**, and **Other settings** (where the other settings live: buttons that open Atlas's fusion settings and its imports and connected sources, and a link to the Google Account's third-party connections for Google permissions).
- Each control shows its value and its default; a section's **Reset** puts the section back, and **Reset everything** all of them. Changes apply at once and are saved by themselves; a line says when they were saved, or that saving failed.
- Each section has a small **preview**: sample text at each size, Echo's pointers, map symbols on a dark map background (with the grab distance as a ring), route lines of each type and piece, chart lines and the zones.
- Changes made in a browser are never lost: if saving fails, or the page is closed before saving, the browser keeps them and saves them the next time Echo opens.

#### FR-69: Text sizes — Proposed
The Owner sets, in pixels:
- **Body text** (default 15): most text, lists, panels, buttons; secondary body text follows it, half a pixel smaller.
- **Small text** (default 13): captions, labels, chips, notes, table headers.
- **Headings** (default 100%): titles and section headings, as a percentage of their default sizes (25, 21, 18, 16 px).
- **Charts** (default 11): axes and labels of charts and the timeline.
- **Map labels** (default 11): numbers on marked points, the area label, the map's attribution and controls.
- **Navigation** (default 11.5): the labels of the navigation rail.

Every text in Echo belongs to one of these kinds, including the texts sized outside the theme, so no text is left behind.

#### FR-70: Map symbols, grabbing and the pointer — Proposed
- **Symbol sizes**, in pixels: visits (radius 5) and the selected visit (8), photo points (4), photo pins (34), the recorded points of the selected route (2), the hover marker on the selected route (7), airports (3.5), and marked points (20).
- **Grab distance** (default 0 px, up to 20): how far beyond a point or line a click or hover still takes it. A larger distance makes small points easy to grab without making them look larger.
- **Pointer** (default: the system's): Echo's own pointer, drawn at 24, 32 or 48 px, for the arrow, the hand over things that can be clicked, the hand that grabs and drags the map, the crosshair of marking and drawing, and the text cursor. Browsers draw pointers of up to 128 px; where a browser or system limits them, it falls back to its own.

#### FR-71: Colours and route styles — Proposed
- **Colours** of each activity type (ride, hike, run, walk, drive, transit, flight, ski, swim, other), visits, photos, heart rate and health, the area filter, the inferred pieces of a route, gaps, marked points, and the selected route's highlight. The density map, the legend, the lists, the charts and the panels use the same colours.
- **Route styles**, for each activity type: line width, opacity, and pattern (solid, dashed, dotted). Also: the width of the selected route, the opacity of the others while one is selected, and the width and pattern of each piece of a road-matched route: observed, inferred (dashed by default) and gap (dotted by default).
- **Charts**: the colours of speed, elevation and cadence, and of the five heart-rate zones. Heart rate has one colour everywhere (the chart, the Insights trend, the map's hover marker): the heart rate and health colour.

### 4.15 Atlas: Flights (Proposed)

Echo knows when the Owner flew (the Timeline records a flight as an activity, about 100 of them since 2014 in the first Owner's data), but not which flight it was: the line on the map runs from the phone's last point before take-off to its first point after landing, and the times are the phone's guesses. The Owner wants each flight to be a real flight: its flight number, the airline, the aircraft, the airports, the scheduled and the actual times, and the route it really flew.

- **Who knows the flight number.** The Owner's mail does: booking confirmations, schedule changes and boarding passes name each flight (number, airports, date, local times). The Owner can also type it. Public flight data alone cannot tell which of several flights it was: on 27 Aug 2026 two Etihad flights left Abu Dhabi for Tel Aviv within 20 minutes of the recorded take-off (checked 2026-10-30), and Bangkok to Chiang Mai had three airlines within the hour.
- **Who knows what happened.** Public ADS-B data (aircraft transmit their position, collected by volunteers' receivers) does: when the aircraft actually took off and landed, which aircraft it was, and, for recent flights, the path it flew. Echo uses **The OpenSky Network** for flights and tracks, and **adsbdb** for airline names and aircraft. Both are free; OpenSky needs an account, which the operator holds.
- **Per Owner**, like the rest of Atlas (§2.2, NFR-07). A flight's details are Owner data derived from their activity and their mail; they are deleted with the activity and erased with the Owner (FR-49).

#### FR-72: Which flight it was — Proposed
Each flight activity can carry a **flight number** (airline code and number, as on the ticket: "EY593", "LY 315") and its **date**. It comes from, in order of precedence:
1. **The Owner**, who types it (FR-74) or confirms a suggestion (FR-75); it always wins, and is kept across imports and searches;
2. **The Owner's mail** (FR-73), found automatically;
3. nothing: Echo then **suggests** the public flights that fit the recorded flight, for the Owner to confirm, and never sets one by itself.

- **Airports.** Echo finds the departure and arrival airports from the flight's first and last recorded points: the airports with scheduled flights within **50 km**, nearest first (up to 3 each). A flight with no such airport at one end says so. A "flight" that starts and ends at the same airport, less than 100 km apart, is marked **probably not a flight** (the Timeline sometimes calls a fast drive a flight); the Owner can still type a flight number.
- **When.** Echo looks for every flight by itself, the newest first, within the hour after the flight is imported, and again when the Owner types a number, clears it, confirms a suggestion, or asks **Search again** (what the Owner asked for goes first). A search that failed for a passing reason (a service busy or unreachable) is tried again the next hour, up to 5 times. A flight that landed less than 36 hours ago is looked up again every hour until then: public flight data is published the day after.
- **Fused flights.** A flight recorded by two sources is one flight (FR-40); its details follow the fused activity.

#### FR-73: Find the flight in my mail — Proposed
With **email content** allowed (tier T2, §5.1), Echo searches the Owner's Gmail for the mails about each flight and takes the flight number from them.
- **Which mails.** Mails that name both airports by their codes (as bookings, changes and boarding passes do, "TLV", "AUH"), received from a year before the flight to two days after it; the newest 20.
- **What counts.** In a mail, a flight number counts for this flight when, after it, the mail names the departure airport, then the arrival airport, and a date that is the flight's day (or the day before, in the departure airport's time); and, when a time is given beside it, that time, in the departure airport's time zone, is between 4 hours before and 2 hours after the recorded take-off. Mails that describe their flights in the schema.org format airlines use for Gmail (`FlightReservation`) are read from that first.
- **Which one.** Of the numbers that count, the one whose scheduled time is nearest the recorded take-off; between equals, the newest mail's (a schedule change replaces the booking).
- **What is kept.** The flight number, the scheduled departure (and arrival, when the mail gives it), and which mail it came from: its subject, sender and date, and a link that opens it in Gmail. **Mail bodies are never stored**; they are read in memory and dropped. Nothing from the mail leaves Echo.
- **Not allowed.** Without email content allowed, the flight panel says the mail was not searched and offers **Allow reading email content**, the same permission as the signatures of Contacts (T2). Once allowed, the flights that were not searched in mail are searched.

#### FR-74: Type the flight number — Proposed
In the flight's panel, the Owner can type the flight number. Its day is the flight's recorded day (in the departure airport's time): the flight is the activity, whose day is known.
- Accepted as written on a ticket or a board: an airline's two-character code and a number ("EY593", "EY 593", "ey0593"), or its three-letter code ("ETD593"). Echo stores it in the two-character form, without leading zeros.
- Echo looks it up at once and shows the result within a minute; the panel waits for it.
- **Clear** removes the Owner's number; Echo then looks for the flight again by itself (mail, then suggestions).

#### FR-75: The flight's actual details — Proposed
For a flight with a number, Echo finds the actual flight in public data and shows:
- **Airline**: its name and codes; **Operated by**, when another airline flew it (a codeshare: the mail says "EY7629", the aircraft flew as Bangkok Airways);
- **Airports**: departure and arrival, with code, name and city;
- **Times**, each in its airport's local time: **scheduled** departure and arrival (from the mail; not shown when the number was typed and no mail names it), **actual take-off and landing** (from ADS-B: when receivers first and last saw the aircraft near the airports, within a few minutes of the real take-off and landing), the **delay** against the schedule, and the **time in the air**;
- **Aircraft**: type and registration (for example "Airbus A321neo · A6-AEA"), and the call sign it flew under;
- **Distance**: great-circle distance between the airports.
- **How it is matched.** The aircraft that left the departure airport around the scheduled time (or the recorded take-off) under the flight's call sign; or, when airlines fly under a call sign that is not the flight number (Etihad's EY593 flew as `ETD5SM`), the only flight that left that airport for the arrival airport within the scheduled window. When public data has no such flight (coverage is thinner before 2018 and at some airports), the panel shows what the mail said and says that no public record matched.
- **Suggestions.** A flight without a number shows the public flights that fit it (left the airport near the recorded take-off and landed at the arrival airport near the recorded landing), at most 3, best first, each with its airline, call sign and times, and **This one**. Confirming one makes it the flight (FR-72). The choice names the flight itself (its aircraft and take-off), so a list that changed meanwhile never confirms another flight.
- **Sources.** The panel names the sources ("Flight data: The OpenSky Network · Airlines and aircraft: adsbdb · Airports: OurAirports, mwgg/Airports") and when they were read.

#### FR-76: The route it flew — Proposed
- **The flown path.** For a flight that landed within the last **30 days** when Echo looks it up, Echo keeps the path the aircraft flew (OpenSky keeps tracks for 30 days only), with its altitude, and the map draws the flight along it.
- **Otherwise** the map draws the great-circle arc between the actual departure and arrival airports (no longer between the phone's last and first points), and the panel says the flown path is not available for older flights.
- The Owner's own recorded points stay as they are; the path is the aircraft's, drawn in the flight's style.

### 4.16 Contacts: Auto-fill in the Contact Card (Proposed)

Most of the Owner's Contacts are written in Hebrew. Their phonetic names (the Latin spelling Google and the phone use to sort them and for voice) and their email addresses are often empty, although Echo can usually find both: the Owner's other Contacts already spell most Hebrew names, an English version of the same person is often on file, and the person's mail is in the Owner's mailbox. FR-14 already proposes emails for many Contacts at once, from the activity scan; these requirements bring both fills into the contact card, for the one Contact the Owner is editing, at a click.

- **Only the draft.** Both fills change the card's draft, never Google. The Owner sees the value, can change it, and saves it with **Save** as any edit (FR-05). Closing the card without saving drops it.
- **The card and the merge dialog.** The contact card's form is also the merge dialog's (FR-10), so the fills are there too.

#### FR-77: Fill the phonetic name from the Hebrew name — Proposed
An empty phonetic field (first, middle or last) whose name part is written in Hebrew shows an **auto-fill** symbol inside it. Clicking it fills **every empty phonetic part** whose name part is in Hebrew, in Latin letters, and says where each spelling came from.

- **Where the spelling comes from**, in order:
  1. **The same person in English.** Another of the Owner's Contacts with the same phone number or email address whose name is written in Latin letters (a Hebrew/English twin, FR-10): its name part is the phonetic part ("from Dana Cohen, same phone"). When several such Contacts disagree on a part, none of them is used for it.
  2. **How the Owner's Contacts spell the name.** For each Hebrew word, the Latin spelling the Owner's other Contacts most often carry for it in the same position (the phonetic first name of each Contact named "יוסי" says how the Owner spells יוסי: "Yossi"). It follows the Owner's own conventions ("Cohen", not "Kohen") and needs no service.
  3. **Gemini**, for the words neither of the above spells: only those Hebrew words are sent, with no other detail of the Contact (§5.2). The panel says "suggested by Gemini; check the spelling".
- **A part is filled whole or not at all.** When one word of a part cannot be spelled (Gemini not configured, its limit reached, or no answer), that part stays empty and the card says which word could not be spelled. A part is never half Hebrew.
- **When.** Only when the Owner clicks: Echo never fills a phonetic name by itself, and nothing is sent to Gemini without that click.

#### FR-78: Find the email in my mail — Proposed
An email field that is empty shows a **find in mail** symbol inside it; a Contact with no email at all shows **Find in mail** beside **Add email**. Clicking it looks for the person's address in the Owner's mail, by their name.

- **Which names.** The Contact's names as in the draft (so a phonetic name just filled counts): the full name, first and last name, the phonetic name, and each nickname with the last name. A name needs at least two words (a first name alone matches too many people); without one the symbol says "Needs a first and last name".
- **Where it looks**, both at once:
  1. **The activity scan's names** (tier T1, FR-12): the addresses whose display names in mail headers and meeting invitations match a name, as FR-14 does.
  2. **The mailbox** (tier T2, §5.1): a Gmail search for mails **from** the person by each name, the newest 25, of which Echo reads only the sender (name and address), never the subject or the body. A sender counts when its name or address matches one of the Contact's names (Gmail's search also finds partial names).
- **The Owner's own addresses** are never proposed.
- **What the Owner sees.** At most 3 addresses, best first: the exact name before a partial one, then the most mail. Each says how it was found: "as “Dana Cohen” · 12 mails · last 3 weeks ago". An address already on another Contact says "already on Dana Levi" and is listed last. When the only address found is an exact match on no other Contact, it is filled at once; otherwise the Owner picks one (or none). The type is **Home** for a personal mail service (Gmail, Outlook, Walla and the like) and **Work** otherwise.
- **Without T2** only the activity scan's names are checked, and when nothing is found the card offers **Allow reading email content** (the T2 opt-in; it leaves the page, so the card says so). Without T1 either, only the mailbox search would run, so the symbol asks for that permission.
- **Nothing is stored**: the senders are read in memory for the click and dropped.

## 5. Privacy Model

Privacy is a product pillar, not a compliance appendix. The requirements in §4.10 and NFR-07, NFR-08 and NFR-11 implement it.

### 5.1 Opt-in Tiers

| Tier | What Echo reads | Default | Status |
|---|---|---|---|
| **T0: Base** | Google Contacts (read/write); the Owner's profile (and, from Phase 0, their email address, checked against the allowlist, FR-51) | On at sign-in | Built |
| **T1: Touchpoints** | Gmail headers (From/To/Cc/date), Calendar events (time, attendees, title) | Off | Built |
| **T2: Email content** | The permission (`gmail.readonly`, which replaces the T1 Gmail permission) allows reading the whole mailbox. Echo reads only the last 3 emails from each Contact the Owner chooses (up to 10), and, for each flight in Atlas, the newest 20 mails that name both its airports (FR-73). When the Owner asks to find a Contact's email (FR-78), it reads the sender (name and address) of the newest 25 mails Gmail finds from that Contact's names, and nothing else of them. It processes them in memory and never stores them; of a flight's mails it keeps only the flight number, the scheduled times and which mail (subject, sender, date). | Off | Built (flights, find email: Proposed) |
| **T3: Life sources** | Each Atlas source connected or imported separately (Timeline, Sports Tracker, health exports, OneDrive photo metadata) | Off per source | Built |
| **T4: Faces** | Face detections and templates computed from Photos | Off; separate biometric consent | Proposed |

- Tiers are per Owner. One Owner's grants never widen what Echo reads for another.
- The Connect layer needs no new tier, because it only joins data the Owner has already granted.
- It never fills a gap in a tier the Owner has not granted (FR-31, FR-40).
- Address lookup (FR-47) is not a tier, but it widens what leaves Echo's infrastructure, so it is a separate switch and is off by default (P-6).
- The Planned Assistant actions (FR-16) would need a further *write* tier above T2. It is not defined until those actions are scheduled.

### 5.2 Third-Party Disclosure Register

| Third party | Receives | When | Owner control |
|---|---|---|---|
| **Google (People, Gmail, Calendar)** | API calls for each Owner's own data, with that Owner's own grant | T0–T2 | Revoke tier; Google account permissions |
| **Google Gemini** | Assistant messages, the records the Assistant looks up (contacts and activities; with Connect, only the matched results and citations that Echo computed, which name people with places and times, per FR-31 and Q-5), web search queries, T2 email text for extraction, and, when the Owner clicks to fill a phonetic name (FR-77), the Hebrew words of that Contact's name that the Owner's own Contacts do not already spell (nothing else of the Contact) | Only when the Owner uses the Assistant, enrichment or the phonetic fill; Connect results only while the Owner's switch is on (default on) | Do not use the Assistant; disclosure in the panel; switch "Let Gemini word answers about people and places" (FR-31, FR-47) |
| **OpenStreetMap services** (Nominatim, Photon, Overpass) | Coordinates, including automatic background lookups of frequent Places (never home or work stays) | Manual Identify; background identification | Turn off background lookup (Proposed, FR-47) |
| **OpenStreetMap Nominatim** (address lookup, Proposed) | Free text: the location text of calendar events (FR-34) and the postal addresses of Contacts (FR-36). Each text is sent once and the answer is cached. | Only while the address lookup switch is on; event locations also need T1 | Address lookup switch (FR-47); **off by default** |
| **Map tile servers** (OpenFreeMap, OSM, Esri) | Viewed map areas | When the map is shown | none (inherent) |
| **Microsoft Graph** | OneDrive listing and thumbnail requests | T3 OneDrive | Disconnect |
| **Google Drive** (backups, Proposed; Q-8) | Nightly system-level backups of Echo's database, holding **every user's data**, encrypted before upload with a key held in Vault and never in Drive (NFR-05). They go to the operator's Drive, through a scope that reaches only the files the backup job creates. Google sees file names, sizes and times, not content. Face data is never in a backup. | Daily, once backups are set up (Phase 0) | Operator only (system level); an Owner's erase (FR-49) reaches backups when they expire |
| **Sports Tracker** (unofficial API) | Login once; workout listing and downloads, on a schedule (FR-52) | T3 Sports Tracker | Disconnect |
| **Cloudflare** | All traffic (tunnel, Access) | Always | none (hosting choice) |
| **The OpenSky Network** (Proposed, FR-75, FR-76) | Airport codes and time windows ("flights that left LLBG between 07:00 and 13:00 UTC on 27 Aug 2026") and, for a recent flight, an aircraft's address and a time. No Owner identity is sent: the requests carry the operator's OpenSky account, not the Owner's. Together they reveal that someone using Echo was interested in that flight. | Once per flight looked up | Do not open flights; the operator can remove the OpenSky credentials (FR-72 then shows mail and typed numbers only) |
| **adsbdb** (Proposed, FR-75) | Airline codes ("EY"), call signs ("ETD5SM") and aircraft addresses, without a key and without any Owner identity | Once per flight looked up (airlines cached) | as above |
| **Geofabrik** (OpenStreetMap extracts, Proposed, FR-56, FR-60) | **Nothing about any Owner.** CI downloads the latest public map extract (Israel and Palestine, and later any region the operator adds; Geofabrik updates them daily) once a month to build the routing graph. The request comes from GitHub Actions, not from the cluster, and carries no user data. It is listed for completeness: road matching itself runs on Echo's cluster and sends nothing out (NFR-20). | Monthly, from CI | Operator only: remove a region from the build list |

**Never shared:** face data (T4), stored tokens and secrets, or an Owner's whole data set in bulk to any party, apart from the encrypted backups above. Route tracks are never sent to a hosted routing or map-matching API (NFR-20). **No Owner's data is ever shared with another Owner.**

### 5.3 Biometric Data

- Face templates are biometric data, and they mostly describe people other than the Owner.
- Echo processes them only with T4, only on Echo's own infrastructure, only from photos (never videos), and never for anyone the Owner has marked "never recognize".
- Only named people are kept. Unnamed clusters are discarded when the review session ends (FR-44).
- Face data is per Owner. It is never shared with, shown to or matched against another Owner's.
- They are deleted completely when T4 is revoked (FR-43, FR-44). Face data, and the links and Encounters derived from it, are kept out of backups, so no copy outlives the revocation.
- The first Owner has accepted storing face data of friends and family who have not consented (Q-2, answered 2026-10-28). Each invited user who turns on T4 makes the same choice for their own photos, through the same consent screen (FR-43).

## 6. Cross-Cutting Non-Functional Requirements

*Status follows §0. Several are Phase 0 platform items that came from the as-built scans.*

| ID | Requirement | Measure / done when | Status |
|---|---|---|---|
| **NFR-01** | **No secrets in the repository.** | No credential, token or real password is in any tracked file; the currently committed values (a Vault dev token in `appsettings.json`, a default Postgres password in `values.yaml`) are rotated; a secret scan runs on every PR and blocks on a finding. | Proposed |
| **NFR-02** | **Automated build and test.** | Every PR runs backend and frontend tests; a merge to the trunk produces arm64 images tagged by commit, with no hand build; the time from merge to running in the cluster is ≤ 30 min with at most one human approval. | Planned (Epic 9 §6) |
| **NFR-03** | **Sign-in does not decay.** | No Owner's Google grant is forced to expire on a 7-day cycle; after Phase 0 an Owner re-consents only when they change tiers or revoke access. Done by publishing the Google app to Production, unverified (Q-3), within the limits stated in §2.2: the unverified warning and at most 100 users in the app's lifetime. | Planned (Epic 9 §6) |
| **NFR-04** | **Durable Owner state.** | Opt-ins, refresh tokens, the Activity Snapshot, own addresses, dismissals, Person Links and Encounters survive a restart or loss of the cache; losing the cache costs at most a sign-in. | Planned (Epic 9 §6) |
| **NFR-05** | **Backup and restore.** | All Echo-held data (every user's) is backed up daily by the operator, as a system-level job, to the operator's **Google Drive** (Q-8), which shares no node or disk with the cluster. Each backup is encrypted before upload with a key held in Vault and never stored in Drive. Recovery point ≤ 24 h; a restore is tested at least once per phase; losing one node does not lose data. Face data is never backed up (FR-43). | Proposed |
| **NFR-06** | **Docs match reality.** | Deployment, project-context and index documents describe the as-built system. Epic documents that disagree with the code are corrected or marked superseded by the as-built inventories. Examples: Epic 11's tolerances and table names; and Epic 7's claim that sign-in uses `include_granted_scopes`, which is false and stays false (the code asks for exact scopes on purpose, see addendum §A2), so that claim is marked superseded. Stale documents are fixed in Phase 0, and each later phase updates them in the same PR as the change. | Proposed |
| **NFR-07** | **Owner isolation (hard gate).** | Every stored record, cache entry, queue item, file, MCP token and Assistant tool result is scoped to one Owner, and every query and MCP call is filtered by it. Automated per-user isolation tests, with at least two Owners, prove for every module, REST route, MCP tool, worker and cache that one Owner's session or token can neither read nor change another Owner's data, and that the admin role reads no Owner's data. **Hard gate:** the tests run on every PR and block the merge on a failure, and no user other than the first Owner is added to the allowlist until they pass. | Built for scoping; Proposed for isolation tests and the gate (Phase 0) |
| **NFR-08** | **Encryption at rest for sensitive data.** | Tokens, source secrets, the Activity Snapshot, own addresses and (T4) face templates are encrypted at rest; the key material is not stored with the data it protects. | Partial. Encryption of tokens and the Activity Snapshot is Built. Keeping the key ring apart from the data is Proposed for Phase 0, because today the key ring sits in the same Redis. Faces and Connect data are Proposed. |
| **NFR-09** | **Responsive at personal scale on the Pi.** | With the first Owner's real volume (157k Photos, years of tracks) and the expected handful of users (A-10), the map, timeline, contact card Encounters and a Unified Timeline day each load at p95 ≤ 2 s; background work (fusion, photo placement, face clustering), for any Owner, never pushes interactive p95 above that. `[ASSUMPTION A-6: 2 s is achievable and adequate on the Pi at this scale.]` | Proposed |
| **NFR-10** | **Graceful degradation.** | Done when these tests pass and are re-run each phase: **(a) Database down** (Postgres stopped, cache up, backend restarted while it is down): the Owner can sign in, list, search and edit Contacts. While the database is down, Echo may ask the Owner to sign in again when Google's short-lived access expires (about hourly), and that sign-in works. Atlas, Touchpoints, Connect, the Timeline and the Privacy Center each show a "temporarily unavailable" message within 5 s, and the Owner is not signed out. **(b) Third party down** (each of Gemini, OpenStreetMap, Microsoft Graph and Sports Tracker blocked in turn): only the features that use that party show an error that names it, and a smoke test of every other Area passes. **(c) Power cut** (both nodes power-cycled): every Echo service is Ready within 15 min of Vault being unsealed, with no other manual step. **(d) Database down and cache emptied at the same time:** Echo is down and says so, and it recovers with no data loss once the database returns. | Built in part (Contacts does not use the database today); Proposed for the rest: (a), (c) and (d) in Phase 0, (b) in Phase 1 |
| **NFR-11** | **Third-party etiquette and cost caps.** | OSM usage policies are respected (Nominatim ≤ 1 req/s **across all users together**, with an identifying User-Agent; answers cached); Gemini and geocoder use each stay within **per-Owner** daily caps and a global daily cap (NFR-18); the cross-domain Assistant does not raise the per-question token cost by more than 2× compared with today. | Built for OSM and Gemini caps; Proposed for per-Owner geocoder caps and the budget |
| **NFR-12** | **Recomputable derived data.** | Fusion, Person Links, Encounters, Face Clusters, Route Quality and Matched Routes can be rebuilt from Source Records plus Owner decisions with no loss of Owner decisions. | Built for fusion; Proposed for the rest |
| **NFR-13** | **Bilingual correctness.** | Search, sort, matching (names, Person Links, suggested emails) and Assistant answers handle Hebrew, English and mixed script; a Hebrew/English twin of one person is treated as one Person. Done when an automated suite with at least these cases passes: (1) "דנה כהן" and "Dana Cohen" with the same phone are one duplicate group, and, unmerged, one Person; (2) searching "דנה", "dana" or the phonetic name finds the same Contact; (3) a list of Hebrew and Latin names sorts Hebrew by Hebrew collation and Latin by Latin collation, with each in its own A–Z / א–ת section; (4) a mail display name in one script proposes a Contact whose name is in the other (suggested emails, attendee Person Links); (5) the same when/where/with-whom question in Hebrew and in English cites the same records. | Built for Contacts; Proposed for Connect |
| **NFR-14** | **Observability of background work.** | The Owner can see the state of every background job (activity scan, source sync, fusion, photo placement, place identification, face clustering): queued, running, last success, last error. Logs are structured. A usage view shows scans, Assistant calls and their cost. | Built in part; usage view Planned (Epic 9 §6); Proposed for Connect and faces |
| **NFR-15** | **Accessibility.** | Core views (contact table, card, Atlas panels, Unified Timeline, Privacy Center) are usable by keyboard and pass automated axe checks with no serious violations. | Built in part (jest-axe in Contacts); Proposed for new views |
| **NFR-16** | **Security of external access.** | Personal Access Tokens are hashed, revocable and scoped (FR-32); remote MCP access requires Cloudflare Access (A-9); OAuth flows are protected with a state parameter; every REST route except sign-in, health and the legal pages requires authentication, enforced in one place rather than per route. | Built for Atlas; Proposed for central enforcement (Phase 0) and token scopes (Phase 1) |
| **NFR-17** | **Supported runtimes only.** | Every runtime and framework Echo runs on (the .NET runtime, the Node.js build toolchain, the database server) is within its vendor's support window, and a move to a supported version is planned at least one month before support ends. Today the backend's runtime leaves support on 2026-11-10, so the move is part of Phase 0. | Proposed (Phase 0) |
| **NFR-18** | **Per-user fairness and capacity on the Pi.** | Each Owner has their own caps on Gemini calls and on geocoder lookups (OpenStreetMap), per minute and per day, which survive a restart; one Owner reaching a cap never blocks another. Background work (scans, syncs, fusion, derivation, face clustering) is shared **round-robin** across Owners, so no Owner's backlog (for example a first photo pass) starves another's: with one Owner's large backlog queued, another Owner's new import starts within 5 min. Echo is sized for a handful of users (A-10); the allowlist maximum (FR-51) enforces it, and the admin page shows each Owner's usage. | Proposed (Phase 0 for caps and the allowlist maximum; Phase 1 for worker fairness) |
| **NFR-19** | **Google verification before opening up.** | Before Echo admits anyone beyond an invited circle, or approaches Google's 100-user lifetime cap for unverified apps, the Google app passes OAuth verification (brand, privacy pages, demo video) and the Gmail restricted scopes pass a CASA security assessment, renewed yearly. Until then, Echo stays invite-only (FR-51). | Planned (Epic 9 §6); a gate, not in a phase |
| **NFR-20** | **Route matching stays home.** | Assessment and road matching (FR-55 to FR-60) run only on Echo's own cluster. No track, point or Activity id is sent to a hosted routing, map-matching or roads API. The route-quality pod can reach only the backend, Postgres and cluster DNS (enforced by a network policy on the pod), and the routing engine's configuration makes no outbound calls and fetches no map tiles. Map data reaches the cluster only as a published image built in CI. A test that runs a match with egress blocked passes. | Proposed (Phase R1) |
| **NFR-21** | **Route quality fits the Pi.** | The route-quality service and its routing engine together stay within their declared CPU and memory limits (AD-36). The backfill of the first Owner's about 25,000 Activities completes within 24 h. While it runs, interactive p95 stays within NFR-09. A single "Fix this route" completes within 60 s, even during a backfill. Route work is shared round-robin across Owners, as NFR-18 requires of all background work: with one Owner's backfill running, another Owner's new import is assessed within 5 min. | Proposed (Phase R1) |

## 7. Non-Goals

- **Not a social product or an open service.** Echo has several users, each with their own private data, but there is no sharing between them, no family accounts, no public profiles, no notifications to other people and no public sign-up (§2.2).
- **No cross-user matching.** Echo never links, matches or compares one Owner's people, places or faces with another's, even when the same person appears in both.
- **Not a replacement for Google Contacts or OneDrive.** They remain the stores. Echo does not copy contact fields or photo files.
- **No autonomous actions.** Echo never sends an email, a message or an invitation, and never edits a Contact, unless the Owner applies it.
- **No scraping and no terms violations.**
  - LinkedIn and Facebook data comes only from the Owner's own exports.
  - WhatsApp, phone calls and SMS come only from exports or backups (Planned), never from a device hook.
- **No cloud face recognition.** No face data goes to any third-party service.
- **Not a medical product.** Health insights describe the Owner's own trends and are not diagnoses.
- **No advertising, analytics trackers or data sale.**

## 8. Roadmap and Scope

| Phase | Theme | Contents | Exit criterion |
|---|---|---|---|
| **0: Realign** | Hygiene before growth, and ready for invited users | NFR-01 to NFR-06 (NFR-03 by Production, unverified; NFR-05 to Google Drive), NFR-07 isolation tests as a hard gate, NFR-08 key separation, NFR-10 tests (a), (c) and (d), NFR-16 central authentication, NFR-17 supported runtime, NFR-18 per-Owner caps and allowlist maximum; FR-03 Echo branding; FR-50 legal pages; FR-51 invite-only sign-up; FR-53 Samsung Health time alignment | No secrets in the repo; CI builds and deploys; no weekly re-consent; Contacts state is durable; encrypted backups to Google Drive are tested; docs are true; the backend runs on a supported runtime; NFR-10 (a), (c), (d) pass; the isolation gate passes and a second user can be invited (UJ-7); Samsung Health median start-time difference vs matched GPS sources < 60 s |
| **1: Connect** | Contacts ↔ Atlas | FR-31 to FR-42, FR-47, FR-48, FR-49, FR-52; NFR-09, NFR-12 to NFR-14 for Connect data; NFR-10 test (b); NFR-11 token budget; NFR-15 for the Unified Timeline, Privacy Center and Person views; NFR-16 token scopes; NFR-18 worker fairness | UJ-3 and UJ-4 work on the first Owner's real data; SM-1 to SM-3 are measured; NFR-10 (b), the NFR-11 budget and the NFR-18 fairness test pass; any Owner can export and erase their own data |
| **2: People in Photos** | Opt-in faces | FR-43 to FR-45; NFR-14 and NFR-15 for the face views | UJ-5 works; revocation is proven to delete all face data; unnamed clusters are proven discarded after review; no face data is reachable across Owners |
| **3+: More of me** | Breadth | FR-54 LinkedIn first (Q-7), then FR-04, FR-11, FR-16, FR-21, FR-26, FR-30 and the other §4.11 sources | Chosen per phase; LinkedIn connections link to Contacts |
| **R1: Routes MVP** (an Atlas track parallel to Phase 1) | Car trips on the roads | FR-55 (all types assessed); FR-61 (roads travelled and the road filter); FR-62 and FR-63 (rebuild from similar drives, mark the way); FR-56 and FR-57 for drive and bus or taxi transit, Israel map data only, without the MCP and Assistant tools or re-matching on a new map data version; FR-58 without accepting a Matched Route below the threshold; NFR-20, NFR-21 | It needs the `EchoService` scheme and CI image builds of Phase 0 (architecture AD-3, AD-22); if Phase 0 has not delivered them when R1 starts, R1 builds them itself (decided, architecture §12). Exit: the first Owner's drives in Israel are drawn on roads; the spike on the Pi confirms the AD-36 budget; NFR-20 and NFR-21 pass; the two-Owner isolation gate (NFR-07) and the round-robin fairness test (NFR-18) cover the route service |
| **R2: Routes on foot and bike** | Every matchable type; insights; the rest of R1's features | FR-56 for ride, walk, run and hike; FR-57's MCP and Assistant tools and re-matching on a new map data version; FR-58's "Use this route" below the threshold; photos placed on the Matched Route; FR-59 | Matched foot and bike routes are rejected by the Owner in ≤ 10% of the cases they review (the SM-3 measure applied to routes) |
| **R3: Routes abroad** | Coverage | FR-60 | The regions added on demand (Q-13) are matched; the rest are flagged "outside map coverage" |
| **Gate: beyond the invited circle** | Opening up | NFR-19 Google verification and CASA | Not scheduled. Required before anyone outside the invited circle is admitted, or before the user count nears 100 |

### 8.1 In Scope for the Next Release (Phases 0 and 1)

- The Phase 0 platform items listed above, including invite-only sign-up, per-user isolation and caps, and the Samsung Health time fix.
- Echo branding.
- Person Links, Encounters (from the calendar and from the Owner), the address ↔ Place match, and the Person views in Contacts and Atlas.
- The Unified Timeline.
- One Assistant, and MCP read access to people.
- The Privacy Center, export and whole-Echo delete, per user.
- Scheduled Sports Tracker sync.
- Routes MVP (Phase R1): assessment of every Activity, and road matching of drives and road transit in Israel, with the original or matched toggle, for every Owner. The first Owner asked for this directly. It runs as its own track and does not wait for Connect.

### 8.2 Out of Scope for the Next Release

- Faces (Phase 2).
- Deeper health and self-insight (FR-26).
- New sources (§4.11), including LinkedIn (FR-54), which comes first after Phase 2.
- Hebrew UI (FR-04).
- Persistent chat history (FR-30).
- Google verification and CASA (NFR-19), and any opening beyond the invited circle.

## 9. Success Metrics

*This is a personal product. Each metric is measured by Echo itself or by a short quarterly self-check by the Owner. The targets are proposed starting values. `[ASSUMPTION A-5: the targets are not confirmed by the Owner.]`*

**Primary**
- **SM-1: Time to answer a life question.** The median time for the Owner to answer a fixed set of 10 "when, where, with whom" questions, using the Assistant or the Unified Timeline. Target ≤ 30 s, against several minutes across apps today. Measured quarterly. Validates FR-31, FR-40, FR-41.
- **SM-2: Connected life coverage.** Target ≥ 70%, measured monthly over the last 12 months. Validates FR-33, FR-34.
  - *Denominator:* calendar events on the calendars Echo reads that were not declined or cancelled, have a location that names a physical place (not a video link), have at least one attendee other than the Owner who is linked to a Contact, and overlap a Visit or Activity of the Owner.
  - *Numerator:* the events in the denominator that have at least one Encounter with a Place, either derived and not rejected, or confirmed.
  - *Second measure (no target):* of the Contacts with at least one Touchpoint in the last 12 months, the share that have at least one confirmed Person Link of a kind other than the automatic email link from their own fields (an attendee, a twin or a Face Cluster).
- **SM-3: Connect precision.** Of the derived Encounters and proposed Person Links and address matches that the Owner reviews, the share the Owner rejects. Target ≤ 10%, measured monthly. Validates FR-33, FR-34, FR-36, FR-42.

**Secondary**
- **SM-4: Place coverage of Photos.** The share of Photos placed in time and space. Today it is 47% (74,061 of 157,021). This metric tracks it, with no target, because Photos without any location evidence cannot be placed. Validates FR-27.
- **SM-5: Habit.** The Owner opens Echo on at least 3 days a week. Validates the product thesis.
- **SM-6: Platform health.** After Phase 0: forced re-consents = 0 per month, hand-built deploys = 0, failed restores = 0, secret-scan findings on the trunk = 0, and known-stale documents = 0. Validates NFR-01 to NFR-06.
- **SM-7: Insight usefulness.** The share of Contacts and Atlas Proposals the Owner applies or acts on, rather than dismisses. Target ≥ 50% over a month. Validates FR-13, FR-14, FR-15, FR-25.

**Counter-metrics (do not optimize)**
- **SM-C1: Number of links and Encounters created.** Precision matters more than volume. A wrong "you met X" is worse than a missing one. This counterbalances SM-2. SM-3 guards it directly.
- **SM-C2: Data volume ingested.** More sources are not better if they add noise or exposure. This counterbalances SM-2 and SM-4.
- **SM-C3: Data sent to third parties.** It should fall or stay flat as features grow, and a rise must be a deliberate decision. This counterbalances SM-1, because a faster answer must not come from sending more to Gemini.
- **SM-C4: Silent writes.** The number of writes to Google Contacts that did not come from an Owner edit or an applied Proposal. It must be 0, always. This is a guardrail, not a target.

## 10. Risks

| Risk | Effect | Mitigation (product level) |
|---|---|---|
| Wrong Encounters ("you met X" when you did not) | Loss of trust in the whole product | Precision over recall (SM-C1); evidence and "Why?" on every Encounter (FR-42); Owner confirm/reject (P-5) |
| Biometric data about non-consenting people | Legal and ethical exposure | T4 off by default and per Owner, kept on Echo's infrastructure, named people only, photos only, "never recognize", full delete, never matched across Owners (§5.3); accepted by the first Owner (Q-2) |
| One Owner sees another Owner's data | Loss of trust; privacy breach | Tenancy on every store, queue, cache and token; NFR-07 isolation tests as a hard gate; the admin role reads no data (FR-51) |
| Cross-domain context sent to Gemini reveals more combined information | Privacy | Only matched results go to Gemini (FR-31, Q-5); per-user switch to keep them out; disclosure names the combined data; the budget in NFR-11 |
| Pi capacity (face clustering over 157k Photos, Unified Timeline queries, several users) | Slow or starved interactive use; one user starving another | NFR-09 priority for interactive work; batch background jobs; round-robin across Owners and per-Owner caps (NFR-18); allowlist maximum of a handful of users (A-10) |
| Node loss or local-path volumes | Data loss or a stranded Area | NFR-05 off-node backups |
| Unofficial Sports Tracker API changes | Sync breaks | Failures shown as connection status, scheduled retries with backoff (FR-52); file import as a fallback |
| Google restricted-scope policy and the unverified-app cap | T1 or T2 could be blocked for an unverified app; at most 100 users ever | Invite-only with an allowlist maximum (FR-51); keep T1 and T2 optional; verification and CASA as a gate before opening up (NFR-19) |

## 11. Open Questions

*Questions Q-1 to Q-10 were answered by the Owner on 2026-10-28. Q-11 to Q-14 came with the route-quality plan (§4.12); the Owner asked for them to be decided rather than asked ("make the decisions"), so they are recorded as decisions with their reasons. None is open. The answers and decisions are kept here as the record.*

1. **Q-1. Multi-user ever? — Answered (2026-10-28): yes.**
   - Echo supports more than one user. Each user owns their data, and no user sees another's (§2.2).
   - Sign-up is invite-only, with an allowlist managed by the admin, the first Owner (FR-51; default logged).
   - Per-user caps (NFR-11, NFR-18), isolation tests as a hard gate (NFR-07), per-user export and erase (FR-48, FR-49, which moved to Phase 1), a per-user consent ledger (FR-47) and per-user grants (FR-18, FR-46).
   - Google: Production, unverified (warning, at most 100 users ever); Gmail scopes are restricted, so verification plus a yearly CASA assessment is a gate before opening beyond an invited circle (NFR-19).
2. **Q-2. Faces. — Answered (2026-10-28).**
   - Storing face data of friends and family who have not consented is acceptable to the Owner.
   - Only named people are kept: unnamed clusters are discarded when the review session ends (FR-44).
   - Photos only, no videos.
   - Face data is per user, and is never shared or matched across users.
3. **Q-3. Google OAuth path. — Answered (2026-10-28):** move Google sign-in to Production, unverified, in Phase 0 (NFR-03), within the constraints in §2.2 and NFR-19.
4. **Q-4. Branding depth. — Answered (2026-10-28):** Echo is the in-app name only (FR-03). No repository, chart, image or domain renames; contacts.mayaffit.com stays.
5. **Q-5. Gemini wording answers about people. — Answered (2026-10-28).**
   - *The question, in plain words:* when you ask "When did I last see Dana?", Echo finds the answer itself (dinner with Dana at a café in Haifa on 3 Sep, 4 photos). Should Echo send that found answer to Gemini so it can write the reply in natural language, as the Assistant already does for Contacts and Atlas, or should such answers be shown without Gemini?
   - *Answer:* send it to Gemini. Echo computes the joins and sends only the matched results and their citations, and Gemini writes the reply (FR-31). A per-user switch in the Privacy Center (FR-47) turns this off; with it off, the results are shown as plain cards, without Gemini.
6. **Q-6. Email touchpoints in the Unified Timeline. — Answered (2026-10-28):** a count per Person per day. Individual messages are not shown (FR-40).
7. **Q-7. Order of later sources. — Answered (2026-10-28):** LinkedIn comes first after Phase 2: `Connections.csv` from the LinkedIn data export, linked to Contacts (FR-54). LinkedIn has no personal API, so it is an export import.
8. **Q-8. Backup destination. — Answered (2026-10-28):** Google Drive, not OneDrive. Backups are system-level, made by the operator into the operator's Drive, and hold every user's data. They are encrypted before upload with a key held in Vault, never in Drive (NFR-05, §5.2).
9. **Q-9. Sports Tracker scheduling. — Answered (2026-10-28):** yes, a scheduled incremental sync at a polite rate, with failures shown as connection status (FR-52).
10. **Q-10. Samsung Health. — Answered (2026-10-28):** do not exclude it; verify and fix its time alignment against matched GPS sources, in Phase 0, until the median start-time difference is under 60 s (FR-53).
11. **Q-11. Activities with no track. — Decided (2026-10-28): no route is synthesised.** Samsung Health activities, and any Activity with fewer than 2 points, are flagged "no track" and not drawn (FR-55). *Reason:* a route between the Visits before and after would be made up wholly of inferred pieces. That is a guess, not a correction of a recording, and P-4 says a guess is not an answer.
12. **Q-12. Rail transit. — Decided (2026-10-28): left as recorded.** Echo starts keeping the source's own type (FR-56, architecture AD-35), matches buses (and taxis) on roads, and leaves trains, trams, subways, ferries and cable cars as recorded. Transit recorded before Echo kept the source's type is not matched in R1 and is marked "type unknown" until the Timeline is re-imported, which fills the type; Echo does not guess rail from speed. *Reason:* the road graph has no rail lines, so a train placed on roads would be wrong, and a separate rail network costs a second graph and matcher for 473 transit trips, only some of them rail. Revisit if the Owner asks for it.
13. **Q-13. Which regions abroad? — Decided (2026-10-28): none at MVP; added on demand.** R1 and R2 hold Israel only. In R3, regions are added on demand from the "outside coverage" list, ranked by activity count (FR-60). *Reason:* each region grows the monthly build and the tiles image, so it should be added where the unmatched trips actually are, not guessed in advance.
14. **Q-14. Shown by default? — Decided (2026-10-28): yes, above a confidence of 0.7.** A Matched Route at or above 0.7 replaces the straight lines on the map by default, with the toggle in every Activity panel and no acceptance needed; below it, it is not drawn, and from R2 it is offered with "Use this route" (FR-56, FR-58). Each Owner can change the threshold and the default (FR-59). *Reason:* the Owner reported the straight lines as the defect, so the fix should be visible without a setting, and the threshold plus the always-available original and revert (FR-58) keep a wrong match from being silently trusted.

## 12. References

- As-built inventories: [contacts-as-built.md](contacts-as-built.md), [atlas-as-built.md](atlas-as-built.md) (in this folder).
- Epics: `docs/planning/epic-4-contacts-workspace.md` (Epics 4–6), `epic-7-activity-and-assistant.md` (Epics 7–8), `epic-9-productivity-roadmap.md`, `epic-10-personal-atlas.md`, `epic-11-fusion.md`, `epic-12-route-quality.md` (FR-55 to FR-60).
- Technical addendum for the architect: [prd-addendum.md](prd-addendum.md).
- Architecture: [architecture.md](architecture.md).

## 13. Assumptions Index

*A-1 was withdrawn during drafting. A-2 was withdrawn on 2026-10-28, because the Owner's answers to Q-1 and Q-3 replaced it with decisions (§2.2, NFR-03, NFR-19). Ids are not reused.*
- **A-3** (FR-31): Gemini remains the Assistant model. The cross-domain Assistant extends the existing disclosure and does not add a provider.
- **A-4** (FR-43): face detection and clustering can run in batch on Echo's cluster, and taking hours to days for the first pass over 157k Photos is acceptable. The architect must confirm this.
- **A-5** (§9): the metric targets (≤ 30 s, ≥ 70%, ≤ 10% rejected, ≥ 50%, 3 days a week) are proposed starting values that the Owner has not confirmed.
- **A-6** (NFR-09): p95 ≤ 2 s is achievable and adequate on the Pi at the Owner's scale.
- **A-7** (FR-34): the calendar (attendees, time, location) plus Atlas Visits and Activities are enough evidence for Phase 1 Encounters. Calls and WhatsApp stay out of reach until their imports are built.
- **A-8** (FR-34): the Encounter defaults (50% overlap, 500 m, confidence cap 0.6 without a location, threshold 0.5) are starting values for the Owner to tune.
- **A-9** (FR-29, NFR-16): remote MCP clients pass through a Cloudflare Access policy. That policy lives in Cloudflare, outside this repository, and was not checked for this PRD.
- **A-10** (§2.2, FR-51, NFR-09, NFR-18): "a handful of users" means at most about 10, the default allowlist maximum, and the other users hold less data than the first Owner. The Pi sizing rests on this.
- **A-11** (FR-55, FR-56): the quality thresholds (the score below which a route needs improvement, the per-type straight-jump lengths) and the 0.7 confidence threshold are starting values for each Owner to tune. They were chosen against the first Owner's production measurements of 2026-10-28. For example, Google Timeline drives have 0.94 points per km and Sports Tracker rides about 91.
- **A-12** (FR-56, NFR-21): a self-hosted routing engine can match sparse drives (points about 440 m and 60 s apart) on a Raspberry Pi within the NFR-21 budget, for the first Owner's history and a handful of smaller ones (A-10). The architecture's numbers are estimates until a spike on the Pi measures them (architecture AD-32, AD-36).
- **A-13** (FR-56): OpenStreetMap's road network in Israel is complete enough that a confident match is almost always the road actually driven. Where it is not, the confidence check and the Owner's revert (FR-58) are the safeguards.
