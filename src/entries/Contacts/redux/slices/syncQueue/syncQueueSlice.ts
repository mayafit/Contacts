/**
 * @fileoverview Sync queue Redux slice for tracking pending and failed contact update operations
 * @module Contacts/redux/slices/syncQueue/syncQueueSlice
 *
 * Story 3.2: Implements normalized sync queue state using createEntityAdapter
 * Provides reducers for managing operation lifecycle: pending → in-progress → success/failed
 */

import { createSlice, createEntityAdapter, PayloadAction } from '@reduxjs/toolkit';
import type { SyncOperation, SyncQueueState } from '../../../types/SyncOperation';

/**
 * Entity adapter for normalized sync operations state
 * Uses UUID string as the entity ID for O(1) lookups
 */
const syncQueueAdapter = createEntityAdapter<SyncOperation, string>({
  selectId: (operation) => operation.id,
});

/**
 * Initial state with entity adapter defaults and retry tracking
 */
const initialState: SyncQueueState = {
  ...syncQueueAdapter.getInitialState(),
  retryCount: {},
  maxRetries: 3,
};

/**
 * Sync queue Redux slice
 * Manages the lifecycle of contact update operations for optimistic updates and retries
 */
const syncQueueSlice = createSlice({
  name: 'syncQueue',
  initialState,
  reducers: {
    /**
     * Add a new sync operation to the queue with status "pending"
     * Initializes retry count to 0
     */
    addToQueue(state, action: PayloadAction<SyncOperation>) {
      const operation = { ...action.payload, status: 'pending' as const };
      syncQueueAdapter.addOne(state, operation);
      state.retryCount[operation.id] = 0;
    },

    /**
     * Mark an operation as in-progress (being sent to backend)
     */
    operationStarted(state, action: PayloadAction<string>) {
      syncQueueAdapter.updateOne(state, {
        id: action.payload,
        changes: { status: 'in-progress' },
      });
    },

    /**
     * Mark an operation as successful and remove it from the queue
     * Clears the retry count for this operation
     */
    operationSuccess(state, action: PayloadAction<string>) {
      syncQueueAdapter.removeOne(state, action.payload);
      delete state.retryCount[action.payload];
    },

    /**
     * Mark an operation as failed, store the error, and increment retry count
     */
    operationFailed(state, action: PayloadAction<{ id: string; error: string }>) {
      const { id, error } = action.payload;
      syncQueueAdapter.updateOne(state, {
        id,
        changes: { status: 'failed', error },
      });
      state.retryCount[id] = (state.retryCount[id] ?? 0) + 1;
    },

    /**
     * Reset a failed operation to "pending" for retry
     * Maintains the current retry count for exponential backoff calculation
     */
    retryOperation(state, action: PayloadAction<string>) {
      syncQueueAdapter.updateOne(state, {
        id: action.payload,
        changes: { status: 'pending', error: null },
      });
    },

    /**
     * Clear all operations from the queue and reset retry counts
     */
    clearQueue(state) {
      syncQueueAdapter.removeAll(state);
      state.retryCount = {};
    },
  },
});

export const {
  addToQueue,
  operationStarted,
  operationSuccess,
  operationFailed,
  retryOperation,
  clearQueue,
} = syncQueueSlice.actions;

export default syncQueueSlice.reducer;

/** Exported entity adapter selectors for use in memoized selectors */
export const syncQueueAdapterSelectors = syncQueueAdapter.getSelectors();
