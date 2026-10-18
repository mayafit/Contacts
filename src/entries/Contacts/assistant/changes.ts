/**
 * @fileoverview Turns the assistant's proposed edits into a contact draft and a readable diff
 * @module Contacts/assistant/changes
 */

import type { Contact, ContactGroup, ContactPatch } from '../types/Contact';
import { EDITABLE_GROUPS } from '../types/Contact';
import { createDraft } from '../components/card/cardModel';
import {
  clean,
  digitsOf,
  editableName,
  formatBirthday,
  getJobTitle,
  getName,
  getOrganization,
  getPhoneticName,
} from '../model/contactView';

/** Single-value fields the assistant can set ("" clears) */
export interface FieldChanges {
  givenName?: string;
  middleName?: string;
  familyName?: string;
  phoneticGivenName?: string;
  phoneticMiddleName?: string;
  phoneticFamilyName?: string;
  nickname?: string;
  organization?: string;
  jobTitle?: string;
  department?: string;
  /** YYYY-MM-DD, or --MM-DD without a year */
  birthday?: string;
  notes?: string;
}

/** A value with an optional type (home, work, mobile...) */
export interface TypedValue {
  value: string;
  type?: string;
}

/** Everything the assistant may change on one contact */
export interface ContactChange {
  /** Contact resource name */
  id: string;
  set?: FieldChanges;
  addEmails?: TypedValue[];
  removeEmails?: string[];
  addPhones?: TypedValue[];
  removePhones?: string[];
  addUrls?: TypedValue[];
  /** Label names; missing labels are created */
  addLabels?: string[];
  removeLabels?: string[];
}

/** One line of a diff */
export interface DiffLine {
  field: string;
  from: string;
  to: string;
}

/** A change resolved against the current contact */
export interface ResolvedChange {
  /** Contact as it was when the change was proposed */
  base: Contact;
  /** Contact with the field changes applied */
  draft: Contact;
  /** Human-readable changes, labels included */
  diff: DiffLine[];
  addLabels: string[];
  removeLabels: string[];
  /** Problems found (unknown label to remove, bad date...) */
  warnings: string[];
}

const NAME_KEYS = [
  'givenName',
  'middleName',
  'familyName',
  'phoneticGivenName',
  'phoneticMiddleName',
  'phoneticFamilyName',
] as const;

const NAME_LABELS: Record<(typeof NAME_KEYS)[number], string> = {
  givenName: 'First name',
  middleName: 'Middle name',
  familyName: 'Last name',
  phoneticGivenName: 'Phonetic first name',
  phoneticMiddleName: 'Phonetic middle name',
  phoneticFamilyName: 'Phonetic last name',
};

/**
 * Parses YYYY-MM-DD, --MM-DD or MM-DD into a date.
 * @param text - Date text
 * @returns Date parts, or null when invalid
 */
export const parseBirthday = (
  text: string,
): { year?: number; month: number; day: number } | null => {
  const m = /^(?:(\d{4})|-?-?)-?(\d{1,2})-(\d{1,2})$/.exec(text.trim());
  if (!m) {
    return null;
  }
  const month = Number(m[2]);
  const day = Number(m[3]);
  if (month < 1 || month > 12 || day < 1 || day > 31) {
    return null;
  }
  return m[1] ? { year: Number(m[1]), month, day } : { month, day };
};

const sameEmail = (a: string, b: string) => clean(a).toLowerCase() === clean(b).toLowerCase();
const samePhone = (a: string, b: string) => {
  const da = digitsOf(a);
  const db = digitsOf(b);
  // Compare the last 9 digits so +972 54... and 054... match
  return !!da && !!db && da.slice(-9) === db.slice(-9);
};

/**
 * Applies a change to a copy of the contact.
 * @param contact - Current contact
 * @param change - Proposed change
 * @param groups - Labels by resource name
 * @returns Draft, diff and label changes
 */
