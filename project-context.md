# Project Context: Contacts Application

> **Critical AI Agent Reference**: Read this file before implementing any code. It contains essential rules, patterns, and conventions that must be followed consistently.

---

## Project Overview

**Name**: Contacts
**Type**: React 18.3.1 + TypeScript 5.7.2 SPA
**Foundation**: WebKit (fork from mayafit/WebKit)
**Architecture**: Feature-based organization with Redux Toolkit state management

**Purpose**: Google Contacts management application with inline editing, optimistic updates, and real-time sync.

---

## Critical Implementation Rules

### 1. Framework & Language Rules

**TypeScript Strict Mode - MANDATORY**
```typescript
// ✅ ALWAYS: Explicit types for all function parameters and returns
async function fetchContacts(): Promise<APIResponse<Contact[]>> {
  // implementation
}

// ❌ NEVER: Implicit any or missing return types
async function fetchContacts() {  // Missing return type
  // implementation
}
```

**React 18.3.1 Patterns**
```typescript
// ✅ ALWAYS: Use hooks for state management
const [contacts, setContacts] = useState<Contact[]>([])
const dispatch = useAppDispatch()

// ❌ NEVER: Class components (not used in this project)
class ContactGrid extends React.Component { }  // Don't use
```

**Redux Toolkit Patterns**
```typescript
// ✅ ALWAYS: Use createSlice for Redux state
const contactsSlice = createSlice({
  name: 'contacts',
  initialState,
  reducers: {
    addContact(state, action: PayloadAction<Contact>) {
      state.byId[action.payload.resourceName] = action.payload
    }
  }
})

// ❌ NEVER: Manual action creators or reducers
const ADD_CONTACT = 'ADD_CONTACT'  // Don't use string constants
```

### 2. Project Structure Rules

**Feature-Based Organization**
```
src/entries/Contacts/
├── features/              # Feature modules (auth, contactGrid, etc.)
├── redux/slices/          # Redux slices
├── services/              # API services
├── components/shared/     # Shared components
└── types/                 # TypeScript types
```

**File Organization Rules**
- ✅ Component files: `PascalCase.tsx` (e.g., `EditableContactGrid.tsx`)
- ✅ Service files: `camelCase.ts` (e.g., `googleContactsService.ts`)
- ✅ Test files: Colocated with source (e.g., `Component.test.tsx` next to `Component.tsx`)
- ❌ NEVER: kebab-case or snake_case for TypeScript files

**Import Rules**
```typescript
// ✅ ALWAYS: Use type-only imports for types
import type { Contact, SyncStatus } from '@/types'

// ✅ ALWAYS: Barrel imports from types/index.ts
import { Contact, SyncQueue, UIState } from '@/types'

// ❌ NEVER: Import types from implementation files
import { Contact } from '@/services/GoogleContactsService'  // Wrong
```

### 3. Naming Conventions

**Redux Patterns**
```typescript
// ✅ Actions: {slice}/{action} format
'contacts/addContact'
'syncQueue/markSyncing'

// ✅ Selectors: select{Entity}{Property} format
selectAllContacts
selectContactById
selectSyncQueueStatus

// ✅ Thunks: verb + noun format
fetchContacts
updateContact
deleteContact

// ❌ NEVER: CONSTANT_CASE or get* prefix
'ADD_CONTACT'           // Wrong
getContacts             // Wrong - use selectContacts
```

**Component Props**
```typescript
// ✅ Event handlers: on{EventName} prefix
interface ContactGridProps {
  onContactUpdate: (id: string, changes: Partial<Contact>) => void
  onContactSelect: (id: string) => void
}

// ✅ Boolean flags: is/has/should prefix
interface ContactGridProps {
  isLoading: boolean
  hasError: boolean
  shouldAutoSync: boolean
}

// ❌ NEVER: Generic or abbreviated props
interface ContactGridProps {
  update: Function      // Wrong - use onContactUpdate
  loading: boolean      // Wrong - use isLoading
  sel?: string          // Wrong - use selectedId
}
```

### 4. State Management Rules

