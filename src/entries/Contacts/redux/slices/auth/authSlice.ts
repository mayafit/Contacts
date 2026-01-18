/**
 * @fileoverview Authentication Redux slice with OAuth state management
 * @module Contacts/redux/slices/auth/authSlice
 */

import { createSlice, createAsyncThunk, PayloadAction } from '@reduxjs/toolkit';
import type { AuthState, User, AppError, GoogleCredentialResponse } from '../../../types';
import { ErrorCode } from '../../../types';

/**
 * Initial authentication state
 */
const initialState: AuthState = {
  user: null,
  accessToken: null,
  tokenExpiry: null,
  isLoading: false,
  error: null,
  isAuthenticated: false,
};

/**
 * Async thunk for Google OAuth login
 * Handles credential validation and user profile fetching
 */
export const loginWithGoogle = createAsyncThunk<
  { user: User; token: string; expiresIn: number },
  GoogleCredentialResponse,
  { rejectValue: AppError }
>('auth/loginWithGoogle', async (credentialResponse, { rejectWithValue }) => {
  try {
    // Decode JWT credential to extract user info
    const base64Url = credentialResponse.credential.split('.')[1];
    const base64 = base64Url.replace(/-/g, '+').replace(/_/g, '/');
    const jsonPayload = decodeURIComponent(
      atob(base64)
        .split('')
        .map((c) => '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2))
        .join(''),
    );
    const payload = JSON.parse(jsonPayload);

    const user: User = {
      id: payload.sub,
      name: payload.name,
      email: payload.email,
      profilePicture: payload.picture,
    };

    // Token expires in 3600 seconds (1 hour) by default
    const expiresIn = payload.exp ? payload.exp - Math.floor(Date.now() / 1000) : 3600;

    return {
      user,
      token: credentialResponse.credential,
      expiresIn,
    };
  } catch (err) {
    return rejectWithValue({
      code: ErrorCode.AUTH_FAILED,
      message: 'Failed to authenticate with Google',
      technicalMessage: err instanceof Error ? err.message : String(err),
      timestamp: new Date().toISOString(),
    });
  }
});

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
     * Set access token and expiry
     */
    setToken(state, action: PayloadAction<{ token: string; expiresIn: number }>) {
      state.accessToken = action.payload.token;
      state.tokenExpiry = new Date(Date.now() + action.payload.expiresIn * 1000).toISOString();
    },

    /**
     * Clear authentication state (logout)
     */
    logout(state) {
      state.user = null;
      state.accessToken = null;
      state.tokenExpiry = null;
      state.isAuthenticated = false;
      state.error = null;
      // Clear tokens from sessionStorage
      sessionStorage.removeItem('access_token');
      sessionStorage.removeItem('token_expiry');
      sessionStorage.removeItem('user');
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
     * Restore auth state from sessionStorage
     */
    restoreAuthState(
      state,
      action: PayloadAction<{ user: User; token: string; tokenExpiry: string }>,
    ) {
      state.user = action.payload.user;
      state.accessToken = action.payload.token;
      state.tokenExpiry = action.payload.tokenExpiry;
      state.isAuthenticated = true;
    },
  },
  extraReducers: (builder) => {
    builder
      // Login pending
      .addCase(loginWithGoogle.pending, (state) => {
        state.isLoading = true;
        state.error = null;
      })
      // Login fulfilled
      .addCase(loginWithGoogle.fulfilled, (state, action) => {
        state.isLoading = false;
        state.user = action.payload.user;
        state.accessToken = action.payload.token;
        state.tokenExpiry = new Date(Date.now() + action.payload.expiresIn * 1000).toISOString();
        state.isAuthenticated = true;
        state.error = null;

        // Store in sessionStorage for persistence
        sessionStorage.setItem('access_token', action.payload.token);
        sessionStorage.setItem('token_expiry', state.tokenExpiry);
        sessionStorage.setItem('user', JSON.stringify(action.payload.user));
      })
      // Login rejected
      .addCase(loginWithGoogle.rejected, (state, action) => {
        state.isLoading = false;
        state.error = action.payload || {
          code: ErrorCode.UNKNOWN_ERROR,
          message: 'An unknown error occurred',
          timestamp: new Date().toISOString(),
        };
        state.isAuthenticated = false;
      });
  },
});

export const { setUser, setToken, logout, setAuthError, clearAuthError, restoreAuthState } =
  authSlice.actions;

export default authSlice.reducer;
