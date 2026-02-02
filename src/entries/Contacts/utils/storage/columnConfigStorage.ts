/**
 * @fileoverview LocalStorage utility for persisting column configuration
 * @module utils/storage/columnConfigStorage
 */

import { logger } from '../../../../shared/logger';
import { ColumnConfigSchema } from '../../features/columnConfig/schemas/columnConfigSchema';
import type { ColumnConfig } from '../../features/columnConfig/types';

/**
 * Storage key for column configuration in localStorage
 * Namespaced to avoid conflicts with other applications
 */
export const STORAGE_KEY = 'contacts:columnConfig';

/**
 * Save column configuration to browser localStorage
 *
 * @param config - Column configuration object to persist
 *
 * Handles errors gracefully:
 * - JSON serialization errors
 * - localStorage quota exceeded errors
 * - localStorage disabled (private browsing)
 *
 * Logs warnings on failure but does not throw
 */
export const saveColumnConfig = (config: ColumnConfig): void => {
  try {
    const json = JSON.stringify(config);
    localStorage.setItem(STORAGE_KEY, json);
  } catch (error) {
    logger.warn(
      {
        context: 'columnConfigStorage/save',
        error,
        metadata: { config },
      },
      'Failed to save column config to localStorage'
    );
  }
};

/**
 * Load column configuration from browser localStorage
 *
 * @returns Column configuration if found and valid, null otherwise
 *
 * Validates loaded data using Zod schema to ensure:
 * - Structure matches ColumnConfig interface
 * - Column IDs are valid (exist in AVAILABLE_COLUMNS)
 *
 * Returns null in these cases:
 * - No data found in localStorage
 * - JSON parse error (corrupted data)
 * - Zod validation error (invalid structure or column IDs)
 *
 * Logs warnings on failures but does not throw
 * Caller should fall back to default columns when null is returned
 */
export const loadColumnConfig = (): ColumnConfig | null => {
  try {
    const json = localStorage.getItem(STORAGE_KEY);
    if (!json) {
      return null;
    }

    const data = JSON.parse(json);
    const validated = ColumnConfigSchema.parse(data);
    return validated as ColumnConfig;
  } catch (error) {
    logger.warn(
      {
        context: 'columnConfigStorage/load',
        error,
      },
      'Failed to load column config from localStorage, using defaults'
    );
    return null;
  }
};
