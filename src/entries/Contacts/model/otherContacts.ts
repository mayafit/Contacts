/**
 * @fileoverview Suggestions from Google's Other contacts: addresses and phones for the
 * Owner's Contacts, and people worth saving (AD-48 rules 4 and 5)
 * @module Contacts/model/otherContacts
 */

import type { Contact } from '../types/Contact';
import type { OtherContactEntry, PersonActivity } from '../../../services/api/BackendApiClient';
import { clean, foldForSearch, getDisplayName, phoneKey } from './contactView';
import { nameVariants } from './emailFind';

/** Why an Other contact is taken to be a Contact, strongest first */
export type MatchReason = 'email' | 'phone' | 'name' | 'address';

const REASON_ORDER: MatchReason[] = ['email', 'phone', 'name', 'address'];

/** Addresses and phones to add to a Contact */
export interface UpdateSuggestion {
  key: string;
  /** `missing`: the Contact has no address; `extra`: it has one */
  kind: 'missing' | 'extra';
  contact: Contact;
  emails: string[];
  phones: string[];
  reasons: MatchReason[];
  /** The names Other contacts gave this person */
  names: string[];
}

/** An Other contact worth saving as a new Contact */
export interface SaveSuggestion {
  key: string;
  name: string | null;
  emails: string[];
  phones: string[];
  sent: number;
  received: number;
  meetings: number;
  lastAt: string | null;
}

/** Everything the review lists */
export interface OtherSuggestions {
  updates: UpdateSuggestion[];
  saves: SaveSuggestion[];
  /** Entries left out because a step found several Contacts */
  ambiguous: number;
}

/** Saves listed at most */
export const MAX_SAVES = 100;

/**
 * A name as a set of words, for an order-free comparison: folded, lower case, every
 * non-letter a space, words sorted. Fewer than two words never match.
 * @param text - Name
 * @returns Key, or null
 */
export const wordKey = (text: string | null | undefined): string | null => {
  const words = foldForSearch(clean(text))
    .replace(/[^\p{L}\p{N}]+/gu, ' ')
    .trim()
    .split(' ')
    .filter(Boolean);
  return words.length >= 2 ? [...new Set(words)].sort().join(' ') : null;
};

/**
 * The name an address spells (dana.cohen@, dana_cohen2@), as a word key.
 * @param email - Address
 * @returns Key, or null
 */
export const localPartKey = (email: string): string | null =>
  wordKey(
    email
      .split('@')[0]
      .split('+')[0]
      .replace(/[._-]+/g, ' ')
      .replace(/\d+/g, ' '),
  );

const lower = (v: string | null | undefined) => clean(v).toLowerCase();

/** Index of a value to the Contacts holding it */
const indexBy = (contacts: Contact[], keysOf: (c: Contact) => (string | null)[]) => {
  const index = new Map<string, Set<string>>();
  contacts.forEach((c) =>
    keysOf(c).forEach((k) => {
      if (k) {
        index.set(k, (index.get(k) ?? new Set()).add(c.resourceName));
      }
    }),
  );
  return index;
};

/** The Contacts any of the keys points at */
const ownersOf = (index: Map<string, Set<string>>, keys: (string | null)[]): Set<string> => {
  const owners = new Set<string>();
  keys.forEach((k) => k && index.get(k)?.forEach((o) => owners.add(o)));
  return owners;
};

const phoneKeysOf = (c: Contact) =>
  (c.phoneNumbers ?? []).map((p) => phoneKey(p.value, p.canonicalForm));

const meetingsOf = (p: PersonActivity | undefined) =>
  Object.values(p?.meetingMonths ?? {}).reduce((s, n) => s + n, 0);

const later = (a: string | null, b: string | null | undefined) => (!b ? a : !a || b > a ? b : a);

/**
 * Matches one entry to a Contact: by address, then phone, then name, then the name the
 * address spells. The first step that finds exactly one decides; a step that finds
 * several stops (never a weaker step after an ambiguous one).
 */
const matchEntry = (
  entry: OtherContactEntry,
  steps: { reason: MatchReason; owners: () => Set<string> }[],
): { owner: string; reason: MatchReason } | 'ambiguous' | null => {
  for (const step of steps) {
    const owners = step.owners();
    if (owners.size === 1) {
      return { owner: [...owners][0], reason: step.reason };
    }
    if (owners.size > 1) {
      return 'ambiguous';
    }
  }
  return null;
};

/**
 * The review's suggestions (AD-48 rules 4 and 5).
 * @param entries - Other contacts, automated addresses already removed
 * @param contacts - Active Contacts (the targets)
 * @param allContacts - Every Contact, Trash included (their addresses are known)
 * @param people - Activity per address, or null without Gmail & Calendar
 * @param dismissed - Dismissed suggestion keys
 * @returns Updates, saves and how many entries were ambiguous
 */
