# Testing Requirements for Story 1.4

## Status: DEFERRED TO STORY 1.8

Story 1.8 is explicitly dedicated to establishing comprehensive testing infrastructure.
The tests below should be implemented during Story 1.8 execution.

## Required Test Files (Story 1.4 Definition of Done)

### 1. LoginPage.test.tsx
**Location**: `src/entries/Contacts/features/auth/components/__tests__/LoginPage.test.tsx`

**Test Cases**:
- LoginPage_Should_RenderGoogleButton_When_Mounted
- LoginPage_Should_CallDispatch_When_GoogleLoginSucceeds
- LoginPage_Should_LogError_When_GoogleLoginFails
- LoginPage_Should_Navigate_When_LoginSuccessful
- LoginPage_Should_LogError_When_NoCredentialReceived

**Mock Requirements**:
```typescript
jest.mock('@react-oauth/google', () => ({
  GoogleLogin: ({ onSuccess, onError }) => (
    <button onClick={() => onSuccess({ credential: 'mock-jwt' })}>
      Sign in with Google
    </button>
  ),
}));
```

### 2. ProtectedRoute.test.tsx
**Location**: `src/entries/Contacts/routes/__tests__/ProtectedRoute.test.tsx`

**Test Cases**:
- ProtectedRoute_Should_RenderChildren_When_Authenticated
- ProtectedRoute_Should_RedirectToLogin_When_NotAuthenticated
- ProtectedRoute_Should_RedirectToLogin_When_TokenExpired
- ProtectedRoute_Should_CallLogout_When_TokenExpired
- ProtectedRoute_Should_LogWarning_When_ClearingExpiredToken

### 3. AuthCallback.test.tsx
**Location**: `src/entries/Contacts/routes/__tests__/AuthCallback.test.tsx`

**Test Cases**:
- AuthCallback_Should_DisplayError_When_OAuthErrorInURL
- AuthCallback_Should_RedirectToLogin_When_NoError
- AuthCallback_Should_LogError_When_OAuthFails
- AuthCallback_Should_NavigateAfterTimeout_When_Mounted

### 4. authSlice.test.tsx
**Location**: `src/entries/Contacts/redux/slices/auth/__tests__/authSlice.test.ts`

**Test Cases**:
- logout_Should_ClearAuthState_When_Called
- logout_Should_ClearSessionStorage_When_Called
- restoreAuthState_Should_RestoreUser_When_ValidSessionData
- loginWithGoogle_Should_DecodeJWT_When_ValidCredential
- loginWithGoogle_Should_StoreInSessionStorage_When_Successful
- loginWithGoogle_Should_RejectInvalidJWT_When_MalformedToken
- loginWithGoogle_Should_ValidateIssuer_When_Processing
- loginWithGoogle_Should_ValidateAudience_When_Processing
- loginWithGoogle_Should_RejectExpiredToken_When_PastExpiry

### 5. Integration Test
**Location**: `src/entries/Contacts/__tests__/AuthFlow.integration.test.tsx`

**Test Cases**:
- FullOAuthFlow_Should_AuthenticateAndRedirect_When_UserLogsIn
- FullOAuthFlow_Should_RestoreSession_When_ValidTokenExists
- FullOAuthFlow_Should_LogoutAndClear_When_UserSignsOut

## Coverage Requirements

- **Target**: 100% coverage for authentication business logic
- **Critical Paths**:
  - OAuth login flow
  - Token validation
  - Session persistence
  - Protected route access control
  - Token expiry handling

## Test Infrastructure Needed (Story 1.8)

1. **Jest Configuration**: Ensure jest.config.js configured properly
2. **Testing Library Setup**: @testing-library/react installed
3. **Mock Setup**: Mock sessionStorage, localStorage, window.location
4. **Redux Test Utilities**: configureStore with mock reducers
5. **Coverage Reporting**: Jest coverage thresholds configured

## Acceptance Criteria Impact

Story 1.4 Definition of Done includes:
- [ ] LoginPage.test.tsx created with unit tests
- [ ] GoogleLoginButton.test.tsx created with unit tests *(Not needed - GoogleLogin is from library)*
- [ ] AuthCallback.test.tsx created with unit tests
- [ ] ProtectedRoute.test.tsx created with unit tests
- [ ] authSlice.test.tsx created with unit tests
- [ ] Integration test for full OAuth flow
- [ ] All tests pass with 100% coverage for auth logic
- [ ] Mock @react-oauth/google components in tests

**Current Status**: ❌ Tests NOT written
**Reason**: Story 1.8 explicitly covers testing infrastructure establishment
**Action**: Implement during Story 1.8 execution

## Notes

- Basic authSlice.test.ts created as template: `src/entries/Contacts/redux/slices/auth/__tests__/authSlice.test.ts`
- All 7 critical code review issues have been FIXED
- Application compiles successfully with zero TypeScript errors
- Tests deferred to Story 1.8 per project workflow

