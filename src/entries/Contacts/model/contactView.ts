/**
 * @fileoverview Pure helpers that derive display values from a Contact.
 * Shared by the table, the contact card, insights and the duplicate finder.
 * @module Contacts/model/contactView
 */

import type { Contact, ContactGroup, ContactName } from '../types/Contact';

/** Locale-aware collator for Hebrew + Latin names ("base" ignores case and niqqud) */
export const nameCollator = new Intl.Collator(['he', 'en'], { sensitivity: 'base', numeric: true });

/** Hebrew final letters fold to their regular form for indexing */
const HEBREW_FINALS: Record<string, string> = { ך: 'כ', ם: 'מ', ן: 'נ', ף: 'פ', ץ: 'צ' };

/** Hebrew alphabet in order (no final forms) */
export const HEBREW_LETTERS = 'אבגדהוזחטיכלמנסעפצקרשת'.split('');

/** Latin alphabet in order */
export const LATIN_LETTERS = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('');

/** Groups that every contact belongs to and that carry no information */
const SYSTEM_LABELS_HIDDEN = new Set(['contactGroups/myContacts', 'contactGroups/all']);

const HEBREW_RE = /[֐-׿]/;
const LATIN_RE = /[A-Za-z]/;

/**
 * Collapses whitespace and trims; returns '' for null/undefined.
 * @param value - Any string-ish value
 * @returns Clean string
 */
export const clean = (value: string | null | undefined): string =>
  (value ?? '').replace(/\s+/g, ' ').trim();

/**
 * First name entry of a contact (Google allows one).
 * @param contact - Contact
 * @returns The name, or undefined
 */
export const getName = (contact: Contact): ContactName | undefined =>
  contact.names?.[0] ?? undefined;

/**
 * True when the contact has any real name part.
 * @param contact - Contact
 * @returns Whether a name exists
 */
export const hasName = (contact: Contact): boolean => {
  const n = getName(contact);
  return !!clean(
    n?.displayName || [n?.givenName, n?.middleName, n?.familyName].filter(Boolean).join(' '),
  );
};

/**
 * Name to show for a contact, falling back to nickname, organization, email or phone
 * so that no row is blank.
 * @param contact - Contact
 * @returns Display name, never empty
 */
export const getDisplayName = (contact: Contact): string => {
  const n = getName(contact);
  const fromParts = clean(
    [n?.honorificPrefix, n?.givenName, n?.middleName, n?.familyName].filter(Boolean).join(' '),
  );
  return (
    clean(n?.displayName) ||
    fromParts ||
    clean(contact.nicknames?.[0]?.value) ||
    clean(contact.organizations?.[0]?.name) ||
    clean(contact.emailAddresses?.[0]?.value) ||
    clean(contact.phoneNumbers?.[0]?.value) ||
    '(no name)'
  );
};

/**
 * Phonetic name: structured parts when present, else Google's full phonetic form.
 * @param contact - Contact
 * @returns Phonetic name or ''
 */
export const getPhoneticName = (contact: Contact): string => {
  const n = getName(contact);
  const parts = clean(
    [n?.phoneticGivenName, n?.phoneticMiddleName, n?.phoneticFamilyName].filter(Boolean).join(' '),
  );
  return parts || clean(n?.phoneticFullName);
};

/**
 * Splits free text into a first word and the rest, the way the table edits
 * a full name in one cell ("Shmulik Mosek Baruch" -> "Shmulik" / "Mosek Baruch").
 * @param text - Full name text
 * @returns First and last parts ('' when absent)
 */
export const splitFullName = (text: string): { first: string; last: string } => {
  const words = clean(text).split(' ').filter(Boolean);
  return { first: words[0] ?? '', last: words.slice(1).join(' ') };
};

/**
 * A name object with phonetic parts filled from phoneticFullName when only the
 * full form exists, so editing the parts never silently drops it.
 * @param name - Name entry or undefined
 * @returns A copy safe to edit
 */
export const editableName = (name: ContactName | undefined): ContactName => {
  const copy: ContactName = { ...(name ?? {}) };
  const hasParts = !!(copy.phoneticGivenName || copy.phoneticMiddleName || copy.phoneticFamilyName);
  if (!hasParts && copy.phoneticFullName) {
    const { first, last } = splitFullName(copy.phoneticFullName);
    copy.phoneticGivenName = first;
    copy.phoneticFamilyName = last;
  }
  return copy;
};

