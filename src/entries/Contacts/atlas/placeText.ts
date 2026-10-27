/**
 * @fileoverview Text and links for places: coordinates, map links, OpenStreetMap and
 * Wikipedia links, unnamed places, the details worth showing, how a place got its identity
 * @module Contacts/atlas/placeText
 */

import type { LookupAddress, NearbyPlace } from './types';
import { isSet } from './types';

/**
 * Coordinates as one line, 5 decimals (about 1 m).
 * @param lat - Latitude
 * @param lon - Longitude
 * @returns e.g. "31.81061, 34.77423"
 */
export const formatCoordinates = (lat: number, lon: number): string =>
  `${lat.toFixed(5)}, ${lon.toFixed(5)}`;

/**
 * Google Maps at a point.
 * @param lat - Latitude
 * @param lon - Longitude
 * @returns The search URL
 */
export const googleMapsUrl = (lat: number, lon: number): string =>
  `https://www.google.com/maps/search/?api=1&query=${lat.toFixed(6)},${lon.toFixed(6)}`;

/**
 * OpenStreetMap at a point, with a marker.
 * @param lat - Latitude
 * @param lon - Longitude
 * @returns The map URL
 */
export const openStreetMapUrl = (lat: number, lon: number): string => {
  const a = lat.toFixed(6);
  const o = lon.toFixed(6);
  return `https://www.openstreetmap.org/?mlat=${a}&mlon=${o}#map=18/${a}/${o}`;
};

/**
 * The OpenStreetMap page of a linked location.
 * @param ref - e.g. "osm:node/123456"
 * @returns e.g. "https://www.openstreetmap.org/node/123456"; null when not an OpenStreetMap ref
 */
export const osmObjectUrl = (ref: string | null | undefined): string | null => {
  const m = /^osm:(node|way|relation)\/(\d+)$/.exec((ref ?? '').trim());
  return m ? `https://www.openstreetmap.org/${m[1]}/${m[2]}` : null;
};

/**
 * The Wikipedia article of an OpenStreetMap wikipedia tag.
 * @param value - "en:Title" or "he:Title"
 * @returns The article URL; null when the value has no language prefix
 */
export const wikipediaUrl = (value: string | null | undefined): string | null => {
  const m = /^([a-z]{2,3}(?:-[a-z]+)?):(.+)$/i.exec((value ?? '').trim());
  if (!m) {
    return null;
  }
  const title = m[2].trim().replace(/ /g, '_');
  return title
    ? `https://${m[1].toLowerCase()}.wikipedia.org/wiki/${encodeURIComponent(title)}`
    : null;
};

/**
 * A website as a safe link (http/https only; a bare domain gets https://).
 * @param url - The website as found, e.g. "https://cafe.example" or "cafe.example/menu"
 * @returns The link; null for anything else (e.g. "javascript:…")
 */
export const safeWebUrl = (url: string | null | undefined): string | null => {
  const u = (url ?? '').trim();
  if (/^https?:\/\/[^\s]+$/i.test(u)) {
    return u;
  }
  return /^[a-z0-9-]+(\.[a-z0-9-]+)+(\/[^\s]*)?$/i.test(u) ? `https://${u}` : null;
};

/**
 * A website without the scheme and the trailing slash, for display.
 * @param url - e.g. "https://www.cafe.example/"
 * @returns e.g. "www.cafe.example"
 */
