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
  /** Formatted address string */
  formattedValue?: string;
}

/**
 * Contact organization
 */
export interface ContactOrganization {
  /** Organization name */
  name?: string;
  /** Job title */
  title?: string;
  /** Department */
  department?: string;
}

/**
 * Contact birthday
 */
export interface ContactBirthday {
  /** Birthday date */
  date?: {
    /** Year */
    year?: number;
    /** Month (1-12) */
    month?: number;
    /** Day of month */
    day?: number;
  };
}

/**
 * Contact URL
 */
export interface ContactUrl {
  /** URL value */
  value: string;
  /** URL type */
  type?: string;
}

/**
 * Contact biography/notes
 */
export interface ContactBiography {
  /** Biography text */
  value: string;
  /** Content type */
  contentType?: string;
}

/**
 * User-defined custom field
 */
export interface ContactUserDefined {
  /** Field key */
  key: string;
  /** Field value */
  value: string;
}

/**
 * Contact relation
 */
export interface ContactRelation {
  /** Related person name */
  person: string;
  /** Relation type (spouse, child, etc.) */
  type?: string;
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
  /** Organizations and job titles */
  organizations?: ContactOrganization[];
  /** Birthdays */
  birthdays?: ContactBirthday[];
  /** URLs/websites */
  urls?: ContactUrl[];
  /** Biographies/notes */
  biographies?: ContactBiography[];
  /** User-defined custom fields */
  userDefined?: ContactUserDefined[];
  /** Relations (spouse, child, etc.) */
  relations?: ContactRelation[];
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
