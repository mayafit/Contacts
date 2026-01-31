/**
 * @fileoverview Store type definitions for Contacts entry
 * @module Contacts/types/store
 */

import type { ThunkDispatch, UnknownAction } from '@reduxjs/toolkit';
import type { AuthState } from './AuthTypes';
import type { ContactsState } from '../redux/slices/contacts/contactsSlice';

/**
 * Root state type
 * Updated for Story 2.2: Added normalized contacts state
 */
export interface RootState {
  contacts?: {
    auth?: AuthState;
    contacts?: ContactsState;
  };
  // Dynamic reducers can add additional state slices
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  [key: string]: any;
}

/**
 * App dispatch type with thunk support
 */
export type AppDispatch = ThunkDispatch<RootState, undefined, UnknownAction>;
