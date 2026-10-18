/**
 * @fileoverview Address-book statistics for the Insights view.
 * @module Contacts/model/stats
 */

import type { Contact, ContactGroup } from '../types/Contact';
import type { DuplicateGroup } from './duplicates';
import {
  classifyPhone,
  getDisplayName,
  getLabelNames,
  getPhoneticName,
  getPhotoUrl,
  hasName,
  scriptOf,
  type NameScript,
  type PhoneOrigin,
} from './contactView';

/** Filters the Contacts view understands; Insights links into them */
export type ContactFilter =
  | 'all'
  | 'missingPhonetic'
  | 'noPhone'
  | 'noEmail'
  | 'noName'
  | 'duplicates';

/** One completeness row: how many contacts have a field */
export interface CompletenessRow {
  /** Stable key */
  key: string;
  /** Field label */
  label: string;
  /** Contacts that have the field */
  count: number;
  /** Filter that lists the contacts MISSING the field, when one exists */
  missingFilter?: ContactFilter;
}

/** A labelled count */
export interface CountRow {
  /** Label */
  label: string;
  /** Count */
  count: number;
}

/** Everything the Insights view shows */
export interface ContactStats {
  /** Number of contacts */
  total: number;
  /** Contacts with a phonetic name */
  withPhonetic: number;
  /** Contacts with a Hebrew (or mixed) name */
  hebrewNamed: number;
  /** Hebrew-named contacts that also have a phonetic name */
  hebrewNamedWithPhonetic: number;
  /** Field completeness rows */
  completeness: CompletenessRow[];
  /** Phone numbers by origin (counted per number) */
  phoneOrigins: CountRow[];
  /** Total phone numbers */
  phoneCount: number;
  /** Contacts by name script */
  nameScripts: CountRow[];
  /** Most used labels */
  topLabels: CountRow[];
  /** Birthdays per month, January first */
  birthdaysByMonth: CountRow[];
  /** Number of duplicate groups */
  duplicateGroups: number;
  /** Contacts that belong to a duplicate group */
  contactsInDuplicates: number;
}

const ORIGIN_LABELS: Record<PhoneOrigin, string> = {
  'il-mobile': 'Israeli mobile',
  'il-landline': 'Israeli landline',
  international: 'International',
  unparseable: 'Unrecognized',
};

const SCRIPT_LABELS: Record<NameScript, string> = {
  hebrew: 'Hebrew',
  latin: 'Latin',
  mixed: 'Mixed',
  other: 'Other script',
  none: 'No name',
};

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/**
 * Computes all statistics in one pass over the contacts.
 * @param contacts - All contacts
 * @param groups - Labels by resource name
 * @param duplicateGroups - Output of findDuplicateGroups
 * @param topLabelCount - How many labels to list
 * @returns Statistics
 */
export const computeContactStats = (
  contacts: Contact[],
  groups: Record<string, ContactGroup>,
  duplicateGroups: DuplicateGroup[],
  topLabelCount = 8,
): ContactStats => {
  const has = {
    name: 0,
    phonetic: 0,
    phone: 0,
    email: 0,
    organization: 0,
    birthday: 0,
    address: 0,
    photo: 0,
    notes: 0,
  };
  const origins: Record<PhoneOrigin, number> = {
    'il-mobile': 0,
    'il-landline': 0,
    international: 0,
    unparseable: 0,
  };
  const scripts: Record<NameScript, number> = { hebrew: 0, latin: 0, mixed: 0, other: 0, none: 0 };
  const labels = new Map<string, number>();
  const months = new Array<number>(12).fill(0);
  let phoneCount = 0;
  let hebrewNamed = 0;
  let hebrewNamedWithPhonetic = 0;

  contacts.forEach((c) => {
    const named = hasName(c);
    const phonetic = !!getPhoneticName(c);
    const script = named ? scriptOf(getDisplayName(c)) : 'none';
    scripts[script] += 1;
    if (named) {
      has.name += 1;
    }
    if (phonetic) {
      has.phonetic += 1;
    }
    if (script === 'hebrew' || script === 'mixed') {
      hebrewNamed += 1;
      if (phonetic) {
        hebrewNamedWithPhonetic += 1;
      }
    }
    if (c.phoneNumbers?.some((p) => p.value)) {
      has.phone += 1;
    }
    if (c.emailAddresses?.some((e) => e.value)) {
      has.email += 1;
    }
    if (c.organizations?.some((o) => o.name || o.title)) {
      has.organization += 1;
    }
    if (c.birthdays?.length) {
      has.birthday += 1;
    }
    if (c.addresses?.length) {
      has.address += 1;
    }
    if (getPhotoUrl(c)) {
      has.photo += 1;
    }
    if (c.biographies?.some((b) => b.value)) {
      has.notes += 1;
    }

    (c.phoneNumbers ?? []).forEach((p) => {
      if (!p.value) {
        return;
      }
      phoneCount += 1;
      origins[classifyPhone(p.value, p.canonicalForm)] += 1;
    });

    getLabelNames(c, groups).forEach((name) => labels.set(name, (labels.get(name) ?? 0) + 1));

    const month = c.birthdays?.[0]?.date?.month;
    if (month && month >= 1 && month <= 12) {
      months[month - 1] += 1;
    }
  });

  return {
    total: contacts.length,
    withPhonetic: has.phonetic,
    hebrewNamed,
    hebrewNamedWithPhonetic,
    completeness: [
      { key: 'name', label: 'Name', count: has.name, missingFilter: 'noName' },
      {
        key: 'phonetic',
        label: 'Phonetic name',
        count: has.phonetic,
        missingFilter: 'missingPhonetic',
      },
      { key: 'phone', label: 'Phone', count: has.phone, missingFilter: 'noPhone' },
      { key: 'email', label: 'Email', count: has.email, missingFilter: 'noEmail' },
      { key: 'organization', label: 'Organization', count: has.organization },
      { key: 'birthday', label: 'Birthday', count: has.birthday },
      { key: 'address', label: 'Address', count: has.address },
      { key: 'photo', label: 'Photo', count: has.photo },
      { key: 'notes', label: 'Notes', count: has.notes },
    ],
    phoneOrigins: (Object.keys(ORIGIN_LABELS) as PhoneOrigin[]).map((k) => ({
      label: ORIGIN_LABELS[k],
      count: origins[k],
    })),
    phoneCount,
    nameScripts: (Object.keys(SCRIPT_LABELS) as NameScript[]).map((k) => ({
      label: SCRIPT_LABELS[k],
      count: scripts[k],
    })),
    topLabels: Array.from(labels.entries())
      .map(([label, count]) => ({ label, count }))
      .sort((a, b) => b.count - a.count || a.label.localeCompare(b.label))
      .slice(0, topLabelCount),
    birthdaysByMonth: months.map((count, i) => ({ label: MONTHS[i], count })),
    duplicateGroups: duplicateGroups.length,
    contactsInDuplicates: duplicateGroups.reduce((sum, g) => sum + g.members.length, 0),
  };
};
