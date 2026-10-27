/**
 * @fileoverview The base map style: OpenFreeMap's dark vector style with
 * every label in English (or a Latin transliteration) instead of the local language
 * @module Contacts/atlas/mapStyle
 */

/** OpenFreeMap dark style (free, no key; OpenStreetMap data) */
export const STYLE_URL = 'https://tiles.openfreemap.org/styles/dark';

/** The base maps to choose from */
export type MapStyle = 'streets' | 'dark' | 'light' | 'satellite';

/** Choices in menu order */
export const MAP_STYLES: {
  value: MapStyle;
  label: string;
  /** Vector style (English labels), or a raster tile URL for satellite */
  url: string;
  raster?: boolean;
  /** The map is light: the selected track is drawn dark */
  light: boolean;
  attribution?: string;
}[] = [
  {
    value: 'streets',
    label: 'Streets',
    url: 'https://tiles.openfreemap.org/styles/liberty',
    light: true,
  },
  { value: 'dark', label: 'Dark', url: STYLE_URL, light: false },
  {
    value: 'light',
    label: 'Light',
    url: 'https://tiles.openfreemap.org/styles/positron',
    light: true,
  },
  {
    value: 'satellite',
    label: 'Satellite',
    url: 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
    raster: true,
    light: false,
    attribution:
      'Imagery &copy; <a href="https://www.esri.com" target="_blank" rel="noreferrer">Esri</a>, Maxar, Earthstar Geographics',
  },
];

/** Used until the user picks one: streets, places and buildings to the last detail */
export const DEFAULT_MAP_STYLE: MapStyle = 'streets';

const MAP_KEY = 'atlas.mapStyle';

/** The remembered base map (the default when none, or storage is unavailable) */
export const loadMapStyle = (): MapStyle => {
  try {
    const v = localStorage.getItem(MAP_KEY);
    return MAP_STYLES.some((m) => m.value === v) ? (v as MapStyle) : DEFAULT_MAP_STYLE;
  } catch {
    return DEFAULT_MAP_STYLE;
  }
};

/** Remembers the base map in this browser */
export const saveMapStyle = (style: MapStyle): void => {
  try {
    localStorage.setItem(MAP_KEY, style);
  } catch {
    // blocked storage: the choice lasts for this visit
  }
};

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

/** What the map draws: each item (clickable), or how often places were visited (density) */
export type MapMode = 'items' | 'density';

const MODE_KEY = 'atlas.mapMode';

/** The remembered map mode ('items' when none, or storage is unavailable) */
export const loadMapMode = (): MapMode => {
  try {
    return localStorage.getItem(MODE_KEY) === 'density' ? 'density' : 'items';
  } catch {
    return 'items';
  }
};

/** Remembers the map mode in this browser */
export const saveMapMode = (mode: MapMode): void => {
  try {
    localStorage.setItem(MODE_KEY, mode);
  } catch {
    // blocked storage: the choice lasts for this visit
  }
};

/** Where the photos toggle is kept */
const PHOTOS_KEY = 'atlas.mapPhotos';

/** Whether the map shows all photos (remembered in this browser) */
export const loadMapPhotos = (): boolean => {
  try {
    return localStorage.getItem(PHOTOS_KEY) === '1';
  } catch {
    return false;
  }
};

/** Remembers the photos toggle in this browser */
export const saveMapPhotos = (on: boolean): void => {
  try {
    localStorage.setItem(PHOTOS_KEY, on ? '1' : '0');
  } catch {
    // blocked storage: the choice lasts for this visit
  }
};
