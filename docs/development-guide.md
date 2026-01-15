# Development Guide

**Generated:** 2026-01-10
**Workflow:** document-project v1.2.0
**Project:** WebKit Frontend
**Tech Stack:** React 18.3.1 + TypeScript 5.7.2 + Redux Toolkit 2.4.0

---

## Overview

This guide provides comprehensive instructions for setting up, developing, and maintaining the WebKit frontend application.

---

## Prerequisites

### Required Software

| Tool | Version | Purpose |
|------|---------|---------|
| **Node.js** | 22.12.0 (LTS) | JavaScript runtime |
| **npm** | 10.x | Package manager |
| **Git** | Latest | Version control |

### Recommended IDE Setup

- **Visual Studio Code** with extensions:
  - ESLint
  - Prettier
  - TypeScript and JavaScript Language Features
  - ES7+ React/Redux/React-Native snippets
  - Jest Runner
  - Storybook

---

## Environment Setup

### 1. Clone the Repository

```bash
git clone <repository-url> webkit
cd webkit
```

### 2. Install Dependencies

```bash
# Clean install from package-lock.json
npm ci

# Or regular install
npm install
```

**Note:** The project uses `npm ci` for consistent installations across environments.

### Memory Configuration

The project requires elevated Node.js memory limits for build processes:

- **Development Server:** 8192 MB (`--max_old_space_size=8192`)
- **Production Build:** 4096 MB (`--max_old_space_size=4096`)

These are pre-configured in npm scripts.

### 3. Environment Configuration

**Configuration Files:**
- `env.config.json` - Base environment configuration
- `features-flags-dev.json` - Feature flags for development
- `features-flags-prod.json` - Feature flags for production

**Environment Variables:**
- `E2E` - Set to `"false"` for production builds (default: true)
- `DEV_BUILD` - Set to `"true"` for debug builds with source maps

---

## Development Workflow

### Starting the Development Server

```bash
npm run dev
```

**What this does:**
- Starts Webpack Dev Server with hot module replacement (HMR)
- Enables React Fast Refresh for instant component updates
- Runs on default port (check console output)
- Memory limit: 8192 MB
- Uses BlueFiber webpack configuration (`blueFiberSrc/_webpack.bf.dev.js`)

**Access the app:**
- Open browser to `http://localhost:<port>` (port displayed in console)

### File Structure for Development

When developing new features:

```
src/
└── entries/
    └── {YourFeature}/
        ├── components/           # React components
        │   └── {Component}/
        │       ├── index.tsx     # Component implementation
        │       └── __stories__/  # Storybook stories
        ├── layouts/              # Page layouts
        ├── store/                # Redux slice + selectors
        │   ├── {feature}.slice.ts
        │   └── {feature}.selectors.ts
        ├── {feature}.entry.ts    # Module initialization
        ├── {feature}.logic.ts    # Business logic
        ├── {feature}.services.ts # API/service layer (if needed)
        └── decs.ts              # TypeScript type definitions
```

### Development Commands

| Command | Description | Use When |
|---------|-------------|----------|
| `npm run dev` | Start development server | Active development with hot reload |
| `npm run build:debug` | Production build with source maps | Debugging production issues |
| `npm run build:prod` | Optimized production build | Preparing for deployment |
| `npm run build:e2e` | Build for E2E tests | Running end-to-end tests |
| `npm run test:jest` | Run Jest unit tests | Testing code changes |
| `npm run storybook` | Start Storybook server | Component development/documentation |
| `npm run build-storybook` | Build static Storybook | Deploying component docs |
| `npm run serve` | Serve built dist folder | Testing production build locally |
| `npm run clean` | Remove dist folder | Cleaning build artifacts |

---

## Building the Application

### Development Build (with Source Maps)

```bash
npm run build:debug
```

**Output:** `dist/` folder with source maps for debugging

**Environment Variables Set:**
- `DEV_BUILD="true"`
- Memory: 4096 MB

### Production Build (Optimized)

```bash
npm run build:prod
```

**Output:** `dist/` folder with minified, optimized bundles

**Environment Variables Set:**
- `E2E="false"` - Disables E2E test-specific code
- Memory: 4096 MB

