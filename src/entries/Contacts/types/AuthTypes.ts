/**
 * @fileoverview Authentication type definitions
 * @module Contacts/types/AuthTypes
 */

/**
 * User profile information from Google OAuth
 */
export interface User {
  /** User's full name */
  name: string;
  /** User's email address */
  email: string;
  /** URL to user's profile picture */
  profilePicture?: string;
  /** Google user ID */
  id: string;
}

/**
 * Authentication state shape for Redux store
 */
export interface AuthState {
  /** Authenticated user profile or null if not authenticated */
  user: User | null;
  /** OAuth access token for API requests */
  accessToken: string | null;
  /** ISO 8601 timestamp when token expires */
  tokenExpiry: string | null;
  /** Loading state for async auth operations */
  isLoading: boolean;
  /** Error state for auth failures */
  error: AppError | null;
  /** Computed authentication status */
  isAuthenticated: boolean;
}

/**
 * Structured error type for authentication failures
 */
export interface AppError {
  /** Error code for categorization */
  code: ErrorCode;
  /** User-friendly error message */
  message: string;
  /** Technical error message for debugging */
  technicalMessage?: string;
  /** ISO 8601 timestamp when error occurred */
  timestamp: string;
  /** Additional context about the error */
  context?: Record<string, unknown>;
}

/**
 * Error codes for authentication and API failures
 */
export enum ErrorCode {
  NETWORK_ERROR = 'NETWORK_ERROR',
  AUTH_FAILED = 'AUTH_FAILED',
  TOKEN_EXPIRED = 'TOKEN_EXPIRED',
  INVALID_TOKEN = 'INVALID_TOKEN',
  FETCH_FAILED = 'FETCH_FAILED',
  UPDATE_FAILED = 'UPDATE_FAILED',
  UNKNOWN_ERROR = 'UNKNOWN_ERROR',
}

/**
 * OAuth token response from Google
 */
export interface TokenResponse {
  /** OAuth access token */
  access_token: string;
  /** Token expiry in seconds */
  expires_in: number;
  /** OAuth refresh token (optional for some flows) */
  refresh_token?: string;
  /** OAuth scope granted */
  scope: string;
  /** Token type (usually 'Bearer') */
  token_type: string;
}

/**
 * Google OAuth credential response
 */
export interface GoogleCredentialResponse {
  /** JWT credential token from Google */
  credential: string;
  /** Selected user identifier */
  select_by?: string;
}
