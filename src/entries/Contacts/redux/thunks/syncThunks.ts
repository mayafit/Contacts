/**
 * @fileoverview Sync orchestrator thunks for field-level contact updates
 * @module Contacts/redux/thunks/syncThunks
 *
 * Story 3.3: Coordinates optimistic updates, sync queue operations,
 * and API calls with exponential backoff retry logic.
 */

import { createAsyncThunk } from '@reduxjs/toolkit';
import { logger } from '../../../../shared/logger';
import { GoogleContactsService } from '../../services/GoogleContactsService';
import {
  addToQueue,
  operationStarted,
  operationSuccess,
  operationFailed,
  retryOperation,
} from '../slices/syncQueue/syncQueueSlice';
import { contactUpdated } from '../slices/contacts/contactsSlice';
import {
  selectPendingOperations,
  selectInProgressOperations,
} from '../slices/syncQueue/selectors';
import type { AppDispatch, RootState } from '../../types/store';
import type { SyncOperation } from '../../types/SyncOperation';
import type { Contact } from '../../types/Contact';

/** Base delay in ms for exponential backoff */
const BACKOFF_BASE_MS = 1000;

/** Max retries before permanent failure (matches syncQueueSlice maxRetries) */
const MAX_RETRIES = 3;

/**
 * Calculate exponential backoff delay
 * @param retryCount - Current retry attempt (0-based)
 * @returns Delay in milliseconds: 1000, 2000, 4000
 */
export const calculateBackoff = (retryCount: number): number =>
  BACKOFF_BASE_MS * Math.pow(2, retryCount);

/**
 * Delay helper that returns a cancellable promise
 */
const delay = (ms: number): Promise<void> =>
  new Promise((resolve) => setTimeout(resolve, ms));

/**
 * Execute a field-level contact update with optimistic UI and background sync.
 *
 * Flow:
 * 1. Generate operation ID, add to sync queue as pending
 * 2. Apply optimistic update to contacts state immediately
 * 3. Mark operation as in-progress, call API
 * 4. On success: remove from queue, update contact with server response
 * 5. On failure: mark failed, schedule retry if under max retries
 */
export const executeFieldUpdate = createAsyncThunk<
  void,
  {
    resourceName: string;
    fieldPath: string;
    newValue: unknown;
    oldValue: unknown;
  },
  { state: RootState }
>(
  'sync/executeFieldUpdate',
  async ({ resourceName, fieldPath, newValue, oldValue }, { dispatch, getState }) => {
    const operationId = crypto.randomUUID();

    logger.info(
      {
        context: 'syncThunks/executeFieldUpdate',
        metadata: { operationId, resourceName, fieldPath },
      },
      'Starting field update',
    );

    // 1. Create sync operation and add to queue
    const operation: SyncOperation = {
      id: operationId,
      resourceName,
      fieldPath,
      newValue,
      oldValue,
      status: 'pending',
      timestamp: new Date().toISOString(),
      error: null,
    };
    dispatch(addToQueue(operation));

    // 2. Optimistic update — apply new value to contacts state immediately
    const state = getState();
    const existingContact = state.contacts?.contacts?.entities[resourceName];
    if (existingContact) {
      const optimisticContact: Contact = {
        ...existingContact,
        [fieldPath]: newValue,
      };
      dispatch(contactUpdated(optimisticContact));
    }

    // 3. Execute the API call
    await executeApiCall(dispatch, getState, operationId, resourceName, fieldPath, newValue, oldValue);
  },
);

/**
 * Internal: Execute the API call for a sync operation with retry handling.
 */
async function executeApiCall(
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  dispatch: AppDispatch | ((...args: any[]) => any),
  getState: () => RootState,
  operationId: string,
  resourceName: string,
  fieldPath: string,
  newValue: unknown,
  oldValue: unknown,
): Promise<void> {
  dispatch(operationStarted(operationId));

  const result = await GoogleContactsService.updateContactField(
    resourceName,
    fieldPath,
    newValue,
  );

  if (result.success && result.data) {
    // Success: remove from queue, update contact with server-confirmed data
    dispatch(operationSuccess(operationId));
    dispatch(contactUpdated(result.data));

    logger.info(
      {
        context: 'syncThunks/executeApiCall',
        metadata: { operationId, resourceName, fieldPath },
      },
      'Field update synced successfully',
    );
  } else {
    // Failure: mark failed and attempt retry
    const errorMessage = result.error?.message || 'Unknown error';
    dispatch(operationFailed({ id: operationId, error: errorMessage }));

    logger.warn(
      {
        context: 'syncThunks/executeApiCall',
        metadata: { operationId, resourceName, fieldPath, error: errorMessage },
      },
      'Field update failed',
    );

    // Check retry eligibility
    const currentState = getState();
    const syncQueueState = currentState.contacts?.syncQueue;
    const retryCount = syncQueueState?.retryCount[operationId] ?? 0;

    if (retryCount < MAX_RETRIES) {
      const backoffMs = calculateBackoff(retryCount - 1);

      logger.info(
        {
          context: 'syncThunks/executeApiCall',
          metadata: { operationId, retryCount, backoffMs },
        },
        'Scheduling retry with backoff',
      );

      await delay(backoffMs);

      // Reset to pending and re-execute
      dispatch(retryOperation(operationId));
      await executeApiCall(dispatch, getState, operationId, resourceName, fieldPath, newValue, oldValue);
    } else {
      // Max retries exceeded: rollback optimistic update
      logger.error(
        {
          context: 'syncThunks/executeApiCall',
          metadata: { operationId, resourceName, fieldPath, retryCount },
        },
        'Max retries exceeded, rolling back optimistic update',
      );

      const existingContact = getState().contacts?.contacts?.entities[resourceName];
      if (existingContact) {
        const rolledBackContact: Contact = {
          ...existingContact,
          [fieldPath]: oldValue,
        };
        dispatch(contactUpdated(rolledBackContact));
      }
    }
  }
}

/**
 * Process the next pending operation in the sync queue (FIFO order).
 * Skips if there's already an in-progress operation to prevent concurrent API calls.
 */
export const processQueue = createAsyncThunk<
  void,
  void,
  { state: RootState }
>(
  'sync/processQueue',
  async (_, { dispatch, getState }) => {
    const state = getState();

    // Guard: don't process if something is already in-progress
    const inProgress = selectInProgressOperations(state);
    if (inProgress.length > 0) {
      logger.debug(
        {
          context: 'syncThunks/processQueue',
          metadata: { inProgressCount: inProgress.length },
        },
        'Queue processing skipped, operation already in progress',
      );
      return;
    }

    const pending = selectPendingOperations(state);
    if (pending.length === 0) {
      return;
    }

    // Pick the first pending operation (FIFO)
    const next = pending[0];

    logger.info(
      {
        context: 'syncThunks/processQueue',
        metadata: { operationId: next.id, queueSize: pending.length },
      },
      'Processing next queue operation',
    );

    await executeApiCall(
      dispatch,
      getState,
      next.id,
      next.resourceName,
      next.fieldPath,
      next.newValue,
      next.oldValue,
    );
  },
);
