/**
 * @fileoverview Sync orchestrator thunks tests
 * @module Contacts/redux/thunks/__tests__/syncThunks.test
 * @jest-environment node
 *
 * Story 3.3: Original tests for sync orchestration
 * Story 3.6: Extended tests for 5-level backoff, error classification, timer tracking
 */

import {
  executeFieldUpdate,
  processQueue,
  calculateBackoff,
  isRetryableError,
  is412Error,
  extractFieldValue,
  cancelRetryTimer,
  cancelAllRetryTimers,
  getPendingRetryTimers,
} from '../syncThunks';
import { GoogleContactsService } from '../../../services/GoogleContactsService';
import {
  addToQueue,
  operationStarted,
  operationSuccess,
  operationFailed,
  operationConflict,
  retryOperation,
} from '../../slices/syncQueue/syncQueueSlice';
import { contactUpdated } from '../../slices/contacts/contactsSlice';
import type { RootState } from '../../../types/store';
import type { Contact } from '../../../types/Contact';
import type { SyncOperation } from '../../../types/SyncOperation';

// Mock GoogleContactsService
jest.mock('../../../services/GoogleContactsService');

// Mock crypto.randomUUID
const mockUUID = 'test-uuid-1234';
Object.defineProperty(globalThis, 'crypto', {
  value: { randomUUID: jest.fn(() => mockUUID) },
});

// Mock timers for backoff delay testing
jest.useFakeTimers();

const mockContact: Contact = {
  resourceName: 'people/c123',
  etag: 'etag-1',
  names: [{ givenName: 'John', familyName: 'Doe', displayName: 'John Doe' }],
  phoneNumbers: [{ value: '555-0100' }],
  emailAddresses: [{ value: 'john@example.com' }],
};

const mockUpdatedContact: Contact = {
  ...mockContact,
  names: [{ givenName: 'Jane', familyName: 'Doe', displayName: 'Jane Doe' }],
};

const createMockState = (overrides?: {
  contacts?: Record<string, Contact>;
  syncQueue?: {
    ids: string[];
    entities: Record<string, SyncOperation>;
    retryCount: Record<string, number>;
    maxRetries: number;
  };
}): RootState => ({
  contacts: {
    contacts: {
      ids: Object.keys(overrides?.contacts || { 'people/c123': mockContact }),
      entities: overrides?.contacts || { 'people/c123': mockContact },
      isLoading: false,
      lastFetched: null,
      error: null,
    },
    syncQueue: overrides?.syncQueue || {
      ids: [],
      entities: {},
      retryCount: {},
      maxRetries: 5,
    },
  },
});

