/**
 * @fileoverview Sync orchestrator thunks for field-level contact updates
 * @module Contacts/redux/thunks/syncThunks
 *
 * Story 3.3: Coordinates optimistic updates, sync queue operations,
 * and API calls with exponential backoff retry logic.
 * Story 3.6: Extends retry to 5 levels, adds error classification and timer tracking.
 */

import { createAsyncThunk } from '@reduxjs/toolkit';
import { logger } from '../../../../shared/logger';
import { GoogleContactsService } from '../../services/GoogleContactsService';
import {
  addToQueue,
  operationStarted,
  operationSuccess,
  operationFailed,
  operationConflict,
  retryOperation,
} from '../slices/syncQueue/syncQueueSlice';
import { contactUpdated } from '../slices/contacts/contactsSlice';
import { selectPendingOperations, selectInProgressOperations } from '../slices/syncQueue/selectors';
import type { AppDispatch, RootState } from '../../types/store';
import type { SyncOperation } from '../../types/SyncOperation';
import type { Contact } from '../../types/Contact';

/** Exponential backoff delays in ms for retry attempts 1-5 */
const BACKOFF_DELAYS_MS = [1000, 2000, 4000, 8000, 16000] as const;

/** Pending retry timers tracked per operation ID for cleanup on success/failure/app-close */
const pendingRetryTimers = new Map<string, ReturnType<typeof setTimeout>>();

/**
 * Calculate exponential backoff delay for a given retry count (0-based).
 * @param retryCount - Current retry attempt (0-based index into BACKOFF_DELAYS_MS)
 * @returns Delay in milliseconds: 1000, 2000, 4000, 8000, 16000
 */
export const calculateBackoff = (retryCount: number): number =>
  BACKOFF_DELAYS_MS[retryCount] ?? BACKOFF_DELAYS_MS[BACKOFF_DELAYS_MS.length - 1];

/**
 * Determine whether an error is retryable based on its type and message content.
 * Retryable: network errors, timeouts, 5xx, 429
 * Non-retryable: 4xx (except 429) including 400, 401, 403, 404, 412, 422
 * @param errorMessage - Error message string from the API response
 * @param errorDetails - Optional error details object (may be an Error instance)
 * @returns true if the error is transient and should be retried
 */
export function isRetryableError(errorMessage: string, errorDetails?: unknown): boolean {
  // Network/fetch failures (TypeError thrown by fetch)
  if (errorDetails instanceof TypeError) {
    return true;
  }

  const msg = (errorMessage || '').toLowerCase();

  // Explicit network/timeout indicators
  if (msg.includes('timeout') || msg.includes('network')) {
    return true;
  }

  // Check for HTTP status codes in the error message
  const statusMatch = msg.match(/(\b|status[:\s]+)(\d{3})\b/);
  if (statusMatch) {
    const status = parseInt(statusMatch[2], 10);
    if (status === 429) {
      return true;
    }
    if (status >= 400 && status < 500) {
      return false;
    }
    if (status >= 500) {
      return true;
    }
  }

  // Default: retry unknown errors (transient until proven otherwise)
  return true;
}

/**
 * Determine whether an error message indicates a 412 Precondition Failed (etag conflict).
 * Story 3.7: Conflict detection for concurrent edits.
 * @param errorMessage - Error message string from the API response
 * @returns true if the error is a 412 conflict
 */
export function is412Error(errorMessage: string): boolean {
  const msg = (errorMessage || '').toLowerCase();
  return msg.includes('412') || msg.includes('precondition failed');
}

/**
 * Extract a specific field value from a Contact object by field path.
 * Story 3.7: Used to read the remote value on conflict resolution.
 * @param contact - The contact object to extract from
 * @param fieldPath - The field path (e.g., "names", "phoneNumbers")
 * @returns The field value, or null if not found
 */
export function extractFieldValue(contact: Contact, fieldPath: string): unknown {
  return (contact as Record<string, unknown>)[fieldPath] ?? null;
}

/**
 * Cancel a pending retry timer for a specific operation.
 * @param operationId - The sync operation ID whose timer should be cancelled
 */
export function cancelRetryTimer(operationId: string): void {
  const timer = pendingRetryTimers.get(operationId);
  if (timer !== undefined) {
    clearTimeout(timer);
    pendingRetryTimers.delete(operationId);
  }
}

