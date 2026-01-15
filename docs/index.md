# WebKit Project Documentation

**Generated:** 2026-01-10
**Workflow:** document-project v1.2.0
**Scan Level:** exhaustive

---

## Project Overview

- **Project Type:** Monolith (Web Frontend)
- **Primary Language:** TypeScript + JavaScript
- **Framework:** React 18.3.1
- **State Management:** Redux Toolkit 2.4.0
- **UI Library:** Material-UI (MUI) 6.1.10
- **Architecture:** Component-Based SPA with Module Federation

---

## Quick Reference

### Technology Stack

| Category | Technology | Version |
|----------|-----------|---------|
| **Framework** | React | 18.3.1 |
| **Language** | TypeScript | 5.7.2 |
| **State Management** | Redux Toolkit | 2.4.0 |
| **UI Library** | Material-UI | 6.1.10 |
| **Build Tool** | Webpack (BlueFiber) | 5.97.1 |
| **Package Manager** | npm | 10.x |
| **Node.js** | Node.js | 22.12.0 |

### Entry Points

| File | Purpose |
|------|---------|
| [blueFiberSrc/bootstrap.jsx](../blueFiberSrc/bootstrap.jsx) | Application bootstrap entry |
| [src/App.jsx](../src/App.jsx) | Main React component |
| [public/index.html](../public/index.html) | HTML template |

### Architecture Pattern

**Module Federation Micro-Frontend Architecture:**
- Dynamic reducer injection for modular state management
- Entry-based feature organization
- Global store access for remote modules
- Component-based UI with Material-UI design system

---

## Core Documentation

### Project Structure & Architecture

- **[Project Structure](./project-structure.md)** - High-level project organization and folder hierarchy
- **[Source Tree Analysis](./source-tree-analysis.md)** - Annotated directory tree with detailed explanations
- **[Project Parts Metadata](./project-parts-metadata.md)** - Project metadata and detection evidence
- **[Technology Stack](./technology_stack.md)** - Comprehensive analysis of 100+ technologies used
- **[Existing Documentation Inventory](./existing_documentation_inventory.md)** - Catalog of all existing documentation

### Development

- **[Development Guide](./development-guide.md)** - Complete setup, development workflow, and best practices
  - Environment setup
  - Development commands
  - Testing strategy
  - Code quality standards
  - Common development tasks

### Deployment & Operations

- **[Deployment Guide](./deployment-guide.md)** - Production deployment procedures and infrastructure
  - Docker deployment
  - Kubernetes configuration
  - CI/CD pipeline
  - Monitoring and scaling

- **[CI/CD Detection](./ci_cd_detection.md)** - CI/CD analysis and GitHub Actions configuration

---

## Technical Documentation

### State Management & Data

- **[Data Models](./data-models-1.md)** - Redux store structure, slices, actions, and selectors
  - Track Slice (location tracking)
  - Todos Slice (todo management)
  - State management patterns
  - Module federation integration

### API & Integration

- **[API Contracts](./api-contracts-1.md)** - API endpoints and integration patterns discovered

### UI Components

- **[UI Component Inventory](./ui_component_inventory_1.md)** - Catalog of all UI components
  - Component categories
  - Material-UI components
  - Custom components
  - Storybook integration

- **[Component Usage Map](./component-usage-map-1.md)** - Component dependencies and usage patterns

---

## Getting Started

### 🎯 Using WebKit as a Bootstrap

**WebKit is a template** - fork it to build your own application in one of two ways:

- **Path A: Standalone SPA** - Complete, independent web application
- **Path B: Module Federation Component** - Reusable components for other apps

**👉 [Complete Getting Started Guide](../GETTING-STARTED.md)** - Essential reading before forking WebKit

The getting started guide covers:
- Choosing between SPA and Module Federation paths
- Step-by-step forking and setup instructions
- Build process differences and configuration
- Integration patterns and deployment

---

### For Developers Working on Existing WebKit Projects

If you're contributing to an existing project built with WebKit:

1. **Read First:**
   - [Project Structure](./project-structure.md) - Understand the codebase layout
   - [Development Guide](./development-guide.md) - Set up your development environment
   - [Technology Stack](./technology_stack.md) - Familiarize yourself with the tech stack

2. **Set Up Environment:**
   ```bash
   # Clone repository
   git clone <repo-url> your-project
   cd your-project

   # Install dependencies
   npm ci

   # Start development server
   npm run dev
   ```

3. **Explore Components:**
   ```bash
   # Start Storybook for component exploration
   npm run storybook
   ```

4. **Review Architecture:**
   - [Source Tree Analysis](./source-tree-analysis.md) - Navigate the codebase
   - [Data Models](./data-models-1.md) - Understand state management

### For Feature Development

**Planning a New Feature?**

1. **Understand Current Architecture:**
   - Review [Source Tree Analysis](./source-tree-analysis.md) for entry pattern
   - Check [Data Models](./data-models-1.md) for state management approach
   - Review [UI Component Inventory](./ui_component_inventory_1.md) for reusable components

2. **Create Feature Module:**
   - Follow entry pattern: `src/entries/{FeatureName}/`
   - Create Redux slice in `store/`
   - Build components in `components/`
   - Add layouts in `layouts/`
   - Document with Storybook stories

3. **Reference Existing Implementation:**
   - Example: `src/entries/Todos/` - Complete feature module with all patterns

### For Deployment & Operations

**Deploying to Production?**

1. **Build & Test:**
   ```bash
   npm run build:prod
   npm run test:jest
   ```

2. **Review Deployment Guide:**
   - [Deployment Guide](./deployment-guide.md) - Full deployment procedures
   - [CI/CD Detection](./ci_cd_detection.md) - Automated pipeline configuration

