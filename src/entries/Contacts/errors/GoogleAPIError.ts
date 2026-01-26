/**
 * @fileoverview Custom error class for Google API errors
 * @module Contacts/errors/GoogleAPIError
 */

/**
 * Custom error class for Google API operations
 * Enriches errors with context and meaningful messages
 */
export class GoogleAPIError extends Error {
  /** Error code for categorization */
  public readonly code: string;
  /** Original error if available */
  public readonly originalError?: Error;
  /** Additional context */
  public readonly context?: Record<string, unknown>;

  /**
   * Creates a new GoogleAPIError
   * @param message - Human-readable error message
   * @param code - Error code (e.g., 'FETCH_CONTACTS_FAILED')
   * @param originalError - Original error object if available
   * @param context - Additional context information
   */
  constructor(
    message: string,
    code: string = 'GOOGLE_API_ERROR',
    originalError?: Error,
    context?: Record<string, unknown>,
  ) {
    super(message);
    this.name = 'GoogleAPIError';
    this.code = code;
    this.originalError = originalError;
    this.context = context;

    // Maintains proper stack trace for where our error was thrown (only in V8 engines)
    if (Error.captureStackTrace) {
      Error.captureStackTrace(this, GoogleAPIError);
    }

    // Preserve original error stack if available
    if (originalError?.stack) {
      this.stack = `${this.stack}\n\nCaused by:\n${originalError.stack}`;
    }
  }
}
