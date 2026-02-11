/**
 * @fileoverview SyncQueueSlice Redux slice tests
 * @module Contacts/redux/slices/syncQueue/__tests__/syncQueueSlice.test
 * @jest-environment node
 */

import syncQueueReducer, {
  addToQueue,
  operationStarted,
  operationSuccess,
  operationFailed,
  retryOperation,
  clearQueue,
} from '../syncQueueSlice';
import type { SyncOperation, SyncQueueState } from '../../../../types/SyncOperation';

describe('syncQueueSlice', () => {
  const mockOperation: SyncOperation = {
    id: 'op-1',
    resourceName: 'people/c12345',
    fieldPath: 'names',
    newValue: { givenName: 'Jane', familyName: 'Doe' },
    oldValue: { givenName: 'John', familyName: 'Doe' },
    status: 'pending',
    timestamp: '2026-02-07T10:00:00Z',
    error: null,
  };

  const mockOperation2: SyncOperation = {
    id: 'op-2',
    resourceName: 'people/c67890',
    fieldPath: 'emailAddresses',
    newValue: { value: 'new@example.com' },
    oldValue: { value: 'old@example.com' },
    status: 'pending',
    timestamp: '2026-02-07T10:01:00Z',
    error: null,
  };

  let initialState: SyncQueueState;

  beforeEach(() => {
    initialState = syncQueueReducer(undefined, { type: '@@INIT' });
  });

  describe('initial state', () => {
    it('initialState_Should_HaveEmptyQueue_When_Created', () => {
      expect(initialState.ids).toEqual([]);
      expect(initialState.entities).toEqual({});
      expect(initialState.retryCount).toEqual({});
      expect(initialState.maxRetries).toBe(3);
    });
  });

  describe('addToQueue', () => {
    it('addToQueue_Should_AddOperation_When_ValidOperation', () => {
      const state = syncQueueReducer(initialState, addToQueue(mockOperation));

      expect(state.ids).toContain('op-1');
      expect(state.entities['op-1']).toBeDefined();
      expect(state.entities['op-1']?.status).toBe('pending');
      expect(state.entities['op-1']?.resourceName).toBe('people/c12345');
      expect(state.entities['op-1']?.fieldPath).toBe('names');
    });

    it('addToQueue_Should_InitializeRetryCount_When_OperationAdded', () => {
      const state = syncQueueReducer(initialState, addToQueue(mockOperation));

      expect(state.retryCount['op-1']).toBe(0);
    });

    it('addToQueue_Should_ForceStatusPending_When_OperationHasDifferentStatus', () => {
      const opWithWrongStatus = { ...mockOperation, status: 'failed' as const };
      const state = syncQueueReducer(initialState, addToQueue(opWithWrongStatus));

      expect(state.entities['op-1']?.status).toBe('pending');
    });

    it('addToQueue_Should_AddMultipleOperations_When_CalledMultipleTimes', () => {
      let state = syncQueueReducer(initialState, addToQueue(mockOperation));
      state = syncQueueReducer(state, addToQueue(mockOperation2));

      expect(state.ids).toHaveLength(2);
      expect(state.ids).toContain('op-1');
      expect(state.ids).toContain('op-2');
      expect(state.retryCount['op-1']).toBe(0);
      expect(state.retryCount['op-2']).toBe(0);
    });
  });

  describe('operationStarted', () => {
    it('operationStarted_Should_UpdateStatusToInProgress_When_OperationExists', () => {
      let state = syncQueueReducer(initialState, addToQueue(mockOperation));
      state = syncQueueReducer(state, operationStarted('op-1'));

      expect(state.entities['op-1']?.status).toBe('in-progress');
    });

    it('operationStarted_Should_NotThrow_When_OperationNotFound', () => {
      const state = syncQueueReducer(initialState, operationStarted('nonexistent'));

      expect(state.ids).toHaveLength(0);
    });
  });

  describe('operationSuccess', () => {
    it('operationSuccess_Should_RemoveOperation_When_OperationExists', () => {
      let state = syncQueueReducer(initialState, addToQueue(mockOperation));
      state = syncQueueReducer(state, operationSuccess('op-1'));

      expect(state.ids).not.toContain('op-1');
      expect(state.entities['op-1']).toBeUndefined();
    });

    it('operationSuccess_Should_ClearRetryCount_When_OperationRemoved', () => {
      let state = syncQueueReducer(initialState, addToQueue(mockOperation));
      state = syncQueueReducer(state, operationSuccess('op-1'));

      expect(state.retryCount['op-1']).toBeUndefined();
    });

    it('operationSuccess_Should_NotAffectOtherOperations_When_OneSucceeds', () => {
      let state = syncQueueReducer(initialState, addToQueue(mockOperation));
      state = syncQueueReducer(state, addToQueue(mockOperation2));
      state = syncQueueReducer(state, operationSuccess('op-1'));

      expect(state.ids).toHaveLength(1);
      expect(state.ids).toContain('op-2');
      expect(state.entities['op-2']).toBeDefined();
    });
  });

  describe('operationFailed', () => {
    it('operationFailed_Should_UpdateStatusToFailed_When_OperationExists', () => {
      let state = syncQueueReducer(initialState, addToQueue(mockOperation));
      state = syncQueueReducer(state, operationFailed({ id: 'op-1', error: 'Network error' }));

      expect(state.entities['op-1']?.status).toBe('failed');
      expect(state.entities['op-1']?.error).toBe('Network error');
    });

    it('operationFailed_Should_IncrementRetryCount_When_OperationFails', () => {
      let state = syncQueueReducer(initialState, addToQueue(mockOperation));
      state = syncQueueReducer(state, operationFailed({ id: 'op-1', error: 'Error 1' }));

      expect(state.retryCount['op-1']).toBe(1);
    });

    it('operationFailed_Should_IncrementRetryCountMultipleTimes_When_FailsRepeatedly', () => {
      let state = syncQueueReducer(initialState, addToQueue(mockOperation));
      state = syncQueueReducer(state, operationFailed({ id: 'op-1', error: 'Error 1' }));
      state = syncQueueReducer(state, retryOperation('op-1'));
      state = syncQueueReducer(state, operationFailed({ id: 'op-1', error: 'Error 2' }));
      state = syncQueueReducer(state, retryOperation('op-1'));
      state = syncQueueReducer(state, operationFailed({ id: 'op-1', error: 'Error 3' }));

      expect(state.retryCount['op-1']).toBe(3);
      expect(state.entities['op-1']?.error).toBe('Error 3');
    });
  });

  describe('retryOperation', () => {
    it('retryOperation_Should_ResetStatusToPending_When_OperationFailed', () => {
      let state = syncQueueReducer(initialState, addToQueue(mockOperation));
      state = syncQueueReducer(state, operationFailed({ id: 'op-1', error: 'Error' }));
      state = syncQueueReducer(state, retryOperation('op-1'));

      expect(state.entities['op-1']?.status).toBe('pending');
      expect(state.entities['op-1']?.error).toBeNull();
    });

    it('retryOperation_Should_PreserveRetryCount_When_Retried', () => {
      let state = syncQueueReducer(initialState, addToQueue(mockOperation));
      state = syncQueueReducer(state, operationFailed({ id: 'op-1', error: 'Error' }));
      const retryCountBefore = state.retryCount['op-1'];
      state = syncQueueReducer(state, retryOperation('op-1'));

      expect(state.retryCount['op-1']).toBe(retryCountBefore);
    });
  });

  describe('clearQueue', () => {
    it('clearQueue_Should_RemoveAllOperations_When_Called', () => {
      let state = syncQueueReducer(initialState, addToQueue(mockOperation));
      state = syncQueueReducer(state, addToQueue(mockOperation2));
      state = syncQueueReducer(state, clearQueue());

      expect(state.ids).toHaveLength(0);
      expect(state.entities).toEqual({});
    });

    it('clearQueue_Should_ResetRetryCount_When_Called', () => {
      let state = syncQueueReducer(initialState, addToQueue(mockOperation));
      state = syncQueueReducer(state, operationFailed({ id: 'op-1', error: 'Error' }));
      state = syncQueueReducer(state, clearQueue());

      expect(state.retryCount).toEqual({});
    });

    it('clearQueue_Should_BeNoOp_When_QueueAlreadyEmpty', () => {
      const state = syncQueueReducer(initialState, clearQueue());

      expect(state.ids).toHaveLength(0);
      expect(state.retryCount).toEqual({});
    });
  });

  describe('edge cases', () => {
    it('operationSuccess_Should_BeNoOp_When_OperationNotFound', () => {
      const state = syncQueueReducer(initialState, operationSuccess('nonexistent'));

      expect(state.ids).toHaveLength(0);
    });

    it('operationFailed_Should_HandleGracefully_When_OperationNotInQueue', () => {
      // operationFailed on a nonexistent operation should not crash
      const state = syncQueueReducer(initialState, operationFailed({ id: 'nonexistent', error: 'Error' }));

      // retryCount incremented even though entity doesn't exist (defensive)
      expect(state.retryCount['nonexistent']).toBe(1);
    });

    it('fullLifecycle_Should_WorkCorrectly_When_OperationGoesThrough', () => {
      // Add → Start → Success
      let state = syncQueueReducer(initialState, addToQueue(mockOperation));
      expect(state.entities['op-1']?.status).toBe('pending');

      state = syncQueueReducer(state, operationStarted('op-1'));
      expect(state.entities['op-1']?.status).toBe('in-progress');

      state = syncQueueReducer(state, operationSuccess('op-1'));
      expect(state.entities['op-1']).toBeUndefined();
      expect(state.retryCount['op-1']).toBeUndefined();
    });

    it('fullLifecycle_Should_WorkCorrectly_When_OperationFailsAndRetries', () => {
      // Add → Start → Fail → Retry → Start → Success
      let state = syncQueueReducer(initialState, addToQueue(mockOperation));
      state = syncQueueReducer(state, operationStarted('op-1'));
      state = syncQueueReducer(state, operationFailed({ id: 'op-1', error: 'Timeout' }));

      expect(state.entities['op-1']?.status).toBe('failed');
      expect(state.retryCount['op-1']).toBe(1);

      state = syncQueueReducer(state, retryOperation('op-1'));
      expect(state.entities['op-1']?.status).toBe('pending');

      state = syncQueueReducer(state, operationStarted('op-1'));
      state = syncQueueReducer(state, operationSuccess('op-1'));

      expect(state.entities['op-1']).toBeUndefined();
    });
  });
});
