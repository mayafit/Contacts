/**
 * @fileoverview Contact state selectors
 * @module Contacts/redux/slices/contacts/selectors
 *
 * Story 2.2: Provides efficient selectors for accessing normalized contact state
 */

import { createSelector } from '@reduxjs/toolkit';
import type { RootState } from '../../../types/store';
import { contactsSelectors } from './contactsSlice';

/**
 * Select contacts state slice
 */
export const selectContactsState = (state: RootState) => state.contacts?.contacts;

/**
 * Select all contacts as denormalized array
 * Uses entity adapter's selectAll for efficient access
 */
export const selectAllContacts = createSelector(
  [selectContactsState],
  (contactsState) => {
    if (!contactsState) return [];
    return contactsSelectors.selectAll(contactsState);
  },
);

/**
 * Select contact by resourceName (ID)
 * O(1) lookup in entities map
 */
export const selectContactById = createSelector(
  [selectContactsState, (_state: RootState, resourceName: string) => resourceName],
  (contactsState, resourceName) => {
    if (!contactsState) return undefined;
    return contactsState.entities[resourceName];
  },
);

/**
 * Select contacts loading state
 */
export const selectContactsLoading = createSelector(
  [selectContactsState],
  (contactsState) => contactsState?.isLoading || false,
);

/**
 * Select contacts error state
 */
export const selectContactsError = createSelector(
  [selectContactsState],
  (contactsState) => contactsState?.error || null,
);

/**
 * Select last fetched timestamp
 */
export const selectContactsLastFetched = createSelector(
  [selectContactsState],
  (contactsState) => contactsState?.lastFetched || null,
);

/**
 * Select total count of contacts
 */
export const selectContactsCount = createSelector(
  [selectContactsState],
  (contactsState) => {
    if (!contactsState) return 0;
    return contactsState.ids.length;
  },
);

/**
 * Select whether contacts data is stale (older than 5 minutes)
 */
export const selectContactsIsStale = createSelector(
  [selectContactsLastFetched],
  (lastFetched) => {
    if (!lastFetched) return true;
    const fiveMinutesAgo = Date.now() - 5 * 60 * 1000;
    return new Date(lastFetched).getTime() < fiveMinutesAgo;
  },
);
