/**
 * @fileoverview Turns details found in a contact's email signature into a change
 * @module Contacts/assistant/signatures
 */

import type { Contact } from '../types/Contact';
import { clean, digitsOf, getJobTitle, getOrganization } from '../model/contactView';
import type { ContactChange } from './changes';

/** What the signature reader found for one contact */
export interface SignatureDetails {
  phones?: { value: string; type?: string }[];
  jobTitle?: string;
  organization?: string;
  department?: string;
  websites?: string[];
  linkedin?: string;
  address?: string;
}

/** A field where the signature says something else than the contact */
export interface SignatureConflict {
  field: 'organization' | 'jobTitle' | 'department';
  label: string;
  current: string;
  found: string;
}

/** The change to propose, and what differs */
export interface SignatureChange {
  /** Additions and empty fields filled */
  change: ContactChange;
  /** Different values (not applied unless chosen) */
  conflicts: SignatureConflict[];
  /** Address found (shown, not written: addresses are structured) */
  address?: string;
}

const PHONE_TYPES: Record<string, string> = {
  mobile: 'mobile',
  cell: 'mobile',
  נייד: 'mobile',
  work: 'work',
  office: 'work',
  משרד: 'work',
  direct: 'work',
  home: 'home',
  fax: 'workFax',
  פקס: 'workFax',
};

/** Phone type from the signature's label */
const phoneType = (type?: string): string => {
  const t = clean(type).toLowerCase();
  return Object.entries(PHONE_TYPES).find(([k]) => t.includes(k))?.[1] ?? 'other';
};

/** A URL with a scheme */
export const withScheme = (url: string): string => {
  const u = clean(url);
  return !u || /^https?:\/\//i.test(u) ? u : `https://${u}`;
};

const sameUrl = (a: string, b: string) =>
  withScheme(a).replace(/\/+$/, '').toLowerCase() ===
  withScheme(b).replace(/\/+$/, '').toLowerCase();

/**
 * Proposes what the signature adds: new phone numbers and links, and the
 * organization, title and department when the contact has none. Values that
 * differ from the contact's are returned as conflicts.
 * @param contact - Contact
 * @param d - Details found
 * @returns Change and conflicts
 */
export const signatureChange = (contact: Contact, d: SignatureDetails): SignatureChange => {
  const have = new Set(
    (contact.phoneNumbers ?? [])
      .flatMap((p) => [p.value, p.canonicalForm])
      .filter(Boolean)
      .map((v) => digitsOf(v!).slice(-9)),
  );
  const addPhones = (d.phones ?? [])
    .filter((p) => digitsOf(p.value).length >= 7 && !have.has(digitsOf(p.value).slice(-9)))
    .filter(
      (p, i, all) =>
        all.findIndex((x) => digitsOf(x.value).slice(-9) === digitsOf(p.value).slice(-9)) === i,
    )
    .map((p) => ({ value: clean(p.value), type: phoneType(p.type) }));

  const urls = [...(d.websites ?? []), ...(d.linkedin ? [d.linkedin] : [])]
    .map(withScheme)
    .filter(Boolean);
  const addUrls = urls
    .filter((u) => !(contact.urls ?? []).some((x) => sameUrl(x.value, u)))
    .filter((u, i, all) => all.findIndex((x) => sameUrl(x, u)) === i)
    .map((u) => ({ value: u, type: /linkedin\.com/i.test(u) ? 'profile' : 'work' }));

  const set: ContactChange['set'] = {};
  const conflicts: SignatureConflict[] = [];
  const fields: [SignatureConflict['field'], string, string, string | undefined][] = [
    ['organization', 'Organization', getOrganization(contact), d.organization],
    ['jobTitle', 'Job title', getJobTitle(contact), d.jobTitle],
    ['department', 'Department', clean(contact.organizations?.[0]?.department), d.department],
  ];
  // A signature from another employer: its title and department belong to that
  // employer, so they become choices too instead of filling the old one's fields
  const orgFound = clean(d.organization);
  const orgCurrent = getOrganization(contact);
  const newEmployer =
    !!orgFound && !!orgCurrent && orgFound.toLowerCase() !== orgCurrent.toLowerCase();
  fields.forEach(([field, label, current, found]) => {
    const f = clean(found);
    if (!f) {
      return;
    }
    if (!current && !(newEmployer && field !== 'organization')) {
      set[field] = f;
    } else if (current.toLowerCase() !== f.toLowerCase()) {
      conflicts.push({ field, label, current, found: f });
    }
  });

  return {
    change: {
      id: contact.resourceName,
      ...(Object.keys(set).length ? { set } : {}),
      ...(addPhones.length ? { addPhones } : {}),
      ...(addUrls.length ? { addUrls } : {}),
    },
    conflicts,
    address: clean(d.address) || undefined,
  };
};
