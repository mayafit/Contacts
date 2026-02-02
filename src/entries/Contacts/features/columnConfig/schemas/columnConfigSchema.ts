/**
 * @fileoverview Zod validation schema for column configuration
 * @module features/columnConfig/schemas/columnConfigSchema
 */

import { z } from 'zod';
import { AVAILABLE_COLUMNS } from '../columnDefinitions';

/**
 * Get all valid column IDs from available columns
 */
const validColumnIds = AVAILABLE_COLUMNS.map((col) => col.id);

/**
 * Zod schema for validating column configuration structure
 * Ensures visibleColumns and columnOrder contain only valid column IDs
 */
export const ColumnConfigSchema = z.object({
  visibleColumns: z
    .array(z.string())
    .refine((cols) => cols.every((id) => validColumnIds.includes(id)), {
      message: 'Invalid column ID in visibleColumns',
    }),
  columnOrder: z
    .array(z.string())
    .refine((cols) => cols.every((id) => validColumnIds.includes(id)), {
      message: 'Invalid column ID in columnOrder',
    }),
});

/**
 * TypeScript type inferred from Zod schema
 */
export type ColumnConfigFromSchema = z.infer<typeof ColumnConfigSchema>;
