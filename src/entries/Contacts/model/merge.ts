/**
 * @fileoverview Merging duplicate contacts into one (Story 5.2).
 * The kept ("main") contact keeps its own values; the others only fill gaps
 * and add entries it does not already have. Nothing is dropped: every phone,
 * email, address, website, relation, custom field, nickname, organization
 * and label of the others ends up on the main contact, de-duplicated.
 * @module Contacts/model/merge
 */

import type { Contact, ContactName, ContactPatch } from '../types/Contact';
import { buildPatch, createDraft } from '../components/card/cardModel';
import { clean, digitsOf, editableName, phoneKey } from './contactView';

/** Result of planning a merge */
export interface MergePlan {
  /** The main contact as it will look after the merge */
  merged: Contact;
  /** Etag plus the groups to write to the main contact; null when it needs no change */
  patch: ContactPatch | null;
  /** Labels of the other contacts that the main contact does not have yet */
  addLabels: string[];
}

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

const SYSTEM_LABELS = new Set(['contactGroups/myContacts', 'contactGroups/all']);

const lower = (v: string | null | undefined): string => clean(v).toLowerCase();

/**
 * Appends the entries of every list, keeping the first entry for each key.
 * @param lists - Lists in priority order (main contact first)
 * @param keyOf - Identity of an entry; '' entries are dropped
 * @returns Combined list, or undefined when empty
 */
function union<T>(lists: (T[] | null | undefined)[], keyOf: (item: T) => string): T[] | undefined {
  const seen = new Set<string>();
  const out: T[] = [];
  lists.forEach((list) =>
    (list ?? []).forEach((item) => {
      const key = keyOf(item);
      if (key && !seen.has(key)) {
        seen.add(key);
        out.push(item);
      }
    }),
  );
  return out.length ? out : undefined;
}

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
    (c.labels?.filter((l) => !SYSTEM_LABELS.has(l)).length ?? 0)
  );
};

/**
 * The contact suggested as the one to keep: most information, then most
 * recently updated.
 * @param contacts - Candidates (at least one)
 * @returns Resource name of the suggested contact
 */
export const suggestPrimary = (contacts: Contact[]): string =>
  [...contacts].sort(
    (a, b) =>
      richnessOf(b) - richnessOf(a) || (b.updateTime ?? '').localeCompare(a.updateTime ?? ''),
  )[0].resourceName;

/**
 * Plans merging `others` into `primary`.
 * @param primary - Contact to keep
 * @param others - Contacts to fold in (and delete afterwards)
 * @returns Merged contact, patch and labels to add
 */
export const planMerge = (primary: Contact, others: Contact[]): MergePlan => {
  const all = [primary, ...others];
  const merged = createDraft(primary);

  // Name: the main contact's parts win; empty parts are filled from the others
  const name: ContactName = { ...(merged.names?.[0] ?? {}) };
  const otherNames = others.map((o) => editableName(o.names?.[0] ?? undefined));
  NAME_PARTS.forEach((part) => {
    if (!clean(name[part] as string | undefined)) {
      const donor = otherNames.find((n) => clean(n[part] as string | undefined));
      if (donor) {
        (name as Record<string, unknown>)[part] = donor[part];
        if (part.startsWith('phonetic')) {
          name.phoneticFullName = null;
        }
      }
    }
  });
  merged.names = [name];

  merged.nicknames = union(
    all.map((c) => c.nicknames),
    (x) => lower(x.value),
  );
  merged.phoneNumbers = union(
    all.map((c) => c.phoneNumbers),
    (x) => phoneKey(x.value, x.canonicalForm) ?? (digitsOf(x.value) || lower(x.value)),
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
    (x) => `${lower(x.name)}|${lower(x.title)}|${lower(x.department)}`.replace(/^\|\|$/, ''),
  );
  merged.urls = union(
    all.map((c) => c.urls),
    (x) => lower(x.value).replace(/\/+$/, ''),
  );
  merged.relations = union(
    all.map((c) => c.relations),
    (x) => `${lower(x.person)}|${lower(x.type)}`.replace(/^\|.*/, ''),
  );
  merged.userDefined = union(
    all.map((c) => c.userDefined),
    (x) => (clean(x.key) ? `${lower(x.key)}|${lower(x.value)}` : ''),
  );

  // Google allows one birthday and one note
  merged.birthdays = all.find((c) => c.birthdays?.length)?.birthdays ?? merged.birthdays;
  const notes = Array.from(
    new Set(all.flatMap((c) => (c.biographies ?? []).map((b) => clean(b.value)).filter(Boolean))),
  );
  if (notes.length > 1) {
    merged.biographies = [{ value: notes.join('\n\n'), contentType: 'TEXT_PLAIN' }];
  } else if (notes.length === 1 && !primary.biographies?.some((b) => clean(b.value))) {
    merged.biographies = [{ value: notes[0], contentType: 'TEXT_PLAIN' }];
  }

  const own = new Set(primary.labels ?? []);
  const addLabels = Array.from(
    new Set(
      others.flatMap((o) => o.labels ?? []).filter((l) => !own.has(l) && !SYSTEM_LABELS.has(l)),
    ),
  );
  merged.labels = [...(primary.labels ?? []), ...addLabels];

  return { merged, patch: buildPatch(primary, merged), addLabels };
};
