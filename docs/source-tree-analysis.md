# Source Tree Analysis

**Generated:** 2026-01-10
**Workflow:** document-project v1.2.0
**Project Type:** Web (Frontend - React + TypeScript + Redux)

---

## Overview

This document provides an annotated directory tree of the WebKit frontend application, highlighting critical folders, entry points, and architectural patterns.

---

## Project Root Structure

```
WebKit/
├── .github/                    # GitHub configuration
│   └── workflows/
│       └── ci.yml             # CI/CD workflow (generated)
│
├── .nginx/                     # NGINX configuration for deployment
│   ├── mime.types             # MIME type mappings
│   └── nginx.conf             # NGINX server configuration
│
├── .storybook/                 # Storybook configuration for component development
│   ├── main.ts                # Main Storybook configuration
│   ├── preview.ts             # Preview configuration
│   ├── ProviderFN.tsx         # Provider wrapper for stories
│   └── _directionWrapper.tsx  # Direction (RTL/LTR) wrapper
│
├── blueFiberSrc/               # BlueFiber webpack module federation framework
│   ├── bootstrap.jsx          # Application bootstrap entry point
│   ├── configureStore.ts      # Redux store configuration with dynamic reducers
│   ├── index.js               # Main BlueFiber entry
│   ├── PluginWrapper.tsx      # Plugin wrapper component
│   ├── _webpack.bf.common.js  # Common webpack config
│   ├── _webpack.bf.dev.js     # Development webpack config
│   ├── _webpack.bf.prod.js    # Production webpack config
│   └── setup.sh               # BlueFiber setup script
│
├── docs/                       # Project documentation
│   ├── index.md               # Documentation index (master entry point)
│   ├── project-structure.md   # High-level structure documentation
│   ├── project-parts-metadata.md  # Project metadata and detection evidence
│   ├── technology_stack.md    # Comprehensive technology stack analysis
│   ├── data-models-1.md       # Redux state models and slices
│   ├── ui_component_inventory_1.md  # UI component catalog
│   ├── component-usage-map-1.md    # Component dependency mapping
│   ├── api-contracts-1.md     # API contracts discovery
│   └── ci_cd_detection.md     # CI/CD detection report
│
├── public/                     # Static assets served directly
│   └── index.html             # HTML template entry point
│
├── src/                        # **PRIMARY SOURCE CODE** → See detailed breakdown below
│
├── .dockerignore               # Docker ignore patterns
├── .gitignore                  # Git ignore patterns
├── .prettierignore             # Prettier formatter ignore patterns
├── .prettierrc.json            # Prettier configuration
├── babel.config.js             # Babel transpiler configuration
├── bluefiber.config.js         # BlueFiber module federation config
├── DockerfileFull              # Docker container definition
├── env.config.json             # Environment configuration
├── eslint.config.mjs           # ESLint linting configuration
├── features-flags-dev.json     # Feature flags for development
├── features-flags-prod.json    # Feature flags for production
├── jest.config.js              # Jest testing framework configuration
├── jest.polyfills.js           # Jest polyfills for modern features
├── jest.setup.js               # Jest test setup and global mocks
├── jsconfig.json               # JavaScript/TypeScript project configuration
├── MODULES_EXPOSE.json         # Module federation exposed modules manifest
├── MuiTheme.js                 # Material-UI theme configuration
├── package.json                # NPM dependencies and scripts
├── package-lock.json           # NPM dependency lock file
└── README.md                   # Project README with quick start guide
```

---

## Source Directory (`src/`) - Detailed Breakdown

The `src/` directory contains the core application code organized in a modular, entry-based architecture.

