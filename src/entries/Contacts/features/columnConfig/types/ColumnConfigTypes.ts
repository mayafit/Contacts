/**
 * @fileoverview Type definitions for column configuration feature
 * @module features/columnConfig/types
 */

import type { Contact, ContactGroup, EditableGroup } from '../../../types/Contact';

/**
 * Data a column accessor may need beyond the contact itself
 */
export interface ColumnContext {
  /** Labels by resource name */
  groups: Record<string, ContactGroup>;
}

/**
 * How a column is edited inline: the text shown in the editor, and how the
 * typed text becomes a new value for one field group.
 */
export interface ColumnEdit {
  /** Field group the edit replaces */
  fieldPath: EditableGroup;
  /** Text to start editing from */
  value: (contact: Contact) => string;
  /** New value of the whole group, built from the typed text */
  apply: (contact: Contact, text: string) => unknown;
}

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
  /** Whether this column is locked on (cannot be removed) */
  isDefault: boolean;
  /** Whether the column is shown for a new user */
  defaultVisible?: boolean;
  /** Function to extract the display value ('' when empty) */
  accessor: (contact: Contact, context?: ColumnContext) => string | undefined;
  /** Preferred width in px; columns share leftover space proportionally */
  width?: number;
  /** Inline edit behaviour; absent for read-only columns */
  edit?: ColumnEdit;
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
