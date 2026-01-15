# Getting Started with WebKit Bootstrap

**WebKit is a production-ready React + TypeScript bootstrap that serves as the foundation for building frontend applications.**

This guide will help you understand how to use WebKit and get started with your project.

---

## 📋 Table of Contents

- [Understanding WebKit](#understanding-webkit)
- [Prerequisites](#prerequisites)
- [Choose Your Path](#choose-your-path)
  - [Path A: Building a Standalone SPA](#path-a-building-a-standalone-spa)
  - [Path B: Building a Module Federation Component](#path-b-building-a-module-federation-component)
- [Build Process Explained](#build-process-explained)
- [Common Configuration](#common-configuration)
- [Next Steps](#next-steps)

---

## Understanding WebKit

**WebKit is a bootstrap template** - a starting point that provides:

✅ **Complete React + TypeScript setup** with modern tooling
✅ **Redux Toolkit** for state management with dynamic reducer injection
✅ **Material-UI** component library for consistent design
✅ **Webpack Module Federation** ready for micro-frontend architecture
✅ **Production-ready build system** with Docker and Kubernetes support
✅ **Comprehensive testing** setup (Jest + Storybook)
✅ **CI/CD pipeline** configuration

**You can use WebKit in two ways:**

1. **Standalone SPA (Single Page Application)** - Fork WebKit to create a complete, independent web application
2. **Module Federation Component** - Fork WebKit to create reusable components that integrate into other SPAs

Both approaches share the same codebase, tooling, and build system - the difference is in **what you expose** and **how you configure** the output.

![WebKit Bootstrap Architecture](./webkit-bootstrap-architecture.svg)

---

## Prerequisites

Before starting, ensure you have:

- **Node.js** 18.x or higher (22.12.0 recommended)
- **npm** 10.x or higher
- **Git** for version control
- **Basic knowledge** of React, TypeScript, and Redux

**Recommended IDE:**
- Visual Studio Code with ESLint, Prettier, and TypeScript extensions

---

## Choose Your Path

### Path A: Building a Standalone SPA

**Use this path when:** You want to create a complete, independent web application.

#### Step 1: Fork and Clone

```bash
# Fork the repository on GitHub first, then:
git clone https://github.com/YOUR-USERNAME/YOUR-APP-NAME.git
cd YOUR-APP-NAME

# Install dependencies
npm ci
```

#### Step 2: Customize Your Application

**Update Project Identity:**

1. **`package.json`** - Change the name, description, and version:
   ```json
   {
     "name": "your-app-name",
     "version": "1.0.0",
     "description": "Your application description"
   }
   ```

2. **`public/index.html`** - Update the title:
   ```html
   <title>Your App Name</title>
   ```

3. **`README.md`** - Replace with your application's description

4. **Remove Example Code:**
   - Delete `src/entries/Todos/` (example feature)
   - Clean up `src/App.jsx` - remove references to Todos

**Configure Module Federation (Optional for SPA):**

If your SPA won't expose modules to others, you can simplify `bluefiber.config.js`:

```javascript
module.exports = {
  name: 'YourAppName',
  // Remove or minimize 'exposes' if not sharing components
  exposes: {},
  // Remove 'remotes' if not consuming external modules
  remotes: {},
};
```

#### Step 3: Develop Your Application

```bash
# Start development server
npm run dev

# Access at: http://localhost:<port>
```

**Development workflow:**
1. Create feature modules in `src/entries/{FeatureName}/`
2. Build components in `src/entries/{FeatureName}/components/`
3. Create Redux slices in `src/entries/{FeatureName}/store/`
4. Add layouts in `src/entries/{FeatureName}/layouts/`

See [Development Guide](./docs/development-guide.md) for detailed instructions.

#### Step 4: Build for Production

```bash
# Production build
npm run build:prod

# Output: dist/ folder ready for deployment
```

**What gets built:**
- Standalone SPA bundle
- All assets (JS, CSS, images)
- Optimized and minified code
- Module Federation runtime (even if not exposing modules)

**Deploy:**
```bash
# Docker
docker build -f DockerfileFull -t your-app:latest .
docker run -d -p 8080:8080 your-app:latest

# Or deploy dist/ folder to any static hosting
```

See [Deployment Guide](./docs/deployment-guide.md) for complete procedures.

---

### Path B: Building a Module Federation Component

**Use this path when:** You want to create reusable components that integrate into other applications (host SPAs).

**Key Concept:** Your module will share the Redux store with the host application and expose specific components for consumption.

![Module Federation Integration](./module-federation-integration.svg)

#### Step 1: Fork and Clone

```bash
# Fork the repository on GitHub first, then:
git clone https://github.com/YOUR-USERNAME/YOUR-MODULE-NAME.git
cd YOUR-MODULE-NAME

# Install dependencies
npm ci
```

#### Step 2: Configure Module Federation

**Update `bluefiber.config.js`:**

```javascript
module.exports = {
  name: 'YourModuleName', // Unique name for your module

  // Define what components you're exposing
  exposes: {
    './MyComponent': './src/entries/MyFeature/components/MyComponent',
    './MyLayout': './src/entries/MyFeature/layouts/MyLayout',
    // Add more exports as needed
  },

  // Shared dependencies with host (important!)
  shared: {
    react: { singleton: true, requiredVersion: '^18.3.1' },
    'react-dom': { singleton: true, requiredVersion: '^18.3.1' },
    '@reduxjs/toolkit': { singleton: true },
    'react-redux': { singleton: true },
    '@mui/material': { singleton: true },
  },
};
```

**Update `MODULES_EXPOSE.json`:**

This file documents what you're exposing for consumers:

```json
{
  "moduleName": "YourModuleName",
  "version": "1.0.0",
  "exposes": {
    "./MyComponent": {
      "description": "Main component that does X",
      "import": "./src/entries/MyFeature/components/MyComponent",
      "props": {
        "onAction": "function - Called when user performs action",
        "data": "object - Initial data to display"
      }
    },
    "./MyLayout": {
      "description": "Layout wrapper for Y",
      "import": "./src/entries/MyFeature/layouts/MyLayout"
    }
  }
}
```

#### Step 3: Build Your Module Components

**Create your feature module:**

```bash
src/entries/MyFeature/
├── components/
│   └── MyComponent/
│       ├── index.tsx          # Your component
│       └── __stories__/       # Storybook stories
├── store/
│   ├── myFeature.slice.ts     # Redux slice
│   └── myFeature.selectors.ts # Selectors
├── myFeature.entry.ts         # Entry lifecycle
└── decs.ts                    # TypeScript types
```

**Entry Lifecycle (`myFeature.entry.ts`):**

```typescript
import { addDynamicReducer, removeDynamicReducer } from 'src/GLOBALS';
import myFeatureReducer from './store/myFeature.slice';

export const initMyFeatureEntry = () => {
  // Register reducer with host's Redux store
  addDynamicReducer('myFeatureSlice', myFeatureReducer);

  // Additional initialization
  console.log('MyFeature module initialized');
};

export const destroyMyFeatureEntry = () => {
  // Clean up
  removeDynamicReducer('myFeatureSlice');
  console.log('MyFeature module destroyed');
};
```

**Important:** Your module shares the Redux store with the host application via `window.GLOBALS`:
- `addDynamicReducer` - Register your slice with the host store
- `removeDynamicReducer` - Unregister when unmounting
- `dispatch` - Dispatch actions to the shared store
- `getState` - Access the shared state

#### Step 4: Test Your Module

```bash
# Start development server
npm run dev

# Start Storybook for component testing
npm run storybook
```

Storybook is crucial for module development - it lets you test components in isolation before integrating with a host.

#### Step 5: Build Your Module

```bash
# Production build
npm run build:prod
```

**What gets built:**
- Module Federation remote bundle
- Exposed components accessible by host applications
- `remoteEntry.js` - Entry point for module consumption
- Shared dependencies marked as externals (provided by host)

**Build outputs in `dist/`:**
```
dist/
├── remoteEntry.js        # Module Federation entry point
├── src_entries_MyFeature_components_MyComponent.js
├── src_entries_MyFeature_layouts_MyLayout.js
└── [other chunk files]
```

#### Step 6: Publish and Document

**For Host Applications to Consume Your Module:**

1. **Deploy your module** (to CDN, static hosting, or artifact repository)

2. **Provide integration instructions:**

```javascript
// In host application's webpack config or bluefiber.config.js
module.exports = {
  remotes: {
    YourModuleName: 'YourModuleName@https://your-cdn.com/remoteEntry.js',
  },
};
```

3. **Usage in host application:**

```typescript
// Import your exposed component
import { lazy } from 'react';

const MyComponent = lazy(() => import('YourModuleName/MyComponent'));

// Initialize module lifecycle
import('YourModuleName/myFeature.entry').then(({ initMyFeatureEntry }) => {
  initMyFeatureEntry();
});

// Use the component
<MyComponent onAction={handleAction} data={initialData} />
```

4. **Document your exposed API** in MODULES_EXPOSE.json and README

---

## Build Process Explained

### What Happens During Build

**Both SPA and Module builds use the same process:**

```bash
npm run build:prod
```

This triggers `blueFiberSrc/_webpack.bf.prod.js` which:

1. **Bundles your code** with Webpack 5
2. **Configures Module Federation** based on `bluefiber.config.js`
3. **Optimizes output**:
   - Tree shaking (removes unused code)
   - Minification (Terser for JS, cssnano for CSS)
   - Code splitting (creates optimized chunks)
   - Compression (gzip)

4. **Generates outputs**:
   - **For SPA:** Complete standalone bundle in `dist/`
   - **For Module:** Federation remote bundle with `remoteEntry.js`

### Key Differences: SPA vs Module

| Aspect | SPA Build | Module Build |
|--------|-----------|--------------|
| **Purpose** | Standalone deployable application | Consumable component library |
| **Entry Point** | `dist/index.html` | `dist/remoteEntry.js` |
| **Dependencies** | Bundled with the app | Shared with host (externals) |
| **Deployment** | Full application deployment | CDN/artifact repository |
| **Redux Store** | Self-contained | Integrates with host store |
| **Routing** | Own React Router | Uses host's router or isolated |

### Build Configuration

**`bluefiber.config.js` controls the output:**

```javascript
module.exports = {
  name: 'AppName',           // Module Federation name

  exposes: {                 // What to expose (Module only)
    './Component': './src/...',
  },

  remotes: {                 // What to consume (optional)
    'RemoteApp': 'RemoteApp@url/remoteEntry.js',
  },

  shared: {                  // Shared dependencies
    react: { singleton: true },
    // ... other shared deps
  },
};
```

**For SPA:** Minimal `exposes`, no `remotes` (unless consuming other modules)
**For Module:** Define `exposes` for all components you want to share

### Build Commands Reference

| Command | Purpose | Use Case |
|---------|---------|----------|
| `npm run dev` | Development server with HMR | Active development |
| `npm run build:prod` | Production build (optimized) | Deployment |
| `npm run build:debug` | Debug build (source maps) | Troubleshooting production |
| `npm run build:e2e` | E2E test build | Testing |

**Memory Configuration:**
- Builds use elevated Node.js memory: `--max_old_space_size=4096`
- Pre-configured in package.json scripts

---

## Common Configuration

### Environment Variables

**`env.config.json`** - Runtime configuration:

```json
{
  "API_URL": "https://api.example.com",
  "FEATURE_FLAGS": {
    "newFeature": true
  }
}
```

This file is injected at runtime via `blueFiberSrc/setup.sh` in Docker containers.

### Feature Flags

**Development:** `features-flags-dev.json`
**Production:** `features-flags-prod.json`

Toggle features without rebuilding:

```json
{
  "enableBetaFeature": false,
  "showDebugPanel": true
}
```

### Module Federation Globals

**`src/InitComp.tsx`** sets up `window.GLOBALS` for module integration:

```typescript
window.GLOBALS = {
  addDynamicReducer,     // Register Redux reducer
  removeDynamicReducer,  // Unregister Redux reducer
  dispatch,              // Dispatch actions
  getStore,              // Access store
  getState,              // Get current state
  // ... other global methods
};
```

**For Module developers:** Your modules access these globals to integrate with host applications.
**For SPA developers:** These globals are available if you consume external modules.

---

## Next Steps

### After Forking WebKit

**1. Clean Up Example Code**
- Remove `src/entries/Todos/` directory
- Clean `src/App.jsx` to remove Todos references
- Update `src/store/` to remove example slices if not needed

**2. Set Up Your Project**
- Update package.json (name, version, description)
- Configure bluefiber.config.js for your needs
- Update README.md with your project details

**3. Start Building**
- **SPA Path:** Create your application features in `src/entries/`
- **Module Path:** Build and expose specific components in `src/entries/`

**4. Comprehensive Documentation Available**
- [Development Guide](./docs/development-guide.md) - Day-to-day development workflows
- [Deployment Guide](./docs/deployment-guide.md) - Production deployment procedures
- [Source Tree Analysis](./docs/source-tree-analysis.md) - Navigate the codebase
- [Data Models](./docs/data-models-1.md) - Redux state management patterns
- [Documentation Index](./docs/index.md) - Complete documentation navigation

### Common Tasks

**Adding a New Feature:**
```bash
# Create feature structure
mkdir -p src/entries/MyFeature/{components,store,layouts}

# Create Redux slice
touch src/entries/MyFeature/store/myFeature.slice.ts
touch src/entries/MyFeature/store/myFeature.selectors.ts

# Create entry lifecycle
touch src/entries/MyFeature/myFeature.entry.ts

# Create main component
mkdir src/entries/MyFeature/components/MyComponent
touch src/entries/MyFeature/components/MyComponent/index.tsx
```

See [Development Guide - Adding a New Feature](./docs/development-guide.md#adding-a-new-feature-module) for detailed instructions.

**Running Tests:**
```bash
# Unit tests
npm run test:jest

# Component development/testing
npm run storybook
```

**Code Quality:**
```bash
# Lint code
npx eslint .

# Type checking
npx tsc --noEmit

# Format code
npx prettier --write .
```

---

## Support

### Getting Help

1. **Documentation:** Start with [docs/index.md](./docs/index.md) for comprehensive navigation
2. **Development Questions:** See [Development Guide](./docs/development-guide.md)
3. **Deployment Issues:** Check [Deployment Guide](./docs/deployment-guide.md)
4. **Architecture Questions:** Review [Source Tree Analysis](./docs/source-tree-analysis.md)

### For AI Agents

This documentation is optimized for AI-assisted development. Key context files:
- `GETTING-STARTED.md` (this file) - Bootstrap instructions
- `docs/index.md` - Documentation master index
- `docs/source-tree-analysis.md` - Codebase structure
- `docs/development-guide.md` - Development patterns
- `docs/data-models-1.md` - State management architecture

When creating PRDs or planning features for projects built with WebKit, reference these documents for architectural context.

---

## Key Concepts Summary

### For Human Developers

✅ **WebKit is a template** - Fork it to start your project
✅ **Two usage modes** - SPA or Module Federation component
✅ **Same build process** - Configuration determines output
✅ **Shared Redux store** - Modules integrate with host stores
✅ **Production-ready** - Includes testing, CI/CD, and deployment configs

### For AI Agents

✅ **Bootstrap pattern** - WebKit serves as foundation for new React applications
✅ **Module Federation architecture** - Supports micro-frontend patterns with shared dependencies
✅ **Dynamic Redux** - Reducers registered at runtime via global API
✅ **Entry-based features** - Modular structure with lifecycle methods
✅ **Build configuration** - `bluefiber.config.js` and `MODULES_EXPOSE.json` control Federation behavior

---

**Ready to build? Choose your path above and start coding!** 🚀
