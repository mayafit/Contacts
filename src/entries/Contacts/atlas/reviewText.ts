/**
 * @fileoverview Texts of the Owner's Google Maps reviews and review reminders (AD-39)
 * @module Contacts/atlas/reviewText
 */

import type { PlaceReview, ReviewState, ReviewsOverview } from './types';

/** Day only, e.g. "3 Sep 2025" */
const day = (iso: string): string =>
  new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });

/**
 * When a review was written, or that it is a draft.
 * @param r - The review
 * @returns e.g. "Reviewed 3 Sep 2025", or "Draft, never published (3 Sep 2025)"
 */
export const reviewedText = (r: PlaceReview): string =>
  r.published ? `Reviewed ${day(r.reviewedAt)}` : `Draft, never published (${day(r.reviewedAt)})`;

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