export const buildOtherSuggestions = (
  entries: OtherContactEntry[],
  contacts: Contact[],
  allContacts: Contact[],
  people: Record<string, PersonActivity> | null,
  dismissed: Set<string>,
): OtherSuggestions => {
  const known = new Set(
    allContacts.flatMap((c) => (c.emailAddresses ?? []).map((e) => lower(e.value))),
  );
  const byEmail = indexBy(contacts, (c) => (c.emailAddresses ?? []).map((e) => lower(e.value)));
  const byPhone = indexBy(contacts, phoneKeysOf);
  const byName = indexBy(contacts, (c) => nameVariants(c).map(wordKey));
  const byId = new Map(contacts.map((c) => [c.resourceName, c]));

  const rows = new Map<
    string,
    {
      emails: Set<string>;
      phones: Map<string, string>;
      reasons: Set<MatchReason>;
      names: Set<string>;
    }
  >();
  const saves: SaveSuggestion[] = [];
  let ambiguous = 0;

  entries.forEach((entry) => {
    const emails = entry.emails.map(lower).filter(Boolean);
    const fresh = emails.filter((e) => !known.has(e));
    const match = matchEntry(entry, [
      { reason: 'email', owners: () => ownersOf(byEmail, emails) },
      {
        reason: 'phone',
        owners: () =>
          ownersOf(
            byPhone,
            entry.phones.map((p) => phoneKey(p)),
          ),
      },
      { reason: 'name', owners: () => ownersOf(byName, [wordKey(entry.name)]) },
      { reason: 'address', owners: () => ownersOf(byName, emails.map(localPartKey)) },
    ]);
    if (match === 'ambiguous') {
      ambiguous += 1;
      return;
    }

    if (match) {
      const contact = byId.get(match.owner)!;
      const have = new Set(phoneKeysOf(contact));
      const row = rows.get(match.owner) ?? {
        emails: new Set<string>(),
        phones: new Map<string, string>(),
        reasons: new Set<MatchReason>(),
        names: new Set<string>(),
      };
      fresh.forEach((e) => row.emails.add(e));
      entry.phones.forEach((p) => {
        const k = phoneKey(p);
        if (k && !have.has(k) && !row.phones.has(k)) {
          row.phones.set(k, clean(p));
        }
      });
      row.reasons.add(match.reason);
      if (entry.name) {
        row.names.add(clean(entry.name));
      }
      rows.set(match.owner, row);
      return;
    }

    // Nobody matches. An address on a Contact in Trash means the Owner put them there
    if (!people || fresh.length === 0 || fresh.length < emails.length) {
      return;
    }
    const activity = fresh.map((e) => people[e]).filter(Boolean);
    const sent = activity.reduce((s, p) => s + (p.sentCount ?? 0), 0);
    const meetings = activity.reduce((s, p) => s + meetingsOf(p), 0);
    if (sent < 1 && meetings < 1) {
      return;
    }
    saves.push({
      key: `n:${fresh[0]}`,
      name: entry.name ? clean(entry.name) : null,
      emails: fresh,
      phones: [...new Set(entry.phones.map(clean).filter(Boolean))],
      sent,
      received: activity.reduce((s, p) => s + (p.receivedCount ?? 0), 0),
      meetings,
      lastAt: activity.reduce<string | null>(
        (at, p) => later(later(at, p.lastEmailAt), p.lastMeetingAt),
        null,
      ),
    });
  });

  const updates = [...rows.entries()]
    .map(([id, row]): UpdateSuggestion | null => {
      const emails = [...row.emails];
      const phones = [...row.phones.values()];
      if (emails.length === 0 && phones.length === 0) {
        return null;
      }
      const contact = byId.get(id)!;
      return {
        key: `u:${id}:${[...emails, ...phones].sort().join(',')}`,
        kind: (contact.emailAddresses ?? []).some((e) => clean(e.value)) ? 'extra' : 'missing',
        contact,
        emails,
        phones,
        reasons: REASON_ORDER.filter((r) => row.reasons.has(r)),
        names: [...row.names],
      };
    })
    .filter((u): u is UpdateSuggestion => !!u && !dismissed.has(u.key))
    .sort(
      (a, b) =>
        (a.kind === b.kind ? 0 : a.kind === 'missing' ? -1 : 1) ||
        getDisplayName(a.contact).localeCompare(getDisplayName(b.contact), ['he', 'en']),
    );

  const seen = new Set<string>();
  const ranked = saves
    .filter((s) => !dismissed.has(s.key) && !seen.has(s.key) && seen.add(s.key))
    .sort(
      (a, b) =>
        b.sent * 3 + b.meetings * 3 + b.received - (a.sent * 3 + a.meetings * 3 + a.received),
    )
    .slice(0, MAX_SAVES);

  return { updates, saves: ranked, ambiguous };
};

/** How a reason reads in the review */
export const REASON_LABEL: Record<MatchReason, string> = {
  email: 'an address already on the contact',
  phone: 'same phone number',
  name: 'same name',
  address: 'the address spells the name',
};