**Optimizations:**
- Tree shaking
- Minification (Terser)
- Code splitting
- Compression (gzip)
- Dead code elimination

### E2E Test Build

```bash
npm run build:e2e
```

**Output:** `dist/` folder optimized for E2E testing

**Environment Variables:**
- Default settings (E2E enabled)
- Memory: 4096 MB

### Serving Built Files Locally

```bash
npm run serve
```

**What this does:**
- Serves the `dist/` folder using `serve` package
- Runs on port 3002
- Useful for testing production builds before deployment

**Access:** `http://localhost:3002`

---

## Testing Strategy

### Unit Tests (Jest)

**Run all tests:**
```bash
npm run test:jest
```

**Configuration:**
- **Framework:** Jest 29.7.0
- **Environment:** jsdom (simulates browser)
- **Testing Library:** React Testing Library 15.0.7
- **Coverage:** Configured in `jest.config.js`
- **Max Workers:** 5 (parallel execution)

**Test File Patterns:**
- `src/**/__tests__/**/*.test.{js,jsx,ts,tsx}`
- `src/**/*.test.{js,jsx,ts,tsx}`

**Writing Tests:**

```typescript
// Example: src/utils/__tests__/utils.test.ts
import { arrayToObject } from '../utils';

describe('arrayToObject', () => {
  it('converts array to object by key', () => {
    const arr = [
      { id: 1, name: 'Todo 1' },
      { id: 2, name: 'Todo 2' },
    ];
    const result = arrayToObject({ arr, keyPropertyPath: 'id' });
    expect(result).toEqual({
      1: { id: 1, name: 'Todo 1' },
      2: { id: 2, name: 'Todo 2' },
    });
  });
});
```

**Test Setup:**
- `jest.setup.js` - Global test setup and mocks
- `jest.polyfills.js` - Polyfills for modern browser features
- `@testing-library/jest-dom` - Custom matchers for DOM assertions

**Output:**
- Console results
- HTML report (via `jest-html-reporters`)
- JUnit XML (via `jest-junit`) for CI integration

### Component Development & Testing (Storybook)

**Start Storybook:**
```bash
npm run storybook
```

**Access:** `http://localhost:6006`

**Build static Storybook:**
```bash
npm run build-storybook
```

**Writing Stories:**

```typescript
// Example: src/entries/Todos/components/TodoListItem/__stories__/TodosListItem.stories.tsx
import type { Meta, StoryObj } from '@storybook/react';
import TodoListItem from '../index';

const meta: Meta<typeof TodoListItem> = {
  title: 'Todos/TodoListItem',
  component: TodoListItem,
  tags: ['autodocs'],
};

export default meta;
type Story = StoryObj<typeof TodoListItem>;

export const Default: Story = {
  args: {
    label: 'Sample Todo Item',
    onClick: () => console.log('Clicked'),
    onDeleteClick: () => console.log('Delete clicked'),
  },
};

export const LongLabel: Story = {
  args: {
    label: 'This is a very long todo item label that might wrap to multiple lines',
  },
};
```

**Storybook Addons:**
- `@storybook/addon-essentials` - Essential Storybook features
- `@storybook/addon-themes` - Theme switching
- `@storybook/addon-links` - Link between stories
- `storybook-addon-performance` - Performance monitoring

---

## Code Quality & Standards

### Linting (ESLint)

**Configuration:** `eslint.config.mjs`

**Plugins Enabled:**
- `@typescript-eslint` - TypeScript-specific rules
- `react` - React best practices
- `react-hooks` - React Hooks rules
- `react-redux` - Redux best practices
- `jsx-a11y` - Accessibility rules
- `sonarjs` - Code quality rules
- `prettier` - Prettier integration
- `jest-dom` - Jest DOM testing rules
- `testing-library` - Testing Library best practices

**Run linting:**
```bash
# Lint all files
npx eslint .

# Auto-fix issues
npx eslint . --fix
```

**VSCode Integration:**
ESLint auto-fixes on save (if configured in `.vscode/settings.json`)

### Code Formatting (Prettier)

**Configuration:** `.prettierrc.json`

**Format code:**
```bash
# Format all files
npx prettier --write .

# Check formatting
npx prettier --check .
```

