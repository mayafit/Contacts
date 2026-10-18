/**
 * @fileoverview Delete and merge thunks (Story 5.1, 5.2)
 * @module Contacts/redux/thunks/cleanupThunks
 */

import { createAsyncThunk } from '@reduxjs/toolkit';
import {
  ApiRequestError,
  backendApiClient,
  type MergeRequest,
} from '../../../../services/api/BackendApiClient';
import { contactsRemoved, contactUpdated } from '../slices/contacts/contactsSlice';
import { logger } from '../../../../shared/logger';
import type { Contact } from '../../types/Contact';

/** Why a delete or merge failed */
export interface CleanupError {
  /** Backend error code (MERGE_WOULD_LOSE_DATA, CONTACT_CONFLICT, ...) */
  code: string;
  /** Human-readable message */
  message: string;
  /** For MERGE_WOULD_LOSE_DATA: resource name to the fields that would be lost */
  lostData?: Record<string, string[]>;
}

const toCleanupError = (error: unknown): CleanupError => {
  if (error instanceof ApiRequestError) {
    return {
      code: error.code,
      message: error.message,
      lostData:
        error.code === 'MERGE_WOULD_LOSE_DATA'
          ? (error.details as Record<string, string[]> | undefined)
          : undefined,
    };
  }
  return { code: 'UNKNOWN_ERROR', message: error instanceof Error ? error.message : String(error) };
};

/**
 * Deletes contacts in Google and removes them from the store.
 */
export const deleteContacts = createAsyncThunk<number, string[], { rejectValue: CleanupError }>(
  'contacts/deleteContacts',
  async (resourceNames, { dispatch, rejectWithValue }) => {
    let deleted = 0;
    try {
      // One request per 500; each successful chunk leaves the store at once, so a
      // later failure never leaves deleted contacts on screen.
      for (let i = 0; i < resourceNames.length; i += 500) {
        const chunk = resourceNames.slice(i, i + 500);

        deleted += await backendApiClient.deleteContacts(chunk);
        dispatch(contactsRemoved(chunk));
      }
      logger.info(
        { context: 'cleanupThunks/deleteContacts', metadata: { deleted } },
        'Contacts deleted',
      );
      return deleted;
    } catch (error) {
      const failure = toCleanupError(error);
      return rejectWithValue(
        deleted ? { ...failure, message: `${deleted} deleted, then: ${failure.message}` } : failure,
      );
    }
  },
);

/**
 * Merges contacts in Google: the kept contact is saved, the others deleted.
 */
export const mergeContacts = createAsyncThunk<Contact, MergeRequest, { rejectValue: CleanupError }>(
  'contacts/mergeContacts',
  async (request, { dispatch, rejectWithValue }) => {
    try {
      const { contact, deleted } = await backendApiClient.mergeContacts(request);
      dispatch(contactUpdated(contact));
      dispatch(contactsRemoved(deleted));
      logger.info(
        {
          context: 'cleanupThunks/mergeContacts',
          metadata: { kept: contact.resourceName, deleted: deleted.length },
        },
        'Contacts merged',
      );
      return contact;
    } catch (error) {
      return rejectWithValue(toCleanupError(error));
    }
  },
);
