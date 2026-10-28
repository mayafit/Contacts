/**
 * @fileoverview Small helpers for the Atlas map and timeline
 * @module Contacts/atlas/geo
 */

import type { ActivitySummary } from './types';
import { ACTIVITY_TYPES, isSet } from './types';
import { photoCountText } from './photos';

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

/**
 * The great-circle route between two points (the way planes fly), as a line of
 * points; longitudes are unwrapped so the line never jumps across the map.
 * @param from - [lat, lon] of the start
 * @param to - [lat, lon] of the end
 * @param segments - Number of pieces
 * @returns [lat, lon] points, start and end included
 */
export const greatCircle = (
  from: [number, number],
  to: [number, number],
  segments = 64,
): [number, number][] => {
  const rad = Math.PI / 180;
  const [lat1, lon1] = [from[0] * rad, from[1] * rad];
  const [lat2, lon2] = [to[0] * rad, to[1] * rad];
  const d =
    2 *
    Math.asin(
      Math.sqrt(
        Math.sin((lat2 - lat1) / 2) ** 2 +
          Math.cos(lat1) * Math.cos(lat2) * Math.sin((lon2 - lon1) / 2) ** 2,
      ),
    );
  if (d < 1e-9) {
    return [from, to];
  }
  const out: [number, number][] = [];
  let prevLon = from[1];
  for (let i = 0; i <= segments; i++) {
    const f = i / segments;
    const a = Math.sin((1 - f) * d) / Math.sin(d);
    const b = Math.sin(f * d) / Math.sin(d);
    const x = a * Math.cos(lat1) * Math.cos(lon1) + b * Math.cos(lat2) * Math.cos(lon2);
    const y = a * Math.cos(lat1) * Math.sin(lon1) + b * Math.cos(lat2) * Math.sin(lon2);
    const z = a * Math.sin(lat1) + b * Math.sin(lat2);
    const lat = Math.atan2(z, Math.sqrt(x * x + y * y)) / rad;
    let lon = Math.atan2(y, x) / rad;
    // Keep each step within 180° of the previous one
    while (lon - prevLon > 180) {
      lon -= 360;
    }
    while (lon - prevLon < -180) {
      lon += 360;
    }
    prevLon = lon;
    out.push([lat, lon]);
  }
  return out;
};

/**
 * What a track is, for its tooltip: "Flight · 26 Aug 2026 20:12 · 6:17 h · 5,794 km"
 * @param a - Activity
 * @returns Text
 */
export const describeActivity = (a: ActivitySummary): string =>
  [
    a.name || ACTIVITY_TYPES[a.type]?.label || a.type,
    formatWhen(a.startTime),
    formatDuration((Date.parse(a.endTime) - Date.parse(a.startTime)) / 1000),
    a.distanceM ? formatDistance(a.distanceM) : null,
    photoCountText(a.photoCount),
  ]
    .filter(Boolean)
    .join(' · ');
