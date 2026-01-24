# JSDoc Documentation Standards

This document outlines the JSDoc documentation standards for the Contacts application.

## File Headers

Every TypeScript/JavaScript file should have a `@fileoverview` and `@module` header:

```typescript
/**
 * @fileoverview Brief description of what this file does
 * @module Path/To/Module (e.g., Contacts/services/auth/GoogleAuthService)
 */
```

### Examples

**Component file:**
```typescript
/**
 * @fileoverview User profile card component for displaying authenticated user info
 * @module Contacts/components/UserProfileCard
 */
```

**Service file:**
```typescript
/**
 * @fileoverview Google OAuth authentication service for token management
 * @module Contacts/services/auth/GoogleAuthService
 */
```

**Redux slice:**
```typescript
/**
 * @fileoverview Authentication state management slice with OAuth flow support
 * @module Contacts/redux/slices/auth/authSlice
 */
```

**Test file:**
```typescript
/**
 * @fileoverview Authentication Redux slice tests
 * @module Contacts/redux/slices/auth/__tests__/authSlice
 */
```

## Function Documentation

### Public Functions

All exported functions should have JSDoc documentation:

```typescript
/**
 * Refreshes the access token using the stored refresh token
 * @param userId - The user's unique identifier
 * @param options - Optional configuration for the refresh request
 * @returns Promise resolving to the new access token or error response
 * @throws {AuthenticationError} When refresh token is invalid or expired
 */
export async function refreshToken(
  userId: string,
  options?: RefreshOptions
): Promise<TokenResponse> {
  // implementation
}
```

### Private/Internal Functions

Document complex internal logic with brief comments:

```typescript
/**
 * Process queued requests after token refresh completes
 * @param error - Error if refresh failed, null if successful
 * @param token - New access token if refresh successful
 */
const processQueue = (error: unknown, token: string | null = null): void => {
  // implementation
};
```

## Interface Documentation

Document interfaces and type aliases:

```typescript
/**
 * API Response wrapper type for service methods
 */
interface APIResponse<T> {
  /** Whether the request was successful */
  success: boolean;
  /** Response data if successful */
  data?: T;
  /** Error details if failed */
  error?: {
    code: string;
    message: string;
    details?: unknown;
  };
}
```

## React Component Documentation

```typescript
/**
 * Protected route wrapper that redirects unauthenticated users to login
 * @param props.children - Child components to render when authenticated
 * @param props.redirectTo - Path to redirect unauthenticated users (default: '/login')
 */
const ProtectedRoute: React.FC<ProtectedRouteProps> = ({ children, redirectTo = '/login' }) => {
  // implementation
};
```

## Test Documentation

Follow the naming convention: `MethodName_Should_DoSomething_When_Condition`

```typescript
/**
 * Test: Component should have no accessibility violations
 * This is the primary accessibility test pattern using jest-axe.
 */
it('should_HaveNoViolations_When_ProperlyStructured', async () => {
  // test implementation
});
```

## Common JSDoc Tags

| Tag | Usage |
|-----|-------|
| `@fileoverview` | File-level description (required) |
| `@module` | Module path for documentation generators (required) |
| `@param` | Function parameter description |
| `@returns` | Return value description |
| `@throws` | Exceptions that may be thrown |
| `@example` | Usage examples |
| `@see` | Reference to related documentation |
| `@deprecated` | Mark deprecated code |
| `@internal` | Mark as internal implementation detail |

## Enforcement

- ESLint is configured to enforce `no-console` (use Logger instead)
- Pre-commit hooks run linting on all staged files
- Consider enabling `eslint-plugin-jsdoc` for stricter enforcement
