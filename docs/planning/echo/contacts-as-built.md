# Contacts — as-built inventory (repo D:\Code\Contacts, dev @ 1e05abc, 2026-10-28)

## Before writing the PRD and Architecture
- **Secrets committed in the repo:**
  - ContactsApi/src/ContactsApi.WebApi/appsettings.json holds a Vault dev token, a placeholder DB connection string and the real Google OAuth client id;
  - helm/contacts/values.yaml holds a default Postgres password;
  - no values were copied.
- **Stale docs:**
  - DEPLOYMENT.md describes a frontend-only setup with no database;
  - project-context.md lists PATCH /api/contacts/{rn}/fields (it doesn't exist) and axios retry interceptors;
  - docs/index.md (2026-01) describes the WebKit template only.
- **No CI and no Flux files in the repo:**
  - GitOps wiring for the Pi lives in the separate raspberry_deployment repo (Flux HelmRelease);
  - images are built by hand with docker buildx for arm64 and pushed to GHCR; merging to main does not deploy.

## 1. User-facing capabilities
- **Sign-in:**
  - Google sign-in from the signed-out home (components/SignedOutHome.tsx); the session lasts 7 days;
  - SessionWatcher sends the user to sign in again when it expires (ContactsApp.tsx);
  - opt-in extra consents: "Connect Gmail & Calendar" (`/api/auth/login?activity=true`) and "Email content / signatures" (`?signatures=true`) (AuthController.cs).
- **Contacts workspace** (components/ContactsHome.tsx, table/ContactsTable.tsx, table/MobileContactList.tsx):
  - views: Contacts, Insights, Duplicates (with a badge), Trash;
  - a virtualized table with configurable columns (features/columnConfig); a list on phones;
  - search across names, phonetic name, nickname, phone digits, email and organization;
  - filter chips and a label filter; sort with Hebrew and Latin collation; an A–Z / א–ת rail;
  - inline editing like a spreadsheet; optimistic saves with retries and a 412 conflict dialog (EditableCell, SyncStatusIcon, ConflictResolutionDialog, redux/thunks/syncThunks.ts).
- **Contact card** (card/ContactCardDrawer.tsx, ContactForm.tsx):
  - edits every field group; only changed groups are saved, checked against the etag;
  - shows the contact's email and meeting activity; look-up links to Google, LinkedIn and Facebook.
- **Clean-up** (cleanup/*, duplicates/DuplicatesView.tsx, model/duplicates.ts, model/merge.ts):
  - multi-select; soft delete to the app's own Trash label; restore; permanent delete (batch up to 500);
  - a duplicate finder (last 9 phone digits, email, normalized name);
  - merge, including Hebrew and English versions of one person; the server refuses if data would be lost unless confirmed, and refuses a contact that changed since it was loaded.
- **Insights** (insights/InsightsView.tsx, model/stats.ts):
  - completeness, phone origin, name script, labels, birthdays by month; a chart click filters the list;
  - organization suggestions from work-email domains (OrgSuggestionsDialog.tsx, model/organizations.ts).
- **Keeping in touch** (needs the Gmail & Calendar opt-in; insights/ActivitySection.tsx, ReachOutSection.tsx, model/activity.ts, model/reachOut.ts):
  - a last-in-touch column; most in touch, gone quiet, never in touch; people you email who aren't contacts;
  - "Reach out today" from each person's rhythm, with dismissals;
  - awaiting-reply and follow-up threads; "this is me" addresses;
  - suggested emails by name matching, Hebrew ↔ English (SuggestEmailsDialog.tsx);
  - refresh, and forget my activity data.
- **Signature enrichment** (email-content opt-in; insights/SignatureDialog.tsx, assistant/signatures.ts): up to 10 contacts, their last 3 emails, fields extracted by Gemini, proposed for approval.
- **Device** (device/*):
  - call, SMS, WhatsApp and mailto links (Israeli numbers converted to international);
  - vCard 3.0 export through the share sheet or a .vcf;
  - compare the phone's contacts (Contact Picker) with Google and add the missing ones.
- **Assistant** (components/assistant/AssistantPanel.tsx, assistant/agent.ts, tools.ts, changes.ts, apply.ts):
  - Gemini chat in Hebrew or English with contact: links;
  - tools: find_person, get_overview, search_contacts, get_contact, find_duplicates, organization_suggestions, web_search, propose_changes, propose_label, propose_new_contact, propose_trash, open_merge, reach_out, awaiting_reply, read_signature, contact_person, open_contact, show_contacts;
  - every write is a proposal shown as a diff and applied with its etag;
  - the panel discloses that data goes to Gemini.
- **Site shell:** a left rail (bottom bar on phones) for Contacts and Atlas; `/` opens the last area used.

## 2. Planned but not built (docs/planning/epic-4, epic-7, epic-9)
- Automatic web suggestions through Programmable Search.
- LinkedIn Connections.csv import.
- Streaming replies; persistent chat history (it's in memory only).
- Gmail and Calendar extras:
  - meeting prep and a morning digest;
  - all calendars (only the primary calendar is read, GoogleActivityClient.cs:257) and 1:1 detection;
  - a relationship graph;
  - a configurable scan window (fixed at 365 days and 10k messages);
  - personal vs professional.
- Other sources: multiple Google accounts, Microsoft 365, call/SMS import through Drive, WhatsApp export.
- Features:
  - keep-in-touch targets; birthday reminders with a drafted greeting;
  - assistant actions in Gmail, Calendar and Tasks;
  - per-contact notes or an interaction log;
  - command palette, saved views, bulk edit, undo, CSV export;
  - a scheduled clean-up report; Hebrew UI.
- Platform:
  - Google OAuth Production and verification, privacy policy and terms pages;
  - durable per-user storage in Postgres instead of Redis;
  - per-user data export and delete;
  - a usage dashboard; CI.
- Code TODOs:
  - token-encryption TODOs in AuthController;
  - `app.UseAuthentication()` commented out;
  - unused ITokenRepository, User, UserToken and Core/Services/GooglePeopleService.cs.

## 3. Data model
- **Contacts are not stored by the app:** Google People API is the store.
  - Core/Entities/Contact.cs is a DTO: ResourceName, Etag, Names (with phonetic), Nicknames, PhoneNumbers (canonical), EmailAddresses, Addresses, Organizations, Birthdays, Urls, Biographies, Relations, UserDefined, Photos (read-only), Labels, UpdateTime. A null group in an update means unchanged.
  - Trash is a Google contact group.
- **Per-user state is in Redis** (IDistributedCache, keyed by the Google `sub`):

  | Key | Contents | Encrypted | Expiry |
  |---|---|---|---|
  | `refresh-token:{sub}` | refresh token | yes, Data Protection | 180 days sliding |
  | `optins:{sub}` | opt-ins | no | 365 days |
  | `activity:{sub}` | ActivitySnapshot | yes | 90 days sliding |
  | `self-emails:{sub}` | own addresses | yes | — |
  | session | ASP.NET session (access token, UserId, email, name, picture, scopes, OAuth state) | — | 7 days |

- **ActivitySnapshot** (Infrastructure/Activity/ActivityModels.cs):
  - status, progress, heartbeat;
  - People by email: last sent/received, counts, monthly emails, last/next meeting, monthly meetings;
  - Directory by email: names seen, sent, received, copied together, meetings, last seen;
  - Threads (45 days); MyEmails; GmailHistoryId; FullScanAt.
- **Links:** a contact is linked to activity by email only (frontend index, model/activity.ts).
- **Postgres:** no Contacts tables; only the `atlas` schema.
- **Frontend Redux:** auth, contacts (normalized), ui (columns), syncQueue. localStorage holds UI preferences and dismissals only.

## 4. Integrations and auth
- **Google OAuth 2.0**, authorization-code flow on the server (AuthController.cs):
  - CSRF state in the session; access_type=offline;
  - consent is asked only when a refresh token is needed; a silent second step adds opted-in scopes using login_hint;
  - the ID token is validated with GoogleJsonWebSignature; the identity is the Google `sub`.
- **Scopes:**
  - base: `openid profile email https://www.googleapis.com/auth/contacts`;
  - activity opt-in: `gmail.metadata` + `calendar.events.readonly`;
  - signatures: `gmail.readonly`, which replaces metadata;
  - include_granted_scopes is not used (ScopePreferences.cs).
- **The OAuth app is in Google "Testing" mode**, so refresh tokens expire after 7 days.
- **Google APIs:** People v1 (Infrastructure/Google/GooglePeopleApiClient.cs), Gmail v1 and Calendar v3 (Infrastructure/Activity/GoogleActivityClient.cs, SignatureReader.cs).
- **Gemini** (`gemini-flash-latest`, GeminiService.cs):
  - in-memory rate limits: per user per minute 40 generate and 10 search, 600 per user per day, 3,000 per day globally;
  - one shared key; only the app's own function declarations are passed; /search uses google_search grounding.
- **Secrets:**
  - in the cluster, the Vault Agent Injector renders secret/data/contacts into env vars: GoogleOAuth__ClientId/Secret, Database__ConnectionString, Gemini__ApiKey, MicrosoftOAuth__*;
  - the chart's vault.enabled defaults to false, with a K8s Secret fallback;
  - for local development, VaultConfigurationService and docker-compose with a seeded dev Vault.
- **Data Protection:** the key ring is in Redis (`DataProtection-Keys`, app `ContactsApi`); separate protectors.

## 5. Backend architecture
- **Solution:** ContactsApi/ContactsApi.sln, .NET 9.
  - Core: entities, GoogleApiException with codes, unused interfaces;
  - Infrastructure: Google/ (People client, PersonExtrasMerger), Activity/ (aggregator, Gmail/Calendar client, name matcher, RFC 2047, own addresses, signature text), Vault/, Atlas/;
  - WebApi: controllers, middleware, services, Program.cs.
- **Routes:**
  - api/auth: login, callback, status, logout, refresh;
  - api/contacts: GET paged (≤1,000), POST, groups, batch-delete, labels, trash?restore=, merge, GET/PUT/DELETE {resourceName};
  - api/activity: status, GET, self, directory, suggest-emails, refresh, DELETE;
  - api/enrich/signatures;
  - api/assistant: status, generate, search;
  - /health and Swagger.
- **ActivityWorker:**
  - a Channel queue, 3 scans at once, a heartbeat (dead after 3 minutes);
  - a full scan every 7 days, else Gmail history.list increments;
  - 365 days and 10k messages at most; the primary calendar only.
- **Middleware:**
  - ExceptionHandlingMiddleware maps GoogleApiException to the ApiResponse envelope;
  - RequestLoggingMiddleware;
  - the session is loaded up front with a retry, returning 503 SESSION_STORE_UNAVAILABLE instead of a sign-out;
  - Redis AbortOnConnectFail=false (after the 2026-08-31 power-outage crash loop).
- **Postgres:** EF Core with Npgsql, AtlasDbContext only. Without a connection string Atlas disables itself and Contacts keeps working.
- **Tests:** xUnit; the Contacts files are ActivityAggregatorTests, OwnAddressesTests, PersonExtrasMergerTests, SignatureTextTests and ThreadsAndNamesTests.

## 6. Frontend architecture
- **Stack:** WebKit/BlueFiber template (blueFiberSrc/), React 18.3, TS 5.7 strict, MUI 6, Redux Toolkit, react-router 7, TanStack Table/Virtual, zod, dnd-kit.
- **Module Federation:** the container `webkit` exposes ./Init and ./ExternalAction, but the app runs standalone: bootstrap.jsx → PluginWrapper → App.jsx → initContactsEntry() → ContactsApp.
- **Routes:** /login and /auth/callback redirect home; /* → SiteShell → /contacts (ContactsWorkspace) and /atlas (AtlasHome).
- **API:** one shared src/services/api/BackendApiClient.ts (credentials: include, SESSION_EXPIRED_EVENT, build-time REACT_APP_API_BASE_URL=/api).
- **Logic:** Redux slices auth, contacts, ui, syncQueue; thunks syncThunks and cleanupThunks; pure logic in model/; theme/contactsTheme.ts (dark); src/shared/logger.ts.
- **Version:** CalVer (version.json).

## 7. Deployment and runtime
- **Helm umbrella** helm/contacts (0.4.2):
  - frontend: nginx (ghcr.io/mayafit/contacts-frontend);
  - backend: ghcr.io/mayafit/contacts-backend, port 5000, uid 1001, read-only root filesystem;
  - Bitnami postgresql 16 (local-path, 5Gi) and redis (no auth, 1Gi).
- **Routing:**
  - Traefik IngressRoute to nginx;
  - nginx proxies /api/ (120 s, 20 MB) and /api/mcp (300 s); CSP and security headers.
- **Public URL:**
  - global.publicUrl drives the OAuth redirects, Frontend__BaseUrl and CORS;
  - the public URL is https://contacts.mayaffit.com through a Cloudflare tunnel, and must be kept.
- **Cluster:** a 2-node Raspberry Pi k3s cluster ("home-rasps") with Flux from the raspberry_deployment repo; Vault is in the cluster.
- **Builds:**
  - images are built for arm64 by hand (docker buildx), tagged latest and the short sha, then a rollout restart;
  - the chart version must be bumped for Flux to pick up chart changes.
- **Observability:** the config map has OTEL/Jaeger env vars, but the backend has no OpenTelemetry package; Serilog writes compact JSON to the console and to files.

## 8. Conventions
- **Errors:** the ApiResponse{success, data, error{code, message, details, timestamp}} envelope.
  - Codes: CONTACT_CONFLICT (412), INSUFFICIENT_SCOPE, CONTACT_NOT_FOUND, RATE_LIMITED, MERGE_WOULD_LOSE_DATA, TOKEN_REFRESH_FAILED, SESSION_EXPIRED, INVALID_REQUEST, ASSISTANT_DISABLED, SESSION_STORE_UNAVAILABLE, OAUTH_CONFIG_ERROR.
  - Atlas uses AtlasNotFoundException and codes like ONEDRIVE_*.
- **Tests:** Jest, Testing Library and jest-axe; xUnit; husky and lint-staged on src/entries/Contacts.
- **Language:** no i18n and the UI is in English, but Hebrew is handled in data (collation, rail, script detection, phonetic, RFC 2047); the assistant answers in the user's language.
- **Privacy:**
  - Gmail is opt-in; metadata only unless signatures are opted in;
  - signature bodies are processed in memory and never stored;
  - tokens and activity are encrypted at rest; the user can delete activity data;
  - email text is treated as data, not instructions; the panel discloses Gemini.
- **Process:** feature branches into dev, PRs, conventional commits, review-fix commits. Planning docs moved to docs/planning after _bmad-output/ was lost.

## 9. Contacts ↔ Atlas touchpoints
1. **Same owner identity:** the Google `sub` is Atlas's OwnerId (GoogleTokenService.UserIdKey; AtlasController.cs:175,297,436).
2. **Same process and deployment:** one backend, one Helm release, one nginx, one Redis. Postgres is used by Atlas only.
3. **Same UI frame:** SiteShell, SignedOutHome, theme, BackendApiClient, logger.
4. **Same assistant:** AssistantPanel and agent.ts take Atlas's toolset and an onAtlas link handler; one Gemini proxy and its limits.
5. **Same Vault path and Gemini key** (secret/data/contacts).
6. **No data links yet:** no Atlas table or tool references contacts or resourceName. The Contacts activity snapshot (email and meeting counterparts with times) and Atlas visits share only the owner and time.
