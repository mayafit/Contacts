# Technology Stack

**Generated:** 2026-01-10
**Workflow:** document-project v1.2.0
**Scan Level:** exhaustive
**Part:** frontend (web)

---

## Overview

WebKit is built on a modern React + TypeScript stack with comprehensive tooling for development, testing, and deployment. The architecture follows component-based design patterns with centralized state management.

---

## Core Technologies

### Language & Runtime

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **TypeScript** | 5.7.2 | Primary language | Type safety, improved developer experience, better IDE support, catch errors at compile time |
| **JavaScript (ES6+)** | ES2020+ | Secondary language | Legacy code support, compatibility with older modules |
| **Node.js** | 18.x+ (recommended) | Runtime environment | Package management, build tooling, development server |

### Frontend Framework

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **React** | 18.3.1 | UI framework | Component-based architecture, virtual DOM, strong ecosystem, concurrent rendering features |
| **React DOM** | 18.3.1 | DOM rendering | React's DOM-specific methods for web applications |

### State Management

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **Redux Toolkit** | 2.4.0 | Global state management | Simplified Redux with best practices, built-in immer, thunks, and RTK Query support |
| **React Redux** | 8.1.3 | React-Redux bindings | Connect React components to Redux store with hooks API |

---

## UI & Styling

### Component Library

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **Material-UI (MUI)** | 6.1.10 | Component library | Pre-built accessible components, theming system, consistent design language |
| **MUI Icons** | 6.1.10 | Icon library | Material Design icons matching MUI components |
| **MUI Styled Engine** | 6.1.10 | Styling engine | CSS-in-JS styling for MUI components |

### Styling Solutions

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **Emotion** | 11.14.0 | CSS-in-JS library | Dynamic styling, theme support, excellent performance |
| **@emotion/react** | 11.14.0 | Emotion for React | Framework bindings for Emotion |
| **@emotion/styled** | 11.14.0 | Styled components API | Component-level styling with Emotion |
| **@emotion/cache** | 11.14.0 | Style caching | Performance optimization for styles |
| **stylis-plugin-rtl** | 2.1.1 | RTL support | Right-to-left language support |

### Additional UI Libraries

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **react-toastify** | 11.0.5 | Toast notifications | User feedback and notification system |
| **allotment** | 1.19.3 | Split panes | Resizable split view layouts |
| **party-js** | 2.2.0 | Confetti effects | Visual celebration effects |

---

## Build & Development Tools

### Module Bundler

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **Webpack** | 5.97.1 | Module bundler | Industry standard, module federation support, extensive plugin ecosystem |
| **webpack-cli** | 5.1.4 | Webpack CLI | Command-line interface for Webpack |
| **webpack-dev-server** | 5.0.4 | Development server | Hot module replacement, live reloading |
| **webpack-merge** | 6.0.1 | Config merging | Combine webpack configurations for different environments |

### Webpack Plugins & Loaders

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **html-webpack-plugin** | 5.6.3 | HTML generation | Generate HTML files with bundled scripts |
| **copy-webpack-plugin** | 12.0.2 | Asset copying | Copy static assets to output directory |
| **compression-webpack-plugin** | 10.0.0 | Gzip compression | Compress assets for production |
| **clean-webpack-plugin** | 4.0.0 | Clean builds | Remove old build artifacts |
| **external-remotes-plugin** | 1.0.0 | Module federation | Load remote modules dynamically |
| **terser-webpack-plugin** | 5.3.10 | Minification | JavaScript minification and optimization |
| **fork-ts-checker-webpack-plugin** | 9.0.2 | Type checking | Parallel TypeScript type checking |

### Loaders

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **babel-loader** | 9.2.1 | JavaScript transpilation | Transform modern JS/TS to compatible versions |
| **ts-loader** | 9.5.1 | TypeScript compilation | Compile TypeScript files |
| **css-loader** | 7.1.2 | CSS processing | Handle CSS imports |
| **style-loader** | 4.0.0 | Style injection | Inject CSS into DOM |
| **less-loader** | 11.1.2 | LESS compilation | Process LESS stylesheets |
| **file-loader** | 6.2.0 | File handling | Handle file imports |
| **handlebars-loader** | 1.7.3 | Template processing | Compile Handlebars templates |
| **ifdef-loader** | 2.3.2 | Conditional compilation | Remove code based on conditions |

