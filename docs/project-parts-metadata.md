# Project Parts Metadata

**Generated:** 2026-01-10
**Workflow:** document-project v1.2.0
**Scan Level:** exhaustive

---

## Repository Information

**Repository Type:** Monolith
**Total Parts:** 1
**Multi-part Project:** No

---

## Part 1: Frontend

### Basic Information

| Property | Value |
|----------|-------|
| **Part ID** | `frontend` |
| **Part Name** | WebKit Frontend |
| **Project Type** | `web` |
| **Root Path** | `d:\Code\WebKit` |
| **Primary Language** | TypeScript |
| **Framework** | React |

---

### Detection Evidence

**Key File Patterns Matched:**
- ✓ `package.json` - Found at root
- ✓ `tsconfig.json` - Found at root
- ✓ `*.config.js` - Multiple webpack/babel configs found
- ✓ `webpack.config.*` - Webpack configuration files found

**Critical Directories Found:**
- ✓ `src/` - Application source code
- ✓ `components/` - React components (within src)
- ✓ `public/` - Static assets
- ✓ `styles/` - Styling files

**Technology Markers:**
- React 18.3.1 detected in dependencies
- TypeScript 5.7.2 detected in devDependencies
- Redux Toolkit 2.4.0 for state management
- Material-UI 6.1.10 for UI components
- Webpack 5.97.1 for bundling

---

### Documentation Requirements

Based on project type `web`, the following documentation will be generated:

| Requirement | Status | Rationale |
|-------------|--------|-----------|
| **API Scan** | ✓ Required | Web projects typically have API integration layers |
| **Data Models** | ✓ Required | Redux store shapes and data structures |
| **State Management** | ✓ Required | Redux Toolkit state management analysis |
| **UI Components** | ✓ Required | React component inventory and hierarchy |
| **Deployment Config** | ✓ Required | Build and deployment configuration analysis |

---

### Scan Patterns Applied

**Integration Scan Patterns:**
- `*client.ts` - API client modules
- `*service.ts` - Service layer modules
- `*api.ts` - API integration files
- `fetch*.ts` - Fetch utilities
- `axios*.ts` - Axios HTTP clients
- `*http*.ts` - HTTP utilities

**Test File Patterns:**
- `*.test.ts`, `*.spec.ts` - TypeScript tests
- `*.test.tsx`, `*.spec.tsx` - React component tests
- `**/__tests__/**` - Test directories
- `**/*.test.*`, `**/*.spec.*` - Any test files

**Config Patterns:**
- `.env*` - Environment configuration
- `config/*` - Config directory
- `*.config.*` - Config files
- `.config/` - Config directory
- `settings/` - Settings directory

**Authentication/Security Patterns:**
- `*auth*.ts` - Authentication modules
- `*session*.ts` - Session management
- `middleware/auth*` - Auth middleware
- `*.guard.ts` - Route guards
- `*authenticat*` - Authentication utilities
- `*permission*` - Permission handling
- `guards/` - Guard directory

**Schema/Migration Patterns:**
- `migrations/**` - Database migrations
- `prisma/**` - Prisma ORM
- `*.prisma` - Prisma schema files

**Entry Point Patterns:**
- `main.ts`, `index.ts`, `app.ts` - Main entry points
- `_app.tsx`, `_app.ts` - Next.js app entry
- `layout.tsx` - Layout components

**Shared Code Patterns:**
- `shared/**` - Shared modules
- `common/**` - Common utilities
- `utils/**` - Utility functions
- `lib/**` - Library code
- `helpers/**` - Helper functions

**Async/Event Patterns:**
- `*event*.ts` - Event handlers
- `*queue*.ts` - Queue implementations
- `*subscriber*.ts` - Event subscribers
- `*consumer*.ts`, `*producer*.ts` - Message queue
- `*worker*.ts` - Web workers
- `jobs/**` - Background jobs

**CI/CD Patterns:**
- `.github/workflows/**` - GitHub Actions
- `.gitlab-ci.yml` - GitLab CI
- `Jenkinsfile` - Jenkins
- `.circleci/**` - CircleCI
- `azure-pipelines.yml` - Azure Pipelines

**Asset Patterns:**
- `public/**` - Public static assets
- `static/**` - Static files
- `assets/**` - Asset directory
- `images/**` - Image files
- `media/**` - Media files

**Localization Patterns:**
- `i18n/**` - Internationalization
- `locales/**` - Locale files
- `lang/**` - Language files
- `translations/**` - Translation files
- `messages/**` - Message catalogs

**Protocol/Schema Patterns:**
- `*.proto` - Protocol buffers
- `*.graphql` - GraphQL schemas
- `graphql/**` - GraphQL directory
- `schema.graphql` - GraphQL schema
- `openapi.*`, `swagger.*` - API schemas

---

### Architecture Pattern

**Detected Pattern:** Component-based frontend with centralized state management

**Pattern Characteristics:**
- React component hierarchy
- Redux Toolkit for global state
- Material-UI design system
- Webpack module federation ready
- Service layer for API integration
- Utility/helper module organization

---

### Part-Specific Configuration

**Environment Files:**
- `.env*` files for environment-specific configuration
- `env.config.json` for custom environment settings
- `features-flags-dev.json`, `features-flags-prod.json` for feature toggles

**Build Configuration:**
- Custom BlueFiber webpack configs in `blueFiberSrc/`
- Standard webpack configs at root
- Babel configuration in `babel.config.js`
- TypeScript configs: `tsconfig.json`, `tsconfig.dev.json`, `tsconfig.prod.json`

**Testing Configuration:**
- Jest config: `jest.config.js`
- Jest setup: `jest.setup.js`
- Jest polyfills: `jest.polyfills.js`

**Code Quality:**
- ESLint config: `eslint.config.mjs`
- Prettier config: `.prettierrc.json`, `.prettierignore`

---

### Integration Points

**External Integrations:**
- Module federation with Starlight system (see `MODULES_EXPOSE.json`)
- External remotes plugin for remote module loading

**No Multi-Part Integration:**
This is a monolithic project with no separate backend or API parts within the repository.

---

## Summary

This is a **monolithic web frontend** project with a single part (`frontend`). The project follows modern React development practices with TypeScript, Redux Toolkit for state management, and Material-UI for the component library. The architecture is optimized for module federation, allowing integration with the broader Starlight system.

The exhaustive scan will analyze:
- All React components and their relationships
- Redux store structure and state management patterns
- API integration layer and service modules
- UI component inventory and design system usage
- Build configuration and deployment setup
- Testing strategy and coverage

---

*This metadata document was generated by the BMAD `document-project` workflow.*