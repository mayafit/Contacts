/**
 * @fileoverview Store type definitions for Contacts entry
 * @module Contacts/types/store
 */

import type { ThunkDispatch, UnknownAction } from '@reduxjs/toolkit';
import type { AuthState } from './AuthTypes';

/**
 * Root state type
 */
export interface RootState {
  contacts?: {
    auth?: AuthState;
  };
  // Dynamic reducers can add additional state slices
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  [key: string]: any;
}

/**
 * App dispatch type with thunk support
 */
export type AppDispatch = ThunkDispatch<RootState, undefined, UnknownAction>;
