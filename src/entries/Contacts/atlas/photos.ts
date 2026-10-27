/**
 * @fileoverview Photo helpers: texts about a photo, and picking a few to show
 * @module Contacts/atlas/photos
 */

import type { PhotoSummary } from './types';

/** Photos on the map (a warm pink, apart from the activity colours) */
export const PHOTO_COLOR = '#f472b6';

/** How a photo's position was found */
export const placedText = (p: PhotoSummary): string | null => {
  switch (p.placedBy) {
    case 'gps':
      return 'Position from the camera';
    case 'track':
      return 'Placed on the track by its time';
    case 'visit':
      return 'Placed at the visit of its time';
    default:
      return null;
  }
};

/**
 * When a photo was taken, e.g. "Sat 13 Sep 2026, 08:15".
 * @param iso - The time (UTC)
 */
export const takenText = (iso: string): string =>
  new Date(iso).toLocaleString('en-GB', {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });

/**
 * At most n items, spread evenly over the list (first and last kept).
 * @param items - The list
 * @param n - How many at most
 */
export const spread = <T>(items: T[], n: number): T[] => {
  if (items.length <= n) {
    return items;
  }
  if (n <= 1) {
    return items.slice(0, Math.max(0, n));
  }
  const step = (items.length - 1) / (n - 1);
  return Array.from({ length: n }, (_, i) => items[Math.round(i * step)]);
};
