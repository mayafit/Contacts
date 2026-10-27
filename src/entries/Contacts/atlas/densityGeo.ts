/**
 * @fileoverview The density map's data and styling: tracks and visits as GeoJSON, the
 * colour of each activity type as a MapLibre expression, the weight of a visit
 * @module Contacts/atlas/densityGeo
 */

import type { DensityTrack } from './types';
import { ACTIVITY_TYPES, VISIT_COLOR, typeColor } from './types';
import { decodePolyline, greatCircle } from './geo';

/** A GeoJSON line of one track ([lon, lat] positions) */
export interface TrackFeature {
  type: 'Feature';
  properties: { type: string };
  geometry: { type: 'LineString'; coordinates: [number, number][] };
}

/** A GeoJSON point of one visit, with its heat weight */
export interface VisitFeature {
  type: 'Feature';
  properties: { w: number };
  geometry: { type: 'Point'; coordinates: [number, number] };
}

/** A GeoJSON feature collection */
export interface FeatureCollection<F> {
  type: 'FeatureCollection';
  features: F[];
}

/** Hours at which a visit weighs fully on the heat map */
const FULL_WEIGHT_HOURS = 24;
/** The least a visit weighs (a quick stop still shows) */
const MIN_WEIGHT = 0.05;

/**
 * The heat weight of a visit: log-scaled, so a long stay counts more than a stop
 * without drowning everything around it.
 * @param hours - Hours spent
 * @returns 0.05 (a moment, or unknown) to 1 (a day or more)
 */
export const visitWeight = (hours: number): number => {
  if (!Number.isFinite(hours) || hours <= 0) {
    return MIN_WEIGHT;
  }
  const w = Math.log1p(hours) / Math.log1p(FULL_WEIGHT_HOURS);
  return Math.min(1, Math.max(MIN_WEIGHT, w));
};

/**
 * Tracks as GeoJSON lines with their type. Flights are drawn as the arc from departure to
 * arrival, as on the items map; tracks of fewer than two points are left out.
 * @param tracks - The density tracks (encoded polylines)
 * @returns A collection of LineStrings
 */
export const tracksGeoJson = (tracks: DensityTrack[]): FeatureCollection<TrackFeature> => ({
  type: 'FeatureCollection',
  features: tracks.flatMap((t) => {
    const raw = t.polyline ? decodePolyline(t.polyline) : [];
    if (raw.length < 2) {
      return [];
    }
    const path = t.type === 'flight' ? greatCircle(raw[0], raw[raw.length - 1]) : raw;
    return [
      {
        type: 'Feature' as const,
        properties: { type: t.type },
        geometry: {
          type: 'LineString' as const,
          coordinates: path.map(([lat, lon]) => [lon, lat] as [number, number]),
        },
      },
    ];
  }),
});

/**
 * Visits as GeoJSON points weighted by the time spent.
 * @param visits - [lat, lon, hours] per visit
 * @returns A collection of Points
 */
export const visitsGeoJson = (
  visits: [number, number, number][],
): FeatureCollection<VisitFeature> => ({
  type: 'FeatureCollection',
  features: visits
    .filter(([lat, lon]) => Number.isFinite(lat) && Number.isFinite(lon))
    .map(([lat, lon, hours]) => ({
      type: 'Feature' as const,
      properties: { w: visitWeight(hours) },
      geometry: { type: 'Point' as const, coordinates: [lon, lat] as [number, number] },
    })),
});

/**
 * The colour of each activity type as a MapLibre expression (the same colours as the map).
 * @returns ['match', ['get', 'type'], 'ride', '#…', …, other colour]
 */
export const typeColorExpression = (): unknown[] => [
  'match',
  ['get', 'type'],
  ...Object.entries(ACTIVITY_TYPES)
    .filter(([k]) => k !== 'other')
    .flatMap(([k, v]) => [k, v.color]),
  typeColor('other'),
];

/**
 * A "#rrggbb" colour with an alpha.
 * @param hex - e.g. "#e879f9"
 * @param alpha - 0 to 1
 * @returns e.g. "rgba(232,121,249,0.5)"
 */
export const withAlpha = (hex: string, alpha: number): string => {
  const n = parseInt(hex.replace('#', ''), 16);
  return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${alpha})`;
};

/**
 * The heat map's colours, from transparent through the visit colour to white where
 * visits pile up.
 * @returns A MapLibre interpolate expression on heatmap-density
 */
export const visitHeatColor = (): unknown[] => [
  'interpolate',
  ['linear'],
  ['heatmap-density'],
  0,
  withAlpha(VISIT_COLOR, 0),
  0.15,
  withAlpha(VISIT_COLOR, 0.3),
  0.4,
  withAlpha(VISIT_COLOR, 0.65),
  0.7,
  withAlpha(VISIT_COLOR, 0.9),
  1,
  '#ffffff',
];

/**
 * The activity types in the tracks, for the legend.
 * @param tracks - The density tracks
 * @returns Each type once
 */
export const trackTypes = (tracks: DensityTrack[]): string[] => [
  ...new Set(tracks.map((t) => t.type || 'other')),
];
