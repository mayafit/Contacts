/**
 * @fileoverview UI state slice for managing UI-related state (column config, modals, etc.)
 * @module redux/slices/ui
 */

import { createSlice, type PayloadAction } from '@reduxjs/toolkit';
import type { ColumnConfig } from '../../../features/columnConfig/types';
import type { RootState } from '../../store';

/**
 * UI state interface
 */
export interface UIState {
  /** Column configuration for contacts table */
  columnConfig: ColumnConfig;
}

/**
 * Default column configuration
 * Matches the default columns from Story 2.5: Name, Phone, Email
 */
const defaultColumnConfig: ColumnConfig = {
  visibleColumns: ['displayName', 'phoneNumbers', 'emailAddresses'],
  columnOrder: ['displayName', 'phoneNumbers', 'emailAddresses'],
};

/**
 * Initial UI state
 */
const initialState: UIState = {
  columnConfig: defaultColumnConfig,
};

/**
 * UI slice with column configuration reducers
 */
const uiSlice = createSlice({
  name: 'ui',
  initialState,
  reducers: {
    /**
     * Set complete column configuration
     */
    setColumnConfig(state, action: PayloadAction<ColumnConfig>) {
      state.columnConfig = action.payload;
    },

    /**
     * Toggle column visibility (add or remove)
     * Default columns (displayName, phoneNumbers, emailAddresses) cannot be removed
     */
    toggleColumn(state, action: PayloadAction<string>) {
      const columnId = action.payload;
      const { visibleColumns, columnOrder } = state.columnConfig;

      // Prevent removal of default columns
      const defaultColumns = ['displayName', 'phoneNumbers', 'emailAddresses'];
      const isDefaultColumn = defaultColumns.includes(columnId);
      const isCurrentlyVisible = visibleColumns.includes(columnId);

      if (isCurrentlyVisible) {
        // Remove column (only if not a default column)
        if (!isDefaultColumn) {
          state.columnConfig.visibleColumns = visibleColumns.filter(
            (id) => id !== columnId
          );
          state.columnConfig.columnOrder = columnOrder.filter(
            (id) => id !== columnId
          );
        }
      } else {
        // Add column
        state.columnConfig.visibleColumns.push(columnId);
        state.columnConfig.columnOrder.push(columnId);
      }
    },

    /**
     * Reorder columns by moving from oldIndex to newIndex
     */
    reorderColumns(
      state,
      action: PayloadAction<{ oldIndex: number; newIndex: number }>
    ) {
      const { oldIndex, newIndex } = action.payload;
      const newOrder = [...state.columnConfig.columnOrder];

      // Remove item from old position
      const [movedColumn] = newOrder.splice(oldIndex, 1);

      // Insert at new position
      newOrder.splice(newIndex, 0, movedColumn);

      // Update state with new order
      state.columnConfig.columnOrder = newOrder;
    },

    /**
     * Reset column configuration to default
     */
    resetColumnConfig(state) {
      state.columnConfig = defaultColumnConfig;
    },
  },
});

// Export actions
export const {
  setColumnConfig,
  toggleColumn,
  reorderColumns,
  resetColumnConfig,
} = uiSlice.actions;

// Selectors
export const selectColumnConfig = (state: RootState): ColumnConfig =>
  state.ui.columnConfig;

export const selectVisibleColumns = (state: RootState): string[] =>
  state.ui.columnConfig.visibleColumns;

export const selectColumnOrder = (state: RootState): string[] =>
  state.ui.columnConfig.columnOrder;

// Export reducer
export default uiSlice.reducer;