**State Separation**
```typescript
// ✅ Redux: For API state and shared UI state
const contacts = useAppSelector(selectAllContacts)
const syncStatus = useAppSelector(selectSyncQueueStatus)

// ✅ Local State: For transient UI only
const [isHovered, setIsHovered] = useState(false)
const [inputValue, setInputValue] = useState('')

// ❌ NEVER: API data in local state
const [contacts, setContacts] = useState([])  // Wrong - use Redux
```

**Async Operations**
```typescript
// ✅ ALWAYS: Use createAsyncThunk for API calls
export const fetchContacts = createAsyncThunk(
  'contacts/fetchContacts',
  async (_, { rejectWithValue }) => {
    const response = await GoogleContactsService.fetchAllContacts()
    if (!response.success) {
      return rejectWithValue(response.error)
    }
    return response.data
  }
)

// ❌ NEVER: Direct API calls in components
const handleFetch = async () => {
  const data = await fetch('/api/contacts')  // Wrong
}
```

### 5. API & Service Layer Rules (ARCH-6)

**Backend Proxy Architecture**

The application uses a **C# ASP.NET Core backend** as a proxy to Google People API. All Google API calls go through the backend.

**Architecture Decision (2026-02-02):**
- Backend handles: OAuth, token management, rate limiting, error transformation
- Frontend communicates only with backend API (never directly with Google)
- Reference: `_bmad-output/planning-artifacts/epic-3-backend-architecture-plan.md`

**3-Tier Service Layer Pattern**
```typescript
// ✅ CORRECT: Components → Thunks → Services → BackendApiClient → Backend → Google API
// Component:
dispatch(fetchContacts())

// Thunk (in slices/contacts/contactsSlice.ts):
const response = await GoogleContactsService.fetchAllContacts()

// Service (GoogleContactsService.ts):  ← Service Layer
const { contacts, nextPageToken } = await backendApiClient.fetchContacts(1000)

// API Client (BackendApiClient.ts):  ← Backend API Client
const response = await fetch(`${baseUrl}/api/contacts?pageSize=1000`, {
  credentials: 'include',  // Sends auth cookies
})

// Backend API (C# ASP.NET Core):
// → Calls Google People API with googleapis
// → Returns transformed data

// ❌ NEVER: Direct API calls from components or thunks
const response = await fetch('/api/contacts')  // Wrong - use service layer
const response = await axios.get('https://people.googleapis.com/v1/...')  // Wrong - no direct Google API
```

**Backend API Endpoints**
```typescript
// Authentication
POST /api/auth/login       // Initiate OAuth flow (redirects)
POST /api/auth/logout      // Clear session
GET  /api/auth/status      // Check if authenticated
POST /api/auth/refresh     // Refresh access token

// Contacts Operations
GET    /api/contacts                          // Fetch contacts (paginated)
GET    /api/contacts/{resourceName}           // Get single contact
PUT    /api/contacts/{resourceName}           // Batch update contact
PATCH  /api/contacts/{resourceName}/fields    // Field-level update (Epic 3)
```

**Service Layer Methods**
```typescript
// ✅ GoogleContactsService wraps BackendApiClient
export class GoogleContactsService {
  // Fetch all contacts (handles pagination internally)
  static async fetchAllContacts(): Promise<APIResponse<Contact[]>> {
    try {
      const allContacts: Contact[] = [];
      let nextPageToken: string | undefined;

      do {
        const { contacts, nextPageToken: nextToken } =
          await backendApiClient.fetchContacts(1000, nextPageToken);
        allContacts.push(...contacts);
        nextPageToken = nextToken;
      } while (nextPageToken);

      return { success: true, data: allContacts };
    } catch (error) {
      return {
        success: false,
        error: {
          code: 'FETCH_CONTACTS_FAILED',
          message: 'Failed to fetch contacts from backend API',
          details: error
        }
      };
    }
  }

  // Field-level update (Epic 3)
  static async updateContactField(
    resourceName: string,
    fieldPath: string,
    newValue: unknown
  ): Promise<APIResponse<Contact>> {
    try {
      const contact = await backendApiClient.updateContactField(
        resourceName,
        fieldPath,
        newValue
      );
      return { success: true, data: contact };
    } catch (error) {
      return {
        success: false,
        error: {
          code: 'UPDATE_FIELD_FAILED',
          message: `Failed to update ${fieldPath}`,
          details: error
        }
      };
    }
  }
}
```

