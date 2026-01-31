/**
 * @fileoverview Type definitions for column configuration feature
 * @module features/columnConfig/types
 */

import type { Contact } from '../../../types/Contact';

/**
 * Category classification for contact fields
 */
export type ColumnCategory = 'Basic' | 'Additional' | 'Advanced';

/**
 * Column definition with accessor function for data extraction
 */
export interface ColumnDefinition {
  /** Unique identifier for the column */
  id: string;
  /** Display label for the column header */
  label: string;
  /** Category for grouping in configuration UI */
  category: ColumnCategory;
  /** Whether this is a default column that cannot be removed */
  isDefault: boolean;
  /** Function to extract field value from contact object */
  accessor: (contact: Contact) => string | undefined;
}

/**
 * Column configuration state
 */
export interface ColumnConfig {
  /** Array of column IDs currently visible in the table */
  visibleColumns: string[];
  /** Array of column IDs in display order (left to right) */
  columnOrder: string[];
}