### Transpilation

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **Babel** | 7.26.0 | JavaScript compiler | Transform modern JavaScript for browser compatibility |
| **@babel/preset-env** | 7.26.0 | Environment presets | Target specific browser versions |
| **@babel/preset-react** | 7.25.9 | React support | JSX and React-specific transformations |
| **@babel/plugin-transform-runtime** | 7.25.9 | Runtime helpers | Reduce bundle size with shared helpers |
| **@babel/runtime-corejs3** | 7.26.0 | Polyfills | Core-js polyfills for modern features |

---

## Testing

### Test Framework

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **Jest** | 29.7.0 | Test runner | Fast, parallel testing with built-in coverage |
| **jest-environment-jsdom** | 29.7.0 | Browser environment | Simulate browser DOM for testing |
| **jest-fixed-jsdom** | 0.0.9 | JSDOM fixes | Fix JSDOM compatibility issues |
| **jsdom** | 26.0.0 | DOM implementation | Pure JavaScript DOM implementation |

### Testing Libraries

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **React Testing Library** | 15.0.7 | Component testing | Test React components from user perspective |
| **@testing-library/jest-dom** | 6.4.5 | DOM matchers | Custom Jest matchers for DOM testing |
| **@testing-library/user-event** | 14.5.2 | User interactions | Simulate realistic user interactions |
| **react-test-renderer** | 18.2.0 | Snapshot testing | Render React components to JSON for snapshots |

### Test Utilities

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **babel-jest** | 29.7.0 | Jest-Babel integration | Transform code for Jest |
| **ts-jest** | 29.1.2 | TypeScript Jest support | Run TypeScript tests directly |
| **jest-canvas-mock** | 2.5.2 | Canvas mocking | Mock canvas API for tests |
| **jest-html-reporters** | 3.1.7 | HTML reports | Generate HTML test reports |
| **jest-junit** | 16.0.0 | JUnit reports | Generate JUnit XML reports for CI |

---

## Code Quality

### Linting

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **ESLint** | 9.16.0 | Linting | Identify and fix code quality issues |
| **@eslint/js** | 9.24.0 | ESLint core | ESLint JavaScript support |
| **@typescript-eslint/parser** | 8.19.1 | TypeScript parsing | Parse TypeScript for ESLint |
| **@typescript-eslint/eslint-plugin** | 8.19.1 | TypeScript rules | TypeScript-specific linting rules |

### ESLint Plugins

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **eslint-plugin-react** | 7.37.2 | React rules | React-specific linting rules |
| **eslint-plugin-react-hooks** | 5.1.0 | Hooks rules | Enforce Rules of Hooks |
| **eslint-plugin-react-redux** | 4.2.0 | Redux rules | Redux best practices |
| **eslint-plugin-jsx-a11y** | 6.10.2 | Accessibility | Enforce accessibility standards |
| **eslint-plugin-testing-library** | 7.1.1 | Testing rules | Testing Library best practices |
| **eslint-plugin-jest-dom** | 5.5.0 | Jest DOM rules | jest-dom best practices |
| **eslint-plugin-sonarjs** | 3.0.1 | Code complexity | Detect code smells and complexity |
| **eslint-plugin-storybook** | 0.11.1 | Storybook rules | Storybook best practices |
| **eslint-plugin-prettier** | 5.2.1 | Prettier integration | Run Prettier as ESLint rule |

### Formatting

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **Prettier** | 3.4.2 | Code formatting | Consistent code formatting across team |
| **eslint-config-prettier** | 9.1.0 | ESLint-Prettier compat | Disable conflicting ESLint rules |

---

## Component Documentation

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **Storybook** | 8.4.6 | Component playground | Isolated component development and documentation |
| **@storybook/react** | 8.4.6 | React support | Storybook for React components |
| **@storybook/react-webpack5** | 8.4.6 | Webpack 5 support | Use Webpack 5 with Storybook |
| **@storybook/addon-essentials** | 8.4.6 | Essential addons | Docs, controls, actions, viewport, backgrounds |
| **@storybook/addon-links** | 8.3.5 | Link addon | Link between stories |
| **@storybook/addon-themes** | 8.4.6 | Theme switching | Switch between themes in Storybook |
| **@storybook/test** | 8.4.6 | Testing utilities | Testing utilities for Storybook |
| **storybook-addon-performance** | 0.17.3 | Performance monitoring | Monitor component performance |

---

## Documentation Generation

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **TypeDoc** | 0.28.1 | API documentation | Generate documentation from TypeScript code |
| **typedoc-plugin-missing-exports** | 4.0.0 | Export detection | Document missing exports |
| **typedoc-plugin-rename-defaults** | 0.7.3 | Default renaming | Better default export names |
| **JSDoc** | 4.0.2 | Documentation | Generate documentation from comments |
| **tui-jsdoc-template** | 1.2.2 | JSDoc template | Template for JSDoc output |