**Backend API Client Pattern**
```typescript
// ✅ BackendApiClient handles all backend communication
export class BackendApiClient {
  private readonly baseUrl: string;

  constructor(baseUrl: string = API_BASE_URL) {
    this.baseUrl = baseUrl;
  }

  // Field-level update (Epic 3)
  async updateContactField(
    resourceName: string,
    fieldPath: string,
    newValue: unknown
  ): Promise<Contact> {
    const encodedResourceName = encodeURIComponent(resourceName);

    const response = await this.request<Contact>(
      `/contacts/${encodedResourceName}/fields`,
      {
        method: 'PATCH',
        body: JSON.stringify({ fieldPath, newValue }),
      }
    );

    if (!response.success || !response.data) {
      throw new Error(response.error?.message || 'Failed to update field');
    }

    return response.data;
  }

  // Private request method
  private async request<T>(
    endpoint: string,
    options: RequestInit = {}
  ): Promise<ApiResponse<T>> {
    const url = `${this.baseUrl}${endpoint}`;

    const config: RequestInit = {
      ...options,
      credentials: 'include',  // Send cookies with request
      headers: {
        'Content-Type': 'application/json',
        ...options.headers,
      },
    };

    const response = await fetch(url, config);
    return await response.json();
  }
}

// Singleton instance
export const backendApiClient = new BackendApiClient();
```

**API Response Format**
```typescript
// ✅ ALWAYS: Return APIResponse<T> from services
interface APIResponse<T> {
  success: boolean
  data?: T
  error?: {
    code: string      // e.g., 'INVALID_REQUEST', 'UNAUTHORIZED', 'CONTACT_NOT_FOUND'
    message: string   // User-friendly message
    details?: unknown // Technical details for debugging
  }
}

// Backend returns same format
interface ApiResponse<T> {
  success: boolean
  data?: T
  error?: {
    code: string
    message: string
    details?: unknown
    timestamp: string  // ISO 8601
  }
}

// ❌ NEVER: Throw errors from services - always return APIResponse
async function fetchAllContacts(): Promise<Contact[]> {
  const response = await backendApiClient.fetchContacts()
  return response.contacts  // Wrong - no error handling
}

// ✅ CORRECT: Catch errors and return APIResponse
async function fetchAllContacts(): Promise<APIResponse<Contact[]>> {
  try {
    const { contacts } = await backendApiClient.fetchContacts()
    return { success: true, data: contacts }
  } catch (error) {
    return {
      success: false,
      error: {
        code: 'FETCH_CONTACTS_FAILED',
        message: 'Failed to fetch contacts from backend API',
        details: error
      }
    }
  }
}
```

**Backend Error Codes**
```typescript
// Backend transforms Google API errors into user-friendly codes
const ERROR_CODES = {
  // Client errors (4xx)
  'INVALID_REQUEST': 'Invalid request parameters',
  'UNAUTHORIZED': 'Your session has expired. Please log in again.',
  'FORBIDDEN': 'You do not have permission to perform this action.',
  'CONTACT_NOT_FOUND': 'Contact not found. It may have been deleted.',
  'INVALID_FIELD_PATH': 'Invalid field path specified.',
  'VALIDATION_ERROR': 'Invalid input parameters',

  // Server errors (5xx)
  'SERVER_ERROR': 'Google services are temporarily unavailable.',
  'RATE_LIMIT_EXCEEDED': 'Too many requests. Please wait a moment.',

  // Network errors
  'NETWORK_ERROR': 'Network error. Please check your connection.',
  'FETCH_CONTACTS_FAILED': 'Failed to fetch contacts.',
  'UPDATE_CONTACT_FAILED': 'Failed to update contact.',
  'UPDATE_FIELD_FAILED': 'Failed to update field.',
} as const;
```

**Environment Configuration**
```bash
# .env
REACT_APP_API_BASE_URL=http://localhost:5000/api  # Backend API (NOT Google directly)
REACT_APP_GOOGLE_CLIENT_ID=<OAuth client ID>      # For OAuth flow
```

