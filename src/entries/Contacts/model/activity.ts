/**
 * @fileoverview Per-contact activity (last in touch, most / least in touch) from the
 * per-address Gmail and Calendar data
 * @module Contacts/model/activity
 */

import type { Contact } from '../types/Contact';
import type { PersonActivity } from '../../../services/api/BackendApiClient';

/** A contact's activity, combined over all its email addresses */
export interface ContactActivity {
  /** Latest email or past meeting (ISO), null when none */
  lastInTouch: string | null;
  lastEmailAt: string | null;
  lastSentAt: string | null;
  lastReceivedAt: string | null;
  lastMeetingAt: string | null;
  lastMeetingTitle: string | null;
  nextMeetingAt: string | null;
  nextMeetingTitle: string | null;
  /** Emails both ways in the last 12 months */
  emails12m: number;
  /** Past meetings in the last 12 months */
  meetings12m: number;
  /** Emails sent to the contact (whole window) */
  sent: number;
  /** Emails received from the contact (whole window) */
  received: number;
  /** Ranking weight: emails + 3 x meetings, last 12 months */
  score: number;
}

/** Activity per contact resource name */
export type ActivityIndex = Map<string, ContactActivity>;

/** A meeting weighs as much as this many emails */
const MEETING_WEIGHT = 3;

const later = (a: string | null | undefined, b: string | null | undefined): string | null => {
  if (!a) {
    return b ?? null;
  }
  if (!b) {
    return a;
  }
  return Date.parse(b) > Date.parse(a) ? b : a;
};

const earlier = (a: string | null | undefined, b: string | null | undefined): string | null => {
  if (!a) {
    return b ?? null;
  }
  if (!b) {
    return a;
  }
  return Date.parse(b) < Date.parse(a) ? b : a;
};

/**
 * "yyyy-MM" keys of the last n months, this month included.
 * @param now - Current time
 * @param n - Number of months
 * @returns Month keys
 */
export const lastMonths = (now: Date, n: number): Set<string> => {
  const keys = new Set<string>();
  for (let i = 0; i < n; i += 1) {
    const d = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - i, 1));
    keys.add(`${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}`);
  }
  return keys;
};

const sumMonths = (months: Record<string, number> | undefined, keys: Set<string>): number =>
  Object.entries(months ?? {}).reduce((s, [k, v]) => (keys.has(k) ? s + v : s), 0);

/**
 * Combines the activity of each contact's addresses.
 * @param contacts - Contacts
 * @param people - Activity per lower-case address
 * @param now - Current time
 * @returns Activity per contact (only contacts with any activity)
 */
export const buildActivityIndex = (
  contacts: Contact[],
  people: Record<string, PersonActivity>,
  now: Date = new Date(),
): ActivityIndex => {
  const year = lastMonths(now, 12);
  const index: ActivityIndex = new Map();
  contacts.forEach((c) => {
    const addresses = [
      ...new Set((c.emailAddresses ?? []).map((e) => (e.value ?? '').trim().toLowerCase())),
    ].filter(Boolean);
    let a: ContactActivity | null = null;
    addresses.forEach((address) => {
      const p = people[address];
      if (!p) {
        return;
      }
      a ??= {
        lastInTouch: null,
        lastEmailAt: null,
        lastSentAt: null,
        lastReceivedAt: null,
        lastMeetingAt: null,
        lastMeetingTitle: null,
        nextMeetingAt: null,
        nextMeetingTitle: null,
        emails12m: 0,
        meetings12m: 0,
        sent: 0,
        received: 0,
        score: 0,
      };
      a.lastEmailAt = later(a.lastEmailAt, p.lastEmailAt);
      a.lastSentAt = later(a.lastSentAt, p.lastSentAt);
      a.lastReceivedAt = later(a.lastReceivedAt, p.lastReceivedAt);
      if (p.lastMeetingAt && later(a.lastMeetingAt, p.lastMeetingAt) === p.lastMeetingAt) {
        a.lastMeetingAt = p.lastMeetingAt;
        a.lastMeetingTitle = p.lastMeetingTitle ?? null;
      }
      if (p.nextMeetingAt && earlier(a.nextMeetingAt, p.nextMeetingAt) === p.nextMeetingAt) {
        a.nextMeetingAt = p.nextMeetingAt;
        a.nextMeetingTitle = p.nextMeetingTitle ?? null;
      }
      a.emails12m += sumMonths(p.emailMonths, year);
      a.meetings12m += sumMonths(p.meetingMonths, year);
      a.sent += p.sentCount ?? 0;
      a.received += p.receivedCount ?? 0;
    });
    if (a) {
      const found: ContactActivity = a;
      found.lastInTouch = later(found.lastEmailAt, found.lastMeetingAt);
      found.score = found.emails12m + MEETING_WEIGHT * found.meetings12m;
      index.set(c.resourceName, found);
    }
  });
  return index;
};

/** A contact with its activity */
export interface RankedContact {
  contact: Contact;
  activity: ContactActivity;
}

/**
 * Contacts most in touch over the last 12 months.
 * @param contacts - Contacts
 * @param index - Activity index
 * @param limit - How many
 * @returns Highest score first
 */
