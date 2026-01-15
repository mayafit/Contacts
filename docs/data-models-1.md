# Data Models - Frontend

**Generated:** 2026-01-10
**Workflow:** document-project v1.2.0
**Scan Level:** exhaustive
**Part:** frontend (web)

---

## Overview

This document describes the data models and state structures used in the WebKit frontend application. The application uses **Redux Toolkit** for state management with a modular slice-based architecture.

---

## Redux Store Structure

### Store Configuration

The application uses Redux Toolkit's `configureStore` with dynamic reducer injection for module federation support.

**Key Features:**
- Dynamic reducer registration via `addDynamicReducer`
- Module federation support for remote modules
- Redux DevTools integration (development mode)
- Immutable state updates via Immer (built into Redux Toolkit)

---

## State Slices

### 1. Track Slice

**Location:** `src/store/trackSlice.js`
**Namespace:** `track`

#### State Shape

```typescript
interface TrackState {
  lon: number;          // Longitude coordinate
  lat: number;          // Latitude coordinate
  alt: number;          // Altitude
  speed: number;        // Speed value
  classification?: string;  // Track classification
  status?: string;      // Track status
}
```

#### Initial State

```javascript
{
  lon: 0,
  lat: 0,
  alt: 0,
  speed: 0
}
```

#### Actions

| Action | Payload | Description |
|--------|---------|-------------|
| `updateTrack` | `TrackState` | Updates all track data fields |

**Exported Action Creator:** `updateTrackAction`

#### Selectors

**Location:** `src/store/trackSelector.js`

```javascript
getTrackDataSelector(store): {
  lon: number,
  lat: number,
  alt: number,
  speed: number,
  classification?: string,
  status?: string
}
```

Returns complete track data with safe defaults (0 for numeric fields).

---

### 2. Todos Slice

**Location:** `src/entries/Todos/store/todos.slice.ts`
**Namespace:** `todosSlice`

#### State Shape

```typescript
interface Todo {
  id: TodoId;           // Unique identifier
  title: string;        // Todo title/description
  // Additional fields as defined in decs
}

interface TodosState {
  todos: Record<TodoId, Todo>;  // Dictionary of todos by ID
  selectedTodoId?: TodoId;      // Currently selected todo
}
```

#### Initial State

```typescript
{
  todos: {},
  selectedTodoId: undefined
}
```

#### Actions

| Action | Payload | Description |
|--------|---------|-------------|
| `createTodos` | `{ todos: Todo[] }` | Adds multiple todos to the store (merges with existing) |
| `deleteTodoById` | `{ id: TodoId }` | Removes a todo by ID |

**Exported Action Creators:**
- `createTodosAction`
- `deleteTodoByIdAction`

#### Selectors

**Location:** `src/entries/Todos/store/todos.selectors.ts`

Uses Redux Toolkit's `createSelector` for memoized selectors:

```typescript
getTodosSelector(state): Todo[]
```

Transforms the todos dictionary into an array of todo objects.

**Selector Composition:**
1. Base selector: `getTodosDictionary` - extracts `state.todosSlice.todos`
2. Memoized selector: `getTodosSelector` - converts dictionary to array

---

## Data Flow Patterns

### 1. Module Entry Pattern

**Example:** Todos Entry (`src/entries/Todos/`)

The application uses an entry/module pattern for feature organization:

```
entries/
  └── Todos/
      ├── components/      # React components
      ├── store/          # Redux slice and selectors
      ├── layouts/        # Page layouts
      ├── todos.entry.js  # Module initialization
      ├── todos.logic.js  # Business logic
      └── decs.ts        # TypeScript definitions
```

**Lifecycle:**
- `initTodosEntry()` - Registers dynamic reducer, initializes state
- `destroyTodosEntry()` - Cleanup when module unmounts

### 2. Utility Helpers

**Location:** `src/utils/utils`

**Key Utility:**
```typescript
arrayToObject({ arr, keyPropertyPath }): Record<string, T>
```

Converts an array to a dictionary/object using a specified key property. Used extensively in the todos slice to maintain normalized state.

---

## State Management Patterns

### Normalized State

The todos slice uses a **normalized state pattern**:

**Benefits:**
- O(1) lookup by ID
- Easy updates and deletions
- Prevents duplication
- Efficient re-renders

**Example:**
```typescript
// Instead of:
todos: [{ id: 1, title: "..." }, ...]

// Use:
todos: {
  "1": { id: 1, title: "..." },
  "2": { id: 2, title: "..." }
}
```

### Immutable Updates

Redux Toolkit uses **Immer** internally, allowing "mutative" syntax that produces immutable updates:

```typescript
// This looks like mutation but is actually immutable
state.todos = { ...state.todos, ...newTodos };
delete state.todos[todoId];
```

### Selector Memoization

Uses `createSelector` from Redux Toolkit (re-exports Reselect):

**Benefits:**
- Only recomputes when input selectors change
- Prevents unnecessary re-renders
- Efficient derived state

---

## Module Federation Integration

### Global Store Access

**Location:** `src/InitComp.tsx`, `src/GLOBALS`

The application exposes Redux store methods globally for module federation:

```typescript
{
  addDynamicReducer,     // Add reducer at runtime
  removeDynamicReducer,  // Remove reducer
  dispatch,              // Dispatch actions
  getStore,              // Access store
  getState,              // Get current state
  // ... other global utilities
}
```

This enables remote modules to:
1. Register their own Redux slices dynamically
2. Dispatch actions across module boundaries
3. Access shared state

---

## Type Definitions

### Type Safety

The application uses TypeScript for type safety in data models:

**Example from Todos:**
```typescript
// src/entries/Todos/decs.ts
export type TodoId = string | number;

export interface Todo {
  id: TodoId;
  title: string;
}

export interface TodosState {
  todos: Record<TodoId, Todo>;
  selectedTodoId?: TodoId;
}
```

### Payload Action Types

Redux Toolkit provides type-safe action creators:

```typescript
PayloadAction<{ todos: Todo[] }>
PayloadAction<{ id: TodoId }>
```

---

## Data Model Summary

| Slice | State Shape | Primary Use Case |
|-------|-------------|------------------|
| **track** | Track coordinates & metadata | Real-time tracking data |
| **todosSlice** | Normalized todo dictionary | Todo list management |

**Total Slices:** 2
**Total Actions:** 4
**Total Selectors:** 2

---

## Best Practices Observed

1. ✅ **Normalized State** - Dictionary-based storage for efficient lookup
2. ✅ **Type Safety** - TypeScript interfaces for all state shapes
3. ✅ **Memoized Selectors** - Using `createSelector` for performance
4. ✅ **Modular Architecture** - Feature-based folder structure
5. ✅ **Dynamic Reducers** - Support for module federation
6. ✅ **Immutable Updates** - Redux Toolkit + Immer
7. ✅ **Utility Functions** - Reusable transformation utilities

---

## Notes

- **No Server-Side Models:** This is a frontend application; no backend database models
- **API Integration:** No API clients detected in scan (may use external services)
- **Local State:** Component-level state managed via React hooks (not documented here)
- **Session Storage:** No localStorage or sessionStorage patterns detected

---

*This data models documentation was generated by the BMAD `document-project` workflow (exhaustive scan).*