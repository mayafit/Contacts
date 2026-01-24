/**
 * @fileoverview Authentication selectors for Redux store
 * @module Contacts/redux/slices/auth/selectors
 */

import { createSelector } from '@reduxjs/toolkit';
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
 * Returns false if no token expiry is set (not yet authenticated)
 */
export const selectIsTokenExpired = (state: RootState): boolean => {
  const tokenExpiry = selectTokenExpiry(state);
  if (!tokenExpiry) {
    return false; // No token expiry means not authenticated yet, not expired
  }
  return new Date() > new Date(tokenExpiry);
};

/**
 * Memoized selector for auth ready state (not loading)
 * Derived from isLoading state
 */
export const selectAuthReady = createSelector([selectAuthLoading], (isLoading) => !isLoading);

/**
 * Memoized selector for complete auth status
 * Combines authentication status with token expiry
 */
export const selectIsFullyAuthenticated = createSelector(
  [selectIsAuthenticated, selectIsTokenExpired],
  (isAuthenticated, isExpired) => isAuthenticated && !isExpired,
);
