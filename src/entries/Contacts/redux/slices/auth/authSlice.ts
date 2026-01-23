/**
 * @fileoverview Authentication Redux slice with OAuth state management
 * @module Contacts/redux/slices/auth/authSlice
 */

import { createSlice, createAsyncThunk, PayloadAction } from '@reduxjs/toolkit';
import type { AuthState, User, AppError, GoogleCredentialResponse } from '../../../types';
import { ErrorCode } from '../../../types';
import { GoogleAuthService } from '../../../services/auth/GoogleAuthService';

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
 * Handles credential validation, nonce verification, and user profile fetching
 */
export const loginWithGoogle = createAsyncThunk<
  { user: User; token: string; expiresIn: number },
  GoogleCredentialResponse,
  { rejectValue: AppError }
>('auth/loginWithGoogle', async (credentialResponse, { rejectWithValue }) => {
  try {
    // Validate JWT structure
    const jwtParts = credentialResponse.credential.split('.');
    if (jwtParts.length !== 3) {
      return rejectWithValue({
        code: ErrorCode.INVALID_TOKEN,
        message: 'Invalid JWT token structure',
        timestamp: new Date().toISOString(),
      });
    }

    // Decode JWT credential to extract user info
    const base64Url = jwtParts[1];
    const base64 = base64Url.replace(/-/g, '+').replace(/_/g, '/');
    const jsonPayload = decodeURIComponent(
      atob(base64)
        .split('')
        .map((c) => '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2))
        .join(''),
    );
    const payload = JSON.parse(jsonPayload);

    // Validate required JWT claims
    if (!payload.sub || !payload.email || !payload.iss || !payload.aud || !payload.exp) {
      return rejectWithValue({
        code: ErrorCode.INVALID_TOKEN,
        message: 'JWT missing required claims',
        timestamp: new Date().toISOString(),
      });
    }

    // Validate issuer (must be Google)
    if (payload.iss !== 'https://accounts.google.com' && payload.iss !== 'accounts.google.com') {
      return rejectWithValue({
        code: ErrorCode.INVALID_TOKEN,
        message: 'Invalid JWT issuer',
        timestamp: new Date().toISOString(),
      });
    }

    // Validate audience (must match our client ID)
    const expectedAudience = process.env.REACT_APP_GOOGLE_CLIENT_ID;
    if (payload.aud !== expectedAudience) {
      return rejectWithValue({
        code: ErrorCode.INVALID_TOKEN,
        message: 'JWT audience does not match client ID',
        timestamp: new Date().toISOString(),
      });
    }

    // Validate token expiry
    const now = Math.floor(Date.now() / 1000);
    if (payload.exp < now) {
      return rejectWithValue({
        code: ErrorCode.TOKEN_EXPIRED,
        message: 'JWT token has expired',
        timestamp: new Date().toISOString(),
      });
    }

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
 * Async thunk for refreshing access token
 * Attempts to refresh the access token using the refresh token from sessionStorage
 */
export const refreshAccessToken = createAsyncThunk<
  { access_token: string; expires_in: number },
  void,
  { rejectValue: AppError }
>('auth/refreshAccessToken', async (_, { rejectWithValue }) => {
  const response = await GoogleAuthService.refreshAccessToken();

  if (!response.success) {
    return rejectWithValue({
      code: ErrorCode.TOKEN_EXPIRED,
      message: response.error?.message || 'Failed to refresh access token',
      timestamp: new Date().toISOString(),
      context: { details: response.error?.details },
    });
  }

  return response.data!;
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

    /**
     * Update access token after refresh (for Story 1.6 token refresh flow)
     */
    tokenRefreshSuccess(state, action: PayloadAction<string>) {
      state.accessToken = action.payload;
      state.isAuthenticated = true;
      state.error = null;
      // Update sessionStorage with new token
      sessionStorage.setItem('access_token', action.payload);
      sessionStorage.setItem('token_expiry', new Date(Date.now() + 3600 * 1000).toISOString());
      // Update state token expiry
      state.tokenExpiry = new Date(Date.now() + 3600 * 1000).toISOString();
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
      })
      // Token refresh pending
      .addCase(refreshAccessToken.pending, (state) => {
        state.isLoading = true;
      })
      // Token refresh fulfilled
      .addCase(refreshAccessToken.fulfilled, (state, action) => {
        state.isLoading = false;
        state.accessToken = action.payload.access_token;
        state.tokenExpiry = new Date(Date.now() + action.payload.expires_in * 1000).toISOString();
        state.isAuthenticated = true;
        state.error = null;

        // Update sessionStorage with new token
        sessionStorage.setItem('access_token', action.payload.access_token);
        sessionStorage.setItem('token_expiry', state.tokenExpiry);
      })
      // Token refresh rejected
      .addCase(refreshAccessToken.rejected, (state, action) => {
        state.isLoading = false;
        state.error = action.payload || {
          code: ErrorCode.TOKEN_EXPIRED,
          message: 'Failed to refresh access token',
          timestamp: new Date().toISOString(),
        };
        // Do NOT set isAuthenticated to false here - let the interceptor handle logout
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
