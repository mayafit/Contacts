# Project Structure

**Generated:** 2026-01-10
**Workflow:** document-project v1.2.0
**Scan Level:** exhaustive

---

## Repository Classification

**Type:** Monolith
**Architecture:** Single cohesive frontend application

---

## Project Overview

**WebKit** is a monolithic web frontend application serving as a bootstrap for building frontend web modules for the Starlight system.

- **Primary Language:** TypeScript 5.7.2
- **Framework:** React 18.3.1
- **State Management:** Redux Toolkit 2.4.0
- **UI Library:** Material-UI (MUI) 6.1.10
- **Build System:** Webpack 5.97.1
- **Testing Framework:** Jest 29.7.0
- **Component Documentation:** Storybook 8.4.6

---

## Directory Structure

```
d:\Code\WebKit/
├── src/                    # Application source code (React components, state management)
│   ├── assets/            # Static assets
│   ├── entries/           # Module entry points
│   ├── external/          # External integrations
│   ├── store/             # Redux store configuration
│   ├── styles/            # Global styles
│   └── utils/             # Utility functions
├── blueFiberSrc/          # Build helpers and webpack configurations
├── public/                # Static public assets and demo files
├── docs/                  # Generated documentation (this file)
├── _bmad/                 # BMAD framework (workflows, agents, templates)
├── _bmad-output/          # Workflow outputs and status tracking
├── .storybook/            # Storybook configuration
├── .github/               # GitHub Actions CI/CD
├── .vscode/               # VS Code workspace settings
└── [config files]         # Various configuration files at root
```

---

## Key Entry Points

- **Main Application:** `src/App.jsx`, `src/InitComp.tsx`
- **Build Configs:** `blueFiberSrc/_webpack.bf.dev.js`, `blueFiberSrc/_webpack.bf.prod.js`
- **Webpack Entry:** `webpack.common.js`, `webpack.dev.js`, `webpack.prod.js`
- **Test Config:** `jest.config.js`
- **Storybook:** `.storybook/main.js`

---

## Technology Stack

| Category | Technology | Version | Purpose |
|----------|-----------|---------|---------|
| **Core** |
| Language | TypeScript | 5.7.2 | Type-safe JavaScript |
| Framework | React | 18.3.1 | UI framework |
| State Management | Redux Toolkit | 2.4.0 | Global state management |
| **UI/Styling** |
| Component Library | Material-UI | 6.1.10 | Pre-built React components |
| Icons | MUI Icons | 6.1.10 | Material Design icons |
| Styling | Emotion | 11.14.0 | CSS-in-JS library |
| Toast Notifications | react-toastify | 11.0.5 | User notifications |
| **Build/Dev Tools** |
| Bundler | Webpack | 5.97.1 | Module bundler |
| Transpiler | Babel | 7.26.0 | JavaScript compiler |
| Test Runner | Jest | 29.7.0 | Testing framework |
| Component Docs | Storybook | 8.4.6 | Component playground |
| Linter | ESLint | 9.16.0 | Code quality |
| Formatter | Prettier | 3.4.2 | Code formatting |
| **Testing** |
| Testing Library | React Testing Library | 15.0.7 | Component testing utilities |
| DOM Testing | jest-dom | 6.4.5 | DOM matchers for Jest |
| User Interaction | user-event | 14.5.2 | User interaction simulation |

---

## Architecture Pattern

**Pattern:** Component-based frontend architecture with centralized state management

**Characteristics:**
- Single-page application (SPA) structure
- Redux Toolkit for predictable state management
- Material-UI component library for consistent UI
- Webpack module federation ready (see `MODULES_EXPOSE.json`)
- Storybook for component-driven development
- TypeScript for type safety across the codebase

---

## Build System

**Primary:** Webpack 5 with custom BlueFiber configuration

**Build Modes:**
- `dev` - Development server with hot reload
- `build:prod` - Production build (optimized)
- `build:debug` - Debug-friendly build
- `build:e2e` - E2E testing build

**Custom Configuration:**
- Located in `blueFiberSrc/_webpack.bf.*.js`
- Module federation support via `external-remotes-plugin`
- Custom ifdef loader for conditional compilation
- Feature flags support (`features-flags-dev.json`, `features-flags-prod.json`)

---

## Testing Strategy

**Framework:** Jest with React Testing Library

**Configuration:**
- Config: `jest.config.js`
- Setup: `jest.setup.js`
- Polyfills: `jest.polyfills.js`
- Environment: jsdom (browser simulation)

**Test Types:**
- Unit tests (component-level)
- Integration tests
- Snapshot tests (via Storybook)

**Coverage Reporting:**
- HTML reports via `jest-html-reporters`
- JUnit XML via `jest-junit`

---

## Development Workflow

**Prerequisites:**
- Node.js 18.x (recommended)
- npm (or pnpm/yarn)

**Common Commands:**
```bash
npm ci                  # Install dependencies
npm run dev             # Start development server
npm run build:prod      # Production build
npm run test:jest       # Run tests
npm run storybook       # Start Storybook
npm run serve           # Serve production build
```

---

## Module Federation

**Exposed Modules:** See `MODULES_EXPOSE.json`

The project is configured for Webpack Module Federation, allowing it to expose or consume remote modules as part of the Starlight system architecture.

---

## Documentation Generation

This project includes embedded **BMAD workflows** for automated documentation:

- Framework location: `_bmad/`
- Output location: `docs/` (this directory)
- Status tracking: `_bmad-output/planning-artifacts/bmm-workflow-status.yaml`
- Primary workflow: `document-project` (via Analyst agent)

---

## Next Steps

For detailed information about specific aspects of the project, refer to:
- [Technology Stack Details](./technology_stack.md)
- [Component Inventory](./ui_component_inventory_1.md)
- [API Contracts](./api-contracts-1.md)
- [Data Models](./data-models-1.md)
- [CI/CD Configuration](./ci_cd_detection.md)

---

*This document was generated by the BMAD `document-project` workflow (exhaustive scan mode).*