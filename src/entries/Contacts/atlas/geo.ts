/**
 * @fileoverview Small helpers for the Atlas map and timeline
 * @module Contacts/atlas/geo
 */

import { isSet } from './types';

/**
 * Decodes a Google encoded polyline (precision 5).
 * @param encoded - Polyline
 * @returns [lat, lon] pairs
 */
export const decodePolyline = (encoded: string): [number, number][] => {
  const points: [number, number][] = [];
  let index = 0;
  let lat = 0;
  let lon = 0;
  while (index < encoded.length) {
    for (const axis of [0, 1]) {
      let result = 0;
      let shift = 0;
      let b: number;
      do {
        b = encoded.charCodeAt(index++) - 63;
        result |= (b & 0x1f) << shift;
        shift += 5;
      } while (b >= 0x20);
      const delta = result & 1 ? ~(result >> 1) : result >> 1;
      if (axis === 0) {
        lat += delta;
      } else {
        lon += delta;
      }
    }
    points.push([lat / 1e5, lon / 1e5]);
  }
  return points;
};

/** Length of a track in meters (haversine) */
export const trackLength = (points: { lat: number; lon: number }[]): number => {
  const rad = Math.PI / 180;
  let total = 0;
  for (let i = 1; i < points.length; i++) {
    const a = points[i - 1];
    const b = points[i];
    const h =
      Math.sin(((b.lat - a.lat) * rad) / 2) ** 2 +
      Math.cos(a.lat * rad) * Math.cos(b.lat * rad) * Math.sin(((b.lon - a.lon) * rad) / 2) ** 2;
    total += 2 * 6_371_000 * Math.asin(Math.sqrt(h));
  }
  return total;
};

/**
 * Keeps at most max points of a track, evenly spaced, first and last included.
 * @param points - Track
 * @param max - Most points to keep
 * @returns The thinned track (the same array when already short enough)
 */
export const thinTrack = <T>(points: T[], max: number): T[] => {
  if (points.length <= max) {
    return points;
  }
  const step = (points.length - 1) / (max - 1);
  return Array.from({ length: max }, (_, i) => points[Math.round(i * step)]);
};

/** Shortest and longest span the timeline shows */
export const MIN_SPAN_MS = 6 * 3_600_000;
export const MAX_SPAN_MS = 40 * 365 * 86_400_000;

/**
 * Zooms a time range around a moment.
 * @param view - Current range
 * @param center - The moment that stays in place
 * @param factor - Above 1 zooms out, below 1 zooms in
 * @returns New range
 */
export const zoomRange = (
  view: { from: number; to: number },
  center: number,
  factor: number,
): { from: number; to: number } => {
  const span = Math.min(Math.max((view.to - view.from) * factor, MIN_SPAN_MS), MAX_SPAN_MS);
  const ratio = (center - view.from) / (view.to - view.from);
  return { from: center - span * ratio, to: center + span * (1 - ratio) };
};

/**
 * Moves a time range by part of its length.
 * @param view - Current range
 * @param fraction - How far, as a fraction of the span (negative = earlier)
 * @returns New range
 */
export const panRange = (
  view: { from: number; to: number },
  fraction: number,
): { from: number; to: number } => {
  const dt = (view.to - view.from) * fraction;
  return { from: view.from + dt, to: view.to + dt };
};

/** Timeline bucket for a time span: hour, day, week, month or year */
export const bucketFor = (spanMs: number): 'hour' | 'day' | 'week' | 'month' | 'year' => {
  const days = spanMs / 86_400_000;
  if (days <= 4) {
    return 'hour';
  }
  if (days <= 120) {
    return 'day';
  }
  if (days <= 900) {
    return 'week';
  }
  if (days <= 365 * 12) {
    return 'month';
  }
  return 'year';
};

/** Length of a bucket in ms (months and years approximated, for bar widths) */
export const bucketMs = (bucket: string): number =>
  ({
    hour: 3_600_000,
    day: 86_400_000,
    week: 7 * 86_400_000,
    month: 30.44 * 86_400_000,
    year: 365.25 * 86_400_000,
  })[bucket] ?? 86_400_000;

/** "3.2 km", "850 m" */
export const formatDistance = (m: number | null | undefined): string =>
  !isSet(m)
    ? ''
    : m >= 1000
      ? `${(m / 1000).toFixed(m >= 100_000 ? 0 : 1)} km`
      : `${Math.round(m)} m`;

/** "1:05 h", "42 min" */
export const formatDuration = (s: number | null | undefined): string => {
  if (!isSet(s)) {
    return '';
  }
  const min = Math.round(s / 60);
  return min >= 60
    ? `${Math.floor(min / 60)}:${String(min % 60).padStart(2, '0')} h`
    : `${min} min`;
};

/** Short date-time in the user's locale */
export const formatWhen = (iso: string): string =>
  new Date(iso).toLocaleString('en-GB', {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
