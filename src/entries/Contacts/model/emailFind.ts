/**
 * @fileoverview A contact's names to look for in mail, and what the card does with the
 * addresses found (AD-43 rules 6 and 8)
 * @module Contacts/model/emailFind
 */

import type { Contact } from '../types/Contact';
import type { FoundEmail } from '../../../services/api/BackendApiClient';
import { clean, getDisplayName, getName, getPhoneticName } from './contactView';

/**
 * Names to look for: the name, the phonetic name, nickname + family name; two words
 * or more each.
 * @param c - Contact
 * @returns Variants
 */
export const nameVariants = (c: Contact): string[] => {
  const n = getName(c);
  return [
    getDisplayName(c),
    [n?.givenName, n?.familyName].filter(Boolean).join(' '),
    [n?.givenName, n?.middleName, n?.familyName].filter(Boolean).join(' '),
    getPhoneticName(c),
    ...(c.nicknames ?? []).map((x) => [x.value, n?.familyName].filter(Boolean).join(' ')),
  ]
    .map(clean)
    .filter((v) => v.includes(' '))
    .filter((v, i, all) => all.indexOf(v) === i);
};

/** Personal mail services: an address there is a home address */
export const PERSONAL_DOMAINS = new Set([
  'gmail.com',
  'googlemail.com',
  'yahoo.com',
  'yahoo.co.il',
  'hotmail.com',
  'outlook.com',
  'live.com',
  'msn.com',
  'icloud.com',
  'me.com',
  'aol.com',
  'walla.co.il',
  'walla.com',
  'bezeqint.net',
  'netvision.net.il',
  '012.net.il',
  'zahav.net.il',
  'smile.net.il',
  'gmx.com',
  'gmx.net',
  'protonmail.com',
  'proton.me',
]);

/**
 * The type of a found address: home on a personal mail service, else work.
 * @param email - Address
 * @returns Type
 */
export const emailType = (email: string): 'home' | 'work' =>
  PERSONAL_DOMAINS.has(email.split('@')[1]?.toLowerCase() ?? '') ? 'home' : 'work';

/** A candidate as the card shows it */
export interface EmailChoice extends FoundEmail {
  /** The other contact that already has it */
  onContact?: string;
}

/**
 * The candidates the card offers: without the draft's own addresses, those on another
 * contact marked and moved last (order kept otherwise).
 * @param found - The server's candidates, best first
 * @param draft - The card's draft
 * @param contacts - The Owner's contacts
 * @returns Choices
 */
export const choicesFor = (
  found: FoundEmail[],
  draft: Contact,
  contacts: Contact[],
): EmailChoice[] => {
  const own = new Set(
    (draft.emailAddresses ?? []).map((e) => clean(e.value).toLowerCase()).filter(Boolean),
  );
  const holder = new Map<string, string>();
  contacts.forEach((c) => {
    if (c.resourceName === draft.resourceName) {
      return;
    }
    (c.emailAddresses ?? []).forEach((e) => {
      const key = clean(e.value).toLowerCase();
      if (key && !holder.has(key)) {
        holder.set(key, getDisplayName(c));
      }
    });
  });
  const choices = found
    .filter((f) => !own.has(f.email.toLowerCase()))
    .map((f) => ({ ...f, onContact: holder.get(f.email.toLowerCase()) }));
  return [...choices.filter((c) => !c.onContact), ...choices.filter((c) => c.onContact)];
};

/**
 * The address filled without asking: the only choice, an exact name match, on no
 * other contact.
 * @param choices - Choices
 * @returns The address, or null when the Owner picks
 */
export const autoPick = (choices: EmailChoice[]): EmailChoice | null =>
  choices.length === 1 && choices[0].score === 3 && !choices[0].onContact ? choices[0] : null;