/**
 * Primary phone number text.
 * @param contact - Contact
 * @returns Phone or ''
 */
export const getPrimaryPhone = (contact: Contact): string =>
  clean(contact.phoneNumbers?.[0]?.value);

/**
 * Primary email text.
 * @param contact - Contact
 * @returns Email or ''
 */
export const getPrimaryEmail = (contact: Contact): string =>
  clean(contact.emailAddresses?.[0]?.value);

/**
 * Primary organization name.
 * @param contact - Contact
 * @returns Organization or ''
 */
export const getOrganization = (contact: Contact): string =>
  clean(contact.organizations?.[0]?.name);

/**
 * Primary job title.
 * @param contact - Contact
 * @returns Title or ''
 */
export const getJobTitle = (contact: Contact): string => clean(contact.organizations?.[0]?.title);

/**
 * Digits only.
 * @param value - Any text
 * @returns The digits in order
 */
export const digitsOf = (value: string | null | undefined): string =>
  (value ?? '').replace(/\D/g, '');

/**
 * Key used to compare phone numbers regardless of formatting or country prefix:
 * the last 9 digits (an Israeli national number without its leading 0).
 * @param value - Phone text
 * @param canonicalForm - Google's E.164 form when known
 * @returns Comparison key, or null for numbers too short to be meaningful
 */
export const phoneKey = (
  value: string | null | undefined,
  canonicalForm?: string | null,
): string | null => {
  const digits = digitsOf(canonicalForm || value);
  if (digits.length < 7) {
    return null;
  }
  return digits.slice(-9);
};

/** Where a phone number belongs */
export type PhoneOrigin = 'il-mobile' | 'il-landline' | 'international' | 'unparseable';

/**
 * Classifies a phone number for insights.
 * @param value - Phone text
 * @param canonicalForm - Google's E.164 form when known
 * @returns Origin class
 */
export const classifyPhone = (
  value: string | null | undefined,
  canonicalForm?: string | null,
): PhoneOrigin => {
  const raw = clean(canonicalForm || value);
  let digits = digitsOf(raw);
  const explicitInternational = raw.startsWith('+') || digits.startsWith('00');
  if (digits.startsWith('00')) {
    digits = digits.slice(2);
  }
  let national: string;
  if (digits.startsWith('972')) {
    national = `0${digits.slice(3)}`;
  } else if (explicitInternational) {
    return digits.length >= 7 ? 'international' : 'unparseable';
  } else {
    national = digits;
  }
  if (/^05\d{8}$/.test(national)) {
    return 'il-mobile';
  }
  if (
    /^0[23489]\d{7}$/.test(national) ||
    /^07\d{8}$/.test(national) ||
    /^1[78]00\d{6}$/.test(national)
  ) {
    return 'il-landline';
  }
  return 'unparseable';
};

/** Writing system of a name */
export type NameScript = 'hebrew' | 'latin' | 'mixed' | 'other' | 'none';

/**
 * Which script a text is written in.
 * @param text - Text
 * @returns Script class
 */
export const scriptOf = (text: string | null | undefined): NameScript => {
  const t = clean(text);
  if (!t) {
    return 'none';
  }
  const he = HEBREW_RE.test(t);
  const la = LATIN_RE.test(t);
  if (he && la) {
    return 'mixed';
  }
  if (he) {
    return 'hebrew';
  }
  if (la) {
    return 'latin';
  }
  return 'other';
};

/**
 * Letter a name is filed under for the alphabet rail: a Hebrew letter (finals
 * folded), an uppercase Latin letter, or '#'.
 * @param name - Display name
 * @returns Index letter
 */
export const indexLetterOf = (name: string): string => {
  for (const ch of clean(name)) {
    if (HEBREW_RE.test(ch) && /[א-ת]/.test(ch)) {
      return HEBREW_FINALS[ch] ?? ch;
    }
    if (LATIN_RE.test(ch)) {
      return ch.toUpperCase();
    }
    if (/\p{L}/u.test(ch)) {
      return '#';
    }
    if (/\d/.test(ch)) {
      return '#';
    }
  }
  return '#';
};

/**
 * Up to two initials for the avatar.
 * @param name - Display name
 * @returns Initials
 */
