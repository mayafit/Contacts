/**
 * @fileoverview Merging duplicate contacts into one (Story 5.2).
 * The kept ("main") contact keeps all of its own entries and values; the
 * others add entries it does not already have and fill fields it lacks.
 * Anything that cannot be kept (a conflicting birthday or name part, since
 * Google allows one) is reported in `dropped` so the user sees it before
 * confirming.
 * @module Contacts/model/merge
 */

import type { Contact, ContactName, ContactPatch } from '../types/Contact';
import { buildPatch, createDraft } from '../components/card/cardModel';
import {
  clean,
  digitsOf,
  editableName,
  formatBirthday,
  getDisplayName,
  scriptOf,
  splitFullName,
} from './contactView';

/** Result of planning a merge */
export interface MergePlan {
  /** The main contact as it will look after the merge */
  merged: Contact;
  /** Etag plus the groups to write to the main contact; null when it needs no change */
  patch: ContactPatch | null;
  /** Labels of the other contacts that the main contact does not have yet */
  addLabels: string[];
  /** Values of the other contacts that will not be kept, as readable lines */
  dropped: string[];
  /** Names in English taken over as the phonetic name of a Hebrew-named contact */
  transliterated: string[];
}

/** First, middle and last name of a contact as one string */
const fullNameOf = (n: ContactName): string =>
  clean([n.givenName, n.middleName, n.familyName].filter(Boolean).join(' ')) ||
  clean(n.displayName);

/**
 * Whether a group holds the same person written in Hebrew and in English, the
 * case where the English name is the Hebrew contact's phonetic name.
 * @param contacts - Group members
 * @returns True when both scripts appear
 */
export const isHebrewEnglishPair = (contacts: Contact[]): boolean => {
  const scripts = new Set(contacts.map((c) => scriptOf(fullNameOf(c.names?.[0] ?? {}))));
  return scripts.has('hebrew') && scripts.has('latin');
};

const NAME_PARTS: (keyof ContactName)[] = [
  'honorificPrefix',
  'givenName',
  'middleName',
  'familyName',
  'honorificSuffix',
  'phoneticGivenName',
  'phoneticMiddleName',
  'phoneticFamilyName',
];

const NAME_PART_LABELS: Partial<Record<keyof ContactName, string>> = {
  honorificPrefix: 'prefix',
  givenName: 'first name',
  middleName: 'middle name',
  familyName: 'last name',
  honorificSuffix: 'suffix',
  phoneticGivenName: 'phonetic first name',
  phoneticMiddleName: 'phonetic middle name',
  phoneticFamilyName: 'phonetic last name',
};

/**
 * System groups a contact cannot be added to (Google only allows user labels
 * and "starred"); everyone is in myContacts anyway.
 */
const NON_ASSIGNABLE_LABELS = new Set([
  'contactGroups/myContacts',
  'contactGroups/all',
  'contactGroups/friends',
  'contactGroups/family',
  'contactGroups/coworkers',
  'contactGroups/chatBuddies',
  'contactGroups/blocked',
]);

const lower = (v: string | null | undefined): string => clean(v).toLowerCase();

/** Stable text of an entry, used when it has no natural identity */
const fullKey = (item: unknown): string => `#${JSON.stringify(item)}`;

/**
 * Copies of `kept` with its empty fields filled from `extra`.
 * @param kept - Entry that wins
 * @param extra - Entry with the same identity from another contact
 * @returns Combined entry
 */
function fillEmpty<T extends object>(kept: T, extra: T): T {
  const out = { ...kept } as Record<string, unknown>;
  Object.entries(extra).forEach(([k, v]) => {
    const cur = out[k];
    if (
      (cur === undefined || cur === null || cur === '') &&
      v !== undefined &&
      v !== null &&
      v !== ''
    ) {
      out[k] = v;
    }
  });
  return out as T;
}

/**
 * Appends the entries of every list. Entries with the same identity are
 * combined (the first one wins, its empty fields are filled from the
 * others). An entry without a natural identity is never dropped: its full
 * content is used as the identity instead.
 * @param lists - Lists in priority order (main contact first)
 * @param keyOf - Natural identity of an entry ('' when it has none)
 * @returns Combined list, or undefined when empty
 */
function union<T extends object>(
  lists: (T[] | null | undefined)[],
  keyOf: (item: T) => string,
): T[] | undefined {
  const byKey = new Map<string, number>();
  const out: T[] = [];
  lists.forEach((list) =>
    (list ?? []).forEach((item) => {
      const key = keyOf(item) || fullKey(item);
      const at = byKey.get(key);
      if (at === undefined) {
        byKey.set(key, out.length);
        out.push(item);
      } else {
        out[at] = fillEmpty(out[at], item);
      }
    }),
  );
  return out.length ? out : undefined;
}

