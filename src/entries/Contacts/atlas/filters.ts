/**
 * @fileoverview Texts of the filter bar: the time range, the area, the types and
 * the result count, formatted the same way on every machine (en-GB, local time)
 * @module Contacts/atlas/filters
 */

import type { SearchResult } from './types';
import { ACTIVITY_TYPES } from './types';

const DAY_MS = 86_400_000;
const HOUR_MS = 3_600_000;
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** A rectangle on the map (the area filter) */
export interface AreaBox {
  minLat: number;
  minLon: number;
  maxLat: number;
  maxLon: number;
}

/** "3 Sep 2026" (local time) */
const day = (d: Date): string => `${d.getDate()} ${MONTHS[d.getMonth()]} ${d.getFullYear()}`;

/** "14:05" (local time) */
const clock = (d: Date): string =>
  `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;

/** A number with at most one decimal, without a trailing ".0" */
const oneDecimal = (n: number): string => String(Math.round(n * 10) / 10);

/**
 * Length of a time range in words.
 * @param ms - Length in milliseconds
 * @returns e.g. "7 days", "1 day", "5.5 h", "45 min"
 */
export const formatSpan = (ms: number): string => {
  if (ms >= 2 * DAY_MS) {
    const days = Math.round(ms / DAY_MS);
    return `${days} days`;
  }
  if (ms >= HOUR_MS) {
    return `${oneDecimal(ms / HOUR_MS)} h`;
  }
  return `${Math.max(1, Math.round(ms / 60_000))} min`;
};

/**
 * The time filter as one line. Ranges under two days show the times too.
 * @param range - Selected range (ms since the epoch)
 * @returns e.g. "3 Sep 2026 – 10 Sep 2026 (7 days)" or "3 Sep 2026 14:00–16:30 (2.5 h)"
 */
export const formatTimeFilter = (range: { from: number; to: number }): string => {
  const a = new Date(range.from);
  const b = new Date(range.to);
  const span = formatSpan(range.to - range.from);
  if (range.to - range.from >= 2 * DAY_MS) {
    return `${day(a)} – ${day(b)} (${span})`;
  }
  if (day(a) === day(b)) {
    return `${day(a)} ${clock(a)}–${clock(b)} (${span})`;
  }
  return `${day(a)} ${clock(a)} – ${day(b)} ${clock(b)} (${span})`;
};

/** "12", "2.5" or "800" with its unit: meters under 1 km */
const sizePart = (km: number, inMeters: boolean): string =>
  inMeters ? String(Math.round(km * 1000)) : km >= 10 ? String(Math.round(km)) : oneDecimal(km);

/**
 * Size of an area, east-west × north-south.
 * @param area - The rectangle
 * @returns e.g. "12 × 8 km", "2.5 × 1.2 km", "800 × 600 m"
 */
export const formatAreaSize = (area: AreaBox): string => {
  const midLat = ((area.minLat + area.maxLat) / 2) * (Math.PI / 180);
  const widthKm = (area.maxLon - area.minLon) * 111.32 * Math.cos(midLat);
  const heightKm = (area.maxLat - area.minLat) * 110.574;
  const inMeters = widthKm < 1 && heightKm < 1;
  return `${sizePart(widthKm, inMeters)} × ${sizePart(heightKm, inMeters)} ${inMeters ? 'm' : 'km'}`;
};

/**
 * Centre of an area.
 * @param area - The rectangle
 * @returns e.g. "32.85°N 35.02°E"
 */
export const formatAreaCentre = (area: AreaBox): string => {
  const lat = (area.minLat + area.maxLat) / 2;
  const lon = (area.minLon + area.maxLon) / 2;
  return `${Math.abs(lat).toFixed(2)}°${lat < 0 ? 'S' : 'N'} ${Math.abs(lon).toFixed(2)}°${lon < 0 ? 'W' : 'E'}`;
};

/**
 * The area filter as one line.
 * @param area - The rectangle
 * @returns e.g. "Area 12 × 8 km · 32.85°N 35.02°E"
 */
export const formatAreaFilter = (area: AreaBox): string =>
  `Area ${formatAreaSize(area)} · ${formatAreaCentre(area)}`;

/**
 * The type filter as one line.
 * @param types - Activity type ids
 * @returns e.g. "Ride, Hike"
 */
export const formatTypesFilter = (types: string[]): string =>
  types.map((t) => ACTIVITY_TYPES[t]?.label ?? t).join(', ');

/**
 * The road filter's chip label.
 * @param road - The road as filtered
 * @returns "Road 431" for a route number, else the street name
 */
export const formatRoadFilter = (road: string): string =>
  /^[0-9]{1,4}[A-Za-z]?$/.test(road.trim()) ? `Road ${road.trim()}` : road.trim();

/** "road 431", "route 4", "כביש 431": a road search typed into the search box */
const ROAD_QUERY = /^(?:road|route|rd|hwy|highway|כביש)\s+(.+)$/i;

/**
 * A road typed into the search box, as the road filter.
 * @param text - The search text
 * @returns The road ("431" for "road 431"), or null when the text is not a road search
 */
export const parseRoadQuery = (text: string): string | null => {
  const m = ROAD_QUERY.exec(text.trim());
  return m ? m[1].trim() || null : null;
};

/**
 * The roads to show at first: route numbers and the longest streets, in travel order.
 * @param roads - The roads in travel order
 * @param max - How many to show
 * @returns The shown roads (in travel order) and how many are left out
 */
export const visibleRoads = <T extends { name: string; ref: boolean; m: number }>(
  roads: T[],
  max = 8,
): { shown: T[]; hidden: number } => {
  if (roads.length <= max) {
    return { shown: roads, hidden: 0 };
  }
  const keep = new Set(
    [...roads].sort((a, b) => Number(b.ref) - Number(a.ref) || b.m - a.m).slice(0, max),
  );
  return { shown: roads.filter((r) => keep.has(r)), hidden: roads.length - max };
};

/** "1 activity", "12 activities", "12 of 40 activities" */
const counted = (shown: number, total: number, one: string, many: string): string => {
  const n = total > shown ? `${shown} of ${total}` : String(total);
  return `${n} ${total === 1 ? one : many}`;
};

/**
 * How many results the search found.
 * @param result - The search result
 * @returns e.g. "12 activities · 30 visits"; "of N" only when the API found more than it returned
 */
export const formatResultCount = (result: SearchResult): string =>
  `${counted(result.activities.length, result.totalActivities, 'activity', 'activities')} · ${counted(
    result.visits.length,
    result.totalVisits,
    'visit',
    'visits',
  )}`;
