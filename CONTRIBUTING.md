# Contributing to WebKit

Thank you for your interest in contributing to WebKit! This document provides guidelines and instructions for contributing to the project.

---

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [Branching Strategy](#branching-strategy)
- [Getting Started](#getting-started)
- [Development Workflow](#development-workflow)
- [Pull Request Process](#pull-request-process)
- [Code Standards](#code-standards)
- [Testing Requirements](#testing-requirements)

---

## Code of Conduct

Please be respectful and constructive in all interactions. We're here to build great software together.

---

## Branching Strategy

WebKit uses a **Feature Branch Workflow** with the following structure:

### Branch Structure

```
main (production)
  └── dev (development)
        └── feature/* (feature branches)
        └── fix/* (bug fix branches)
```

### Branch Descriptions

| Branch | Purpose | Protected | Deploy Target |
|--------|---------|-----------|---------------|
| **main** | Production-ready code | ✅ Yes | Production |
| **dev** | Active development, integration branch | ✅ Yes | Staging/Dev |
| **feature/*** | New features and enhancements | ❌ No | Local/PR |
| **fix/*** | Bug fixes | ❌ No | Local/PR |

### Branch Rules

1. **main branch**
   - Contains production-ready code only
   - All commits must pass CI/CD
   - Only accepts merges from `dev` branch
   - Tagged with version numbers for releases
   - Never commit directly to `main`

2. **dev branch**
   - Main development branch
   - All feature branches created from `dev`
   - All feature branches merge back to `dev`
   - Regularly merged to `main` for releases
   - Never commit directly to `dev` (use feature branches)

3. **feature/* branches**
   - Created from `dev` branch
   - Naming: `feature/description-of-feature`
   - Examples: `feature/add-dark-mode`, `feature/user-authentication`
   - Merged back to `dev` via Pull Request
   - Deleted after merge

4. **fix/* branches**
   - Created from `dev` branch
   - Naming: `fix/description-of-fix`
   - Examples: `fix/login-button-alignment`, `fix/redux-state-mutation`
   - Merged back to `dev` via Pull Request
   - Deleted after merge

### Hotfix Strategy

For critical production bugs:
1. Create `hotfix/*` branch from `main`
2. Fix the issue and test thoroughly
3. Create PR to merge into both `main` and `dev`
4. Tag the release on `main`

---

## Getting Started

### 1. Fork and Clone

```bash
# Fork the repository on GitHub first, then:
git clone https://github.com/YOUR-USERNAME/WebKit.git
cd WebKit

# Add upstream remote
git remote add upstream https://github.com/mayafit/WebKit.git
```

### 2. Set Up Development Environment

```bash
# Install dependencies
npm ci

# Start development server
npm run dev
```

See [GETTING-STARTED.md](./GETTING-STARTED.md) for detailed setup instructions.

---

## Development Workflow

### Creating a Feature Branch

```bash
# Ensure you're on dev and it's up to date
git checkout dev
git pull upstream dev

# Create your feature branch
git checkout -b feature/your-feature-name

# Make your changes and commit
git add .
git commit -m "feat: add your feature description"

# Push to your fork
git push origin feature/your-feature-name
```

### Keeping Your Branch Updated

```bash
# Fetch latest changes from upstream
git fetch upstream

# Rebase your feature branch on latest dev
git checkout feature/your-feature-name
git rebase upstream/dev

# If there are conflicts, resolve them and continue
git add .
git rebase --continue

# Force push to your fork (after rebase)
git push --force-with-lease origin feature/your-feature-name
```

---

## Pull Request Process

### 1. Before Creating a PR

Ensure your code:
- ✅ Passes all tests (`npm run test:jest`)
- ✅ Builds successfully (`npm run build:prod`)
- ✅ Follows code standards (ESLint, Prettier)
- ✅ Includes appropriate tests
- ✅ Has clear, descriptive commit messages

### 2. Create Pull Request

1. Push your feature branch to your fork
2. Go to GitHub and create a Pull Request to the `dev` branch
3. Fill out the PR template with:
   - **Description**: What does this PR do?
   - **Changes**: List of changes made
   - **Testing**: How was this tested?
   - **Screenshots**: If UI changes, include screenshots

### 3. PR Review Process

- CI/CD pipeline will run automatically
- At least one maintainer review required
- Address review comments by pushing new commits
- Once approved, a maintainer will merge your PR
- Your feature branch will be automatically deleted after merge

### 4. After Merge

```bash
# Switch back to dev and update
git checkout dev
git pull upstream dev

# Delete your local feature branch
git branch -d feature/your-feature-name

# Delete remote feature branch (if not auto-deleted)
git push origin --delete feature/your-feature-name
```

---

## Code Standards

### Commit Message Format

Follow [Conventional Commits](https://www.conventionalcommits.org/):

```
type(scope): description

[optional body]

[optional footer]
```

**Types:**
- `feat`: New feature
- `fix`: Bug fix
- `docs`: Documentation changes
- `style`: Code style changes (formatting, no logic change)
- `refactor`: Code refactoring
- `test`: Adding or updating tests
- `chore`: Maintenance tasks

**Examples:**
```bash
git commit -m "feat: add user authentication module"
git commit -m "fix: resolve Redux state mutation in todos slice"
git commit -m "docs: update API documentation for auth endpoints"
```

### Code Style

- **ESLint**: Run `npx eslint .` before committing
- **Prettier**: Run `npx prettier --write .` to format code
- **TypeScript**: Ensure no type errors with `npx tsc --noEmit`

**Pre-commit checklist:**
```bash
# Lint code
npx eslint .

# Format code
npx prettier --write .

# Type check
npx tsc --noEmit

# Run tests
npm run test:jest
```

### Component Structure

Follow the Entry Pattern (see [Development Guide](./docs/development-guide.md)):

```
src/entries/YourFeature/
├── components/
│   └── YourComponent/
│       ├── index.tsx
│       ├── __stories__/
│       └── __tests__/
├── store/
│   ├── yourFeature.slice.ts
│   └── yourFeature.selectors.ts
├── layouts/
├── yourFeature.entry.ts
└── decs.ts
```

---

## Testing Requirements

### Unit Tests

- Add tests for new components in `__tests__/` directories
- Maintain or improve test coverage
- Use React Testing Library best practices

```bash
# Run tests
npm run test:jest

# Run tests in watch mode
npm run test:jest -- --watch
```

### Component Testing

- Add Storybook stories for new components
- Stories help document component usage

```bash
# Start Storybook
npm run storybook
```

### Test Coverage

- Critical business logic must have tests
- UI components should have basic interaction tests
- Redux slices should have comprehensive tests

---

## Release Process

**For Maintainers:**

### Creating a Release

1. Ensure `dev` branch is stable and tested
2. Update version in `package.json`
3. Update CHANGELOG.md
4. Create PR from `dev` to `main`
5. After merge, tag the release:

```bash
git checkout main
git pull origin main
git tag -a v1.0.0 -m "Release version 1.0.0"
git push origin v1.0.0
```

### Version Numbers

Follow [Semantic Versioning](https://semver.org/):
- **MAJOR**: Breaking changes (v2.0.0)
- **MINOR**: New features, backwards compatible (v1.1.0)
- **PATCH**: Bug fixes, backwards compatible (v1.0.1)

---

## Getting Help

- **Documentation**: Start with [docs/index.md](./docs/index.md)
- **Development Guide**: See [docs/development-guide.md](./docs/development-guide.md)
- **Questions**: Open a GitHub Discussion
- **Bugs**: Open a GitHub Issue with reproduction steps

---

## Additional Resources

- [Getting Started Guide](./GETTING-STARTED.md) - Fork and setup instructions
- [Development Guide](./docs/development-guide.md) - Complete development workflow
- [Deployment Guide](./docs/deployment-guide.md) - Deployment procedures
- [Source Tree Analysis](./docs/source-tree-analysis.md) - Codebase structure

---

**Thank you for contributing to WebKit!** 🚀

Your contributions help make this bootstrap template better for everyone.