**Key Rules**
- ✅ All API calls go through `GoogleContactsService`
- ✅ Service layer calls `backendApiClient` (singleton)
- ✅ Backend handles: auth, rate limiting, error transformation
- ✅ Service methods return `Promise<APIResponse<T>>`
- ✅ Service methods never throw to caller (catch and return error)
- ❌ NO direct Google API calls from frontend
- ❌ NO googleapis or axios in frontend (use fetch via BackendApiClient)
- ❌ NO API calls directly from components or thunks

### 6. Performance Rules

**TanStack Table + Virtual**
```typescript
// ✅ ALWAYS: Use TanStack Table for data grid
import { useReactTable, getCoreRowModel } from '@tanstack/react-table'
import { useVirtualizer } from '@tanstack/react-virtual'

const table = useReactTable({
  data: contacts,
  columns,
  getCoreRowModel: getCoreRowModel(),
})

// ✅ Virtualize rows for 1000+ contacts
const virtualizer = useVirtualizer({
  count: table.getRowModel().rows.length,
  getScrollElement: () => parentRef.current,
  estimateSize: () => 48,  // Row height in pixels
})

// ❌ NEVER: MUI X Data Grid Pro (commercial license)
import { DataGridPro } from '@mui/x-data-grid-pro'  // Don't use
```

**Memoization**
```typescript
// ✅ Use React.memo for cell renderers
const EditableTextCell = React.memo(({ value, onChange }: CellProps) => {
  // implementation
})

// ✅ Use createSelector for derived Redux state
export const selectActiveContacts = createSelector(
  [selectAllContacts],
  (contacts) => contacts.filter(c => !c.deleted)
)

// ❌ NEVER: Compute derived state in render
const activeContacts = contacts.filter(c => !c.deleted)  // Wrong - do in selector
```

### 7. Error Handling Rules

**Error Boundaries**
```typescript
// ✅ ALWAYS: Wrap features in error boundaries
<ErrorBoundary fallback={(error) => <ErrorDisplay error={error} />}>
  <EditableContactGrid />
</ErrorBoundary>

// ✅ ALWAYS: Use structured AppError type
interface AppError {
  code: ErrorCode
  message: string
  technicalMessage?: string
  timestamp: string
  context?: Record<string, unknown>
}
```

**Retry Logic**
```typescript
// ✅ ALWAYS: Use exponential backoff (configured in retryInterceptor)
const RETRY_DELAYS = [100, 200, 400, 800, 1600]  // milliseconds
const MAX_RETRIES = 5

// ✅ Handle in axios interceptors, not in components
// See: services/api/interceptors/retryInterceptor.ts
```

### 8. Testing Rules

**Test Colocating**
```typescript
// ✅ ALWAYS: Colocate tests with source files
EditableContactGrid.tsx
EditableContactGrid.test.tsx      // Unit tests
EditableContactGrid.stories.tsx   // Storybook stories

// ❌ NEVER: Separate test directories (except integration/e2e)
tests/components/EditableContactGrid.test.tsx  // Wrong
```

**Testing Library Patterns**
```typescript
// ✅ ALWAYS: Test behavior, not implementation
test('updates contact name on edit', async () => {
  render(<EditableContactGrid contacts={mockContacts} />)
  const nameCell = screen.getByRole('cell', { name: /john/i })
  await userEvent.click(nameCell)
  await userEvent.type(nameCell, 'Jane')
  expect(screen.getByText('Jane')).toBeInTheDocument()
})

// ❌ NEVER: Test implementation details
test('sets state correctly', () => {
  const { result } = renderHook(() => useState(''))
  expect(result.current[0]).toBe('')  // Wrong - testing React internals
})
```

---

## Technology Stack

### Core Dependencies (EXACT VERSIONS)
```json
{
  "react": "18.3.1",
  "typescript": "5.7.2",
  "redux-toolkit": "2.4.0",
  "@mui/material": "6.1.10",
  "@tanstack/react-table": "8.22.2",
  "@tanstack/react-virtual": "3.10.8",
  "react-router": "7.12.0",
  "react-hook-form": "7.71.0",
  "@dnd-kit/core": "6.3.1",
  "@react-oauth/google": "0.13.4",
  "googleapis": "170.0.0",
  "axios": "1.13.2"
}
```