export const mostInTouch = (
  contacts: Contact[],
  index: ActivityIndex,
  limit = 10,
): RankedContact[] =>
  contacts
    .flatMap((contact) => {
      const activity = index.get(contact.resourceName);
      return activity && activity.score > 0 ? [{ contact, activity }] : [];
    })
    .sort(
      (a, b) =>
        b.activity.score - a.activity.score ||
        Date.parse(b.activity.lastInTouch ?? '') - Date.parse(a.activity.lastInTouch ?? ''),
    )
    .slice(0, limit);

/**
 * Contacts you used to be in touch with, but not for a while: at least
 * `minInteractions` emails/meetings, none in the last `quietMonths` months.
 * @param contacts - Contacts
 * @param index - Activity index
 * @param now - Current time
 * @param quietMonths - Months without contact
 * @param minInteractions - Interactions before that
 * @returns Most interactions first
 */
export const goneQuiet = (
  contacts: Contact[],
  index: ActivityIndex,
  now: Date = new Date(),
  quietMonths = 6,
  minInteractions = 3,
): RankedContact[] => {
  const cutoff = new Date(now);
  cutoff.setMonth(cutoff.getMonth() - quietMonths);
  return contacts
    .flatMap((contact) => {
      const activity = index.get(contact.resourceName);
      if (!activity?.lastInTouch || Date.parse(activity.lastInTouch) >= cutoff.getTime()) {
        return [];
      }
      return activity.sent + activity.received + activity.meetings12m >= minInteractions
        ? [{ contact, activity }]
        : [];
    })
    .sort(
      (a, b) =>
        b.activity.sent + b.activity.received - (a.activity.sent + a.activity.received) ||
        Date.parse(b.activity.lastInTouch ?? '') - Date.parse(a.activity.lastInTouch ?? ''),
    );
};

/**
 * Contacts with an email address but no email or meeting in the scanned window.
 * @param contacts - Contacts
 * @param index - Activity index
 * @returns Those contacts
 */
export const neverInTouch = (contacts: Contact[], index: ActivityIndex): Contact[] =>
  contacts.filter(
    (c) =>
      (c.emailAddresses ?? []).some((e) => (e.value ?? '').trim()) && !index.has(c.resourceName),
  );

/** Someone you email who is not in your contacts */
export interface Correspondent {
  email: string;
  sent: number;
  received: number;
  lastEmailAt: string | null;
}

/**
 * Addresses you have written to at least `minSent` times that no contact has.
 * @param people - Activity per address
 * @param contacts - All contacts (Trash included, so trashed people are not suggested)
 * @param limit - How many
 * @param minSent - Emails sent to them
 * @returns Most written to first
 */
export const correspondentsNotInContacts = (
  people: Record<string, PersonActivity>,
  contacts: Contact[],
  limit = 10,
  minSent = 2,
): Correspondent[] => {
  const known = new Set(
    contacts.flatMap((c) =>
      (c.emailAddresses ?? []).map((e) => (e.value ?? '').trim().toLowerCase()),
    ),
  );
  return Object.entries(people)
    .filter(([email, p]) => !known.has(email) && (p.sentCount ?? 0) >= minSent)
    .map(([email, p]) => ({
      email,
      sent: p.sentCount ?? 0,
      received: p.receivedCount ?? 0,
      lastEmailAt: p.lastEmailAt ?? null,
    }))
    .sort((a, b) => b.sent - a.sent || b.received - a.received)
    .slice(0, limit);
};

/**
 * "today", "3 days ago", "2 months ago"...
 * @param iso - Date
 * @param now - Current time
 * @returns Relative description, '' when no date
 */
export const formatRelative = (iso: string | null | undefined, now: Date = new Date()): string => {
  if (!iso) {
    return '';
  }
  const days = Math.floor((now.getTime() - Date.parse(iso)) / 86_400_000);
  if (days < 0) {
    const ahead = -days;
    if (ahead <= 1) {
      return 'tomorrow';
    }
    return ahead < 14 ? `in ${ahead} days` : `in ${Math.round(ahead / 7)} weeks`;
  }
  if (days === 0) {
    return 'today';
  }
  if (days === 1) {
    return 'yesterday';
  }
  if (days < 14) {
    return `${days} days ago`;
  }
  if (days < 60) {
    return `${Math.round(days / 7)} weeks ago`;
  }
  if (days < 365) {
    return `${Math.round(days / 30)} months ago`;
  }
  const years = Math.floor(days / 365);
  return years === 1 ? 'over a year ago' : `${years} years ago`;
};

/**
 * Compact relative date for table cells: "today", "5d ago", "3w ago", "9mo ago", "2y ago".
 * @param iso - Date
 * @param now - Current time
 * @returns Short description, '' when no date
 */
export const formatRelativeShort = (
  iso: string | null | undefined,
  now: Date = new Date(),
): string => {
  if (!iso) {
    return '';
  }
  const days = Math.floor((now.getTime() - Date.parse(iso)) / 86_400_000);
  if (days <= 0) {
    return 'today';
  }
  if (days < 14) {
    return `${days}d ago`;
  }
  if (days < 60) {
    return `${Math.round(days / 7)}w ago`;
  }
  if (days < 365) {
    return `${Math.round(days / 30)}mo ago`;
  }
  return `${Math.floor(days / 365)}y ago`;
};
