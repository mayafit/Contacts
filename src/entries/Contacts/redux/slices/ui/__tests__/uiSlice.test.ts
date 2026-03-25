/**
 * @fileoverview Tests for UI Redux slice
 * @module redux/slices/ui/__tests__
 */

import uiReducer, {
  setColumnConfig,
  toggleColumn,
  reorderColumns,
  resetColumnConfig,
  selectColumnConfig,
  selectVisibleColumns,
  selectColumnOrder,
  type UIState,
} from '../uiSlice';
import type { RootState } from '../../../../types/store';
import { STORAGE_KEY } from '../../../../utils/storage/columnConfigStorage';

describe('uiSlice', () => {
  const initialState: UIState = {
    columnConfig: {
      visibleColumns: ['displayName', 'phoneNumbers', 'emailAddresses'],
      columnOrder: ['displayName', 'phoneNumbers', 'emailAddresses'],
    },
  };

  describe('reducers', () => {
    it('should return initial state', () => {
      expect(uiReducer(undefined, { type: 'unknown' })).toEqual(initialState);
    });

    describe('setColumnConfig', () => {
      it('should set complete column configuration', () => {
        const newConfig = {
          visibleColumns: ['displayName', 'phoneNumbers', 'emailAddresses', 'organizations'],
          columnOrder: ['displayName', 'organizations', 'phoneNumbers', 'emailAddresses'],
        };

        const actual = uiReducer(initialState, setColumnConfig(newConfig));

        expect(actual.columnConfig).toEqual(newConfig);
      });
    });

    describe('toggleColumn', () => {
      it('should add non-default column when not visible', () => {
        const actual = uiReducer(initialState, toggleColumn('organizations'));

        expect(actual.columnConfig.visibleColumns).toContain('organizations');
        expect(actual.columnConfig.columnOrder).toContain('organizations');
        expect(actual.columnConfig.visibleColumns).toHaveLength(4);
      });

      it('should remove non-default column when visible', () => {
        const stateWithExtra: UIState = {
          columnConfig: {
            visibleColumns: ['displayName', 'phoneNumbers', 'emailAddresses', 'organizations'],
            columnOrder: ['displayName', 'phoneNumbers', 'emailAddresses', 'organizations'],
          },
        };

        const actual = uiReducer(stateWithExtra, toggleColumn('organizations'));

        expect(actual.columnConfig.visibleColumns).not.toContain('organizations');
        expect(actual.columnConfig.columnOrder).not.toContain('organizations');
        expect(actual.columnConfig.visibleColumns).toHaveLength(3);
      });

      it('should NOT remove default column (displayName)', () => {
        const actual = uiReducer(initialState, toggleColumn('displayName'));

        expect(actual.columnConfig.visibleColumns).toContain('displayName');
        expect(actual.columnConfig.columnOrder).toContain('displayName');
        expect(actual.columnConfig).toEqual(initialState.columnConfig);
      });

      it('should NOT remove default column (phoneNumbers)', () => {
        const actual = uiReducer(initialState, toggleColumn('phoneNumbers'));

        expect(actual.columnConfig.visibleColumns).toContain('phoneNumbers');
        expect(actual.columnConfig.columnOrder).toContain('phoneNumbers');
        expect(actual.columnConfig).toEqual(initialState.columnConfig);
      });

      it('should NOT remove default column (emailAddresses)', () => {
        const actual = uiReducer(initialState, toggleColumn('emailAddresses'));

        expect(actual.columnConfig.visibleColumns).toContain('emailAddresses');
        expect(actual.columnConfig.columnOrder).toContain('emailAddresses');
        expect(actual.columnConfig).toEqual(initialState.columnConfig);
      });
    });

    describe('reorderColumns', () => {
      it('should reorder columns from oldIndex to newIndex', () => {
        const actual = uiReducer(
          initialState,
          reorderColumns({ oldIndex: 0, newIndex: 2 })
        );

        expect(actual.columnConfig.columnOrder).toEqual([
          'phoneNumbers',
          'emailAddresses',
          'displayName',
        ]);
      });

      it('should handle reordering to same position', () => {
        const actual = uiReducer(
          initialState,
          reorderColumns({ oldIndex: 1, newIndex: 1 })
        );

        expect(actual.columnConfig.columnOrder).toEqual(initialState.columnConfig.columnOrder);
      });

      it('should reorder from end to start', () => {
        const actual = uiReducer(
          initialState,
          reorderColumns({ oldIndex: 2, newIndex: 0 })
        );

        expect(actual.columnConfig.columnOrder).toEqual([
          'emailAddresses',
          'displayName',
          'phoneNumbers',
        ]);
      });
    });

    describe('resetColumnConfig', () => {
      it('should reset to default configuration', () => {
        const modifiedState: UIState = {
          columnConfig: {
            visibleColumns: ['displayName', 'organizations', 'jobTitle'],
            columnOrder: ['organizations', 'displayName', 'jobTitle'],
          },
        };

        const actual = uiReducer(modifiedState, resetColumnConfig());

        expect(actual.columnConfig).toEqual(initialState.columnConfig);
      });
    });
  });

  describe('selectors', () => {
    const mockRootState: RootState = {
      contacts: {
        ui: {
          columnConfig: {
            visibleColumns: ['displayName', 'phoneNumbers', 'organizations'],
            columnOrder: ['displayName', 'organizations', 'phoneNumbers'],
          },
        },
      },
    };

    it('selectColumnConfig should return column configuration', () => {
      const result = selectColumnConfig(mockRootState);

      expect(result).toEqual(mockRootState.contacts?.ui?.columnConfig);
    });

    it('selectVisibleColumns should return visible columns array', () => {
      const result = selectVisibleColumns(mockRootState);

      expect(result).toEqual(['displayName', 'phoneNumbers', 'organizations']);
    });

    it('selectColumnOrder should return column order array', () => {
      const result = selectColumnOrder(mockRootState);

      expect(result).toEqual(['displayName', 'organizations', 'phoneNumbers']);
    });
  });

  describe('localStorage persistence integration', () => {
    it('should have initial state with default column configuration', () => {
      // Verify the slice initializes with expected default structure
      // The loadPersistedColumnConfig function is called during module init
      // and will either load from localStorage or fall back to defaults
      const state = uiReducer(undefined, { type: '@@INIT' });

      expect(state.columnConfig).toBeDefined();
      expect(state.columnConfig.visibleColumns).toBeInstanceOf(Array);
      expect(state.columnConfig.columnOrder).toBeInstanceOf(Array);
      expect(state.columnConfig.visibleColumns.length).toBeGreaterThan(0);
      expect(state.columnConfig.columnOrder.length).toBeGreaterThan(0);
    });

    it('should use correct storage key constant', () => {
      // Verify the storage key matches acceptance criteria (Story 2.7)
      expect(STORAGE_KEY).toBe('contacts:columnConfig');
    });

    it('should maintain column config structure through reducers', () => {
      // Integration test: Verify that all column config actions maintain
      // the structure that will be persisted to localStorage
      const state1 = uiReducer(initialState, toggleColumn('organizations'));
      expect(state1.columnConfig.visibleColumns).toBeInstanceOf(Array);
      expect(state1.columnConfig.columnOrder).toBeInstanceOf(Array);

      const state2 = uiReducer(state1, reorderColumns({ oldIndex: 0, newIndex: 2 }));
      expect(state2.columnConfig.visibleColumns).toBeInstanceOf(Array);
      expect(state2.columnConfig.columnOrder).toBeInstanceOf(Array);

      const state3 = uiReducer(state2, resetColumnConfig());
      expect(state3.columnConfig).toEqual(initialState.columnConfig);
    });
  });
});
