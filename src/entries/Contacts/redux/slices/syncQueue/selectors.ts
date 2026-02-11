/**
 * @fileoverview Sync queue state selectors
 * @module Contacts/redux/slices/syncQueue/selectors
 *
 * Story 3.2: Provides memoized selectors for accessing sync queue state
 */

import { createSelector } from '@reduxjs/toolkit';
import type { RootState } from '../../../types/store';
import type { SyncQueueState } from '../../../types/SyncOperation';
import { syncQueueAdapterSelectors } from './syncQueueSlice';

/**
 * Select sync queue state slice
 */
export const selectSyncQueueState = (state: RootState): SyncQueueState | undefined =>
  state.contacts?.syncQueue;

/**
 * Select all sync operations as denormalized array
 */
export const selectAllSyncOperations = createSelector(
  [selectSyncQueueState],
  (syncQueueState) => {
    if (!syncQueueState) return [];
    return syncQueueAdapterSelectors.selectAll(syncQueueState);
  },
);

/**
 * Select operations with status "pending"
 */
export const selectPendingOperations = createSelector(
  [selectAllSyncOperations],
  (operations) => operations.filter((op) => op.status === 'pending'),
);

/**
 * Select operations with status "failed"
 */
export const selectFailedOperations = createSelector(
  [selectAllSyncOperations],
  (operations) => operations.filter((op) => op.status === 'failed'),
);

/**
 * Select operations with status "in-progress"
 */
export const selectInProgressOperations = createSelector(
  [selectAllSyncOperations],
  (operations) => operations.filter((op) => op.status === 'in-progress'),
);

/**
 * Select a specific operation by ID
 */
export const selectOperationById = createSelector(
  [selectSyncQueueState, (_state: RootState, operationId: string) => operationId],
  (syncQueueState, operationId) => {
    if (!syncQueueState) return undefined;
    return syncQueueState.entities[operationId];
  },
);

/**
 * Select all operations for a specific contact
 */
export const selectOperationsByContact = createSelector(
  [selectAllSyncOperations, (_state: RootState, resourceName: string) => resourceName],
  (operations, resourceName) =>
    operations.filter((op) => op.resourceName === resourceName),
);

/**
 * Select retry count for a specific operation
 */
export const selectRetryCount = createSelector(
  [selectSyncQueueState, (_state: RootState, operationId: string) => operationId],
  (syncQueueState, operationId) => {
    if (!syncQueueState) return 0;
    return syncQueueState.retryCount[operationId] ?? 0;
  },
);

/**
 * Select whether an operation has exceeded max retries
 */
export const selectHasMaxRetriesExceeded = createSelector(
  [selectSyncQueueState, (_state: RootState, operationId: string) => operationId],
  (syncQueueState, operationId) => {
    if (!syncQueueState) return false;
    const count = syncQueueState.retryCount[operationId] ?? 0;
    return count >= syncQueueState.maxRetries;
  },
);

/**
 * Select total number of operations in queue
 */
export const selectQueueSize = createSelector(
  [selectSyncQueueState],
  (syncQueueState) => {
    if (!syncQueueState) return 0;
    return syncQueueState.ids.length;
  },
);