export const initialsOf = (name: string): string => {
  const words = clean(name)
    .split(' ')
    .filter((w) => /\p{L}/u.test(w));
  if (words.length === 0) {
    return '#';
  }
  const first = Array.from(words[0])[0] ?? '';
  const last = words.length > 1 ? (Array.from(words[words.length - 1])[0] ?? '') : '';
  return (first + last).toUpperCase();
};

/** Muted avatar backgrounds; identity only, never data */
const AVATAR_COLORS = [
  '#3b5bdb',
  '#0c8599',
  '#2b8a3e',
  '#e67700',
  '#c2255c',
  '#7048e8',
  '#1971c2',
  '#5f3dc4',
];

/**
 * Stable avatar color for a contact.
 * @param key - Resource name
 * @returns Hex color
 */
export const avatarColorOf = (key: string): string => {
  let hash = 0;
  for (let i = 0; i < key.length; i += 1) {
    hash = (hash * 31 + key.charCodeAt(i)) | 0;
  }
  return AVATAR_COLORS[Math.abs(hash) % AVATAR_COLORS.length];
};

/**
 * A real (non-placeholder) photo URL, if any.
 * @param contact - Contact
 * @returns Photo URL or undefined
 */
export const getPhotoUrl = (contact: Contact): string | undefined =>
  contact.photos?.find((p) => !p.isDefault && p.url)?.url;

/**
 * Human-readable label names for a contact, hiding system groups everyone is in.
 * @param contact - Contact
 * @param groups - Map of group resource name to group
 * @returns Label names
 */
export const getLabelNames = (contact: Contact, groups: Record<string, ContactGroup>): string[] =>
  (contact.labels ?? [])
    .filter((r) => !SYSTEM_LABELS_HIDDEN.has(r))
    .map((r) => groups[r]?.name ?? '')
    .filter(Boolean);

/**
 * Birthday as d/m[/yyyy] (Israeli order), or the free text.
 * @param contact - Contact
 * @returns Formatted birthday or ''
 */
export const formatBirthday = (contact: Contact): string => {
  const b = contact.birthdays?.[0];
  if (!b) {
    return '';
  }
  const d = b.date;
  if (d?.month && d?.day) {
    return d.year ? `${d.day}/${d.month}/${d.year}` : `${d.day}/${d.month}`;
  }
  return clean(b.text);
};

/**
 * Lowercased text a search query is matched against.
 * @param contact - Contact
 * @returns Searchable text
 */
export const searchTextOf = (contact: Contact): string => {
  const n = getName(contact);
  return [
    getDisplayName(contact),
    n?.givenName,
    n?.familyName,
    getPhoneticName(contact),
    ...(contact.nicknames ?? []).map((x) => x.value),
    ...(contact.emailAddresses ?? []).map((x) => x.value),
    ...(contact.organizations ?? []).flatMap((x) => [x.name, x.title]),
    ...(contact.phoneNumbers ?? []).map((x) => x.value),
  ]
    .filter(Boolean)
    .join(' ')
    .toLowerCase();
};

/**
 * Whether a contact matches a free-text query. Every word must match; a query
 * with 3+ digits also matches phone numbers by digits, ignoring formatting.
 * @param contact - Contact
 * @param query - User query
 * @param text - Precomputed searchTextOf(contact), for speed
 * @returns Match
 */
export const matchesQuery = (
  contact: Contact,
  query: string,
  text = searchTextOf(contact),
): boolean => {
  const q = clean(query).toLowerCase();
  if (!q) {
    return true;
  }
  const qDigits = digitsOf(q);
  if (qDigits.length >= 3 && /^[\d\s()+-]+$/.test(q)) {
    // "050..." typed in local form also matches the same number stored as +972 50...
    // (stripping the 0 instead would make "050" match every number containing "50")
    const international = qDigits.startsWith('0') ? `972${qDigits.slice(1)}` : null;
    return (contact.phoneNumbers ?? []).some((p) =>
      [p.value, p.canonicalForm].some((form) => {
        const d = digitsOf(form);
        return !!d && (d.includes(qDigits) || (!!international && d.includes(international)));
      }),
    );
  }
  return q.split(' ').every((word) => text.includes(word));
};

/**
 * Compares two contacts by display name (Hebrew + Latin aware).
 * @param a - Contact
 * @param b - Contact
 * @returns Sort order
 */
export const compareByName = (a: Contact, b: Contact): number =>
  nameCollator.compare(getDisplayName(a), getDisplayName(b));