export const resolveChange = (
  contact: Contact,
  change: ContactChange,
  groups: Record<string, ContactGroup>,
): ResolvedChange => {
  const draft = createDraft(contact);
  const diff: DiffLine[] = [];
  const warnings: string[] = [];
  const set = change.set ?? {};

  // Names
  if (NAME_KEYS.some((k) => set[k] !== undefined)) {
    const before = editableName(getName(contact));
    const name = { ...before };
    NAME_KEYS.forEach((k) => {
      if (set[k] !== undefined && clean(set[k]) !== clean(before[k])) {
        diff.push({ field: NAME_LABELS[k], from: clean(before[k]), to: clean(set[k]) });
        name[k] = clean(set[k]) || null;
      }
    });
    // Google rebuilds the display name from the parts
    draft.names = [{ ...name, displayName: null, phoneticFullName: null }];
  }

  if (set.nickname !== undefined && clean(set.nickname) !== clean(contact.nicknames?.[0]?.value)) {
    diff.push({
      field: 'Nickname',
      from: clean(contact.nicknames?.[0]?.value),
      to: clean(set.nickname),
    });
    const rest = (draft.nicknames ?? []).slice(1);
    draft.nicknames = clean(set.nickname)
      ? [{ ...(draft.nicknames?.[0] ?? {}), value: clean(set.nickname) }, ...rest]
      : rest;
  }

  // Organization: the first entry
  const orgFields: [keyof FieldChanges, 'name' | 'title' | 'department', string, string][] = [
    ['organization', 'name', 'Organization', getOrganization(contact)],
    ['jobTitle', 'title', 'Job title', getJobTitle(contact)],
    ['department', 'department', 'Department', clean(contact.organizations?.[0]?.department)],
  ];
  if (orgFields.some(([k, , , before]) => set[k] !== undefined && clean(set[k]) !== before)) {
    const orgs = [...(draft.organizations ?? [])];
    const first = { ...(orgs[0] ?? {}) };
    orgFields.forEach(([k, prop, label, before]) => {
      if (set[k] !== undefined && clean(set[k]) !== before) {
        diff.push({ field: label, from: before, to: clean(set[k]) });
        first[prop] = clean(set[k]) || null;
      }
    });
    orgs[0] = first;
    // Drop the entry only when nothing at all is left in it (location, symbol... count)
    draft.organizations = orgs.filter(
      (o, i) => i > 0 || Object.values(o).some((v) => v !== null && v !== undefined && v !== ''),
    );
  }

  if (set.birthday !== undefined) {
    if (!clean(set.birthday)) {
      if (contact.birthdays?.length) {
        diff.push({ field: 'Birthday', from: formatBirthday(contact), to: '' });
        draft.birthdays = [];
      }
    } else {
      const date = parseBirthday(set.birthday);
      if (!date) {
        warnings.push(`"${set.birthday}" is not a date (use YYYY-MM-DD or --MM-DD)`);
      } else {
        const next = { date };
        const shown = formatBirthday({ ...contact, birthdays: [next] } as Contact);
        if (shown !== formatBirthday(contact)) {
          diff.push({ field: 'Birthday', from: formatBirthday(contact), to: shown });
          draft.birthdays = [next, ...(draft.birthdays ?? []).slice(1)];
        }
      }
    }
  }

  if (set.notes !== undefined && clean(set.notes) !== clean(contact.biographies?.[0]?.value)) {
    diff.push({
      field: 'Notes',
      from: clean(contact.biographies?.[0]?.value),
      to: clean(set.notes),
    });
    const rest = (draft.biographies ?? []).slice(1);
    draft.biographies = clean(set.notes)
      ? [
          { ...(draft.biographies?.[0] ?? {}), value: set.notes.trim(), contentType: 'TEXT_PLAIN' },
          ...rest,
        ]
      : rest;
  }

  // Lists
  const emails = [...(draft.emailAddresses ?? [])];
  (change.removeEmails ?? []).forEach((v) => {
    const i = emails.findIndex((e) => sameEmail(e.value, v));
    if (i >= 0) {
      diff.push({ field: 'Email', from: emails[i].value, to: '' });
      emails.splice(i, 1);
    } else {
      warnings.push(`No email ${v} to remove`);
    }
  });
  (change.addEmails ?? []).forEach((e) => {
    if (clean(e.value) && !emails.some((x) => sameEmail(x.value, e.value))) {
      diff.push({ field: 'Email', from: '', to: clean(e.value) });
      emails.push({ value: clean(e.value), type: e.type || 'other' });
    }
  });
  if (change.removeEmails?.length || change.addEmails?.length) {
    draft.emailAddresses = emails;
  }

  const phones = [...(draft.phoneNumbers ?? [])];
  (change.removePhones ?? []).forEach((v) => {
    const i = phones.findIndex((p) => samePhone(p.value, v));
    if (i >= 0) {
      diff.push({ field: 'Phone', from: phones[i].value, to: '' });
      phones.splice(i, 1);
    } else {
      warnings.push(`No phone ${v} to remove`);
    }
  });
  (change.addPhones ?? []).forEach((p) => {
    if (clean(p.value) && !phones.some((x) => samePhone(x.value, p.value))) {
      diff.push({ field: 'Phone', from: '', to: clean(p.value) });
      phones.push({ value: clean(p.value), type: p.type || 'mobile' });
    }
  });
  if (change.removePhones?.length || change.addPhones?.length) {
    draft.phoneNumbers = phones;
  }

  if (change.addUrls?.length) {
    const urls = [...(draft.urls ?? [])];
    change.addUrls.forEach((u) => {
      if (clean(u.value) && !urls.some((x) => clean(x.value) === clean(u.value))) {
        diff.push({ field: 'Website', from: '', to: clean(u.value) });
        urls.push({ value: clean(u.value), type: u.type || 'profile' });
      }
    });
    draft.urls = urls;
  }

  // Labels (by name; written separately from the field update)
  // Label names compare case-insensitively ("friends" is the "Friends" label)
  const current = new Set(
    (contact.labels ?? []).map((rn) => groups[rn]?.name?.toLowerCase()).filter(Boolean) as string[],
  );
  const addLabels = [...new Set((change.addLabels ?? []).map(clean).filter(Boolean))].filter(
    (n) => !current.has(n.toLowerCase()),
  );
  const removeLabels = [...new Set((change.removeLabels ?? []).map(clean).filter(Boolean))].filter(
    (n) => {
      if (!current.has(n.toLowerCase())) {
        warnings.push(`Not labelled "${n}"`);
        return false;
      }
      return true;
    },
  );
  addLabels.forEach((n) => diff.push({ field: 'Label', from: '', to: n }));
  removeLabels.forEach((n) => diff.push({ field: 'Label', from: n, to: '' }));

  return { base: contact, draft, diff, addLabels, removeLabels, warnings };
};

