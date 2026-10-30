/**
 * @fileoverview Segments (AD-45): saved rules evaluated on the contacts, relationship
 * tiers, organization and phone-country keys, and the groups suggested from them. No model:
 * every grouping is an exact rule.
 * @module Contacts/model/segments
 */

import type { Contact, ContactGroup } from '../types/Contact';
import type { ActivityIndex, ContactActivity } from './activity';
import {
  clean,
  getDisplayName,
  getName,
  getOrganization,
  getPhoneticName,
  matchesQuery,
  scriptOf,
} from './contactView';
import { callingCodeOf, countryName } from './callingCodes';
import { PERSONAL_DOMAINS } from './emailFind';

/** Google's favourites */
const STARRED = 'contactGroups/starred';

/** A relationship tier (rule 6) */
export type Tier = 'inner' | 'regular' | 'occasional' | 'dormant' | 'never';

/** Tiers in order, with their names */
export const TIERS: { id: Tier; name: string }[] = [
  { id: 'inner', name: 'Inner circle' },
  { id: 'regular', name: 'Regular' },
  { id: 'occasional', name: 'Occasional' },
  { id: 'dormant', name: 'Dormant' },
  { id: 'never', name: 'Never in touch' },
];

/** Fields a rule can ask for */
export type SegmentField =
  | 'phone'
  | 'email'
  | 'phonetic'
  | 'birthday'
  | 'address'
  | 'organization'
  | 'photo'
  | 'website';

/** One condition (rule 3) */
export type SegmentRule =
  | { kind: 'label'; op: 'in' | 'notIn'; value: string }
  | { kind: 'favourite'; value: boolean }
  | { kind: 'organization'; op: 'is' | 'contains'; value: string }
  | { kind: 'emailDomain'; value: string }
  | { kind: 'phoneCountry'; value: string }
  | { kind: 'script'; value: 'hebrew' | 'latin' | 'mixed' }
  | { kind: 'field'; op: 'has' | 'missing'; value: SegmentField }
  | { kind: 'birthdayMonth'; value: number }
  | { kind: 'lastTouch'; op: 'within' | 'olderThan' | 'never'; days?: number }
  | { kind: 'tier'; value: Tier }
  | { kind: 'text'; value: string };

/** A saved segment */
export interface Segment {
  id: string;
  name: string;
  match: 'all' | 'any';
  rules: SegmentRule[];
  /** A user label kept like the segment on request (rule 8) */
  label?: string | null;
}

/** The stored document (OwnerSettings kind "segments") */
export interface SegmentsDoc {
  version: 1;
  segments: Segment[];
}

/** Most segments kept, and most rules in one */
export const MAX_SEGMENTS = 100;
export const MAX_RULES = 20;

/** What evaluating needs beyond the contact */
export interface SegmentContext {
  /** Activity per contact; null without T1 */
  activity: ActivityIndex | null;
  /** The Owner's main calling code (rule 4) */
  mainCountry: string;
  now: Date;
}

const DAY = 86_400_000;

/**
 * The relationship tier (rule 6): score is emails + 3 × meetings in the last 12 months.
 * @param a - The contact's activity
 * @param now - Now
 * @returns Tier
 */
export const tierOf = (a: ContactActivity | undefined, now: Date = new Date()): Tier => {
  if (!a?.lastInTouch) {
    return 'never';
  }
  const days = (now.getTime() - Date.parse(a.lastInTouch)) / DAY;
  if (a.score >= 24 && days <= 30) {
    return 'inner';
  }
  if (a.score >= 6 && days <= 90) {
    return 'regular';
  }
  return days <= 365 ? 'occasional' : 'dormant';
};

// The last word dropped while it is one of these (rule 5)
const SUFFIXES = new Set([
  'ltd',
  'limited',
  'inc',
  'incorporated',
  'corp',
  'corporation',
  'llc',
  'plc',
  'gmbh',
  'co',
  'company',
  'בעמ',
]);

/**
 * An organization's key (rule 5): lower case, no punctuation, single spaces, without a
 * trailing company suffix ("Elta Systems Ltd." → "elta systems").
 * @param name - Organization name
 * @returns Key ('' when none)
 */
