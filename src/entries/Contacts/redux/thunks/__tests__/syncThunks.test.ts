/**
 * @fileoverview Sync orchestrator thunks tests
 * @module Contacts/redux/thunks/__tests__/syncThunks.test
 * @jest-environment node
 */

import { executeFieldUpdate, processQueue, calculateBackoff } from '../syncThunks';
import { GoogleContactsService } from '../../../services/GoogleContactsService';
import {
  addToQueue,
  operationStarted,
  operationSuccess,
  operationFailed,
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
      maxRetries: 3,
    },
  },
});

describe('syncThunks', () => {
  let dispatch: jest.Mock;
  let getState: jest.Mock;

  beforeEach(() => {
    jest.clearAllMocks();
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
      getState.mockReturnValue(createMockState({
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
          retryCount: { [mockUUID]: 3 },
          maxRetries: 3,
        },
      }));

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

      getState.mockReturnValue(createMockState({
        contacts: { 'people/c123': { ...mockContact, names: [{ givenName: 'Jane', familyName: 'Doe', displayName: 'Jane Doe' }] } },
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
          retryCount: { [mockUUID]: 3 },
          maxRetries: 3,
        },
      }));

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
          maxRetries: 3,
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

      // State where retryCount has NO entry for this operation ID (undefined → fallback to 0)
      getState.mockReturnValue(createMockState({
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
          retryCount: {}, // No entry for mockUUID — hits the ?? 0 fallback
          maxRetries: 3,
        },
      }));

      const thunkPromise = executeFieldUpdate({
        resourceName: 'people/c123',
        fieldPath: 'names',
        newValue: [{ givenName: 'Jane' }],
        oldValue: [{ givenName: 'John' }],
      })(dispatch, getState, undefined);

      await jest.advanceTimersByTimeAsync(5000);
      await thunkPromise;

      // Should have retried (retryCount 0 < MAX_RETRIES 3)
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

      getState.mockReturnValue(createMockState({
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
          retryCount: { [mockUUID]: 3 },
          maxRetries: 3,
        },
      }));

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

      getState.mockReturnValue(createMockState({
        syncQueue: {
          ids: ['op-pending'],
          entities: { 'op-pending': pendingOp },
          retryCount: { 'op-pending': 3 },
          maxRetries: 3,
        },
      }));

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

      getState.mockReturnValue(createMockState({
        syncQueue: {
          ids: ['op-in-progress'],
          entities: { 'op-in-progress': inProgressOp },
          retryCount: {},
          maxRetries: 3,
        },
      }));

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

      getState.mockReturnValue(createMockState({
        syncQueue: {
          ids: ['op-1', 'op-2'],
          entities: { 'op-1': op1, 'op-2': op2 },
          retryCount: { 'op-1': 3, 'op-2': 3 },
          maxRetries: 3,
        },
      }));

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
});