/**
 * Field groups for a new contact.
 * @param fields - Names, phonetic names, organization, notes, birthday
 * @param emails - Email addresses
 * @param phones - Phone numbers
 * @returns Patch to create, and a diff to show
 */
export const newContactPatch = (
  fields: FieldChanges,
  emails: TypedValue[] = [],
  phones: TypedValue[] = [],
): { patch: ContactPatch; diff: DiffLine[]; warnings: string[] } => {
  const empty = { resourceName: 'new', etag: '' } as Contact;
  const resolved = resolveChange(
    empty,
    { id: 'new', set: fields, addEmails: emails, addPhones: phones },
    {},
  );
  const patch: ContactPatch = { etag: '' };
  const hasContent = (entry: unknown) =>
    !!entry &&
    typeof entry === 'object' &&
    Object.values(entry).some((v) => v !== null && v !== undefined && v !== '');
  EDITABLE_GROUPS.forEach((g) => {
    const value = (resolved.draft[g] as unknown[] | null | undefined)?.filter(hasContent);
    if (Array.isArray(value) && value.length > 0) {
      (patch as Record<string, unknown>)[g] = value;
    }
  });
  return { patch, diff: resolved.diff, warnings: resolved.warnings };
};

/** Display name of a contact as a proposal shows it, with the phonetic name */
export const proposalName = (c: Contact): string => {
  const n = getName(c);
  const name =
    clean([n?.givenName, n?.middleName, n?.familyName].filter(Boolean).join(' ')) ||
    clean(n?.displayName) ||
    clean(c.emailAddresses?.[0]?.value) ||
    '(no name)';
  const phonetic = getPhoneticName(c);
  return phonetic && phonetic !== name ? `${name} (${phonetic})` : name;
};
