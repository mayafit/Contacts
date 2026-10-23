/**
 * @fileoverview The base map style: OpenFreeMap's dark vector style with
 * every label in English (or a Latin transliteration) instead of the local language
 * @module Contacts/atlas/mapStyle
 */

/** OpenFreeMap dark style (free, no key; OpenStreetMap data) */
export const STYLE_URL = 'https://tiles.openfreemap.org/styles/dark';

/** Attribution required by OpenFreeMap / OpenMapTiles / OpenStreetMap */
export const ATTRIBUTION =
  '<a href="https://openfreemap.org" target="_blank" rel="noreferrer">OpenFreeMap</a> ' +
  '&copy; <a href="https://www.openmaptiles.org/" target="_blank" rel="noreferrer">OpenMapTiles</a> ' +
  'Data from <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noreferrer">OpenStreetMap</a>';

/** English name, else the Latin transliteration, else the local name */
export const ENGLISH_NAME = [
  'coalesce',
  ['get', 'name:en'],
  ['get', 'name_en'],
  ['get', 'name:latin'],
  ['get', 'name'],
];

interface StyleLayer {
  type?: string;
  layout?: Record<string, unknown>;
}

/**
 * Switches every name label of a style to English. Labels that show
 * something else (road numbers, house numbers) are left alone.
 * @param style - A MapLibre style document (changed in place)
 * @returns The same style
 */
export const englishLabels = <T extends { layers?: StyleLayer[] }>(style: T): T => {
  (style.layers ?? []).forEach((layer) => {
    const field = layer.layout?.['text-field'];
    if (layer.type === 'symbol' && field && /name/.test(JSON.stringify(field))) {
      layer.layout!['text-field'] = ENGLISH_NAME;
    }
  });
  return style;
};
