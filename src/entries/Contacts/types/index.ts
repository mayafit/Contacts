/**
 * @fileoverview Barrel export for Contacts types
 * @module Contacts/types
 */

export type {
  User,
  AuthState,
  AppError,
  TokenResponse,
  GoogleCredentialResponse,
} from './AuthTypes';

export { ErrorCode } from './AuthTypes';

export type {
  Contact,
  ContactName,
  ContactPhone,
  ContactEmail,
  ContactAddress,
  APIResponse,
} from './Contact';

export type {
  SyncOperation,
  SyncOperationStatus,
  SyncQueueState,
} from './SyncOperation';
