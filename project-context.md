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

### 5. API & Service Layer Rules

**Service Layer Abstraction**
```typescript
// ✅ Components → Thunks → Services → API Clients
// Component:
dispatch(fetchContacts())

// Thunk (in slices/contacts/thunks.ts):
const response = await GoogleContactsService.fetchAllContacts()

// Service (GoogleContactsService.ts):
const result = await PeopleAPIClient.listContacts()

// API Client (PeopleAPIClient.ts):
return await axiosInstance.get('/people/me/connections')

// ❌ NEVER: Direct API calls from components
const response = await axios.get('/api/contacts')  // Wrong
```

**API Response Format**
```typescript
// ✅ ALWAYS: Return APIResponse<T> from services
interface APIResponse<T> {
  success: boolean
  data?: T
  error?: {
    code: string
    message: string
    details?: unknown
  }
}

async function fetchAllContacts(): Promise<APIResponse<Contact[]>> {
  try {
    const response = await peopleAPI.list()
    return { success: true, data: transformContacts(response.data) }
  } catch (err) {
    return {
      success: false,
      error: {
        code: mapErrorCode(err),
        message: 'Failed to load contacts',
        details: err
      }
    }
  }
}

// ❌ NEVER: Throw errors from services
async function fetchAllContacts(): Promise<Contact[]> {
  const response = await peopleAPI.list()
  return response.data  // Wrong - no error handling
}
```

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

**Before Writing ANY Code:**

1. **Verify `dev` branch exists** (base branch for all features):
   ```bash
   git fetch origin
   git branch -a | grep -E '(^|\/)dev$'
   ```
   If `dev` does not exist, STOP and ask the user to create it.

2. **Create feature branch from `dev`**:
   ```bash
   git checkout dev
   git pull origin dev
   git checkout -b <branch-type>/<short-description>
   ```

3. **Branch Naming Convention**:
   - `feature/<description>` — New functionality
   - `bugfix/<description>` — Bug fixes
   - `hotfix/<description>` — Urgent production fixes
   - `chore/<description>` — Maintenance tasks (dependencies, config, docs)

   Examples:
   - `feature/oauth-login-flow`
   - `bugfix/contact-sync-race-condition`
   - `chore/update-dependencies`

**During Development:**

4. **Commit frequently with conventional commits**:
   ```bash
   git add .
   git commit -m "type(scope): description"
   ```

   Types: `feat`, `fix`, `chore`, `docs`, `refactor`, `test`, `style`, `perf`

   Examples:
   - `feat(auth): implement Google OAuth login flow`
   - `fix(sync): resolve race condition in optimistic updates`
   - `chore(deps): update React to 18.3.1`

**When Development is Complete:**

5. **Push feature branch to origin**:
   ```bash
   git push -u origin <branch-name>
   ```

6. **Create Pull Request**:
   - Target branch: `dev`
   - Use `gh pr create` or GitHub web interface
   - Include description of changes and testing performed

7. **Inform user**: "Feature branch `<branch-name>` pushed and ready for PR to `dev`"

**NEVER:**
- ❌ Commit directly to `dev` or `main`
- ❌ Use `git commit --amend` on pushed commits
- ❌ Force push to shared branches (`git push --force`)
- ❌ Skip hooks with `--no-verify` unless explicitly requested

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