describe('syncThunks', () => {
  let dispatch: jest.Mock;
  let getState: jest.Mock;

  beforeEach(() => {
    jest.clearAllMocks();
    cancelAllRetryTimers();
    dispatch = jest.fn((action) => {
      if (typeof action === 'function') {
        return action(dispatch, getState, undefined);
      }
      return action;
    });
    getState = jest.fn(() => createMockState());
  });

  describe('calculateBackoff', () => {
    it('calculateBackoff_Should_Return1000ms_When_RetryCount0', () => {
      expect(calculateBackoff(0)).toBe(1000);
    });

    it('calculateBackoff_Should_Return2000ms_When_RetryCount1', () => {
      expect(calculateBackoff(1)).toBe(2000);
    });

    it('calculateBackoff_Should_Return4000ms_When_RetryCount2', () => {
      expect(calculateBackoff(2)).toBe(4000);
    });

    it('calculateBackoff_Should_Return8000ms_When_RetryCount3', () => {
      expect(calculateBackoff(3)).toBe(8000);
    });

    it('calculateBackoff_Should_Return16000ms_When_RetryCount4', () => {
      expect(calculateBackoff(4)).toBe(16000);
    });

    it('calculateBackoff_Should_Return16000ms_When_RetryCountExceedsBounds', () => {
      expect(calculateBackoff(10)).toBe(16000);
    });
  });

  describe('isRetryableError', () => {
    it('isRetryableError_Should_ReturnTrue_When_NetworkError', () => {
      expect(isRetryableError('Network error occurred')).toBe(true);
    });

    it('isRetryableError_Should_ReturnTrue_When_TimeoutError', () => {
      expect(isRetryableError('Request timeout')).toBe(true);
    });

    it('isRetryableError_Should_ReturnTrue_When_TypeError', () => {
      expect(isRetryableError('Failed to fetch', new TypeError('Failed to fetch'))).toBe(true);
    });

    it('isRetryableError_Should_ReturnTrue_When_5xxError', () => {
      expect(isRetryableError('Server returned status 500')).toBe(true);
      expect(isRetryableError('Request failed with status: 502')).toBe(true);
      expect(isRetryableError('HTTP 503 Service Unavailable')).toBe(true);
    });

    it('isRetryableError_Should_ReturnTrue_When_429Error', () => {
      expect(isRetryableError('Rate limited: 429 Too Many Requests')).toBe(true);
    });

    it('isRetryableError_Should_ReturnFalse_When_400Error', () => {
      expect(isRetryableError('Bad request: 400')).toBe(false);
    });

    it('isRetryableError_Should_ReturnFalse_When_401Error', () => {
      expect(isRetryableError('Unauthorized: 401')).toBe(false);
    });

    it('isRetryableError_Should_ReturnFalse_When_403Error', () => {
      expect(isRetryableError('Forbidden: 403')).toBe(false);
    });

    it('isRetryableError_Should_ReturnFalse_When_404Error', () => {
      expect(isRetryableError('Not found: 404')).toBe(false);
    });

    it('isRetryableError_Should_ReturnFalse_When_422Error', () => {
      expect(isRetryableError('Unprocessable entity: 422')).toBe(false);
    });

    it('isRetryableError_Should_ReturnTrue_When_UnknownError', () => {
      expect(isRetryableError('Something went wrong')).toBe(true);
    });
  });

  describe('executeFieldUpdate', () => {
    it('executeFieldUpdate_Should_AddToQueueAndApplyOptimisticUpdate_When_Called', async () => {
      (GoogleContactsService.updateContactField as jest.Mock).mockResolvedValue({
        success: true,
        data: mockUpdatedContact,
      });

      const thunk = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane', familyName: 'Doe', displayName: 'Jane Doe' }],
        oldValue: [{ givenName: 'John', familyName: 'Doe', displayName: 'John Doe' }],
      });

      await thunk(dispatch, getState, undefined);

      // Should dispatch addToQueue with pending operation
      const addToQueueCall = dispatch.mock.calls.find(
        (call: unknown[]) => call[0]?.type === addToQueue.type,
      );
      expect(addToQueueCall).toBeDefined();
      expect(addToQueueCall[0].payload.id).toBe(mockUUID);
      expect(addToQueueCall[0].payload.status).toBe('pending');
      expect(addToQueueCall[0].payload.resourceName).toBe('people/c123');
      expect(addToQueueCall[0].payload.fieldPath).toBe('names');

      // Should dispatch contactUpdated for optimistic update
      const optimisticCall = dispatch.mock.calls.find(
        (call: unknown[]) => call[0]?.type === contactUpdated.type,
      );
      expect(optimisticCall).toBeDefined();
    });

    it('executeFieldUpdate_Should_DispatchOperationStarted_When_ApiCallBegins', async () => {
      (GoogleContactsService.updateContactField as jest.Mock).mockResolvedValue({
        success: true,
        data: mockUpdatedContact,
      });

      const thunk = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      });

      await thunk(dispatch, getState, undefined);

      const startedCall = dispatch.mock.calls.find(
        (call: unknown[]) => call[0]?.type === operationStarted.type,
      );
      expect(startedCall).toBeDefined();
      expect(startedCall[0].payload).toBe(mockUUID);
    });

    it('executeFieldUpdate_Should_DispatchOperationSuccess_When_ApiSucceeds', async () => {
      (GoogleContactsService.updateContactField as jest.Mock).mockResolvedValue({
        success: true,
        data: mockUpdatedContact,
      });

      const thunk = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      });

      await thunk(dispatch, getState, undefined);

      const successCall = dispatch.mock.calls.find(
        (call: unknown[]) => call[0]?.type === operationSuccess.type,
      );
      expect(successCall).toBeDefined();
      expect(successCall[0].payload).toBe(mockUUID);
    });

    it('executeFieldUpdate_Should_UpdateContactWithServerData_When_ApiSucceeds', async () => {
      (GoogleContactsService.updateContactField as jest.Mock).mockResolvedValue({
        success: true,
        data: mockUpdatedContact,
      });

      const thunk = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      });

      await thunk(dispatch, getState, undefined);

      // Should dispatch contactUpdated with server response (after success)
      const contactUpdatedCalls = dispatch.mock.calls.filter(
        (call: unknown[]) => call[0]?.type === contactUpdated.type,
      );
      // First call: optimistic, second call: server-confirmed
      expect(contactUpdatedCalls.length).toBe(2);
      expect(contactUpdatedCalls[1][0].payload).toEqual(mockUpdatedContact);
    });

    it('executeFieldUpdate_Should_DispatchOperationFailed_When_ApiFails', async () => {
      (GoogleContactsService.updateContactField as jest.Mock).mockResolvedValue({
        success: false,
        error: { code: 'UPDATE_FIELD_FAILED', message: 'Network error' },
      });

      // State with max retries already exceeded to prevent retry loop
      getState.mockReturnValue(
        createMockState({
          syncQueue: {
            ids: [mockUUID],
            entities: {
              [mockUUID]: {
                id: mockUUID,
                resourceName: 'people/c123',
                fieldPath: 'names',
                newValue: [{ givenName: 'Jane' }],
                oldValue: [{ givenName: 'John' }],
                status: 'in-progress',
                timestamp: new Date().toISOString(),
                error: null,
              },
            },
            retryCount: { [mockUUID]: 5 },
            maxRetries: 5,
          },
        }),
      );

      const thunk = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      });

      await thunk(dispatch, getState, undefined);

      const failedCall = dispatch.mock.calls.find(
        (call: unknown[]) => call[0]?.type === operationFailed.type,
      );
      expect(failedCall).toBeDefined();
      expect(failedCall[0].payload.id).toBe(mockUUID);
      expect(failedCall[0].payload.error).toBe('Network error');
    });

    it('executeFieldUpdate_Should_RollbackOptimisticUpdate_When_MaxRetriesExceeded', async () => {
      (GoogleContactsService.updateContactField as jest.Mock).mockResolvedValue({
        success: false,
        error: { code: 'UPDATE_FIELD_FAILED', message: 'Server error' },
      });

      getState.mockReturnValue(
        createMockState({
          contacts: {
            'people/c123': {
              ...mockContact,
              names: [{ givenName: 'Jane', familyName: 'Doe', displayName: 'Jane Doe' }],
            },
          },
          syncQueue: {
            ids: [mockUUID],
            entities: {
              [mockUUID]: {
                id: mockUUID,
                resourceName: 'people/c123',
                fieldPath: 'names',
                newValue: [{ givenName: 'Jane' }],
                oldValue: [{ givenName: 'John' }],
                status: 'failed',
                timestamp: new Date().toISOString(),
                error: 'Server error',
              },
            },
            retryCount: { [mockUUID]: 5 },
            maxRetries: 5,
          },
        }),
      );

      const thunk = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      });

      await thunk(dispatch, getState, undefined);

      // Should dispatch contactUpdated with OLD value (rollback)
      const contactUpdatedCalls = dispatch.mock.calls.filter(
        (call: unknown[]) => call[0]?.type === contactUpdated.type,
      );
      const lastContactUpdate = contactUpdatedCalls[contactUpdatedCalls.length - 1];
      expect(lastContactUpdate[0].payload.names).toEqual([{ givenName: 'John' }]);
    });

    it('executeFieldUpdate_Should_RetryWithBackoff_When_UnderMaxRetries', async () => {
      let callCount = 0;
      (GoogleContactsService.updateContactField as jest.Mock).mockImplementation(() => {
        callCount++;
        if (callCount === 1) {
          return Promise.resolve({
            success: false,
            error: { code: 'UPDATE_FIELD_FAILED', message: 'Temporary error' },
          });
        }
        return Promise.resolve({
          success: true,
          data: mockUpdatedContact,
        });
      });

      // First call: retryCount=0 (will be 1 after failure), under max
      const stateAfterFailure = createMockState({
        syncQueue: {
          ids: [mockUUID],
          entities: {
            [mockUUID]: {
              id: mockUUID,
              resourceName: 'people/c123',
              fieldPath: 'names',
              newValue: [{ givenName: 'Jane' }],
              oldValue: [{ givenName: 'John' }],
              status: 'failed',
              timestamp: new Date().toISOString(),
              error: 'Temporary error',
            },
          },
          retryCount: { [mockUUID]: 1 },
          maxRetries: 5,
        },
      });
      getState.mockReturnValue(stateAfterFailure);

      const thunkPromise = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      })(dispatch, getState, undefined);

      // Advance timers to trigger the backoff delay
      await jest.advanceTimersByTimeAsync(5000);
      await thunkPromise;

      // Should dispatch retryOperation
      const retryCalls = dispatch.mock.calls.filter(
        (call: unknown[]) => call[0]?.type === retryOperation.type,
      );
      expect(retryCalls.length).toBeGreaterThanOrEqual(1);

      // API should be called at least twice (initial + retry)
      expect(GoogleContactsService.updateContactField).toHaveBeenCalledTimes(2);
    });

    it('executeFieldUpdate_Should_RetryFromZero_When_RetryCountNotInState', async () => {
      let callCount = 0;
      (GoogleContactsService.updateContactField as jest.Mock).mockImplementation(() => {
        callCount++;
        if (callCount === 1) {
          return Promise.resolve({
            success: false,
            error: { code: 'UPDATE_FIELD_FAILED', message: 'Temporary error' },
          });
        }
        return Promise.resolve({ success: true, data: mockUpdatedContact });
      });

      // State where retryCount has NO entry for this operation ID (undefined -> fallback to 0)
      getState.mockReturnValue(
        createMockState({
          syncQueue: {
            ids: [mockUUID],
            entities: {
              [mockUUID]: {
                id: mockUUID,
                resourceName: 'people/c123',
                fieldPath: 'names',
                newValue: [{ givenName: 'Jane' }],
                oldValue: [{ givenName: 'John' }],
                status: 'failed',
                timestamp: new Date().toISOString(),
                error: null,
              },
            },
            retryCount: {}, // No entry for mockUUID -- hits the ?? 0 fallback
            maxRetries: 5,
          },
        }),
      );

      const thunkPromise = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      })(dispatch, getState, undefined);

      await jest.advanceTimersByTimeAsync(5000);
      await thunkPromise;

      // Should have retried (retryCount 0 < maxRetries 5)
      const retryCalls = dispatch.mock.calls.filter(
        (call: unknown[]) => call[0]?.type === retryOperation.type,
      );
      expect(retryCalls.length).toBeGreaterThanOrEqual(1);
      expect(GoogleContactsService.updateContactField).toHaveBeenCalledTimes(2);
    });

    it('executeFieldUpdate_Should_UseUnknownError_When_ErrorObjectMissing', async () => {
      (GoogleContactsService.updateContactField as jest.Mock).mockResolvedValue({
        success: false,
        // No error object at all
      });

      getState.mockReturnValue(
        createMockState({
          syncQueue: {
            ids: [mockUUID],
            entities: {
              [mockUUID]: {
                id: mockUUID,
                resourceName: 'people/c123',
                fieldPath: 'names',
                newValue: [{ givenName: 'Jane' }],
                oldValue: [{ givenName: 'John' }],
                status: 'in-progress',
                timestamp: new Date().toISOString(),
                error: null,
              },
            },
            retryCount: { [mockUUID]: 5 },
            maxRetries: 5,
          },
        }),
      );

      const thunk = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      });

      await thunk(dispatch, getState, undefined);

      const failedCall = dispatch.mock.calls.find(
        (call: unknown[]) => call[0]?.type === operationFailed.type,
      );
      expect(failedCall).toBeDefined();
      expect(failedCall[0].payload.error).toBe('Unknown error');
    });

    it('executeFieldUpdate_Should_SkipOptimisticUpdate_When_ContactNotInState', async () => {
      getState.mockReturnValue(createMockState({ contacts: {} }));

      (GoogleContactsService.updateContactField as jest.Mock).mockResolvedValue({
        success: true,
        data: mockUpdatedContact,
      });

      const thunk = executeFieldUpdate({
        resourceName: 'people/nonexistent',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      });

      await thunk(dispatch, getState, undefined);

      // addToQueue should still be called
      const addToQueueCall = dispatch.mock.calls.find(
        (call: unknown[]) => call[0]?.type === addToQueue.type,
      );
      expect(addToQueueCall).toBeDefined();

      // contactUpdated for optimistic should NOT be called (no existing contact)
      // but will be called once for server response
      const contactUpdatedCalls = dispatch.mock.calls.filter(
        (call: unknown[]) => call[0]?.type === contactUpdated.type,
      );
      expect(contactUpdatedCalls.length).toBe(1); // Only server response
    });

    it('executeFieldUpdate_Should_FailImmediately_When_NonRetryable400Error', async () => {
      (GoogleContactsService.updateContactField as jest.Mock).mockResolvedValue({
        success: false,
        error: { code: 'UPDATE_FIELD_FAILED', message: 'Bad request: 400 Invalid data' },
      });

      getState.mockReturnValue(
        createMockState({
          syncQueue: {
            ids: [mockUUID],
            entities: {
              [mockUUID]: {
                id: mockUUID,
                resourceName: 'people/c123',
                fieldPath: 'names',
                newValue: [{ givenName: 'Jane' }],
                oldValue: [{ givenName: 'John' }],
                status: 'in-progress',
                timestamp: new Date().toISOString(),
                error: null,
              },
            },
            retryCount: { [mockUUID]: 1 },
            maxRetries: 5,
          },
        }),
      );

      const thunk = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      });

      await thunk(dispatch, getState, undefined);

      // Should dispatch operationFailed but NOT retryOperation
      const failedCall = dispatch.mock.calls.find(
        (call: unknown[]) => call[0]?.type === operationFailed.type,
      );
      expect(failedCall).toBeDefined();

      const retryCalls = dispatch.mock.calls.filter(
        (call: unknown[]) => call[0]?.type === retryOperation.type,
      );
      expect(retryCalls).toHaveLength(0);

      // Should rollback optimistic update immediately
      const contactUpdatedCalls = dispatch.mock.calls.filter(
        (call: unknown[]) => call[0]?.type === contactUpdated.type,
      );
      const rollbackCall = contactUpdatedCalls[contactUpdatedCalls.length - 1];
      expect(rollbackCall[0].payload.names).toEqual([{ givenName: 'John' }]);

      // API should only be called once (no retry)
      expect(GoogleContactsService.updateContactField).toHaveBeenCalledTimes(1);
    });

    it('executeFieldUpdate_Should_Retry_When_429RateLimitError', async () => {
      let callCount = 0;
      (GoogleContactsService.updateContactField as jest.Mock).mockImplementation(() => {
        callCount++;
        if (callCount === 1) {
          return Promise.resolve({
            success: false,
            error: { code: 'UPDATE_FIELD_FAILED', message: 'Rate limited: 429 Too Many Requests' },
          });
        }
        return Promise.resolve({
          success: true,
          data: mockUpdatedContact,
        });
      });

      getState.mockReturnValue(
        createMockState({
          syncQueue: {
            ids: [mockUUID],
            entities: {
              [mockUUID]: {
                id: mockUUID,
                resourceName: 'people/c123',
                fieldPath: 'names',
                newValue: [{ givenName: 'Jane' }],
                oldValue: [{ givenName: 'John' }],
                status: 'failed',
                timestamp: new Date().toISOString(),
                error: null,
              },
            },
            retryCount: { [mockUUID]: 1 },
            maxRetries: 5,
          },
        }),
      );

      const thunkPromise = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      })(dispatch, getState, undefined);

      // Advance timers past backoff delay
      await jest.advanceTimersByTimeAsync(5000);
      await thunkPromise;

      // Should have retried (429 is retryable)
      const retryCalls = dispatch.mock.calls.filter(
        (call: unknown[]) => call[0]?.type === retryOperation.type,
      );
      expect(retryCalls.length).toBeGreaterThanOrEqual(1);

      // API called at least twice (initial + retry)
      expect(GoogleContactsService.updateContactField).toHaveBeenCalledTimes(2);
    });

    it('executeFieldUpdate_Should_DispatchOperationSuccess_When_RetrySucceeds', async () => {
      let callCount = 0;
      (GoogleContactsService.updateContactField as jest.Mock).mockImplementation(() => {
        callCount++;
        if (callCount <= 2) {
          return Promise.resolve({
            success: false,
            error: { code: 'UPDATE_FIELD_FAILED', message: 'Temporary server error' },
          });
        }
        return Promise.resolve({
          success: true,
          data: mockUpdatedContact,
        });
      });

      // Simulate state after 2 failures (retryCount=2), still under max 5
      getState.mockReturnValue(
        createMockState({
          syncQueue: {
            ids: [mockUUID],
            entities: {
              [mockUUID]: {
                id: mockUUID,
                resourceName: 'people/c123',
                fieldPath: 'names',
                newValue: [{ givenName: 'Jane' }],
                oldValue: [{ givenName: 'John' }],
                status: 'failed',
                timestamp: new Date().toISOString(),
                error: 'Temporary server error',
              },
            },
            retryCount: { [mockUUID]: 2 },
            maxRetries: 5,
          },
        }),
      );

      const thunkPromise = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      })(dispatch, getState, undefined);

      // Advance timers enough for multiple retry backoffs
      await jest.advanceTimersByTimeAsync(30000);
      await thunkPromise;

      // Should dispatch operationSuccess at some point
      const successCalls = dispatch.mock.calls.filter(
        (call: unknown[]) => call[0]?.type === operationSuccess.type,
      );
      expect(successCalls.length).toBeGreaterThanOrEqual(1);
    });

    it('executeFieldUpdate_Should_EnforceMaxRetries5_When_AllRetriesExhausted', async () => {
      (GoogleContactsService.updateContactField as jest.Mock).mockResolvedValue({
        success: false,
        error: { code: 'UPDATE_FIELD_FAILED', message: 'Persistent server error' },
      });

      getState.mockReturnValue(
        createMockState({
          contacts: { 'people/c123': mockContact },
          syncQueue: {
            ids: [mockUUID],
            entities: {
              [mockUUID]: {
                id: mockUUID,
                resourceName: 'people/c123',
                fieldPath: 'names',
                newValue: [{ givenName: 'Jane' }],
                oldValue: [{ givenName: 'John' }],
                status: 'failed',
                timestamp: new Date().toISOString(),
                error: 'Persistent server error',
              },
            },
            retryCount: { [mockUUID]: 5 },
            maxRetries: 5,
          },
        }),
      );

      const thunk = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      });

      await thunk(dispatch, getState, undefined);

      // Should NOT dispatch retryOperation (max reached)
      const retryCalls = dispatch.mock.calls.filter(
        (call: unknown[]) => call[0]?.type === retryOperation.type,
      );
      expect(retryCalls).toHaveLength(0);

      // Should rollback
      const contactUpdatedCalls = dispatch.mock.calls.filter(
        (call: unknown[]) => call[0]?.type === contactUpdated.type,
      );
      const lastUpdate = contactUpdatedCalls[contactUpdatedCalls.length - 1];
      expect(lastUpdate[0].payload.names).toEqual([{ givenName: 'John' }]);

      // API called only once (no retry after max exceeded)
      expect(GoogleContactsService.updateContactField).toHaveBeenCalledTimes(1);
    });

    it('executeFieldUpdate_Should_Apply5LevelBackoffDelays_When_Retrying', async () => {
      // Track setTimeout calls to verify backoff delays
      const setTimeoutSpy = jest.spyOn(globalThis, 'setTimeout');

      let callCount = 0;
      (GoogleContactsService.updateContactField as jest.Mock).mockImplementation(() => {
        callCount++;
        if (callCount === 1) {
          return Promise.resolve({
            success: false,
            error: { code: 'UPDATE_FIELD_FAILED', message: 'Temporary error' },
          });
        }
        return Promise.resolve({
          success: true,
          data: mockUpdatedContact,
        });
      });

      // retryCount = 1 means first retry, which uses BACKOFF_DELAYS_MS[0] = 1000ms
      getState.mockReturnValue(
        createMockState({
          syncQueue: {
            ids: [mockUUID],
            entities: {
              [mockUUID]: {
                id: mockUUID,
                resourceName: 'people/c123',
                fieldPath: 'names',
                newValue: [{ givenName: 'Jane' }],
                oldValue: [{ givenName: 'John' }],
                status: 'failed',
                timestamp: new Date().toISOString(),
                error: 'Temporary error',
              },
            },
            retryCount: { [mockUUID]: 1 },
            maxRetries: 5,
          },
        }),
      );

      const thunkPromise = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      })(dispatch, getState, undefined);

      await jest.advanceTimersByTimeAsync(5000);
      await thunkPromise;

      // Verify setTimeout was called with correct backoff delay (1000ms for retryCount=1)
      const backoffCall = setTimeoutSpy.mock.calls.find((call) => call[1] === 1000);
      expect(backoffCall).toBeDefined();

      setTimeoutSpy.mockRestore();
    });
  });

  describe('timer tracking', () => {
    it('cancelRetryTimer_Should_ClearTimer_When_TimerExists', () => {
      const timers = getPendingRetryTimers();
      const timer = setTimeout(() => {
        // noop
      }, 10000);
      timers.set('op-1', timer);

      expect(timers.has('op-1')).toBe(true);

      cancelRetryTimer('op-1');

      expect(timers.has('op-1')).toBe(false);
    });

    it('cancelRetryTimer_Should_DoNothing_When_NoTimerExists', () => {
      // Should not throw
      cancelRetryTimer('nonexistent-op');
      expect(getPendingRetryTimers().has('nonexistent-op')).toBe(false);
    });

    it('cancelAllRetryTimers_Should_ClearAllTimers_When_MultipleExist', () => {
      const timers = getPendingRetryTimers();
      timers.set(
        'op-1',
        setTimeout(() => {
          /* noop */
        }, 10000),
      );
      timers.set(
        'op-2',
        setTimeout(() => {
          /* noop */
        }, 10000),
      );
      timers.set(
        'op-3',
        setTimeout(() => {
          /* noop */
        }, 10000),
      );

      expect(timers.size).toBe(3);

      cancelAllRetryTimers();

      expect(timers.size).toBe(0);
    });

    it('executeFieldUpdate_Should_ClearTimer_When_ApiSucceeds', async () => {
      (GoogleContactsService.updateContactField as jest.Mock).mockResolvedValue({
        success: true,
        data: mockUpdatedContact,
      });

      // Pre-set a timer for this operation
      const timers = getPendingRetryTimers();
      timers.set(
        mockUUID,
        setTimeout(() => {
          /* noop */
        }, 10000),
      );
      expect(timers.has(mockUUID)).toBe(true);

      const thunk = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      });

      await thunk(dispatch, getState, undefined);

      // Timer should be cleared on success
      expect(timers.has(mockUUID)).toBe(false);
    });
  });

  describe('processQueue', () => {
    it('processQueue_Should_ProcessNextPending_When_NothingInProgress', async () => {
      const pendingOp: SyncOperation = {
        id: 'op-pending',
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
        status: 'pending',
        timestamp: new Date().toISOString(),
        error: null,
      };

      getState.mockReturnValue(
        createMockState({
          syncQueue: {
            ids: ['op-pending'],
            entities: { 'op-pending': pendingOp },
            retryCount: { 'op-pending': 5 },
            maxRetries: 5,
          },
        }),
      );

      (GoogleContactsService.updateContactField as jest.Mock).mockResolvedValue({
        success: true,
        data: mockUpdatedContact,
      });

      const thunk = processQueue();
      await thunk(dispatch, getState, undefined);

      // Should dispatch operationStarted for the pending operation
      const startedCall = dispatch.mock.calls.find(
        (call: unknown[]) => call[0]?.type === operationStarted.type,
      );
      expect(startedCall).toBeDefined();
      expect(startedCall[0].payload).toBe('op-pending');
    });

    it('processQueue_Should_Skip_When_OperationAlreadyInProgress', async () => {
      const inProgressOp: SyncOperation = {
        id: 'op-in-progress',
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
        status: 'in-progress',
        timestamp: new Date().toISOString(),
        error: null,
      };

      getState.mockReturnValue(
        createMockState({
          syncQueue: {
            ids: ['op-in-progress'],
            entities: { 'op-in-progress': inProgressOp },
            retryCount: {},
            maxRetries: 5,
          },
        }),
      );

      const thunk = processQueue();
      await thunk(dispatch, getState, undefined);

      // Should NOT call the API
      expect(GoogleContactsService.updateContactField).not.toHaveBeenCalled();
    });

    it('processQueue_Should_DoNothing_When_QueueEmpty', async () => {
      getState.mockReturnValue(createMockState());

      const thunk = processQueue();
      await thunk(dispatch, getState, undefined);

      // No API calls
      expect(GoogleContactsService.updateContactField).not.toHaveBeenCalled();

      // No operationStarted dispatched
      const startedCalls = dispatch.mock.calls.filter(
        (call: unknown[]) => call[0]?.type === operationStarted.type,
      );
      expect(startedCalls).toHaveLength(0);
    });

    it('processQueue_Should_ProcessFIFO_When_MultiplePending', async () => {
      const op1: SyncOperation = {
        id: 'op-1',
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: 'new1',
        oldValue: 'old1',
        status: 'pending',
        timestamp: '2026-02-11T10:00:00Z',
        error: null,
      };
      const op2: SyncOperation = {
        id: 'op-2',
        resourceName: 'people/c456',
        fieldPath: 'emailAddresses',
        newValue: 'new2',
        oldValue: 'old2',
        status: 'pending',
        timestamp: '2026-02-11T10:01:00Z',
        error: null,
      };

      getState.mockReturnValue(
        createMockState({
          syncQueue: {
            ids: ['op-1', 'op-2'],
            entities: { 'op-1': op1, 'op-2': op2 },
            retryCount: { 'op-1': 5, 'op-2': 5 },
            maxRetries: 5,
          },
        }),
      );

      (GoogleContactsService.updateContactField as jest.Mock).mockResolvedValue({
        success: true,
        data: mockUpdatedContact,
      });

      const thunk = processQueue();
      await thunk(dispatch, getState, undefined);

      // Should process op-1 first (FIFO)
      const startedCall = dispatch.mock.calls.find(
        (call: unknown[]) => call[0]?.type === operationStarted.type,
      );
      expect(startedCall[0].payload).toBe('op-1');

      // API called with op-1's data
      expect(GoogleContactsService.updateContactField).toHaveBeenCalledWith(
        'people/c123',
        'names',
        'new1',
      );
    });
  });

  describe('is412Error', () => {
    it('is412Error_Should_ReturnTrue_When_MessageContains412', () => {
      expect(is412Error('HTTP 412 Precondition Failed')).toBe(true);
    });

    it('is412Error_Should_ReturnTrue_When_MessageContainsPreconditionFailed', () => {
      expect(is412Error('Request failed: precondition failed')).toBe(true);
    });

    it('is412Error_Should_ReturnFalse_When_MessageDoesNotContain412', () => {
      expect(is412Error('Bad request: 400')).toBe(false);
    });

    it('is412Error_Should_ReturnFalse_When_EmptyMessage', () => {
      expect(is412Error('')).toBe(false);
    });
  });

  describe('extractFieldValue', () => {
    it('extractFieldValue_Should_ReturnFieldValue_When_FieldExists', () => {
      const contact = {
        resourceName: 'people/c123',
        names: [{ givenName: 'John' }],
      };
      expect(extractFieldValue(contact, 'names')).toEqual([{ givenName: 'John' }]);
    });

    it('extractFieldValue_Should_ReturnNull_When_FieldDoesNotExist', () => {
      const contact = {
        resourceName: 'people/c123',
      };
      expect(extractFieldValue(contact, 'phoneNumbers')).toBeNull();
    });
  });

  describe('conflict detection (Story 3.7)', () => {
    it('executeFieldUpdate_Should_DispatchOperationConflict_When_412Error', async () => {
      const remoteContact: Contact = {
        ...mockContact,
        names: [{ givenName: 'Remote', familyName: 'Doe', displayName: 'Remote Doe' }],
      };

      (GoogleContactsService.updateContactField as jest.Mock).mockResolvedValue({
        success: false,
        error: {
          code: 'UPDATE_FIELD_FAILED',
          message: 'HTTP 412 Precondition Failed',
        },
      });

      (GoogleContactsService.getContact as jest.Mock).mockResolvedValue({
        success: true,
        data: remoteContact,
      });

      const thunk = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      });

      await thunk(dispatch, getState, undefined);

      // Should dispatch operationConflict (not retryOperation)
      const conflictCall = dispatch.mock.calls.find(
        (call: unknown[]) => call[0]?.type === operationConflict.type,
      );
      expect(conflictCall).toBeDefined();
      expect(conflictCall[0].payload.id).toBe(mockUUID);
      expect(conflictCall[0].payload.remoteValue).toEqual([
        { givenName: 'Remote', familyName: 'Doe', displayName: 'Remote Doe' },
      ]);

      // Should NOT dispatch retryOperation
      const retryCalls = dispatch.mock.calls.filter(
        (call: unknown[]) => call[0]?.type === retryOperation.type,
      );
      expect(retryCalls).toHaveLength(0);
    });

    it('executeFieldUpdate_Should_DispatchOperationConflict_When_PreconditionFailedMessage', async () => {
      const remoteContact: Contact = {
        ...mockContact,
        emailAddresses: [{ value: 'remote@example.com' }],
      };

      (GoogleContactsService.updateContactField as jest.Mock).mockResolvedValue({
        success: false,
        error: {
          code: 'UPDATE_FIELD_FAILED',
          message: 'Request failed: precondition failed',
        },
      });

      (GoogleContactsService.getContact as jest.Mock).mockResolvedValue({
        success: true,
        data: remoteContact,
      });

      const thunk = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'emailAddresses',
        newValue: [{ value: 'new@example.com' }],
        oldValue: [{ value: 'old@example.com' }],
      });

      await thunk(dispatch, getState, undefined);

      const conflictCall = dispatch.mock.calls.find(
        (call: unknown[]) => call[0]?.type === operationConflict.type,
      );
      expect(conflictCall).toBeDefined();
      expect(conflictCall[0].payload.remoteValue).toEqual([{ value: 'remote@example.com' }]);
    });

    it('executeFieldUpdate_Should_SetRemoteValueNull_When_GetContactFails', async () => {
      (GoogleContactsService.updateContactField as jest.Mock).mockResolvedValue({
        success: false,
        error: {
          code: 'UPDATE_FIELD_FAILED',
          message: 'HTTP 412 Precondition Failed',
        },
      });

      (GoogleContactsService.getContact as jest.Mock).mockResolvedValue({
        success: false,
        error: { code: 'GET_CONTACT_FAILED', message: 'Network error' },
      });

      const thunk = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      });

      await thunk(dispatch, getState, undefined);

      const conflictCall = dispatch.mock.calls.find(
        (call: unknown[]) => call[0]?.type === operationConflict.type,
      );
      expect(conflictCall).toBeDefined();
      expect(conflictCall[0].payload.remoteValue).toBeNull();
    });

    it('executeFieldUpdate_Should_NotRetry_When_412ConflictDetected', async () => {
      (GoogleContactsService.updateContactField as jest.Mock).mockResolvedValue({
        success: false,
        error: {
          code: 'UPDATE_FIELD_FAILED',
          message: 'HTTP 412 Precondition Failed',
        },
      });

      (GoogleContactsService.getContact as jest.Mock).mockResolvedValue({
        success: true,
        data: mockContact,
      });

      // State with retries remaining — should still NOT retry on 412
      getState.mockReturnValue(
        createMockState({
          syncQueue: {
            ids: [mockUUID],
            entities: {
              [mockUUID]: {
                id: mockUUID,
                resourceName: 'people/c123',
                fieldPath: 'names',
                newValue: [{ givenName: 'Jane' }],
                oldValue: [{ givenName: 'John' }],
                status: 'in-progress',
                timestamp: new Date().toISOString(),
                error: null,
              },
            },
            retryCount: { [mockUUID]: 0 },
            maxRetries: 5,
          },
        }),
      );

      const thunk = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      });

      await thunk(dispatch, getState, undefined);

      // API should only be called once (no retry)
      expect(GoogleContactsService.updateContactField).toHaveBeenCalledTimes(1);

      // Should dispatch operationConflict, not retryOperation
      const conflictCall = dispatch.mock.calls.find(
        (call: unknown[]) => call[0]?.type === operationConflict.type,
      );
      expect(conflictCall).toBeDefined();

      const retryCalls = dispatch.mock.calls.filter(
        (call: unknown[]) => call[0]?.type === retryOperation.type,
      );
      expect(retryCalls).toHaveLength(0);
    });

    it('executeFieldUpdate_Should_NotRollback_When_412ConflictDetected', async () => {
      (GoogleContactsService.updateContactField as jest.Mock).mockResolvedValue({
        success: false,
        error: {
          code: 'UPDATE_FIELD_FAILED',
          message: 'HTTP 412 Precondition Failed',
        },
      });

      (GoogleContactsService.getContact as jest.Mock).mockResolvedValue({
        success: true,
        data: mockContact,
      });

      const thunk = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      });

      await thunk(dispatch, getState, undefined);

      // Should NOT rollback (user decides via dialog)
      // contactUpdated calls: 1 for optimistic update only (not a rollback)
      const contactUpdatedCalls = dispatch.mock.calls.filter(
        (call: unknown[]) => call[0]?.type === contactUpdated.type,
      );
      // Only the optimistic update, no rollback
      expect(contactUpdatedCalls.length).toBe(1);
    });

    it('executeFieldUpdate_Should_ClearRetryTimer_When_412ConflictDetected', async () => {
      (GoogleContactsService.updateContactField as jest.Mock).mockResolvedValue({
        success: false,
        error: {
          code: 'UPDATE_FIELD_FAILED',
          message: 'HTTP 412 Precondition Failed',
        },
      });

      (GoogleContactsService.getContact as jest.Mock).mockResolvedValue({
        success: true,
        data: mockContact,
      });

      // Pre-set a timer for this operation
      const timers = getPendingRetryTimers();
      timers.set(
        mockUUID,
        setTimeout(() => {
          /* noop */
        }, 10000),
      );
      expect(timers.has(mockUUID)).toBe(true);

      const thunk = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      });

      await thunk(dispatch, getState, undefined);

      // Timer should be cleared on conflict
      expect(timers.has(mockUUID)).toBe(false);
    });
  });
});