**IMPORTANT**: Use EXACT versions as specified. Do not use `^` or `~` prefixes.

---

## Anti-Patterns & Common Mistakes

### ❌ DON'T DO THIS

**1. Mixing State Management Approaches**
```typescript
// ❌ WRONG: API data in local state
const [contacts, setContacts] = useState([])
useEffect(() => {
  fetch('/api/contacts').then(r => setContacts(r.data))
}, [])

// ✅ CORRECT: Use Redux thunks
const contacts = useAppSelector(selectAllContacts)
useEffect(() => {
  dispatch(fetchContacts())
}, [dispatch])
```

**2. Skipping Service Layer**
```typescript
// ❌ WRONG: Direct API calls from thunks
const fetchContacts = createAsyncThunk('contacts/fetch', async () => {
  return await axios.get('/api/contacts')  // Wrong
})

// ✅ CORRECT: Use service layer
const fetchContacts = createAsyncThunk('contacts/fetch', async () => {
  return await GoogleContactsService.fetchAllContacts()
})
```

**3. Not Using TypeScript Properly**
```typescript
// ❌ WRONG: Using 'any'
const handleUpdate = (data: any) => {  // Wrong
  // implementation
}

// ✅ CORRECT: Explicit types
const handleUpdate = (data: Partial<Contact>) => {
  // implementation
}
```

**4. Incorrect File Organization**
```typescript
// ❌ WRONG: Cross-feature imports
// In features/contactGrid/EditableContactGrid.tsx
import { LoginButton } from '../auth/LoginButton'  // Wrong

// ✅ CORRECT: Use shared components or Redux state
// Move shared UI to components/shared/
// Use Redux for cross-feature communication
```

---

## Environment Configuration

```bash
# .env.example
REACT_APP_GOOGLE_CLIENT_ID=<OAuth client ID from Google Cloud Console>
REACT_APP_API_BASE_URL=https://people.googleapis.com/v1
```

**SECURITY RULES**:
- ✅ OAuth tokens in secure storage (httpOnly cookies or sessionStorage)
- ✅ NO contact data in localStorage (only UI preferences like column config)
- ❌ NEVER commit `.env` file to git
- ❌ NEVER expose API keys in client code

---

## Development Workflow

**Before Starting Implementation**:
1. ✅ Read `architecture.md` in `_bmad-output/planning-artifacts/`
2. ✅ Read this `project-context.md` file
3. ✅ Understand the feature's requirements from PRD/stories
4. ✅ Follow established patterns exactly

### ⚠️ MANDATORY: Git Feature Branch Workflow

**CRITICAL**: NEVER commit directly to `dev` or `main` branches. Always use feature branches.

**The Complete Git Development Flow:**

#### Step 1: Setup and Branch Creation

1. **Verify `dev` branch exists** (base branch for all features):
   ```bash
   git fetch origin
   git branch -a | grep -E '(^|\/)dev$'
   ```
   If `dev` does not exist, STOP and ask the user to create it.

2. **Ensure local `dev` is up to date**:
   ```bash
   git checkout dev
   git pull origin dev
   ```

3. **Create feature branch from `dev`**:
   ```bash
   git checkout -b <branch-type>/<short-description>
   ```

4. **Branch Naming Convention**:
   - `feature/<description>` — New functionality
   - `bugfix/<description>` — Bug fixes
   - `hotfix/<description>` — Urgent production fixes
   - `chore/<description>` — Maintenance tasks (dependencies, config, docs)
   - `docs/<description>` — Documentation updates

   Examples:
   - `feature/oauth-login-flow`
   - `bugfix/contact-sync-race-condition`
   - `chore/update-dependencies`
   - `docs/update-api-documentation`

#### Step 2: Development

5. **Develop and commit frequently** with conventional commits:
   ```bash
   git add .
   git commit -m "type(scope): description"
   ```

   Types: `feat`, `fix`, `chore`, `docs`, `refactor`, `test`, `style`, `perf`

   Examples:
   - `feat(auth): implement Google OAuth login flow`
   - `fix(sync): resolve race condition in optimistic updates`
   - `chore(deps): update React to 18.3.1`