---

## Utilities & Helpers

### JavaScript Utilities

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **lodash-es** | 4.17.21 | Utility library | Functional programming utilities (ES module version) |
| **handlebars** | 4.7.8 | Templating | Template engine for dynamic content |

### Polyfills & Compatibility

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **stream-browserify** | 3.0.0 | Stream polyfill | Node.js stream API for browser |
| **timers-browserify** | 2.0.11 | Timers polyfill | Node.js timers for browser |
| **web-streams-polyfill** | 4.0.0 | Streams API | Polyfill for Streams API |
| **raf** | 3.4.1 | requestAnimationFrame | Polyfill for requestAnimationFrame |
| **zlib** | 1.0.5 | Compression | zlib compression library |
| **undici** | 6.20.1 | HTTP client | Modern HTTP client |

### Development Utilities

| Technology | Version | Purpose | Justification |
|-----------|---------|---------|---------------|
| **@pmmmwh/react-refresh-webpack-plugin** | 0.5.15 | Fast refresh | Hot reloading for React components |
| **react-refresh** | 0.14.2 | React refresh | React Fast Refresh runtime |
| **react-refresh-typescript** | 2.0.9 | TypeScript refresh | Fast Refresh for TypeScript |
| **@welldone-software/why-did-you-render** | 8.0.3 | Performance debugging | Detect unnecessary re-renders |
| **portfinder-sync** | 0.0.2 | Port finder | Find available ports |
| **cross-env** | 7.0.3 | Environment variables | Cross-platform environment variables |
| **serve** | 14.0.1 | Static server | Serve static files locally |

---

## Architecture Pattern

**Detected Pattern:** Layered Component-Based Architecture with Module Federation

### Pattern Characteristics

1. **Presentation Layer**
   - React functional components with hooks
   - Material-UI for consistent UI
   - Emotion for dynamic styling
   - Storybook for component isolation

2. **State Management Layer**
   - Redux Toolkit for global state
   - React hooks for local state
   - Selectors for computed state

3. **Service Layer**
   - API integration modules (pattern: `*service.ts`, `*client.ts`)
   - External module federation support
   - Event-driven patterns for async operations

4. **Build/Module Layer**
   - Webpack 5 with module federation
   - Custom BlueFiber configuration
   - Feature flag support for conditional compilation

---

## Development Workflow Technologies

### Hot Module Replacement (HMR)
- Webpack Dev Server with React Refresh
- Fast rebuilds with incremental compilation
- TypeScript type checking in parallel (fork-ts-checker)

### Development Server Features
- Custom BlueFiber webpack config
- Environment-based configuration (dev/prod/e2e)
- Feature flags for conditional compilation
- Module federation for micro-frontend architecture

---

## Build Optimization

### Production Optimizations
- **Terser** - Minification and dead code elimination
- **Compression** - Gzip compression for assets
- **Code Splitting** - Dynamic imports and lazy loading
- **Tree Shaking** - Remove unused code
- **Bundle Analysis** - Webpack bundle analyzer (available)

### Development Optimizations
- **Fast Refresh** - Instant feedback without losing state
- **Parallel Type Checking** - TypeScript checking in separate process
- **Incremental Compilation** - Only rebuild changed modules
- **Source Maps** - Debug original source code

---

## Quality Assurance Stack

### Code Quality Tools
- ESLint with 13+ plugins for comprehensive linting
- Prettier for consistent formatting
- TypeScript for type safety
- SonarJS for code complexity and smell detection

### Testing Strategy
- **Unit Tests** - Jest + React Testing Library
- **Snapshot Tests** - React Test Renderer
- **Integration Tests** - Multi-component testing
- **Accessibility Tests** - jest-dom + jsx-a11y plugin
- **Performance Tests** - Storybook performance addon

### Coverage Reporting
- HTML reports for local viewing
- JUnit XML for CI/CD integration
- Coverage thresholds configurable in jest.config.js

---

## Technology Rationale Summary

### Why This Stack?

1. **Type Safety** - TypeScript prevents runtime errors and improves IDE experience
2. **Modern React** - Latest React 18 with concurrent features
3. **Scalable State** - Redux Toolkit for predictable, scalable state management
4. **Design System** - Material-UI for consistent, accessible UI
5. **Developer Experience** - Hot reload, type checking, linting, formatting
6. **Testing** - Comprehensive testing with Jest and Testing Library
7. **Module Federation** - Micro-frontend architecture support
8. **Performance** - Optimized builds with code splitting and compression

---

*This technology stack analysis was generated by the BMAD `document-project` workflow (exhaustive scan).*