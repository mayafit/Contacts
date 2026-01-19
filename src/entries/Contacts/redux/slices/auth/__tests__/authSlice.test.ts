/**
 * @fileoverview Authentication Redux slice tests
 * @module Contacts/redux/slices/auth/__tests__/authSlice
 */

import authReducer, { loginWithGoogle, logout, restoreAuthState } from '../authSlice';
import { ErrorCode } from '../../../../types';
import type { AuthState, GoogleCredentialResponse } from '../../../../types';

describe('authSlice', () => {
  const initialState: AuthState = {
    user: null,
    accessToken: null,
    tokenExpiry: null,
    isLoading: false,
    error: null,
    isAuthenticated: false,
  };

  afterEach(() => {
    sessionStorage.clear();
  });

  describe('reducers', () => {
    it('logout_Should_ClearAuthState_When_Called', () => {
      const authenticatedState: AuthState = {
        ...initialState,
        user: { id: '123', name: 'Test User', email: 'test@example.com' },
        accessToken: 'token',
        tokenExpiry: new Date().toISOString(),
        isAuthenticated: true,
      };

      const newState = authReducer(authenticatedState, logout());

      expect(newState.user).toBeNull();
      expect(newState.accessToken).toBeNull();
      expect(newState.isAuthenticated).toBe(false);
    });

    it('restoreAuthState_Should_RestoreUser_When_ValidSessionData', () => {
      const user = { id: '123', name: 'Test', email: 'test@example.com' };
      const token = 'test-token';
      const tokenExpiry = new Date(Date.now() + 3600000).toISOString();

      const newState = authReducer(initialState, restoreAuthState({ user, token, tokenExpiry }));

      expect(newState.user).toEqual(user);
      expect(newState.accessToken).toBe(token);
      expect(newState.isAuthenticated).toBe(true);
    });
  });

  describe('loginWithGoogle async thunk', () => {
    it('loginWithGoogle_Should_RejectInvalidJWT_When_MalformedToken', async () => {
      const invalidResponse: GoogleCredentialResponse = {
        credential: 'invalid.token',
      };

      const result = await loginWithGoogle(invalidResponse)(jest.fn(), jest.fn(), undefined);

      expect(result.type).toBe('auth/loginWithGoogle/rejected');
      expect(result.payload).toHaveProperty('code', ErrorCode.INVALID_TOKEN);
    });
  });
});