/**
 * Identity of a phone number for merging: the full international digits, so
 * +1 212 555 1234 and +1 312 555 1234 stay separate while 052-... and
 * +972 52-... are recognized as one.
 * @param value - Phone text
 * @param canonicalForm - Google's E.164 form
 * @returns Digits key ('' when there are none)
 */
export const mergePhoneKey = (value: string, canonicalForm?: string | null): string => {
  let d = digitsOf(canonicalForm || value);
  if (d.startsWith('00')) {
    d = d.slice(2);
  } else if (d.startsWith('0') && (d.length === 9 || d.length === 10)) {
    d = `972${d.slice(1)}`;
  }
  return d;
};

/**
 * How much information a contact holds; used to suggest the contact to keep.
 * @param c - Contact
 * @returns Score
 */
export const richnessOf = (c: Contact): number => {
  const n = c.names?.[0];
  const nameParts = NAME_PARTS.filter((k) => clean(n?.[k] as string | undefined)).length;
  return (
    nameParts +
    (c.phoneNumbers?.length ?? 0) * 2 +
    (c.emailAddresses?.length ?? 0) * 2 +
    (c.addresses?.length ?? 0) +
    (c.organizations?.length ?? 0) +
    (c.birthdays?.length ?? 0) +
    (c.urls?.length ?? 0) +
    (c.relations?.length ?? 0) +
    (c.userDefined?.length ?? 0) +
    (c.biographies?.some((b) => clean(b.value)) ? 1 : 0) +
    (c.labels?.filter((l) => !NON_ASSIGNABLE_LABELS.has(l)).length ?? 0)
  );
};

/**
 * The contact suggested as the one to keep: most information, then most
 * recently updated.
 * @param contacts - Candidates (at least one)
 * @returns Resource name of the suggested contact
 */
export const suggestPrimary = (contacts: Contact[]): string => {
  // For a Hebrew/English pair keep the Hebrew one: the English name becomes its phonetic name
  const hebrew = contacts.filter((c) => scriptOf(fullNameOf(c.names?.[0] ?? {})) === 'hebrew');
  const pool = isHebrewEnglishPair(contacts) && hebrew.length ? hebrew : contacts;
  return [...pool].sort(
    (a, b) =>
      richnessOf(b) - richnessOf(a) || (b.updateTime ?? '').localeCompare(a.updateTime ?? ''),
  )[0].resourceName;
};

/**
 * Plans merging `others` into `primary`.
 * @param primary - Contact to keep
 * @param others - Contacts to fold in (and delete afterwards)
 * @returns Merged contact, patch, labels to add and what will be dropped
 */
