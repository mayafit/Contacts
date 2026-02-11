/**
 * @fileoverview Zod validation schema for updateContactField parameters
 * @module Contacts/services/schemas/updateContactFieldSchema
 * Story: 3.1 - Service Layer for Google Contacts API Operations
 */

import { z } from 'zod';

/**
 * Allowed field paths for field-level contact updates
 * Based on Google People API v1 field masks
 */
export const ALLOWED_FIELD_PATHS = [
  'names',
  'phoneNumbers',
  'emailAddresses',
  'addresses',
  'organizations',
  'birthdays',
  'urls',
  'biographies',
  'userDefined',
  'relations',
] as const;

/**
 * Zod schema for updateContactField method parameters
 * Validates resourceName format and fieldPath against allowed values
 */
export const UpdateContactFieldParamsSchema = z.object({
  resourceName: z
    .string()
    .regex(/^people\/[a-zA-Z0-9]+$/, 'Invalid resourceName format. Expected: people/{id}'),
  fieldPath: z.enum(ALLOWED_FIELD_PATHS, {
    error: `Invalid field path. Must be one of: ${ALLOWED_FIELD_PATHS.join(', ')}`,
  }),
  newValue: z.unknown(), // Type varies by fieldPath, backend validates structure
});

/**
 * TypeScript type inferred from schema
 */
export type UpdateContactFieldParams = z.infer<typeof UpdateContactFieldParamsSchema>;
