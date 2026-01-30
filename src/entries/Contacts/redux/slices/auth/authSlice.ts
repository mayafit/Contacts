/**
 * @fileoverview Authentication Redux slice with backend session management
 * @module Contacts/redux/slices/auth/authSlice
 *
 * Updated for Story 2.1B: Simplified to work with backend cookie-based authentication
 * OAuth flow and token management now handled server-side
 */

import { createSlice, createAsyncThunk, PayloadAction } from '@reduxjs/toolkit';
import { GoogleContactsService } from '../../../services/GoogleContactsService';
import type { AuthState, User, AppError } from '../../../types';
import { ErrorCode } from '../../../types';

/**
 * Initial authentication state
 * Simplified - no more access tokens or expiry tracking (handled by backend)
 */
const initialState: AuthState = {
  user: null,
  accessToken: null, // Kept for backward compatibility but not used
  tokenExpiry: null, // Kept for backward compatibility but not used
  isLoading: false,
  error: null,
  isAuthenticated: false,
};

/**
 * Async thunk to check authentication status with backend
 * Verifies if user has valid session cookie
 */
export const checkAuthStatus = createAsyncThunk<
  { isAuthenticated: boolean },
  void,
  { rejectValue: AppError }
>('auth/checkAuthStatus', async (_, { rejectWithValue }) => {
  const response = await GoogleContactsService.checkAuthStatus();

  if (!response.success) {
    return rejectWithValue({
      code: ErrorCode.AUTH_FAILED,
      message: response.error?.message || 'Failed to check auth status',
      timestamp: new Date().toISOString(),
    });
  }

  return response.data!;
});

/**
 * Async thunk for logout
 * Calls backend to clear session and cookies
 */
export const logoutUser = createAsyncThunk<void, void, { rejectValue: AppError }>(
  'auth/logoutUser',
  async (_, { rejectWithValue }) => {
    const response = await GoogleContactsService.logout();

    if (!response.success) {
      return rejectWithValue({
        code: ErrorCode.AUTH_FAILED,
        message: response.error?.message || 'Failed to logout',
        timestamp: new Date().toISOString(),
      });
    }
  },
);

/**
 * Authentication Redux slice
 */
const authSlice = createSlice({
  name: 'auth',
  initialState,
  reducers: {
    /**
     * Set authenticated user profile
     */
    setUser(state, action: PayloadAction<User>) {
      state.user = action.payload;
      state.isAuthenticated = true;
    },

    /**
     * Set access token and expiry (kept for backward compatibility)
     * In the new architecture, this is not used (backend handles tokens)
     */
    setToken(state, action: PayloadAction<{ token: string; expiresIn: number }>) {
      state.accessToken = action.payload.token;
      state.tokenExpiry = new Date(Date.now() + action.payload.expiresIn * 1000).toISOString();
    },

    /**
     * Clear authentication state (logout)
     * Note: This just clears Redux state. Use logoutUser thunk to also clear backend session
     */
    logout(state) {
      state.user = null;
      state.accessToken = null;
      state.tokenExpiry = null;
      state.isAuthenticated = false;
      state.error = null;
    },

    /**
     * Set authentication error
     */
    setAuthError(state, action: PayloadAction<AppError>) {
      state.error = action.payload;
      state.isLoading = false;
    },

    /**
     * Clear authentication error
     */
    clearAuthError(state) {
      state.error = null;
    },

    /**
     * Restore auth state (simplified - just user info)
     */
    restoreAuthState(state, action: PayloadAction<{ user: User }>) {
      state.user = action.payload.user;
      state.isAuthenticated = true;
    },

    /**
     * Kept for backward compatibility but not used in new architecture
     */
    tokenRefreshSuccess(state, action: PayloadAction<string>) {
      state.accessToken = action.payload;
      state.isAuthenticated = true;
      state.error = null;
    },
  },
  extraReducers: (builder) => {
    builder
      // Check auth status pending
      .addCase(checkAuthStatus.pending, (state) => {
        state.isLoading = true;
        state.error = null;
      })
      // Check auth status fulfilled
      .addCase(checkAuthStatus.fulfilled, (state, action) => {
        state.isLoading = false;
        state.isAuthenticated = action.payload.isAuthenticated;
        if (!action.payload.isAuthenticated) {
          state.user = null;
        }

        // DIAGNOSTIC: This will show in Redux DevTools console
        console.log('[authSlice] checkAuthStatus.fulfilled:', {
          isAuthenticated: action.payload.isAuthenticated,
          payload: action.payload,
        });
      })
      // Check auth status rejected
      .addCase(checkAuthStatus.rejected, (state, action) => {
        state.isLoading = false;
        state.error = action.payload || {
          code: ErrorCode.UNKNOWN_ERROR,
          message: 'An unknown error occurred',
          timestamp: new Date().toISOString(),
        };
        state.isAuthenticated = false;
        state.user = null;
      })
      // Logout pending
      .addCase(logoutUser.pending, (state) => {
        state.isLoading = true;
      })
      // Logout fulfilled
      .addCase(logoutUser.fulfilled, (state) => {
        state.isLoading = false;
        state.user = null;
        state.accessToken = null;
        state.tokenExpiry = null;
        state.isAuthenticated = false;
        state.error = null;
      })
      // Logout rejected
      .addCase(logoutUser.rejected, (state, action) => {
        state.isLoading = false;
        state.error = action.payload || {
          code: ErrorCode.UNKNOWN_ERROR,
          message: 'Failed to logout',
          timestamp: new Date().toISOString(),
        };
        // Still clear local state even if backend logout fails
        state.user = null;
        state.accessToken = null;
        state.tokenExpiry = null;
        state.isAuthenticated = false;
      });
  },
});

export const {
  setUser,
  setToken,
  logout,
  setAuthError,
  clearAuthError,
  restoreAuthState,
  tokenRefreshSuccess,
} = authSlice.actions;

export default authSlice.reducer;