3. **Use Deployment Checklist:**
   - Pre-deployment checklist in [Deployment Guide](./deployment-guide.md)
   - Security review
   - Performance testing
   - Rollback plan

---

## Development Workflows

### Daily Development

```bash
# Start development server
npm run dev

# Run tests
npm run test:jest

# Component development
npm run storybook

# Lint code
npx eslint .

# Format code
npx prettier --write .
```

### Build & Deploy

```bash
# Production build
npm run build:prod

# Debug build (with source maps)
npm run build:debug

# E2E test build
npm run build:e2e

# Serve locally
npm run serve
```

### Code Quality

```bash
# Type checking
npx tsc --noEmit

# Security audit
npm audit

# Clean build artifacts
npm run clean
```

---

## Architecture Highlights

### Module Federation

The application is designed for micro-frontend architecture:

- **Host Application:** Exposes components and utilities
- **Remote Modules:** Can dynamically load and integrate
- **Shared Redux Store:** Accessible globally via `window.GLOBALS`
- **Dynamic Reducer Injection:** Modules register/unregister reducers at runtime

**Key Files:**
- `bluefiber.config.js` - Module federation configuration
- `MODULES_EXPOSE.json` - Exposed modules manifest
- `src/InitComp.tsx` - Global API setup

### Entry Pattern

Feature modules follow a consistent structure:

```
src/entries/{FeatureName}/
├── components/      # React components
├── layouts/         # Page layouts
├── store/          # Redux slice + selectors
├── {feature}.entry.ts   # Init/destroy lifecycle
├── {feature}.logic.ts   # Business logic
├── {feature}.services.ts # API services
└── decs.ts         # Type definitions
```

**Benefits:**
- Modular, isolated features
- Dynamic loading
- Easy to maintain and test
- Clear separation of concerns

### State Management

**Redux Toolkit with Dynamic Reducers:**

- **Global Slices:** `src/store/` - Application-level state
- **Feature Slices:** `src/entries/*/store/` - Feature-specific state
- **Normalized State:** Dictionary-based for O(1) lookups
- **Memoized Selectors:** Using `createSelector` for performance

**Example Slices:**
- `trackSlice` - Track location data (lon, lat, alt, speed)
- `todosSlice` - Todo list management

---

## Testing Strategy

### Unit Tests

- **Framework:** Jest 29.7.0 + React Testing Library
- **Location:** `src/**/__tests__/`
- **Command:** `npm run test:jest`
- **Coverage:** Utilities, components, Redux logic

### Component Testing

- **Framework:** Storybook 8.4.6
- **Location:** `src/**/__stories__/`
- **Command:** `npm run storybook`
- **Purpose:** Visual testing and documentation

### Linting & Type Checking

- **ESLint:** Code quality and best practices
- **TypeScript:** Type safety
- **Prettier:** Code formatting

---

## CI/CD Pipeline

### GitHub Actions

- **Triggers:** Push/PR to `main`, `master`, `develop`
- **Steps:** Install → Build → Test
- **Configuration:** `.github/workflows/ci.yml`

**Recommended Enhancements:**
- Add linting and type checking steps
- Docker image build and push
- Automated deployment to staging/production
- Security scanning

---

## Navigation Guide

### By Role

**Frontend Developer:**
1. [Development Guide](./development-guide.md)
2. [Source Tree Analysis](./source-tree-analysis.md)
3. [Data Models](./data-models-1.md)
4. [UI Component Inventory](./ui_component_inventory_1.md)

**DevOps Engineer:**
1. [Deployment Guide](./deployment-guide.md)
2. [CI/CD Detection](./ci_cd_detection.md)
3. [Technology Stack](./technology_stack.md)

**Architect / Tech Lead:**
1. [Project Structure](./project-structure.md)
2. [Source Tree Analysis](./source-tree-analysis.md)
3. [Technology Stack](./technology_stack.md)
4. [Data Models](./data-models-1.md)

**Product Manager:**
1. [Project Structure](./project-structure.md)
2. [UI Component Inventory](./ui_component_inventory_1.md)
3. [Development Guide](./development-guide.md#common-development-tasks)

### By Task

**Adding a New Feature:**
1. Review [Source Tree Analysis](./source-tree-analysis.md)
2. Follow entry pattern in [Development Guide](./development-guide.md)
3. Reference [Data Models](./data-models-1.md) for state management
4. Check [UI Component Inventory](./ui_component_inventory_1.md) for reusable components

**Debugging Production Issues:**
1. Check [Deployment Guide](./deployment-guide.md#troubleshooting)
2. Review [CI/CD Detection](./ci_cd_detection.md)
3. Use source maps from `npm run build:debug`

**Onboarding New Team Member:**
1. [README.md](../README.md) - Project overview
2. [Development Guide](./development-guide.md) - Setup environment
3. [Project Structure](./project-structure.md) - Understand codebase
4. [Source Tree Analysis](./source-tree-analysis.md) - Navigate directories

**Planning Architecture Changes:**
1. [Technology Stack](./technology_stack.md) - Current tech decisions
2. [Data Models](./data-models-1.md) - State architecture
3. [Source Tree Analysis](./source-tree-analysis.md) - Module patterns
4. [Component Usage Map](./component-usage-map-1.md) - Component dependencies

---

## For Brownfield PRD Creation

When creating a PRD for new features in this existing project, reference this index as the primary input to the PRD workflow. It provides complete context about:
- Current architecture and patterns
- Technology stack and constraints
- Component inventory and reusability
- State management approach
- Development and deployment procedures

---

*This documentation was generated by the BMAD `document-project` workflow (v1.2.0, exhaustive scan, Step 10).*