**Ignored Files:** `.prettierignore`

**VSCode Integration:**
Prettier formats on save (if configured)

### TypeScript

**Configuration:** `jsconfig.json` + TypeScript compiler

**Type Checking:**
```bash
# Type check all TypeScript files
npx tsc --noEmit
```

**Type Definitions:**
- `src/bluefiber.decs.d.ts` - BlueFiber global types
- `src/entries/*/decs.ts` - Feature-specific types
- `@types/*` packages in devDependencies

---

## State Management (Redux)

### Adding a New Redux Slice

**1. Create Slice File:**

```typescript
// src/entries/MyFeature/store/myFeature.slice.ts
import { createSlice, PayloadAction } from '@reduxjs/toolkit';

interface MyFeatureState {
  data: string[];
  loading: boolean;
}

const initialState: MyFeatureState = {
  data: [],
  loading: false,
};

const myFeatureSlice = createSlice({
  name: 'myFeature',
  initialState,
  reducers: {
    setData(state, action: PayloadAction<string[]>) {
      state.data = action.payload;
    },
    setLoading(state, action: PayloadAction<boolean>) {
      state.loading = action.payload;
    },
  },
});

export const { setData, setLoading } = myFeatureSlice.actions;
export default myFeatureSlice.reducer;
```

**2. Create Selectors:**

```typescript
// src/entries/MyFeature/store/myFeature.selectors.ts
import { createSelector } from '@reduxjs/toolkit';

interface State {
  myFeatureSlice: MyFeatureState;
}

const getMyFeatureState = (state: State) => state?.myFeatureSlice;

export const getDataSelector = createSelector(
  [getMyFeatureState],
  (state) => state?.data || []
);

export const getLoadingSelector = createSelector(
  [getMyFeatureState],
  (state) => state?.loading || false
);
```

**3. Register Slice Dynamically:**

```typescript
// src/entries/MyFeature/myFeature.entry.ts
import { addDynamicReducer, removeDynamicReducer } from 'src/GLOBALS';
import myFeatureReducer from './store/myFeature.slice';

export const initMyFeatureEntry = () => {
  addDynamicReducer('myFeatureSlice', myFeatureReducer);
  // Additional initialization...
};

export const destroyMyFeatureEntry = () => {
  removeDynamicReducer('myFeatureSlice');
  // Additional cleanup...
};
```

**4. Use in Components:**

```typescript
import { useSelector, useDispatch } from 'react-redux';
import { getDataSelector } from '../store/myFeature.selectors';
import { setData } from '../store/myFeature.slice';

function MyComponent() {
  const dispatch = useDispatch();
  const data = useSelector(getDataSelector);

  const handleUpdate = () => {
    dispatch(setData(['new', 'data']));
  };

  return <div>{/* ... */}</div>;
}
```

---

## Module Federation

### Exposing Modules

**Configuration:** `bluefiber.config.js` + `MODULES_EXPOSE.json`

**Example: Expose a Component:**

```javascript
// bluefiber.config.js
module.exports = {
  exposes: {
    './MyComponent': './src/entries/MyFeature/components/MyComponent',
  },
};
```

**Update Manifest:**
```json
// MODULES_EXPOSE.json
{
  "exposes": {
    "./MyComponent": {
      "description": "Reusable component for...",
      "import": "./src/entries/MyFeature/components/MyComponent"
    }
  }
}
```

### Consuming Remote Modules

Remote modules access global Redux store via:

```typescript
import { dispatch, getState, addDynamicReducer } from 'src/GLOBALS';

// Add remote reducer
addDynamicReducer('remoteFeature', remoteReducer);

// Dispatch actions
dispatch(someAction());

// Get state
const state = getState();
```

---

## Common Development Tasks

### Adding a New Component

1. Create component folder: `src/entries/{Feature}/components/{ComponentName}/`
2. Create component file: `index.tsx`
3. Define types in `decs.ts` (if complex)
4. Create Storybook story: `__stories__/{ComponentName}.stories.tsx`
5. Add tests: `__tests__/{ComponentName}.test.tsx` (if needed)

### Adding a New Feature Module

