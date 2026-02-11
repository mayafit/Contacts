/**
 * @fileoverview Sync queue selector tests
 * @module Contacts/redux/slices/syncQueue/__tests__/selectors.test
 * @jest-environment node
 */

import type { RootState } from '../../../../types/store';
import type { SyncOperation, SyncQueueState } from '../../../../types/SyncOperation';
import {
  selectSyncQueueState,
  selectAllSyncOperations,
  selectPendingOperations,
  selectFailedOperations,
  selectInProgressOperations,
  selectOperationById,
  selectOperationsByContact,
  selectRetryCount,
  selectHasMaxRetriesExceeded,
  selectQueueSize,
} from '../selectors';

describe('syncQueue selectors', () => {
  const pendingOp: SyncOperation = {
    id: 'op-1',
    resourceName: 'people/c12345',
    fieldPath: 'names',
    newValue: { givenName: 'Jane' },
    oldValue: { givenName: 'John' },
    status: 'pending',
    timestamp: '2026-02-07T10:00:00Z',
    error: null,
  };

  const failedOp: SyncOperation = {
    id: 'op-2',
    resourceName: 'people/c12345',
    fieldPath: 'emailAddresses',
    newValue: { value: 'new@example.com' },
    oldValue: { value: 'old@example.com' },
    status: 'failed',
    timestamp: '2026-02-07T10:01:00Z',
    error: 'Network error',
  };

  const inProgressOp: SyncOperation = {
    id: 'op-3',
    resourceName: 'people/c67890',
    fieldPath: 'phoneNumbers',
    newValue: { value: '+1111111111' },
    oldValue: { value: '+9999999999' },
    status: 'in-progress',
    timestamp: '2026-02-07T10:02:00Z',
    error: null,
  };

  const createMockState = (syncQueue?: Partial<SyncQueueState>): RootState => ({
    contacts: {
      syncQueue: {
        ids: ['op-1', 'op-2', 'op-3'],
        entities: {
          'op-1': pendingOp,
          'op-2': failedOp,
          'op-3': inProgressOp,
        },
        retryCount: { 'op-2': 2 },
        maxRetries: 3,
        ...syncQueue,
      },
    },
  });

  const emptyState: RootState = {
    contacts: {
      syncQueue: {
        ids: [],
        entities: {},
        retryCount: {},
        maxRetries: 3,
      },
    },
  };

  const undefinedState: RootState = {};

  describe('selectSyncQueueState', () => {
    it('selectSyncQueueState_Should_ReturnState_When_Exists', () => {
      const state = createMockState();
      const result = selectSyncQueueState(state);

      expect(result).toBeDefined();
      expect(result?.maxRetries).toBe(3);
    });

    it('selectSyncQueueState_Should_ReturnUndefined_When_NoContactsState', () => {
      const result = selectSyncQueueState(undefinedState);

      expect(result).toBeUndefined();
    });
  });

  describe('selectAllSyncOperations', () => {
    it('selectAllSyncOperations_Should_ReturnAllOperations_When_QueueHasItems', () => {
      const state = createMockState();
      const result = selectAllSyncOperations(state);

      expect(result).toHaveLength(3);
    });

    it('selectAllSyncOperations_Should_ReturnEmptyArray_When_QueueEmpty', () => {
      const result = selectAllSyncOperations(emptyState);

      expect(result).toEqual([]);
    });

    it('selectAllSyncOperations_Should_ReturnEmptyArray_When_StateUndefined', () => {
      const result = selectAllSyncOperations(undefinedState);

      expect(result).toEqual([]);
    });
  });

  describe('selectPendingOperations', () => {
    it('selectPendingOperations_Should_ReturnOnlyPending_When_MixedStatuses', () => {
      const state = createMockState();
      const result = selectPendingOperations(state);

      expect(result).toHaveLength(1);
      expect(result[0].id).toBe('op-1');
      expect(result[0].status).toBe('pending');
    });

    it('selectPendingOperations_Should_ReturnEmptyArray_When_NoPending', () => {
      const state = createMockState({
        ids: ['op-2'],
        entities: { 'op-2': failedOp },
      });
      const result = selectPendingOperations(state);

      expect(result).toEqual([]);
    });
  });

  describe('selectFailedOperations', () => {
    it('selectFailedOperations_Should_ReturnOnlyFailed_When_MixedStatuses', () => {
      const state = createMockState();
      const result = selectFailedOperations(state);

      expect(result).toHaveLength(1);
      expect(result[0].id).toBe('op-2');
      expect(result[0].status).toBe('failed');
      expect(result[0].error).toBe('Network error');
    });
  });

  describe('selectInProgressOperations', () => {
    it('selectInProgressOperations_Should_ReturnOnlyInProgress_When_MixedStatuses', () => {
      const state = createMockState();
      const result = selectInProgressOperations(state);

      expect(result).toHaveLength(1);
      expect(result[0].id).toBe('op-3');
      expect(result[0].status).toBe('in-progress');
    });
  });

  describe('selectOperationById', () => {
    it('selectOperationById_Should_ReturnOperation_When_IdExists', () => {
      const state = createMockState();
      const result = selectOperationById(state, 'op-1');

      expect(result).toBeDefined();
      expect(result?.id).toBe('op-1');
      expect(result?.fieldPath).toBe('names');
    });

    it('selectOperationById_Should_ReturnUndefined_When_IdNotFound', () => {
      const state = createMockState();
      const result = selectOperationById(state, 'nonexistent');

      expect(result).toBeUndefined();
    });

    it('selectOperationById_Should_ReturnUndefined_When_StateUndefined', () => {
      const result = selectOperationById(undefinedState, 'op-1');

      expect(result).toBeUndefined();
    });
  });

  describe('selectOperationsByContact', () => {
    it('selectOperationsByContact_Should_ReturnMatchingOperations_When_ContactHasOps', () => {
      const state = createMockState();
      const result = selectOperationsByContact(state, 'people/c12345');

      expect(result).toHaveLength(2);
      expect(result.map((op) => op.id)).toEqual(['op-1', 'op-2']);
    });

    it('selectOperationsByContact_Should_ReturnEmptyArray_When_NoOpsForContact', () => {
      const state = createMockState();
      const result = selectOperationsByContact(state, 'people/nonexistent');

      expect(result).toEqual([]);
    });
  });

  describe('selectRetryCount', () => {
    it('selectRetryCount_Should_ReturnCount_When_OperationHasRetries', () => {
      const state = createMockState();
      const result = selectRetryCount(state, 'op-2');

      expect(result).toBe(2);
    });

    it('selectRetryCount_Should_ReturnZero_When_NoRetriesRecorded', () => {
      const state = createMockState();
      const result = selectRetryCount(state, 'op-1');

      expect(result).toBe(0);
    });

    it('selectRetryCount_Should_ReturnZero_When_StateUndefined', () => {
      const result = selectRetryCount(undefinedState, 'op-1');

      expect(result).toBe(0);
    });
  });

  describe('selectHasMaxRetriesExceeded', () => {
    it('selectHasMaxRetriesExceeded_Should_ReturnFalse_When_UnderMaxRetries', () => {
      const state = createMockState();
      const result = selectHasMaxRetriesExceeded(state, 'op-2');

      expect(result).toBe(false); // retryCount=2, maxRetries=3
    });

    it('selectHasMaxRetriesExceeded_Should_ReturnTrue_When_AtMaxRetries', () => {
      const state = createMockState({ retryCount: { 'op-2': 3 } });
      const result = selectHasMaxRetriesExceeded(state, 'op-2');

      expect(result).toBe(true); // retryCount=3, maxRetries=3
    });

    it('selectHasMaxRetriesExceeded_Should_ReturnTrue_When_OverMaxRetries', () => {
      const state = createMockState({ retryCount: { 'op-2': 5 } });
      const result = selectHasMaxRetriesExceeded(state, 'op-2');

      expect(result).toBe(true); // retryCount=5, maxRetries=3
    });

    it('selectHasMaxRetriesExceeded_Should_ReturnFalse_When_NoRetryCount', () => {
      const state = createMockState();
      const result = selectHasMaxRetriesExceeded(state, 'op-1');

      expect(result).toBe(false); // retryCount=0 (not in record)
    });

    it('selectHasMaxRetriesExceeded_Should_ReturnFalse_When_StateUndefined', () => {
      const result = selectHasMaxRetriesExceeded(undefinedState, 'op-1');

      expect(result).toBe(false);
    });
  });

  describe('selectQueueSize', () => {
    it('selectQueueSize_Should_ReturnCorrectCount_When_QueueHasItems', () => {
      const state = createMockState();
      const result = selectQueueSize(state);

      expect(result).toBe(3);
    });

    it('selectQueueSize_Should_ReturnZero_When_QueueEmpty', () => {
      const result = selectQueueSize(emptyState);

      expect(result).toBe(0);
    });

    it('selectQueueSize_Should_ReturnZero_When_StateUndefined', () => {
      const result = selectQueueSize(undefinedState);

      expect(result).toBe(0);
    });
  });
});
