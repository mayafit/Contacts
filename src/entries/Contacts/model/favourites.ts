/**
 * @fileoverview Favourites gone quiet and people who behave like favourites (AD-44 rule 6)
 * @module Contacts/model/favourites
 */

import type { Contact } from '../types/Contact';
import { overdueOf, type ActivityIndex } from './activity';
import { tierOf } from './segments';

/** Google's favourites */
const STARRED = 'contactGroups/starred';

/** A favourite out of touch this long has gone quiet, whatever their rhythm */
export const QUIET_DAYS = 180;

/** Most people in each list */
export const LIST_MAX = 10;

const DAY = 86_400_000;

/** A favourite gone quiet */
export interface QuietFavourite {
  contact: Contact;
  /** Days since the last email or meeting; null: never */
  daysSince: number | null;
}

/**
 * Favourites overdue against their rhythm, out of touch for 180 days, or never in touch;
 * the longest silence first (never last), at most 10.
 * @param contacts - Contacts
 * @param activity - Activity per contact
 * @param now - Now
 * @returns Quiet favourites
 */
export const goneQuiet = (
  contacts: Contact[],
  activity: ActivityIndex,
  now: Date = new Date(),
): QuietFavourite[] =>
  contacts
    .filter((c) => (c.labels ?? []).includes(STARRED))
    .map((contact) => {
      const a = activity.get(contact.resourceName);
      const daysSince = a?.lastInTouch
        ? Math.floor((now.getTime() - Date.parse(a.lastInTouch)) / DAY)
        : null;
      const quiet = daysSince === null || daysSince > QUIET_DAYS || !!overdueOf(a, now);
      return quiet ? { contact, daysSince } : null;
    })
    .filter((q): q is QuietFavourite => !!q)
    .sort((a, b) => (b.daysSince ?? -1) - (a.daysSince ?? -1))
    .slice(0, LIST_MAX);

/**
 * People who are not favourites but are in the inner circle or regular contacts, most in
 * touch first, at most 10.
 * @param contacts - Contacts
 * @param activity - Activity per contact
 * @param now - Now
 * @returns Suggestions
 */
export const suggestedFavourites = (
  contacts: Contact[],
  activity: ActivityIndex,
  now: Date = new Date(),
): Contact[] =>
  contacts
    .filter((c) => !(c.labels ?? []).includes(STARRED))
    .filter((c) => {
      const tier = tierOf(activity.get(c.resourceName), now);
      return tier === 'inner' || tier === 'regular';
    })
    .sort(
      (a, b) =>
        (activity.get(b.resourceName)?.score ?? 0) - (activity.get(a.resourceName)?.score ?? 0),
    )
    .slice(0, LIST_MAX);
