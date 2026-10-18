/**
 * @fileoverview Contacts Redux slice with normalized state management
 * @module Contacts/redux/slices/contacts/contactsSlice
 *
 * Story 2.2: Implements normalized contact state structure for efficient storage and access
 * Uses entities/ids pattern for O(1) lookups and maintains referential equality
 */

import {
  createSlice,
  createAsyncThunk,
  PayloadAction,
  createEntityAdapter,
} from '@reduxjs/toolkit';
import { GoogleContactsService } from '../../../services/GoogleContactsService';
import type { Contact, AppError } from '../../../types';
import type { ContactGroup, ContactPatch } from '../../../types/Contact';
import { ErrorCode } from '../../../types';
import { compareByName } from '../../../model/contactView';

/**
 * Entity adapter for normalized contacts state
 * Automatically generates selectors and reducers for normalized data
 * Uses string resourceName as the entity ID
 */
const contactsAdapter = createEntityAdapter<Contact, string>({
  selectId: (contact) => contact.resourceName,
  sortComparer: compareByName,
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
  /** Labels by resource name */
  groups: Record<string, ContactGroup>;
};

/**
 * Initial state with entity adapter
 */
const initialState: ContactsState = contactsAdapter.getInitialState({
  isLoading: false,
  lastFetched: null as string | null,
  error: null as AppError | null,
  groups: {} as Record<string, ContactGroup>,
});

/**
 * Async thunk to fetch label names; failure is non-fatal (labels just show empty)
 */
export const fetchContactGroups = createAsyncThunk<ContactGroup[], void, { rejectValue: AppError }>(
  'contacts/fetchContactGroups',
  async (_, { rejectWithValue }) => {
    const response = await GoogleContactsService.fetchContactGroups();
    if (!response.success || !response.data) {
      return rejectWithValue({
        code: ErrorCode.FETCH_FAILED,
        message: response.error?.message || 'Failed to fetch labels',
        timestamp: new Date().toISOString(),
      });
    }
    return response.data;
  },
);

/**
 * Async thunk to fetch all contacts from backend
 */
export const fetchContacts = createAsyncThunk<Contact[], void, { rejectValue: AppError }>(
  'contacts/fetchContacts',
  async (_, { rejectWithValue }) => {
    const response = await GoogleContactsService.fetchAllContacts();

    if (!response.success || !response.data) {
      return rejectWithValue({
        code: (response.error?.code as ErrorCode) || ErrorCode.FETCH_FAILED,
        message: response.error?.message || 'Failed to fetch contacts',
        timestamp: new Date().toISOString(),
      });
    }

    return response.data;
  },
);

/**
 * Async thunk to update a single contact
 */
export const updateContact = createAsyncThunk<
  Contact,
  { resourceName: string; updates: ContactPatch },
  { rejectValue: AppError }
>('contacts/updateContact', async ({ resourceName, updates }, { rejectWithValue }) => {
  const response = await GoogleContactsService.updateContact(resourceName, updates);

  if (!response.success || !response.data) {
    return rejectWithValue({
      code: (response.error?.code as ErrorCode) || ErrorCode.UPDATE_FAILED,
      message: response.error?.message || 'Failed to update contact',
      timestamp: new Date().toISOString(),
    });
  }

  return response.data;
});

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
    contactsRemoved(state, action: PayloadAction<string[]>) {
      contactsAdapter.removeMany(state, action.payload);
    },

    /** Adds a contact created in this session (or replaces it) */
    contactAdded(state, action: PayloadAction<Contact>) {
      contactsAdapter.upsertOne(state, action.payload);
    },

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
      .addCase(fetchContactGroups.fulfilled, (state, action) => {
        state.groups = Object.fromEntries(action.payload.map((g) => [g.resourceName, g]));
      })
      // Update contact fulfilled
      .addCase(updateContact.fulfilled, (state, action) => {
        const { resourceName } = action.payload;
        contactsAdapter.updateOne(state, {
          id: resourceName,
          changes: action.payload,
        });
      });
    // updateContact.rejected is deliberately not handled here: the card shows the
    // error where the edit happened instead of replacing the list with an error page.
  },
});

export const { contactAdded, contactUpdated, contactsRemoved, clearContactsError, resetContacts } =
  contactsSlice.actions;

export default contactsSlice.reducer;

// Export entity adapter selectors
export const contactsSelectors = contactsAdapter.getSelectors();
