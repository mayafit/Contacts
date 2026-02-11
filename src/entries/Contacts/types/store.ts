/**
 * @fileoverview Store type definitions for Contacts entry
 * @module Contacts/types/store
 */

import type { ThunkDispatch, UnknownAction } from '@reduxjs/toolkit';
import type { AuthState } from './AuthTypes';
import type { ContactsState } from '../redux/slices/contacts/contactsSlice';
import type { UIState } from '../redux/slices/ui/uiSlice';
import type { SyncQueueState } from './SyncOperation';

/**
 * Root state type
 * Updated for Story 2.2: Added normalized contacts state
 * Updated for Story 2.6: Added UI state for column configuration
 * Updated for Story 3.2: Added sync queue state for tracking operations
 */
export interface RootState {
  contacts?: {
    auth?: AuthState;
    contacts?: ContactsState;
    ui?: UIState;
    syncQueue?: SyncQueueState;
  };
  // Dynamic reducers can add additional state slices
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  [key: string]: any;
}

/**
 * App dispatch type with thunk support
 */
export type AppDispatch = ThunkDispatch<RootState, undefined, UnknownAction>;
