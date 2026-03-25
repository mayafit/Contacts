/**
 * @fileoverview Contacts Redux slice with normalized state management
 * @module Contacts/redux/slices/contacts/contactsSlice
 *
 * Story 2.2: Implements normalized contact state structure for efficient storage and access
 * Uses entities/ids pattern for O(1) lookups and maintains referential equality
 */

import { createSlice, createAsyncThunk, PayloadAction, createEntityAdapter } from '@reduxjs/toolkit';
import { GoogleContactsService } from '../../../services/GoogleContactsService';
import type { Contact, AppError } from '../../../types';
import { ErrorCode } from '../../../types';

/**
 * Entity adapter for normalized contacts state
 * Automatically generates selectors and reducers for normalized data
 * Uses string resourceName as the entity ID
 */
const contactsAdapter = createEntityAdapter<Contact, string>({
  selectId: (contact) => contact.resourceName,
  sortComparer: (a, b) => {
    const nameA = a.names?.[0]?.displayName || '';
    const nameB = b.names?.[0]?.displayName || '';
    return nameA.localeCompare(nameB);
  },
});

/**
 * Contacts slice state interface
 * Extends entity adapter state with additional fields
 */
export type ContactsState = ReturnType<typeof contactsAdapter.getInitialState> & {
  /** Loading state for async operations */
  isLoading: boolean;
  /** Timestamp of last successful fetch */
  lastFetched: string | null;
  /** Error state */
  error: AppError | null;
};

/**
 * Initial state with entity adapter
 */
const initialState: ContactsState = contactsAdapter.getInitialState({
  isLoading: false,
  lastFetched: null as string | null,
  error: null as AppError | null,
});

/**
 * Async thunk to fetch all contacts from backend
 */
export const fetchContacts = createAsyncThunk<
  Contact[],
  void,
  { rejectValue: AppError }
>('contacts/fetchContacts', async (_, { rejectWithValue }) => {
  const response = await GoogleContactsService.fetchAllContacts();

  if (!response.success || !response.data) {
    return rejectWithValue({
      code: (response.error?.code as ErrorCode) || ErrorCode.FETCH_FAILED,
      message: response.error?.message || 'Failed to fetch contacts',
      timestamp: new Date().toISOString(),
    });
  }

  return response.data;
});

/**
 * Async thunk to update a single contact
 */
export const updateContact = createAsyncThunk<
  Contact,
  { resourceName: string; updates: Partial<Contact> },
  { rejectValue: AppError }
>(
  'contacts/updateContact',
  async ({ resourceName, updates }, { rejectWithValue }) => {
    const response = await GoogleContactsService.updateContact(resourceName, updates);

    if (!response.success || !response.data) {
      return rejectWithValue({
        code: (response.error?.code as ErrorCode) || ErrorCode.UPDATE_FAILED,
        message: response.error?.message || 'Failed to update contact',
        timestamp: new Date().toISOString(),
      });
    }

    return response.data;
  },
);

/**
 * Contacts Redux slice
 */
const contactsSlice = createSlice({
  name: 'contacts',
  initialState,
  reducers: {
    /**
     * Update a single contact in the normalized state
     * Maintains referential equality for unchanged contacts
     */
    contactUpdated(state, action: PayloadAction<Contact>) {
      const { resourceName } = action.payload;
      if (state.entities[resourceName]) {
        contactsAdapter.updateOne(state, {
          id: resourceName,
          changes: action.payload,
        });
      }
    },

    /**
     * Clear contacts error state
     */
    clearContactsError(state) {
      state.error = null;
    },

    /**
     * Reset contacts state to initial
     */
    resetContacts(state) {
      contactsAdapter.removeAll(state);
      state.isLoading = false;
      state.lastFetched = null;
      state.error = null;
    },
  },
  extraReducers: (builder) => {
    builder
      // Fetch contacts pending
      .addCase(fetchContacts.pending, (state) => {
        state.isLoading = true;
        state.error = null;
      })
      // Fetch contacts fulfilled
      .addCase(fetchContacts.fulfilled, (state, action) => {
        state.isLoading = false;
        state.lastFetched = new Date().toISOString();
        // Normalize contacts into entities and ids
        contactsAdapter.setAll(state, action.payload);
      })
      // Fetch contacts rejected
      .addCase(fetchContacts.rejected, (state, action) => {
        state.isLoading = false;
        state.error = action.payload || {
          code: ErrorCode.UNKNOWN_ERROR,
          message: 'An unknown error occurred',
          timestamp: new Date().toISOString(),
        };
      })
      // Update contact pending
      .addCase(updateContact.pending, (state) => {
        state.error = null;
      })
      // Update contact fulfilled
      .addCase(updateContact.fulfilled, (state, action) => {
        const { resourceName } = action.payload;
        contactsAdapter.updateOne(state, {
          id: resourceName,
          changes: action.payload,
        });
      })
      // Update contact rejected
      .addCase(updateContact.rejected, (state, action) => {
        state.error = action.payload || {
          code: ErrorCode.UNKNOWN_ERROR,
          message: 'Failed to update contact',
          timestamp: new Date().toISOString(),
        };
      });
  },
});

export const { contactUpdated, clearContactsError, resetContacts } = contactsSlice.actions;

export default contactsSlice.reducer;

// Export entity adapter selectors
export const contactsSelectors = contactsAdapter.getSelectors();
