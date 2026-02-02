/**
 * @fileoverview Redux middleware for persisting column configuration to localStorage
 * @module redux/middleware/columnConfigPersistence
 *
 * This middleware intercepts Redux actions that modify column configuration
 * and automatically saves the updated state to localStorage.
 *
 * Actions monitored:
 * - ui/setColumnConfig
 * - ui/toggleColumn
 * - ui/reorderColumns
 * - ui/resetColumnConfig
 *
 * Persistence happens synchronously after the action is processed,
 * ensuring localStorage is always in sync with Redux state.
 */

import type { Middleware } from '@reduxjs/toolkit';
import { saveColumnConfig } from '../../utils/storage';
import { logger } from '../../../../shared/logger';
import type { RootState } from '../../types/store';

/**
 * Column configuration action types that trigger persistence
 */
const COLUMN_CONFIG_ACTIONS = [
  'ui/setColumnConfig',
  'ui/toggleColumn',
  'ui/reorderColumns',
  'ui/resetColumnConfig',
];

/**
 * Redux middleware that persists column configuration changes to localStorage
 *
 * This middleware runs AFTER the reducer updates state, ensuring we save
 * the correct updated configuration.
 *
 * Error handling: If localStorage save fails (quota exceeded, disabled, etc.),
 * the error is logged but does not interrupt the Redux action flow.
 * The app continues to function normally without persistence.
 */
export const columnConfigPersistenceMiddleware: Middleware<
  Record<string, never>,
  RootState
> = (store) => (next) => (action) => {
  // Pass action to next middleware and reducers
  const result = next(action);

  // After state update, check if this was a column config action
  if (
    action &&
    typeof action === 'object' &&
    'type' in action &&
    typeof action.type === 'string' &&
    COLUMN_CONFIG_ACTIONS.includes(action.type)
  ) {
    // Get updated state after reducer ran
    const state = store.getState();
    const columnConfig = state.contacts?.ui?.columnConfig;

    if (columnConfig) {
      // Save to localStorage (errors handled internally by saveColumnConfig)
      saveColumnConfig(columnConfig);

      logger.info(
        {
          context: 'columnConfigPersistenceMiddleware',
          metadata: {
            actionType: action.type,
            columnConfig,
          },
        },
        'Column configuration persisted to localStorage'
      );
    }
  }

  return result;
};