export const planMerge = (primary: Contact, others: Contact[]): MergePlan => {
  const all = [primary, ...others];
  const merged = createDraft(primary);
  const dropped: string[] = [];

  // Name: the main contact's parts win; empty parts are filled from the others.
  // A different non-empty part on another contact cannot be kept (one name per
  // contact), so it is reported.
  const name: ContactName = { ...(merged.names?.[0] ?? {}) };
  const transliterated: string[] = [];
  const keptIsHebrew = scriptOf(fullNameOf(name)) === 'hebrew';
  others.forEach((o) => {
    const n = editableName(o.names?.[0] ?? undefined);
    const theirFull = fullNameOf(n);
    // The same person written in English: that is the Hebrew name's phonetic
    // name, not a conflicting name
    if (keptIsHebrew && scriptOf(theirFull) === 'latin') {
      // Only a display name on file: split it into first word / rest
      const split = splitFullName(theirFull);
      const parts: ContactName =
        n.givenName || n.familyName ? n : { givenName: split.first, familyName: split.last };
      const ourPhonetic = clean(
        [name.phoneticGivenName, name.phoneticMiddleName, name.phoneticFamilyName]
          .filter(Boolean)
          .join(' '),
      );
      // Explicit phonetic parts on the English contact win over its plain name
      const explicit = n.phoneticGivenName || n.phoneticMiddleName || n.phoneticFamilyName;
      if (!ourPhonetic) {
        name.phoneticGivenName = (explicit ? n.phoneticGivenName : parts.givenName) ?? null;
        name.phoneticMiddleName = (explicit ? n.phoneticMiddleName : parts.middleName) ?? null;
        name.phoneticFamilyName = (explicit ? n.phoneticFamilyName : parts.familyName) ?? null;
        name.phoneticFullName = null;
        transliterated.push(theirFull);
      } else if (ourPhonetic.toLowerCase() !== theirFull.toLowerCase()) {
        dropped.push(
          `${getDisplayName(o)}: English name "${theirFull}" (phonetic name is "${ourPhonetic}")`,
        );
      }
      // Prefix/suffix of the English contact are not part of the phonetic name
      (['honorificPrefix', 'honorificSuffix'] as const).forEach((part) => {
        const theirs = clean(n[part]);
        if (theirs && !clean(name[part])) {
          name[part] = n[part];
        } else if (theirs && clean(name[part]).toLowerCase() !== theirs.toLowerCase()) {
          dropped.push(`${getDisplayName(o)}: ${NAME_PART_LABELS[part]} "${theirs}"`);
        }
      });
      return;
    }
    NAME_PARTS.forEach((part) => {
      const theirs = clean(n[part] as string | undefined);
      if (!theirs) {
        return;
      }
      const ours = clean(name[part] as string | undefined);
      if (!ours) {
        (name as Record<string, unknown>)[part] = n[part];
        if (part.startsWith('phonetic')) {
          name.phoneticFullName = null;
        }
      } else if (ours.toLowerCase() !== theirs.toLowerCase()) {
        dropped.push(`${getDisplayName(o)}: ${NAME_PART_LABELS[part]} "${theirs}"`);
      }
    });
  });
  merged.names = [name];

  merged.nicknames = union(
    all.map((c) => c.nicknames),
    (x) => lower(x.value),
  );
  merged.phoneNumbers = union(
    all.map((c) => c.phoneNumbers),
    (x) => mergePhoneKey(x.value, x.canonicalForm) || lower(x.value),
  );
  merged.emailAddresses = union(
    all.map((c) => c.emailAddresses),
    (x) => lower(x.value),
  );
  merged.addresses = union(
    all.map((c) => c.addresses),
    (x) =>
      lower(
        x.formattedValue ||
          [x.streetAddress, x.extendedAddress, x.poBox, x.city, x.region, x.postalCode, x.country]
            .filter(Boolean)
            .join(', '),
      ),
  );
  merged.organizations = union(
    all.map((c) => c.organizations),
    (x) => (lower(x.name) || lower(x.title) ? `${lower(x.name)}|${lower(x.title)}` : ''),
  );
  merged.urls = union(
    all.map((c) => c.urls),
    (x) => lower(x.value).replace(/\/+$/, ''),
  );
  merged.relations = union(
    all.map((c) => c.relations),
    (x) => (lower(x.person) ? `${lower(x.person)}|${lower(x.type)}` : ''),
  );
  merged.userDefined = union(
    all.map((c) => c.userDefined),
    (x) => (clean(x.key) ? `${lower(x.key)}|${lower(x.value)}` : ''),
  );

  // Google allows one birthday: keep the first one; report different ones
  const birthdayOwner = all.find((c) => c.birthdays?.length);
  if (birthdayOwner) {
    merged.birthdays = birthdayOwner.birthdays;
    const keptText = formatBirthday(birthdayOwner);
    others
      .filter((o) => o !== birthdayOwner && o.birthdays?.length)
      .forEach((o) => {
        const theirs = formatBirthday(o);
        if (theirs && theirs !== keptText) {
          dropped.push(`${getDisplayName(o)}: birthday ${theirs}`);
        }
      });
  }

  // Google allows one note: join distinct notes, keeping their line breaks
  const notes = Array.from(
    new Set(
      all.flatMap((c) => (c.biographies ?? []).map((b) => (b.value ?? '').trim()).filter(Boolean)),
    ),
  );
  const ownNote = primary.biographies?.some((b) => (b.value ?? '').trim());
  if (notes.length > 1 || (notes.length === 1 && !ownNote)) {
    merged.biographies = [{ value: notes.join('\n\n'), contentType: 'TEXT_PLAIN' }];
  }

  const own = new Set(primary.labels ?? []);
  const addLabels = Array.from(
    new Set(
      others
        .flatMap((o) => o.labels ?? [])
        .filter((l) => !own.has(l) && !NON_ASSIGNABLE_LABELS.has(l)),
    ),
  );
  merged.labels = [...(primary.labels ?? []), ...addLabels];

  return { merged, patch: buildPatch(primary, merged), addLabels, dropped, transliterated };
};
