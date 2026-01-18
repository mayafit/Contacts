/**
 * @fileoverview Authentication selectors for Redux store
 * @module Contacts/redux/slices/auth/selectors
 */

import type { RootState } from '../../../types/store';

/**
 * Select full auth state
 */
export const selectAuthState = (state: RootState) => state.contacts?.auth;

/**
 * Select authenticated user profile
 */
export const selectUser = (state: RootState) => selectAuthState(state)?.user;

/**
 * Select authentication status
 */
export const selectIsAuthenticated = (state: RootState) =>
  selectAuthState(state)?.isAuthenticated || false;

/**
 * Select access token
 */
export const selectAccessToken = (state: RootState) => selectAuthState(state)?.accessToken;

/**
 * Select token expiry timestamp
 */
export const selectTokenExpiry = (state: RootState) => selectAuthState(state)?.tokenExpiry;

/**
 * Select authentication error
 */
export const selectAuthError = (state: RootState) => selectAuthState(state)?.error;

/**
 * Select loading state
 */
export const selectAuthLoading = (state: RootState) => selectAuthState(state)?.isLoading || false;

/**
 * Select whether token is expired
 */
export const selectIsTokenExpired = (state: RootState): boolean => {
  const tokenExpiry = selectTokenExpiry(state);
  if (!tokenExpiry) return true;
  return new Date() > new Date(tokenExpiry);
};
