/**
 * @fileoverview Duplicate contact detection.
 * Contacts are linked when they share a phone number (compared by its last 9
 * digits, so "052-123-4567" and "+972 52 123 4567" match), an email address
 * (case-insensitive) or an identical normalized real name. Links are merged
 * transitively with union-find, so A~B and B~C form one group.
 *
 * A value shared by more than MAX_SHARED contacts is ignored: an office
 * switchboard, a family landline or a very common name says nothing about
 * duplication, and through transitivity it would chain unrelated people into
 * one giant group.
 * @module Contacts/model/duplicates
 */

import type { Contact } from '../types/Contact';
import { clean, compareByName, getDisplayName, hasName, phoneKey } from './contactView';

/** A key shared by more contacts than this is not evidence of duplication */
export const MAX_SHARED = 4;

/** Why contacts were grouped */
export type DuplicateReason = 'phone' | 'email' | 'name';

/** A set of contacts that are probably the same person */
export interface DuplicateGroup {
  /** Stable id: sorted member resource names */
  id: string;
  /** Members, sorted by name */
  members: Contact[];
  /** Every kind of match that links members of this group */
  reasons: DuplicateReason[];
  /** The shared values that caused the matches (formatted for display) */
  matchedValues: string[];
}

/**
 * Normalized name key: lowercase letters and digits only, so "Dana  Levi",
 * "dana levi" and "Dana-Levi" collide; names shorter than 3 letters are ignored.
 * @param contact - Contact
 * @returns Key or null
 */
export const nameKey = (contact: Contact): string | null => {
  if (!hasName(contact)) {
    return null;
  }
  const n = contact.names?.[0];
  const parts = [n?.givenName, n?.middleName, n?.familyName].filter(Boolean).join(' ');
  // Parts first: displayName includes prefixes, so "Dr. Dana Levi" would miss "Dana Levi"
  const key = (parts || getDisplayName(contact)).toLowerCase().replace(/[^\p{L}\p{N}]/gu, '');
  return key.length >= 3 ? key : null;
};

/**
 * Finds groups of likely-duplicate contacts.
 * @param contacts - All contacts
 * @returns Groups with 2+ members, largest first
 */
export const findDuplicateGroups = (contacts: Contact[]): DuplicateGroup[] => {
  const parent = contacts.map((_, i) => i);
  const find = (start: number): number => {
    let root = start;
    while (parent[root] !== root) {
      root = parent[root];
    }
    // Path compression: point every node on the way directly at the root
    let node = start;
    while (parent[node] !== root) {
      const next = parent[node];
      parent[node] = root;
      node = next;
    }
    return root;
  };
  const union = (a: number, b: number): void => {
    const ra = find(a);
    const rb = find(b);
    if (ra !== rb) {
      parent[rb] = ra;
    }
  };

  // Every distinct key of every contact, as "reason:key" plus its display text
  const keysOf = contacts.map((contact) => {
    const keys = new Map<string, { reason: DuplicateReason; value: string }>();
    (contact.phoneNumbers ?? []).forEach((p) => {
      const key = phoneKey(p.value, p.canonicalForm);
      if (key) {
        keys.set(`phone:${key}`, { reason: 'phone', value: clean(p.value) });
      }
    });
    (contact.emailAddresses ?? []).forEach((e) => {
      const key = clean(e.value).toLowerCase();
      if (key) {
        keys.set(`email:${key}`, { reason: 'email', value: key });
      }
    });
    const name = nameKey(contact);
    if (name) {
      keys.set(`name:${name}`, { reason: 'name', value: getDisplayName(contact) });
    }
    return keys;
  });

  const usage = new Map<string, number>();
  keysOf.forEach((keys) => keys.forEach((_, k) => usage.set(k, (usage.get(k) ?? 0) + 1)));

  // key -> first contact index seen with it
  const firstByKey = new Map<string, number>();
  // index pairs linked by a key, to attribute reasons afterwards
  const links: { a: number; b: number; reason: DuplicateReason; value: string }[] = [];

  keysOf.forEach((keys, index) => {
    keys.forEach(({ reason, value }, key) => {
      if ((usage.get(key) ?? 0) > MAX_SHARED) {
        return;
      }
      const seen = firstByKey.get(key);
      if (seen === undefined) {
        firstByKey.set(key, index);
      } else {
        union(seen, index);
        links.push({ a: seen, b: index, reason, value });
      }
    });
  });

  const byRoot = new Map<number, number[]>();
  contacts.forEach((_, i) => {
    const root = find(i);
    const list = byRoot.get(root) ?? [];
    list.push(i);
    byRoot.set(root, list);
  });

  const reasonsByRoot = new Map<number, { reasons: Set<DuplicateReason>; values: Set<string> }>();
  links.forEach(({ a, reason, value }) => {
    const root = find(a);
    const entry = reasonsByRoot.get(root) ?? { reasons: new Set(), values: new Set() };
    entry.reasons.add(reason);
    entry.values.add(value);
    reasonsByRoot.set(root, entry);
  });

  const order: DuplicateReason[] = ['phone', 'email', 'name'];
  const groups: DuplicateGroup[] = [];
  byRoot.forEach((indices, root) => {
    if (indices.length < 2) {
      return;
    }
    const members = indices.map((i) => contacts[i]).sort(compareByName);
    const meta = reasonsByRoot.get(root);
    groups.push({
      id: members
        .map((m) => m.resourceName)
        .sort()
        .join('|'),
      members,
      reasons: order.filter((r) => meta?.reasons.has(r)),
      matchedValues: Array.from(meta?.values ?? []),
    });
  });

  return groups.sort(
    (x, y) => y.members.length - x.members.length || compareByName(x.members[0], y.members[0]),
  );
};

/**
 * Resource names of every contact that belongs to some duplicate group.
 * @param groups - Duplicate groups
 * @returns Set of resource names
 */
export const duplicateMemberSet = (groups: DuplicateGroup[]): Set<string> =>
  new Set(groups.flatMap((g) => g.members.map((m) => m.resourceName)));