```
src/
├── assets/                     # Static assets (images, data)
│   ├── images/
│   │   └── logo-blue.png      # Application logo
│   └── demoTodos.json         # Demo data for todos feature
│
├── entries/                    # **MODULAR ENTRIES PATTERN** - Feature-based modules
│   └── Todos/                 # Todo management entry/module
│       ├── components/        # React components for Todos entry
│       │   └── TodoListItem/  # Individual todo item component
│       │       ├── index.tsx  # TodoListItem component implementation
│       │       └── __stories__/  # Storybook stories for TodoListItem
│       │           └── TodosListItem.stories.tsx
│       │
│       ├── layouts/           # Page layouts for Todos entry
│       │   └── DemoTrackPage.jsx  # Main demo track page layout
│       │
│       ├── store/             # Redux state management for Todos
│       │   ├── todos.slice.ts      # Todos Redux slice (actions + reducer)
│       │   └── todos.selectors.ts  # Memoized selectors for todos state
│       │
│       ├── decs.ts            # TypeScript type definitions
│       ├── todos.entry.ts     # Entry initialization/cleanup (dynamic reducer registration)
│       ├── todos.logic.ts     # Business logic for todos feature
│       └── demoTrack.services.ts  # Services for demo track functionality
│
├── external/                   # External action components (module federation)
│   ├── ExternalAction.tsx     # External action component wrapper
│   ├── __stories__/           # Storybook stories
│   └── __tests__/             # Unit tests for external components
│
├── store/                      # **GLOBAL REDUX STORE** - Application-level state
│   ├── trackSlice.js          # Track slice (lon, lat, alt, speed state)
│   └── trackSelector.js       # Track data selector
│
├── styles/                     # Global styles and theming
│
├── utils/                      # Shared utility functions
│   ├── __tests__/             # Unit tests for utilities
│   └── [utility modules]      # Helper functions (e.g., arrayToObject)
│
├── App.jsx                     # **MAIN APPLICATION COMPONENT** - Root React component
├── bluefiber.decs.d.ts        # TypeScript declarations for BlueFiber globals
└── InitComp.tsx               # Initialization component (sets up global store access)
```

---

## Critical Folders Summary

### Application Entry Points

| Path | Purpose | Role |
|------|---------|------|
| [blueFiberSrc/bootstrap.jsx](../blueFiberSrc/bootstrap.jsx) | Application bootstrap | Module federation setup + app initialization |
| [src/App.jsx](../src/App.jsx) | Main React component | Root application UI component |
| [src/InitComp.tsx](../src/InitComp.tsx) | Global initialization | Exposes Redux store methods globally for module federation |
| [public/index.html](../public/index.html) | HTML entry point | Root HTML template |

### State Management

| Path | Purpose | State Managed |
|------|---------|---------------|
| [blueFiberSrc/configureStore.ts](../blueFiberSrc/configureStore.ts) | Redux store configuration | Dynamic reducer injection, store setup |
| [src/store/](../src/store/) | Global state slices | Track data (lon, lat, alt, speed) |
| [src/entries/Todos/store/](../src/entries/Todos/store/) | Todos feature state | Todo items, selected todo ID |

### Module Federation Architecture

| Path | Purpose | Integration Point |
|------|---------|-------------------|
| [bluefiber.config.js](../bluefiber.config.js) | Module federation config | Defines exposed/consumed modules |
| [MODULES_EXPOSE.json](../MODULES_EXPOSE.json) | Exposed modules manifest | Lists modules available to remote apps |
| [src/InitComp.tsx](../src/InitComp.tsx) | Global API exposure | Exposes addDynamicReducer, dispatch, getStore, etc. |

### Feature Modules (Entry Pattern)

The application uses an **entry/module pattern** for feature organization:

```
entries/
└── {FeatureName}/
    ├── components/      # Feature-specific React components
    ├── layouts/         # Page layouts
    ├── store/          # Redux slice, actions, selectors
    ├── {feature}.entry.ts   # Module registration (initEntry, destroyEntry)
    ├── {feature}.logic.ts   # Business logic
    ├── {feature}.services.ts  # API/service layer
    └── decs.ts         # TypeScript definitions
```

**Current Entries:**
- `Todos/` - Todo list management feature

**Entry Lifecycle:**
1. `init{Feature}Entry()` - Registers dynamic reducer, initializes state
2. Component rendering - Uses Redux hooks to access feature state
3. `destroy{Feature}Entry()` - Cleanup when module unmounts

### Build & Configuration

| Path | Purpose | Technology |
|------|---------|------------|
| [blueFiberSrc/_webpack.bf.*.js](../blueFiberSrc/) | Webpack configs | Module federation + build optimization |
| [babel.config.js](../babel.config.js) | Transpilation | React + TypeScript → ES5 |
| [jest.config.js](../jest.config.js) | Testing | Unit test configuration |
| [.storybook/](../.storybook/) | Component development | Isolated component testing & docs |

### Testing Strategy

| Path | Test Type | Coverage |
|------|-----------|----------|
| `src/**/__tests__/` | Unit tests | Utilities, components, store |
| `src/**/__stories__/` | Storybook stories | Visual component testing |
| [jest.config.js](../jest.config.js) | Configuration | Jest + React Testing Library |

---

## Architecture Patterns

### 1. Module Federation Pattern

The application is designed as a **module federation host** capable of:
- Exposing components/utilities to remote applications
- Consuming remote modules dynamically
- Sharing Redux store across module boundaries

