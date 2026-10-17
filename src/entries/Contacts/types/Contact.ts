/**
 * @fileoverview Contact data model types (mirrors ContactsApi.Core.Entities.Contact)
 * @module Contacts/types/Contact
 */

/**
 * Contact name components
 */
export interface ContactName {
  /** Full display name (computed by Google, read-only) */
  displayName?: string | null;
  /** Prefix, e.g. "Dr." */
  honorificPrefix?: string | null;
  /** Given name (first name) */
  givenName?: string | null;
  /** Middle name */
  middleName?: string | null;
  /** Family name (last name) */
  familyName?: string | null;
  /** Suffix, e.g. "Jr." */
  honorificSuffix?: string | null;
  /** Phonetic full name as Google reports it */
  phoneticFullName?: string | null;
  /** Phonetic given name */
  phoneticGivenName?: string | null;
  /** Phonetic middle name */
  phoneticMiddleName?: string | null;
  /** Phonetic family name */
  phoneticFamilyName?: string | null;
}

/** Contact nickname */
export interface ContactNickname {
  /** Nickname value */
  value: string;
}

/** Contact phone number */
export interface ContactPhone {
  /** Phone number as entered */
  value: string;
  /** Phone type (mobile, work, home, or a custom label) */
  type?: string | null;
  /** E.164 form computed by Google when parseable (read-only) */
  canonicalForm?: string | null;
}

/** Contact email address */
export interface ContactEmail {
  /** Email address */
  value: string;
  /** Email type (home, work, other, or a custom label) */
  type?: string | null;
  /** Display name for the address */
  displayName?: string | null;
}

/** Contact physical address */
export interface ContactAddress {
  /** Unstructured address as Google formats it */
  formattedValue?: string | null;
  /** Type (home, work, other, or a custom label) */
  type?: string | null;
  /** Street address */
  streetAddress?: string | null;
  /** Extended address, e.g. apartment */
  extendedAddress?: string | null;
  /** P.O. box */
  poBox?: string | null;
  /** City */
  city?: string | null;
  /** Region / state */
  region?: string | null;
  /** Postal code */
  postalCode?: string | null;
  /** Country name */
  country?: string | null;
  /** ISO 3166-1 alpha-2 country code */
  countryCode?: string | null;
}

/** Contact organization */
export interface ContactOrganization {
  /** Organization name */
  name?: string | null;
  /** Job title */
  title?: string | null;
  /** Department */
  department?: string | null;
  /** Type (work, school, or a custom label) */
  type?: string | null;
  /** Job description */
  jobDescription?: string | null;
}

/** Calendar date where any part may be missing */
export interface ContactDate {
  /** Year, absent/0 when unknown */
  year?: number | null;
  /** Month 1-12 */
  month?: number | null;
  /** Day of month */
  day?: number | null;
}

/** Contact birthday */
export interface ContactBirthday {
  /** Structured date */
  date?: ContactDate | null;
  /** Free-text birthday when no structured date exists */
  text?: string | null;
}

/** Contact URL */
export interface ContactUrl {
  /** URL value */
  value: string;
  /** URL type */
  type?: string | null;
}

/** Contact notes */
export interface ContactBiography {
  /** Note text */
  value: string;
  /** Content type (TEXT_PLAIN or TEXT_HTML) */
  contentType?: string | null;
}

/** User-defined custom field */
export interface ContactUserDefined {
  /** Field key */
  key: string;
  /** Field value */
  value: string;
}

/** Contact relation */
export interface ContactRelation {
  /** Related person name */
  person: string;
  /** Relation type (spouse, child, etc.) */
  type?: string | null;
}

/** Contact photo */
export interface ContactPhoto {
  /** Photo URL */
  url: string;
  /** True when Google shows a generated placeholder */
  isDefault?: boolean;
}

/** Contact group (label) */
export interface ContactGroup {
  /** Resource name, e.g. "contactGroups/abc" */
  resourceName: string;
  /** Human-readable name */
  name: string;
  /** USER_CONTACT_GROUP or SYSTEM_CONTACT_GROUP */
  groupType?: string | null;
  /** Number of members */
  memberCount?: number;
}

/**
 * Main contact interface matching the backend Contact entity
 */
export interface Contact {
  /** Resource name identifier (e.g., "people/c123") */
  resourceName: string;
  /** Version tag; must accompany every update */
  etag?: string | null;
  /** Names (one per contact) */
  names?: ContactName[] | null;
  /** Nicknames */
  nicknames?: ContactNickname[] | null;
  /** Phone numbers */
  phoneNumbers?: ContactPhone[] | null;
  /** Email addresses */
  emailAddresses?: ContactEmail[] | null;
  /** Physical addresses */
  addresses?: ContactAddress[] | null;
  /** Organizations and job titles */
  organizations?: ContactOrganization[] | null;
  /** Birthdays */
  birthdays?: ContactBirthday[] | null;
  /** URLs/websites */
  urls?: ContactUrl[] | null;
  /** Biographies/notes */
  biographies?: ContactBiography[] | null;
  /** Relations (spouse, child, etc.) */
  relations?: ContactRelation[] | null;
  /** User-defined custom fields */
  userDefined?: ContactUserDefined[] | null;
  /** Photos (read-only) */
  photos?: ContactPhoto[] | null;
  /** Label resource names (read-only) */
  labels?: string[] | null;
  /** Last update time in Google, ISO 8601 (read-only) */
  updateTime?: string | null;
}

/**
 * The field groups that can be written back to Google.
 * An update sends only the groups that changed.
 */
export type EditableGroup =
  | 'names'
  | 'nicknames'
  | 'phoneNumbers'
  | 'emailAddresses'
  | 'addresses'
  | 'organizations'
  | 'birthdays'
  | 'urls'
  | 'biographies'
  | 'relations'
  | 'userDefined';

/** All editable groups, in card display order */
export const EDITABLE_GROUPS: EditableGroup[] = [
  'names',
  'nicknames',
  'phoneNumbers',
  'emailAddresses',
  'addresses',
  'organizations',
  'birthdays',
  'urls',
  'biographies',
  'relations',
  'userDefined',
];

/** A partial update: etag plus the changed groups */
export type ContactPatch = Pick<Contact, 'etag'> & Partial<Pick<Contact, EditableGroup>>;

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
    /** HTTP status, when the failure came from the backend */
    status?: number;
    /** Additional error details */
    details?: unknown;
  };
}
