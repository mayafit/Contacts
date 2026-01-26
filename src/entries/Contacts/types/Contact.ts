/**
 * @fileoverview Contact data model types for Google People API
 * @module Contacts/types/Contact
 */

/**
 * Contact name components
 */
export interface ContactName {
  /** Given name (first name) */
  givenName?: string;
  /** Family name (last name) */
  familyName?: string;
  /** Full display name */
  displayName?: string;
  /** Phonetic representation of full name */
  phoneticFullName?: string;
}

/**
 * Contact phone number
 */
export interface ContactPhone {
  /** Phone number value */
  value: string;
  /** Phone type (mobile, work, home, etc.) */
  type?: string;
}

/**
 * Contact email address
 */
export interface ContactEmail {
  /** Email address value */
  value: string;
  /** Email type (work, home, etc.) */
  type?: string;
}

/**
 * Contact physical address
 */
export interface ContactAddress {
  /** Street address */
  street?: string;
  /** City */
  city?: string;
  /** Country */
  country?: string;
  /** Postal code */
  postalCode?: string;
}

/**
 * Main contact interface matching Google People API v1 schema
 */
export interface Contact {
  /** Resource name identifier (e.g., "people/123") */
  resourceName: string;
  /** Contact names */
  names?: ContactName[];
  /** Phone numbers */
  phoneNumbers?: ContactPhone[];
  /** Email addresses */
  emailAddresses?: ContactEmail[];
  /** Physical addresses */
  addresses?: ContactAddress[];
}

/**
 * API Response wrapper for service methods
 */
export interface APIResponse<T> {
  /** Whether the operation succeeded */
  success: boolean;
  /** Response data if successful */
  data?: T;
  /** Error details if failed */
  error?: {
    /** Error code */
    code: string;
    /** Error message */
    message: string;
    /** Additional error details */
    details?: unknown;
  };
}
