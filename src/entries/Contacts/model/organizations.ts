/**
 * @fileoverview Organization suggestions from work email domains (Story 6.1).
 * A contact with an address at a company domain probably works there; when
 * its organization is missing or different, suggest the organization other
 * contacts at the same domain already use (else a name derived from the
 * domain).
 * @module Contacts/model/organizations
 */

import type { Contact, ContactOrganization } from '../types/Contact';
import { clean, compareByName } from './contactView';

/** Personal email providers: a domain here says nothing about an employer */
export const FREE_MAIL_DOMAINS = new Set([
  'gmail.com',
  'googlemail.com',
  'yahoo.com',
  'yahoo.co.il',
  'yahoo.co.uk',
  'ymail.com',
  'hotmail.com',
  'hotmail.co.il',
  'hotmail.co.uk',
  'outlook.com',
  'outlook.co.il',
  'live.com',
  'live.co.uk',
  'msn.com',
  'aol.com',
  'icloud.com',
  'me.com',
  'mac.com',
  'protonmail.com',
  'proton.me',
  'pm.me',
  'gmx.com',
  'gmx.net',
  'mail.com',
  'zoho.com',
  'yandex.com',
  'yandex.ru',
  'mail.ru',
  'walla.co.il',
  'walla.com',
  'bezeqint.net',
  'netvision.net.il',
  '012.net.il',
  'zahav.net.il',
  'smile.net.il',
  'inter.net.il',
  'barak.net.il',
  'actcom.co.il',
  'nana.co.il',
  'nana10.co.il',
  'hot.net.il',
  'qq.com',
  '163.com',
  'web.de',
  't-online.de',
  'orange.fr',
  'free.fr',
  'libero.it',
]);

/** Second-level suffixes where the organization is the label before them */
const COMPOUND_SUFFIXES = [
  'co.il',
  'org.il',
  'ac.il',
  'gov.il',
  'net.il',
  'muni.il',
  'k12.il',
  'idf.il',
  'co.uk',
  'org.uk',
  'ac.uk',
  'com.au',
  'co.jp',
  'com.br',
  'co.in',
];

/** One work domain and what it implies */
export interface DomainInsight {
  /** Email domain, e.g. "elta.co.il" */
  domain: string;
  /** Suggested organization name */
  suggested: string;
  /** Whether the suggestion comes from contacts' existing organizations (vs the domain) */
  fromContacts: boolean;
  /** Contacts with an address at this domain, by name */
  members: Contact[];
  /** Members whose organization is missing or different from the suggestion */
  needsUpdate: { contact: Contact; reason: 'missing' | 'different'; current: string }[];
}

const norm = (v: string | null | undefined): string => clean(v).toLowerCase();

/**
 * Domain of an email address, lowercased.
 * @param email - Email address
 * @returns Domain or '' when invalid
 */
export const domainOf = (email: string | null | undefined): string => {
  const m = /@([^@\s]+\.[^@\s]+)$/.exec(clean(email).toLowerCase());
  return m ? m[1].replace(/\.$/, '') : '';
};

/**
 * Whether a domain looks like an employer's (not a personal mail provider).
 * @param domain - Email domain
 * @returns True for work domains
 */
export const isWorkDomain = (domain: string): boolean => !!domain && !FREE_MAIL_DOMAINS.has(domain);

/**
 * Organization name guessed from a domain: "mail.elta.co.il" -> "Elta",
 * "intel.com" -> "Intel", "iai.co.il" -> "IAI".
 * @param domain - Email domain
 * @returns Name
 */
export const orgNameFromDomain = (domain: string): string => {
  const d = domain.toLowerCase();
  const suffix = COMPOUND_SUFFIXES.find((s) => d.endsWith(`.${s}`));
  const labels = (
    suffix ? d.slice(0, -(suffix.length + 1)) : d.split('.').slice(0, -1).join('.')
  ).split('.');
  const label = labels[labels.length - 1] || d;
  // Short labels that do not read as a word are acronyms: iai -> IAI, hp -> HP;
  // but wix -> Wix (exactly one vowel reads as a word)
  const vowels = (label.match(/[aeiou]/g) ?? []).length;
  if (label.length <= 3 && vowels !== 1) {
    return label.toUpperCase();
  }
  return label
    .split('-')
    .map((w) => w.charAt(0).toUpperCase() + w.slice(1))
    .join(' ');
};

/**
 * Organization names a contact lists, as displayed.
 * @param c - Contact
 * @returns Names
 */
const orgNames = (c: Contact): string[] =>
  (c.organizations ?? []).map((o) => clean(o.name)).filter(Boolean);

/**
 * Groups contacts by work email domain and finds organizations to fix.
 * @param contacts - All contacts
 * @param minMembers - Smallest domain group worth listing unless it needs an update
 * @returns Domains, those needing updates first, then by size
 */
export const analyzeOrganizations = (contacts: Contact[], minMembers = 2): DomainInsight[] => {
  const byDomain = new Map<string, Contact[]>();
  contacts.forEach((c) => {
    const domains = new Set(
      (c.emailAddresses ?? []).map((e) => domainOf(e.value)).filter(isWorkDomain),
    );
    domains.forEach((d) => byDomain.set(d, [...(byDomain.get(d) ?? []), c]));
  });

  const insights: DomainInsight[] = [];
  byDomain.forEach((members, domain) => {
    // Most common existing organization name among the domain's contacts
    const counts = new Map<string, { name: string; count: number }>();
    members.forEach((m) =>
      new Set(orgNames(m)).forEach((name) => {
        const key = name.toLowerCase();
        const entry = counts.get(key) ?? { name, count: 0 };
        entry.count += 1;
        counts.set(key, entry);
      }),
    );
    const best = Array.from(counts.values()).sort(
      (a, b) => b.count - a.count || a.name.localeCompare(b.name),
    )[0];
    const suggested = best?.name ?? orgNameFromDomain(domain);

    const needsUpdate = members
      .filter((m) => !orgNames(m).some((n) => n.toLowerCase() === suggested.toLowerCase()))
      .map((m) => ({
        contact: m,
        reason: orgNames(m).length ? ('different' as const) : ('missing' as const),
        current: orgNames(m).join(', '),
      }));

    if (members.length >= minMembers || needsUpdate.length > 0) {
      insights.push({
        domain,
        suggested,
        fromContacts: !!best,
        members: [...members].sort(compareByName),
        needsUpdate,
      });
    }
  });

  return insights.sort(
    (a, b) =>
      b.needsUpdate.length - a.needsUpdate.length ||
      b.members.length - a.members.length ||
      a.domain.localeCompare(b.domain),
  );
};

/**
 * New organizations list for a contact with `name` applied, without losing
 * anything: an entry with no name gets the name (keeping its title etc.);
 * otherwise the new organization is added first and existing ones are kept.
 * @param contact - Contact
 * @param name - Organization name to apply
 * @returns Organizations group to save
 */
export const withOrganization = (contact: Contact, name: string): ContactOrganization[] => {
  const orgs = [...(contact.organizations ?? [])];
  if (orgs.some((o) => norm(o.name) === norm(name))) {
    return orgs;
  }
  const unnamed = orgs.findIndex((o) => !clean(o.name));
  if (unnamed >= 0) {
    orgs[unnamed] = { ...orgs[unnamed], name };
    return orgs;
  }
  return [{ name }, ...orgs];
};