1. Create entry folder: `src/entries/{FeatureName}/`
2. Create folder structure: `components/`, `layouts/`, `store/`
3. Create entry file: `{feature}.entry.ts` with init/destroy functions
4. Create Redux slice: `store/{feature}.slice.ts`
5. Create selectors: `store/{feature}.selectors.ts`
6. Create type definitions: `decs.ts`
7. Implement business logic: `{feature}.logic.ts`
8. Add services: `{feature}.services.ts` (if needed)

### Debugging

**React DevTools:**
- Install React DevTools browser extension
- Inspect component hierarchy and props
- View Redux state with Redux DevTools

**Source Maps:**
- Use `npm run build:debug` for source maps in production builds
- Enables debugging original TypeScript/JSX code in browser

**Performance Profiling:**
- Use React DevTools Profiler
- Storybook Performance addon
- Chrome DevTools Performance tab

**Why Did You Render:**
- Configured via `@welldone-software/why-did-you-render`
- Detects unnecessary re-renders (development only)

---

## Performance Optimization

### Bundle Analysis

```bash
# Build with webpack bundle analyzer
npm run build:prod
# Bundle report generated automatically
```

**Optimization Techniques:**
- Code splitting (automatic via webpack)
- Tree shaking (enabled in production)
- Lazy loading modules
- Memoized selectors (Redux Toolkit `createSelector`)
- React.memo for expensive components

### Build Performance

**Faster Builds:**
- Use `npm run dev` for incremental compilation
- Fork TS Checker Webpack Plugin runs TypeScript checks in separate process
- Babel caching enabled
- Webpack caching enabled

---

## Troubleshooting

### Common Issues

**Issue:** Out of memory during build
**Solution:** Memory limits are pre-configured in npm scripts. If still failing, increase `--max_old_space_size`.

**Issue:** Module not found errors
**Solution:**
```bash
rm -rf node_modules package-lock.json
npm install
```

**Issue:** Webpack dev server not hot reloading
**Solution:** Check React Fast Refresh configuration, restart dev server.

**Issue:** TypeScript errors in IDE but not in build
**Solution:** Restart TypeScript server in VSCode, or run `npx tsc --noEmit` to check.

**Issue:** Storybook not loading stories
**Solution:** Ensure story files match pattern in `.storybook/main.ts`, restart Storybook.

---

## Git Workflow

### Branch Strategy

- `main` / `master` - Production-ready code
- `develop` - Development branch
- `feature/*` - Feature branches
- `fix/*` - Bug fix branches

### Commit Conventions

Follow conventional commits format (recommended):

```
feat: Add new todo filter
fix: Resolve Redux state mutation
docs: Update development guide
test: Add tests for TodoListItem
chore: Update dependencies
```

### Pull Request Process

1. Create feature branch from `develop`
2. Make changes and commit
3. Push to remote
4. Create Pull Request to `develop`
5. CI runs automatically (lint, test, build)
6. Get code review
7. Merge when approved and CI passes

---

## Additional Resources

### Documentation

- [Project Structure](./project-structure.md) - High-level architecture
- [Source Tree Analysis](./source-tree-analysis.md) - Annotated directory tree
- [Data Models](./data-models-1.md) - Redux state documentation
- [Technology Stack](./technology_stack.md) - Full tech stack analysis
- [UI Component Inventory](./ui_component_inventory_1.md) - Component catalog

### External Resources

- [React Documentation](https://react.dev/)
- [Redux Toolkit Documentation](https://redux-toolkit.js.org/)
- [Material-UI Documentation](https://mui.com/)
- [Webpack Module Federation](https://webpack.js.org/concepts/module-federation/)
- [TypeScript Handbook](https://www.typescriptlang.org/docs/)
- [Jest Documentation](https://jestjs.io/)
- [Storybook Documentation](https://storybook.js.org/)

---

## Getting Help

**IDE Settings:** Check `.vscode/settings.json` for recommended VSCode configuration

**Configuration Files:**
- `babel.config.js` - Babel transpilation
- `jest.config.js` - Test configuration
- `eslint.config.mjs` - Linting rules
- `bluefiber.config.js` - Module federation
- `.storybook/main.ts` - Storybook setup

---

*This development guide was generated by the BMAD `document-project` workflow (exhaustive scan, Step 6).*