export const normalizeOrganization = (name: string | null | undefined): string => {
  const words = clean(name)
    .toLowerCase()
    .replace(/[״"'`׳.,()&/\\-]/g, ' ')
    .split(/\s+/)
    .filter(Boolean);
  // "בע מ" (the abbreviation's letters split by its quote mark)
  while (words.length > 1) {
    const last = words[words.length - 1];
    if (SUFFIXES.has(last)) {
      words.pop();
    } else if (last === 'מ' && words[words.length - 2] === 'בע' && words.length > 2) {
      words.splice(-2, 2);
    } else {
      break;
    }
  }
  return words.join(' ');
};

/**
 * The calling codes of a contact's numbers (rule 4): international numbers by their
 * prefix, national ones ("0…") as the main country.
 * @param c - Contact
 * @param main - The main calling code
 * @returns Codes
 */
export const phoneCountriesOf = (c: Contact, main: string): Set<string> => {
  const codes = new Set<string>();
  (c.phoneNumbers ?? []).forEach((p) => {
    const raw = clean(p.canonicalForm || p.value);
    const digits = raw.replace(/\D/g, '');
    if (!digits) {
      return;
    }
    if (raw.startsWith('+') || digits.startsWith('00')) {
      const code = callingCodeOf(digits.startsWith('00') ? digits.slice(2) : digits);
      if (code) {
        codes.add(code);
      }
    } else if (digits.startsWith('0')) {
      codes.add(main);
    }
  });
  return codes;
};

/**
 * The Owner's main country: the most common code of the international numbers, 972
 * when none.
 * @param contacts - Contacts
 * @returns Code
 */
export const mainCountryOf = (contacts: Contact[]): string => {
  const counts = new Map<string, number>();
  contacts.forEach((c) =>
    phoneCountriesOf(c, '').forEach((code) => {
      if (code) {
        counts.set(code, (counts.get(code) ?? 0) + 1);
      }
    }),
  );
  return [...counts.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] ?? '972';
};

/** The domains of a contact's addresses, lower case */
export const emailDomainsOf = (c: Contact): string[] =>
  (c.emailAddresses ?? [])
    .map((e) => clean(e.value).toLowerCase().split('@')[1] ?? '')
    .filter(Boolean);

/** Whether a contact holds a field (rule 3) */
export const hasField = (c: Contact, f: SegmentField): boolean => {
  switch (f) {
    case 'phone':
      return !!c.phoneNumbers?.some((p) => clean(p.value));
    case 'email':
      return !!c.emailAddresses?.some((e) => clean(e.value));
    case 'phonetic':
      return !!getPhoneticName(c);
    case 'birthday':
      return !!c.birthdays?.some((b) => b.date?.month || clean(b.text));
    case 'address':
      return !!c.addresses?.some(
        (a) => clean(a.formattedValue) || clean(a.streetAddress) || clean(a.city),
      );
    case 'organization':
      return !!getOrganization(c);
    case 'photo':
      return !!c.photos?.some((p) => p.url && !p.isDefault);
    case 'website':
      return !!c.urls?.some((u) => clean(u.value));
    default:
      return false;
  }
};

const fullName = (c: Contact): string => {
  const n = getName(c);
  return (
    clean([n?.givenName, n?.middleName, n?.familyName].filter(Boolean).join(' ')) ||
    clean(n?.displayName)
  );
};

/**
 * Whether a contact meets one rule. A rule needing the activity matches no one without it.
 * @param c - Contact
 * @param r - Rule
 * @param ctx - Context
 * @returns Match
 */
export const matchesRule = (c: Contact, r: SegmentRule, ctx: SegmentContext): boolean => {
  switch (r.kind) {
    case 'label':
      return (c.labels ?? []).includes(r.value) === (r.op === 'in');
    case 'favourite':
      return (c.labels ?? []).includes(STARRED) === r.value;
    case 'organization': {
      const org = normalizeOrganization(getOrganization(c));
      const want = normalizeOrganization(r.value);
      return !!want && (r.op === 'is' ? org === want : org.includes(want));
    }
    case 'emailDomain':
      return emailDomainsOf(c).includes(clean(r.value).toLowerCase());
    case 'phoneCountry':
      return phoneCountriesOf(c, ctx.mainCountry).has(r.value);
    case 'script':
      return scriptOf(fullName(c)) === r.value;
    case 'field':
      return hasField(c, r.value) === (r.op === 'has');
    case 'birthdayMonth':
      return !!c.birthdays?.some((b) => b.date?.month === r.value);
    case 'lastTouch': {
      if (!ctx.activity) {
        return false;
      }
      const last = ctx.activity.get(c.resourceName)?.lastInTouch;
      if (r.op === 'never') {
        return !last;
      }
      if (!last) {
        return r.op === 'olderThan';
      }
      const days = (ctx.now.getTime() - Date.parse(last)) / DAY;
      return r.op === 'within' ? days <= (r.days ?? 0) : days > (r.days ?? 0);
    }
    case 'tier':
      return !!ctx.activity && tierOf(ctx.activity.get(c.resourceName), ctx.now) === r.value;
    case 'text':
      return matchesQuery(c, r.value);
    default:
      return false;
  }
};

/** Whether a segment's rules need the activity (T1) */
export const needsActivity = (s: Pick<Segment, 'rules'>): boolean =>
  s.rules.some((r) => r.kind === 'lastTouch' || r.kind === 'tier');

/**
 * Whether a contact is a member: all rules, or any (no rule: all → everyone, any → no one).
 * @param c - Contact
 * @param s - Segment
 * @param ctx - Context
 * @returns Member
 */
export const matchesSegment = (
  c: Contact,
  s: Pick<Segment, 'match' | 'rules'>,
  ctx: SegmentContext,
): boolean =>
  s.match === 'all'
    ? s.rules.every((r) => matchesRule(c, r, ctx))
    : s.rules.some((r) => matchesRule(c, r, ctx));

/**
 * A segment's members.
 * @param contacts - Contacts
 * @param s - Segment
 * @param ctx - Context
 * @returns Members
 */
export const membersOf = (
  contacts: Contact[],
  s: Pick<Segment, 'match' | 'rules'>,
  ctx: SegmentContext,
): Contact[] => contacts.filter((c) => matchesSegment(c, s, ctx));

/**
 * How a linked label differs from its segment (rule 8).
 * @param members - The segment's members
 * @param contacts - All contacts
 * @param label - The label
 * @returns Contacts to add to it and to remove from it
 */
export const labelDiff = (
  members: Contact[],
  contacts: Contact[],
  label: string,
): { add: Contact[]; remove: Contact[] } => {
  const ids = new Set(members.map((m) => m.resourceName));
  return {
    add: members.filter((m) => !(m.labels ?? []).includes(label)),
    remove: contacts.filter((c) => (c.labels ?? []).includes(label) && !ids.has(c.resourceName)),
  };
};

const FIELD_NAMES: Record<SegmentField, string> = {
  phone: 'phone',
  email: 'email',
  phonetic: 'phonetic name',
  birthday: 'birthday',
  address: 'address',
  organization: 'organization',
  photo: 'photo',
  website: 'website',
};

const MONTHS = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December',
];

/**
 * A rule in words, for the editor and the menu.
 * @param r - Rule
 * @param groups - Labels by resource name
 * @returns "Label is Family", "Has no phonetic name"…
 */
export const describeRule = (r: SegmentRule, groups: Record<string, ContactGroup>): string => {
  switch (r.kind) {
    case 'label':
      return `${r.op === 'in' ? 'Has' : 'Has not'} the label ${groups[r.value]?.name ?? '(deleted)'}`;
    case 'favourite':
      return r.value ? 'Is a favourite' : 'Is not a favourite';
    case 'organization':
      return `Organization ${r.op === 'is' ? 'is' : 'contains'} “${r.value}”`;
    case 'emailDomain':
      return `Email at ${r.value}`;
    case 'phoneCountry':
      return `Phone in ${countryName(r.value)}`;
    case 'script':
      return `Name in ${r.value === 'hebrew' ? 'Hebrew' : r.value === 'latin' ? 'English' : 'mixed scripts'}`;
    case 'field':
      return `${r.op === 'has' ? 'Has' : 'Has no'} ${FIELD_NAMES[r.value]}`;
    case 'birthdayMonth':
      return `Birthday in ${MONTHS[r.value - 1] ?? '?'}`;
    case 'lastTouch':
      return r.op === 'never'
        ? 'Never in touch'
        : `In touch ${r.op === 'within' ? 'within' : 'more than'} ${r.days ?? 0} days${r.op === 'olderThan' ? ' ago' : ''}`;
    case 'tier':
      return `Tier: ${TIERS.find((t) => t.id === r.value)?.name ?? r.value}`;
    case 'text':
      return `Matches “${r.value}”`;
    default:
      return '';
  }
};

/** A group Insights proposes (rule 9) */
export interface SuggestedSegment {
  kind: 'organization' | 'emailDomain' | 'phoneCountry' | 'tier' | 'script';
  /** Stable key (kind and value) */
  key: string;
  name: string;
  rule: SegmentRule;
  members: Contact[];
}

/** Smallest suggested group, and most of one kind */
export const MIN_GROUP = 3;
export const MAX_PER_KIND = 8;

/**
 * Groups found in the contacts (rule 9): organization, work email domain, phone
 * country other than the main one, tier (T1) and script; largest first.
 * @param contacts - Contacts
 * @param ctx - Context
 * @returns Suggestions
 */
export const suggestSegments = (contacts: Contact[], ctx: SegmentContext): SuggestedSegment[] => {
  const out: SuggestedSegment[] = [];
  const grouped = (
    kind: SuggestedSegment['kind'],
    keysOf: (c: Contact) => string[],
    make: (key: string, sample: Contact) => { name: string; rule: SegmentRule },
    all = false,
  ) => {
    const by = new Map<string, Contact[]>();
    contacts.forEach((c) =>
      new Set(keysOf(c)).forEach((k) => {
        if (k) {
          by.set(k, [...(by.get(k) ?? []), c]);
        }
      }),
    );
    [...by.entries()]
      .filter(([, members]) => all || members.length >= MIN_GROUP)
      .sort((a, b) => b[1].length - a[1].length || a[0].localeCompare(b[0]))
      .slice(0, all ? undefined : MAX_PER_KIND)
      .forEach(([key, members]) => {
        const { name, rule } = make(key, members[0]);
        out.push({ kind, key: `${kind}:${key}`, name, rule, members });
      });
  };
  grouped(
    'organization',
    (c) => [normalizeOrganization(getOrganization(c))],
    (key, sample) => {
      const shown = getOrganization(sample) || key;
      return { name: shown, rule: { kind: 'organization', op: 'is', value: shown } };
    },
  );
  grouped(
    'emailDomain',
    (c) => emailDomainsOf(c).filter((d) => !PERSONAL_DOMAINS.has(d)),
    (key) => ({ name: `@${key}`, rule: { kind: 'emailDomain', value: key } }),
  );
  grouped(
    'phoneCountry',
    (c) => [...phoneCountriesOf(c, ctx.mainCountry)].filter((code) => code !== ctx.mainCountry),
    (key) => ({
      name: `Phones in ${countryName(key)}`,
      rule: { kind: 'phoneCountry', value: key },
    }),
  );
  if (ctx.activity) {
    const activity = ctx.activity;
    grouped(
      'tier',
      (c) => [tierOf(activity.get(c.resourceName), ctx.now)],
      (key) => ({
        name: TIERS.find((t) => t.id === key)?.name ?? key,
        rule: { kind: 'tier', value: key as Tier },
      }),
      true,
    );
  }
  grouped(
    'script',
    (c) => {
      const s = scriptOf(fullName(c));
      return s === 'hebrew' || s === 'latin' || s === 'mixed' ? [s] : [];
    },
    (key) => ({
      name:
        key === 'hebrew'
          ? 'Hebrew names'
          : key === 'latin'
            ? 'English names'
            : 'Mixed-script names',
      rule: { kind: 'script', value: key as 'hebrew' | 'latin' | 'mixed' },
    }),
  );
  return out;
};

/**
 * A new segment's id.
 * @returns Random id
 */
export const newSegmentId = (): string =>
  Math.random().toString(36).slice(2, 10) + Date.now().toString(36);

/**
 * Members sorted for showing (by name).
 * @param list - Contacts
 * @returns Sorted copy
 */
export const byName = (list: Contact[]): Contact[] =>
  [...list].sort((a, b) => getDisplayName(a).localeCompare(getDisplayName(b), ['he', 'en']));
