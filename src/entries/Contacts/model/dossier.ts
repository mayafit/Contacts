/**
 * @fileoverview A contact's shared context for the dossier (AD-46 rule 6): colleagues and
 * the segments it is a member of
 * @module Contacts/model/dossier
 */

import type { Contact } from '../types/Contact';
import type { ActivityIndex } from './activity';
import { getDisplayName, getOrganization } from './contactView';
import { PERSONAL_DOMAINS } from './emailFind';
import {
  emailDomainsOf,
  matchesSegment,
  normalizeOrganization,
  type Segment,
  type SegmentContext,
} from './segments';

/** Most colleagues shown */
export const MAX_COLLEAGUES = 8;

/** A colleague and why */
export interface Colleague {
  contact: Contact;
  /** "Elta" or "@elta.co.il" */
  via: string;
}

/**
 * Other contacts with the same organization (normalized, non-empty) or an address at the
 * same work domain; most in touch first, then by name; at most 8.
 * @param c - The contact
 * @param contacts - All contacts
 * @param activity - Activity per contact, when there is any
 * @returns Colleagues
 */
export const colleaguesOf = (
  c: Contact,
  contacts: Contact[],
  activity: ActivityIndex | null,
): Colleague[] => {
  const org = normalizeOrganization(getOrganization(c));
  const domains = new Set(emailDomainsOf(c).filter((d) => !PERSONAL_DOMAINS.has(d)));
  if (!org && !domains.size) {
    return [];
  }
  return contacts
    .filter((o) => o.resourceName !== c.resourceName)
    .map((o): Colleague | null => {
      if (org && normalizeOrganization(getOrganization(o)) === org) {
        return { contact: o, via: getOrganization(o) };
      }
      const shared = emailDomainsOf(o).find((d) => domains.has(d));
      return shared ? { contact: o, via: `@${shared}` } : null;
    })
    .filter((x): x is Colleague => !!x)
    .sort(
      (a, b) =>
        (activity?.get(b.contact.resourceName)?.score ?? 0) -
          (activity?.get(a.contact.resourceName)?.score ?? 0) ||
        getDisplayName(a.contact).localeCompare(getDisplayName(b.contact), ['he', 'en']),
    )
    .slice(0, MAX_COLLEAGUES);
};

/**
 * The segments a contact is a member of.
 * @param c - The contact
 * @param segments - The Owner's segments
 * @param ctx - Context
 * @returns Segments
 */
export const segmentsOf = (c: Contact, segments: Segment[], ctx: SegmentContext): Segment[] =>
  segments.filter((s) => matchesSegment(c, s, ctx));
