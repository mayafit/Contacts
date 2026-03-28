/**
 * @fileoverview Sync operation types for tracking pending and failed contact updates
 * @module Contacts/types/SyncOperation
 *
 * Story 3.2: Defines the SyncOperation data model for the sync queue Redux slice
 */

import type { EntityState } from '@reduxjs/toolkit';

/**
 * Possible statuses for a sync operation
 * Story 3.7: Added 'conflict' status for 412 Precondition Failed handling
 */
export type SyncOperationStatus = 'pending' | 'in-progress' | 'success' | 'failed' | 'conflict';

/**
 * Represents a single contact field update operation in the sync queue
 */
export interface SyncOperation {
  /** Unique operation identifier (UUID) */
  id: string;
  /** Contact resource name being modified (e.g., "people/c12345") */
  resourceName: string;
  /** Field being updated (e.g., "names", "phoneNumbers") */
  fieldPath: string;
  /** New field value */
  newValue: unknown;
  /** Previous field value (for rollback on failure) */
  oldValue: unknown;
  /** Current operation status */
  status: SyncOperationStatus;
  /** Operation creation time (ISO 8601 string) */
  timestamp: string;
  /** Error message if operation failed */
  error: string | null;
  /** Remote value fetched on conflict (412) for display in resolution dialog */
  remoteValue?: unknown;
}

/**
 * State shape for the sync queue Redux slice
 * Extends entity adapter normalized state with retry tracking
 */
export interface SyncQueueState extends EntityState<SyncOperation, string> {
  /** Retry count per operation ID */
  retryCount: Record<string, number>;
  /** Maximum retry attempts before permanent failure */
  maxRetries: number;
}
