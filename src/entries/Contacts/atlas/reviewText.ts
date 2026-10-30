/**
 * @fileoverview Texts of the Owner's Google Maps reviews and review reminders (AD-39)
 * @module Contacts/atlas/reviewText
 */

import type {
  PlaceReview,
  ReviewState,
  ReviewsOverview,
  SourceConnection,
  TakeoutSeen,
} from './types';

/** Day only, e.g. "3 Sep 2025" */
const day = (iso: string): string =>
  new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });

/**
 * When a review was written, or that it is a draft.
 * @param r - The review
 * @returns e.g. "Reviewed 3 Sep 2025", or "Draft, never published (3 Sep 2025)"
 */
export const reviewedText = (r: PlaceReview): string => {
  if (!r.reviewedAt) {
    return r.published ? 'Reviewed' : 'Draft, never published';
  }
  return r.published
    ? `Reviewed ${day(r.reviewedAt)}`
    : `Draft, never published (${day(r.reviewedAt)})`;
};

/**
 * A link from an import, only when it is http or https (another scheme could run script).
 * @param url - The link
 * @returns The link, or null
 */
export const safeUrl = (url: string | null | undefined): string | null =>
  typeof url === 'string' && /^https?:\/\//i.test(url.trim()) ? url.trim() : null;

/**
 * Stars as text, for labels.
 * @param rating - 1 to 5, or none
 * @returns e.g. "4 of 5 stars", or null
 */
export const starsLabel = (rating: number | null | undefined): string | null =>
  rating ? `${rating} of 5 stars` : null;

/**
 * What a set-aside or due reminder says on the place panel.
 * @param s - Where the place stands
 * @returns The line, or null when there is nothing to say (reviewed, or not one to review)
 */
export const reminderText = (s: ReviewState | null | undefined): string | null => {
  switch (s?.state) {
    case 'to_review':
      return 'You have not reviewed this place';
    case 'later':
      return s.until
        ? `Review reminder hidden until ${day(s.until)}`
        : 'Review reminder hidden for now';
    case 'never':
      return s.category
        ? 'No review reminders for this kind of place'
        : 'No review reminders for this place';
    case 'done':
      return 'You said you reviewed it; the next import brings the review';
    default:
      return null;
  }
};

/**
 * The summary line of the Insights Reviews section.
 * @param o - The overview
 * @returns e.g. "12 reviews · ★ 4.3 · 9 at places you visited"
 */
export const overviewText = (o: ReviewsOverview): string => {
  const parts = [`${o.written} ${o.written === 1 ? 'review' : 'reviews'}`];
  if (typeof o.average === 'number') {
    parts.push(`★ ${o.average.toFixed(1)}`);
  }
  if (o.written > 0) {
    parts.push(`${o.linked} at places you visited`);
  }
  return parts.join(' · ');
};

/**
 * A place to review, under its name.
 * @param category - Its category
 * @param lastVisit - The latest qualifying visit's end (ISO)
 * @param visits - Its visits
 * @returns e.g. "Restaurant · last 3 Oct 2026 · 2 visits"
 */
export const dueText = (category: string | null, lastVisit: string, visits: number): string =>
  [category, `last ${day(lastVisit)}`, `${visits} ${visits === 1 ? 'visit' : 'visits'}`]
    .filter(Boolean)
    .join(' · ');

/** "3 hours ago", "2 days ago", "just now" */
const ago = (iso: string, now = Date.now()): string => {
  const minutes = Math.max(0, Math.round((now - new Date(iso).getTime()) / 60000));
  if (minutes < 2) {
    return 'just now';
  }
  if (minutes < 120) {
    return `${minutes} minutes ago`;
  }
  const hours = Math.round(minutes / 60);
  return hours < 48 ? `${hours} hours ago` : `${Math.round(hours / 24)} days ago`;
};

/**
 * When the Drive pickup last looked, and what it brought in all.
 * @param c - The connection
 * @param now - The time now (ms)
 * @returns e.g. "Checked 3 hours ago · 42 reviews imported"
 */
export const takeoutStatusText = (c: SourceConnection, now = Date.now()): string =>
  [
    c.lastSyncAt ? `Checked ${ago(c.lastSyncAt, now)}` : 'Not checked yet',
    `${c.itemsImported} ${c.itemsImported === 1 ? 'review' : 'reviews'} imported`,
  ].join(' · ');

/**
 * The newest export the pickup handled.
 * @param e - The export, or none
 * @returns e.g. "Last export: takeout-….zip, 3 Sep 2026 — 12 reviews"
 */
export const takeoutLastText = (e: TakeoutSeen | null): string => {
  if (!e) {
    return 'No Takeout export found in Drive yet';
  }
  const what =
    e.reviews === null
      ? (e.note ?? 'not read')
      : `${e.reviews} ${e.reviews === 1 ? 'review' : 'reviews'}`;
  return `Last export: ${e.name}, ${day(e.modified)} — ${what}`;
};