**Key Files:**
- `bluefiber.config.js` - Defines federation configuration
- `MODULES_EXPOSE.json` - Manifest of exposed modules
- `src/InitComp.tsx` - Global API for remote modules

### 2. Dynamic Redux Architecture

**Store Configuration:**
- Base store created in `blueFiberSrc/configureStore.ts`
- Reducers registered dynamically via `addDynamicReducer()`
- Enables module isolation and lazy loading

**Example Flow:**
```
1. Entry loads → initTodosEntry()
2. Entry registers slice → addDynamicReducer('todosSlice', todosReducer)
3. Components use state → useSelector(getTodosSelector)
4. Entry unloads → destroyTodosEntry() → removeDynamicReducer('todosSlice')
```

### 3. Component-Based UI Architecture

**Layers:**
- **Presentational Components** (`src/entries/*/components/`) - Pure UI components
- **Layout Components** (`src/entries/*/layouts/`) - Page structure and composition
- **Container Components** (`src/App.jsx`) - Redux-connected components

**Design System:**
- Material-UI (MUI) v6.1.10 for UI components
- Styled components via MUI's `styled()` API
- Theme configuration in `MuiTheme.js`

---

## Development Workflow

### Local Development

```bash
# Install dependencies
npm install

# Start development server
npm start  # Runs webpack dev server with hot reload

# Run tests
npm test   # Jest unit tests
npm run test:watch  # Watch mode

# Storybook component development
npm run storybook
```

### Build Process

```bash
# Production build
npm run build  # Webpack production build

# Analyze bundle
npm run build:analyze  # Webpack bundle analyzer
```

### Code Quality

```bash
# Linting
npm run lint       # ESLint
npm run lint:fix   # Auto-fix issues

# Formatting
npm run format     # Prettier
```

---

## Deployment Architecture

### Docker Deployment

- **Dockerfile:** `DockerfileFull`
- **NGINX Config:** `.nginx/nginx.conf`
- **Static Assets:** Served via NGINX
- **Container Port:** Configured in nginx.conf

### CI/CD Pipeline

- **Platform:** GitHub Actions
- **Workflow:** `.github/workflows/ci.yml` (generated)
- **Stages:** Install → Lint → Test → Build

**Note:** CI/CD workflow was auto-generated as part of documentation. Review and customize before use.

---

## Integration Points

### Global Store Access

The application exposes Redux store methods globally for module federation:

```typescript
// Set in src/InitComp.tsx, accessible globally
window.GLOBALS = {
  addDynamicReducer,     // Register reducer at runtime
  removeDynamicReducer,  // Remove reducer
  dispatch,              // Dispatch actions
  getStore,              // Access store instance
  getState,              // Get current state
  addGlobalComponent,    // Register global components
  removeGlobalComponentById,  // Remove global components
  addMFTheme,            // Add module federation theme
  removeMFThemeById,     // Remove MF theme
  updateMFTheme,         // Update MF theme
};
```

**Usage by Remote Modules:**
1. Remote module loads
2. Accesses `window.GLOBALS.addDynamicReducer`
3. Registers own Redux slice
4. Uses `window.GLOBALS.dispatch` to dispatch actions
5. Shares state with host application

---

## Navigation Map

### For New Features

**Adding a new entry/module:**
1. Create `src/entries/{FeatureName}/` directory
2. Add `{feature}.entry.ts` with init/destroy functions
3. Create Redux slice in `store/{feature}.slice.ts`
4. Build components in `components/`
5. Create layouts in `layouts/`
6. Register in `bluefiber.config.js` if exposing via module federation

**Adding a global state slice:**
1. Create slice in `src/store/{feature}Slice.js`
2. Register in `blueFiberSrc/configureStore.ts` (static registration)
3. Create selectors in `src/store/{feature}Selector.js`

**Adding a new component:**
1. Create component in appropriate feature `components/` folder
2. Add TypeScript types in feature `decs.ts`
3. Create Storybook story in `__stories__/` for documentation
4. Add unit tests in `__tests__/` if complex logic exists

---

## Notes

- **Monorepo Structure:** Single monolithic frontend application (not a monorepo)
- **Module Federation:** Designed for micro-frontend architecture but currently standalone
- **State Management:** Redux Toolkit with normalized state patterns
- **Type Safety:** TypeScript for core features, JavaScript for legacy code
- **Testing:** Jest + React Testing Library for unit tests, Storybook for visual testing
- **Build Tool:** Custom BlueFiber webpack configuration system

---

*This source tree analysis was generated by the BMAD `document-project` workflow (exhaustive scan, Step 5).*