export const shortUrl = (url: string): string =>
  url.replace(/^https?:\/\//i, '').replace(/\/$/, '');

/**
 * A phone number as a tel: link (the first of several separated by ";").
 * @param phone - e.g. "+972 8-123 4567; +972 50 1234567"
 * @returns e.g. "tel:+97281234567"; null when there are no digits
 */
export const telUrl = (phone: string | null | undefined): string | null => {
  const first = (phone ?? '').split(';')[0].replace(/[^\d+]/g, '');
  return /\d/.test(first) ? `tel:${first}` : null;
};

/** Generic names that do not really name a place */
const GENERIC_NAMES = new Set([
  '',
  'place',
  'unnamed place',
  'saved place',
  'searched place',
  'visit',
]);

/**
 * Whether a place's name is only a placeholder.
 * @param name - The place name
 * @returns True for empty or generic names ("Place", "Unnamed place", "Saved place"…)
 */
export const isUnnamedPlace = (name: string | null | undefined): boolean =>
  GENERIC_NAMES.has((name ?? '').trim().toLowerCase());

/**
 * A short name for a street address: the road and house number.
 * @param address - The looked-up address
 * @returns e.g. "Herzl St 12"; the first part of the display line when there is no road
 */
export const nameFromAddress = (address: LookupAddress): string => {
  if (address.road) {
    return [address.road, address.houseNumber].filter(Boolean).join(' ');
  }
  return address.display.split(',')[0].trim();
};

/** One detail of a place worth showing */
export interface DetailRow {
  key: string;
  label: string;
  value: string;
  /** A link for the value */
  href?: string;
}

/** The details shown, in order, with their labels */
const DETAIL_LABELS: [string, string][] = [
  ['cuisine', 'Cuisine'],
  ['brand', 'Brand'],
  ['operator', 'Operator'],
  ['wikipedia', 'Wikipedia'],
];

/**
 * The useful details of a place (cuisine, brand, operator, Wikipedia).
 * @param details - Every detail found
 * @returns The ones worth showing; values like "pizza;italian" read "pizza, italian"
 */
export const detailRows = (details: Record<string, string> | null | undefined): DetailRow[] =>
  DETAIL_LABELS.flatMap(([key, label]) => {
    const raw = details?.[key];
    if (!isSet(raw) || !raw.trim()) {
      return [];
    }
    if (key === 'wikipedia') {
      const href = wikipediaUrl(raw);
      const value = href ? raw.slice(raw.indexOf(':') + 1).trim() : raw.trim();
      return [href ? { key, label, value, href } : { key, label, value }];
    }
    const value = raw
      .split(';')
      .map((x) => x.trim().replace(/_/g, ' '))
      .filter(Boolean)
      .join(', ');
    return [{ key, label, value }];
  });

/**
 * The line that says how a place got its identity (shown under its name).
 * @param state - The place's identifyState
 * @param identifiedFrom - 'lookup', 'nearby' or null
 * @returns e.g. "Identified automatically from a nearby place"; null when nothing needs saying
 */
export const identifyNote = (
  state: string | null | undefined,
  identifiedFrom?: string | null,
): string | null => {
  if (state === 'auto') {
    return identifiedFrom === 'nearby'
      ? 'Identified automatically from a nearby place'
      : 'Identified automatically';
  }
  if (state === 'ambiguous') {
    return 'Which one is it?';
  }
  if (state === 'pending') {
    return 'Not identified yet';
  }
  return null;
};

/**
 * The small mark of a place in lists and on the map.
 * @param state - The place's identifyState
 * @returns '?' when ambiguous, 'auto' when identified automatically, else null
 */
export const identifyMark = (state: string | null | undefined): '?' | 'auto' | null => {
  if (state === 'ambiguous') {
    return '?';
  }
  return state === 'auto' ? 'auto' : null;
};

/** At most this many nearby suggestions are checked at first */
export const NEARBY_CHECKED_MAX = 3;

/**
 * The nearby places checked at first: all of a few, none of many (the user picks).
 * @param suggested - The nearby places that may be the same
 * @returns Their ids, all when at most NEARBY_CHECKED_MAX, else none
 */
export const defaultNearbySelection = (suggested: NearbyPlace[]): string[] =>
  suggested.length <= NEARBY_CHECKED_MAX ? suggested.map((p) => p.placeId) : [];

/**
 * Says how many nearby places got the identity.
 * @param count - The number of places
 * @returns e.g. "Also named 2 nearby places"
 */
export const alsoNamedText = (count: number): string =>
  `Also named ${count} nearby ${count === 1 ? 'place' : 'places'}`;