#### Step 3: Rebase Before Push (CRITICAL)

6. **Before pushing, rebase on latest `dev`**:
   ```bash
   # Fetch latest changes from remote
   git fetch origin

   # Pull latest dev changes
   git checkout dev
   git pull origin dev

   # Rebase feature branch on top of dev
   git checkout <feature-branch-name>
   git rebase dev
   ```

   If conflicts occur during rebase:
   ```bash
   # Resolve conflicts in your editor
   # Then continue the rebase
   git add .
   git rebase --continue
   ```

   **Why rebase?** Ensures your feature branch contains all latest changes from `dev` and creates a clean, linear history.

#### Step 4: Push and Create PR

7. **Push feature branch to origin**:
   ```bash
   git push -u origin <branch-name>
   ```

   If you've rebased and already pushed before:
   ```bash
   git push --force-with-lease origin <branch-name>
   ```
   ⚠️ Only use `--force-with-lease` on YOUR feature branches, never on shared branches!

8. **Create Pull Request**:
   - Target branch: `dev`
   - Use `gh pr create` or GitHub web interface
   - Include description of changes and testing performed

9. **Inform user**: "Feature branch `<branch-name>` pushed and ready for PR to `dev`"

**NEVER:**
- ❌ Commit directly to `dev` or `main`
- ❌ Use `git commit --amend` on pushed commits (unless it's your feature branch)
- ❌ Force push to `dev` or `main` (use `--force-with-lease` only on feature branches)
- ❌ Skip hooks with `--no-verify` unless explicitly requested
- ❌ Skip the rebase step before pushing - always rebase on latest `dev`

**Development Commands**:
```bash
npm run dev          # Start development server with HMR
npm run test         # Run Jest tests in watch mode
npm run lint         # Run ESLint
npm run build        # Production build
npm run storybook    # Start Storybook
```

**Code Quality Gates**:
- ✅ All files must pass `npm run lint` (ESLint + Prettier)
- ✅ All tests must pass `npm run test`
- ✅ TypeScript must compile with zero errors (`tsc --noEmit`)
- ✅ Components should have colocated `.test.tsx` and `.stories.tsx` files

---

## Key Architectural Patterns

### Optimistic Updates Pattern
```typescript
// 1. Update Redux state immediately (optimistic)
dispatch(updateContact({ id, changes }))

// 2. Add to sync queue
dispatch(addToSyncQueue({ id, changes, status: 'pending' }))

// 3. Background sync via SyncOrchestrator
// 4. On success: Remove from queue
// 5. On failure: Mark failed, schedule retry
```

### Date/Time Handling
```typescript
// ✅ ALWAYS: ISO 8601 for storage and API
const timestamp = new Date().toISOString()  // "2026-01-14T10:30:00Z"

// ✅ ALWAYS: Intl.DateTimeFormat for display
const displayDate = new Intl.DateTimeFormat('en-US', {
  dateStyle: 'medium',
  timeStyle: 'short'
}).format(new Date(timestamp))
```

### Conflict Resolution
```typescript
// ✅ Last-write-wins based on timestamp comparison
if (serverTimestamp > localTimestamp) {
  // Server version is newer - reject local update
  return { success: false, error: { code: 'CONFLICT', message: '...' } }
}
// Otherwise proceed with update
```

---

## References

- **Architecture Document**: `_bmad-output/planning-artifacts/architecture.md`
- **PRD**: `_bmad-output/planning-artifacts/prd.md`
- **UX Design**: `_bmad-output/planning-artifacts/ux-design-specification.md`
- **WebKit Repository**: https://github.com/mayafit/WebKit/tree/dev

---

## Questions & Clarifications

If you encounter ambiguity:
1. ✅ Check the architecture document first
2. ✅ Check this project-context.md file
3. ✅ Follow established patterns in similar components
4. ✅ Ask the user for clarification if still unclear

**Consistency > Cleverness**: Always prefer following existing patterns over introducing new approaches, even if they seem better.

---

_Last Updated: 2026-01-14_
_Architecture Status: READY FOR IMPLEMENTATION ✅_