/**
 * Cancel all pending retry timers (for app teardown / component unmount).
 */
export function cancelAllRetryTimers(): void {
  for (const [operationId, timer] of pendingRetryTimers.entries()) {
    clearTimeout(timer);
    pendingRetryTimers.delete(operationId);
  }
}

/**
 * Get the pending retry timers map (for testing purposes only).
 * @returns The internal pendingRetryTimers Map
 */
export function getPendingRetryTimers(): Map<string, ReturnType<typeof setTimeout>> {
  return pendingRetryTimers;
}

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
    await executeApiCall(
      dispatch,
      getState,
      operationId,
      resourceName,
      fieldPath,
      newValue,
      oldValue,
    );
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

  const result = await GoogleContactsService.updateContactField(resourceName, fieldPath, newValue);

  if (result.success && result.data) {
    // Success: remove from queue, update contact with server-confirmed data
    cancelRetryTimer(operationId);
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
    // Failure path
    const errorMessage = result.error?.message || 'Unknown error';

    logger.warn(
      {
        context: 'syncThunks/executeApiCall',
        metadata: { operationId, resourceName, fieldPath, error: errorMessage },
      },
      'Field update failed',
    );

    // Story 3.7: Check for 412 Precondition Failed (etag conflict) FIRST
    // — do NOT dispatch operationFailed before this check to avoid incorrect retryCount increment
    if (is412Error(errorMessage)) {
      cancelRetryTimer(operationId);

      logger.warn(
        {
          context: 'syncThunks/executeApiCall',
          metadata: { operationId, resourceName, fieldPath },
        },
        'Conflict detected: 412 Precondition Failed',
      );

      // Fetch latest contact to get remote value for conflict resolution dialog
      const remoteResult = await GoogleContactsService.getContact(resourceName);
      let remoteValue: unknown = null;
      if (remoteResult.success && remoteResult.data) {
        remoteValue = extractFieldValue(remoteResult.data, fieldPath);
      }

      dispatch(operationConflict({ id: operationId, remoteValue }));
      return;
    }

    // Now dispatch operationFailed for non-412 errors
    dispatch(operationFailed({ id: operationId, error: errorMessage }));

    // Classify error: non-retryable errors fail immediately
    const retryable = isRetryableError(errorMessage, result.error?.details);

    if (!retryable) {
      // Non-retryable: permanent failure, rollback immediately
      cancelRetryTimer(operationId);

      logger.warn(
        {
          context: 'syncThunks/executeApiCall',
          metadata: { operationId, errorMessage },
        },
        'Non-retryable error, marking as permanent failure',
      );

      const existingContact = getState().contacts?.contacts?.entities[resourceName];
      if (existingContact) {
        const rolledBackContact: Contact = {
          ...existingContact,
          [fieldPath]: oldValue,
        };
        dispatch(contactUpdated(rolledBackContact));
      }
      return;
    }

    // Check retry eligibility using maxRetries from state
    const currentState = getState();
    const syncQueueState = currentState.contacts?.syncQueue;
    const retryCount = syncQueueState?.retryCount[operationId] ?? 0;
    const maxRetries = syncQueueState?.maxRetries ?? 5;

    if (retryCount < maxRetries) {
      const delayIndex = Math.max(0, retryCount - 1);
      const backoffMs =
        BACKOFF_DELAYS_MS[delayIndex] ?? BACKOFF_DELAYS_MS[BACKOFF_DELAYS_MS.length - 1];

      logger.info(
        {
          context: 'syncThunks/executeApiCall',
          metadata: { operationId, retryCount, backoffMs },
        },
        'Scheduling retry with backoff',
      );

      // Schedule retry with tracked timer
      await new Promise<void>((resolve) => {
        const timer = setTimeout(() => {
          pendingRetryTimers.delete(operationId);
          resolve();
        }, backoffMs);
        pendingRetryTimers.set(operationId, timer);
      });

      // Reset to pending and re-execute
      dispatch(retryOperation(operationId));
      await executeApiCall(
        dispatch,
        getState,
        operationId,
        resourceName,
        fieldPath,
        newValue,
        oldValue,
      );
    } else {
      // Max retries exceeded: rollback optimistic update
      cancelRetryTimer(operationId);

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
export const processQueue = createAsyncThunk<void, void, { state: RootState }>(
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
