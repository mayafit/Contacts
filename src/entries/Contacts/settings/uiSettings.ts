/**
 * @fileoverview The Owner's appearance settings (AD-41): the schema, today's Echo as the
 * defaults, the ranges, the merge of a stored document over the defaults (unknown keys
 * dropped, numbers clamped, colours checked), the route patterns, the text sizes outside
 * the theme (fs), and the live store that non-React code reads.
 * @module Contacts/settings/uiSettings
 */

/** The activity types that have a colour and a route style */
export const STYLED_TYPES = [
  'ride',
  'hike',
  'run',
  'walk',
  'drive',
  'transit',
  'flight',
  'ski',
  'swim',
  'other',
] as const;
export type StyledType = (typeof STYLED_TYPES)[number];

/** A line's pattern: drawn in proportion to its width */
export type LinePattern = 'solid' | 'dashed' | 'dotted';

/** A line's style */
export interface LineStyle {
  width: number;
  opacity: number;
  pattern: LinePattern;
}

/** The pointer: the system's, or Echo's own at a size */
export type PointerSize = 'system' | 24 | 32 | 48;

/** Every appearance setting, resolved (no value missing) */
export interface UiSettings {
  version: 1;
  text: {
    body: number;
    small: number;
    headings: number;
    charts: number;
    map: number;
    navigation: number;
  };
  pointer: { size: PointerSize };
  map: {
    visit: number;
    visitSelected: number;
    photo: number;
    photoPin: number;
    trackPoint: number;
    hover: number;
    airport: number;
    waypoint: number;
    grab: number;
  };
  colors: Record<StyledType, string> & {
    visit: string;
    photo: string;
    health: string;
    area: string;
    inferred: string;
    gap: string;
    waypoint: string;
    highlightDark: string;
    highlightLight: string;
  };
  routes: {
    types: Record<StyledType, LineStyle>;
    selectedWidth: number;
    dimmedOpacity: number;
    crowdedWidth: number;
    observedWidth: number;
    inferred: { width: number; pattern: LinePattern };
    gap: { width: number; pattern: LinePattern };
  };
  charts: {
    speed: string;
    elevation: string;
    cadence: string;
    heartRate: string;
    zones: [string, string, string, string, string];
  };
}

const typeStyle = (width = 2.5, pattern: LinePattern = 'solid'): LineStyle => ({
  width,
  opacity: 0.9,
  pattern,
});

/** Today's Echo */
export const DEFAULT_UI: UiSettings = {
  version: 1,
  text: { body: 15, small: 13, headings: 100, charts: 11, map: 11, navigation: 11.5 },
  pointer: { size: 'system' },
  map: {
    visit: 5,
    visitSelected: 8,
    photo: 4,
    photoPin: 34,
    trackPoint: 2,
    hover: 7,
    airport: 3.5,
    waypoint: 20,
    grab: 0,
  },
  colors: {
    ride: '#3fb6ff',
    hike: '#5bd16b',
    run: '#ff8a3d',
    walk: '#c9a7ff',
    drive: '#8b95a5',
    transit: '#6f7d92',
    flight: '#f2d15c',
    ski: '#9fe7ff',
    swim: '#3ee0c5',
    other: '#b0b7c3',
    visit: '#e879f9',
    photo: '#f472b6',
    health: '#ff4d6d',
    area: '#3987e5',
    inferred: '#ffb020',
    gap: '#9ca3af',
    waypoint: '#d946ef',
    highlightDark: '#ffffff',
    highlightLight: '#111827',
  },
  routes: {
    types: {
      ride: typeStyle(),
      hike: typeStyle(),
      run: typeStyle(),
      walk: typeStyle(),
      drive: typeStyle(),
      transit: typeStyle(),
      flight: typeStyle(2, 'dashed'),
      ski: typeStyle(),
      swim: typeStyle(),
      other: typeStyle(),
    },
    selectedWidth: 5,
    dimmedOpacity: 0.3,
    crowdedWidth: 1.5,
    observedWidth: 4.5,
    inferred: { width: 4, pattern: 'dashed' },
    gap: { width: 1.5, pattern: 'dotted' },
  },
  charts: {
    speed: '#3fb6ff',
    elevation: '#5bd16b',
    cadence: '#c9a7ff',
    heartRate: '#ff4d6d',
    zones: ['#7aa7d9', '#5bd16b', '#f2d15c', '#ff8a3d', '#ff4d6d'],
  },
};

/** [min, max, step] of every number, by its path */
export const RANGES: Record<string, [number, number, number]> = {
  'text.body': [11, 28, 0.5],
  'text.small': [9, 24, 0.5],
  'text.headings': [70, 200, 5],
  'text.charts': [8, 22, 0.5],
  'text.map': [8, 22, 0.5],
  'text.navigation': [9, 20, 0.5],
  'map.visit': [2, 25, 0.5],
  'map.visitSelected': [3, 30, 0.5],
  'map.photo': [2, 20, 0.5],
  'map.photoPin': [20, 96, 2],
  'map.trackPoint': [1, 10, 0.5],
  'map.hover': [3, 25, 0.5],
  'map.airport': [2, 15, 0.5],
  'map.waypoint': [12, 48, 1],
  'map.grab': [0, 20, 1],
  'routes.width': [0.5, 10, 0.5],
  'routes.opacity': [0.1, 1, 0.05],
  'routes.selectedWidth': [1, 14, 0.5],
  'routes.dimmedOpacity': [0.05, 1, 0.05],
  'routes.crowdedWidth': [0.5, 6, 0.5],
  'routes.observedWidth': [1, 12, 0.5],
  'routes.inferred.width': [1, 12, 0.5],
  'routes.gap.width': [1, 12, 0.5],
};

export const POINTER_SIZES: PointerSize[] = ['system', 24, 32, 48];
export const PATTERNS: LinePattern[] = ['solid', 'dashed', 'dotted'];

const clamp = (v: unknown, path: string, fallback: number): number => {
  const [min, max] = RANGES[path];
  const n = typeof v === 'number' ? v : typeof v === 'string' ? Number(v) : NaN;
  return Number.isFinite(n) ? Math.min(max, Math.max(min, n)) : fallback;
};

const colour = (v: unknown, fallback: string): string =>
  typeof v === 'string' && /^#[0-9a-f]{6}$/i.test(v.trim()) ? v.trim().toLowerCase() : fallback;

const pattern = (v: unknown, fallback: LinePattern): LinePattern =>
  PATTERNS.includes(v as LinePattern) ? (v as LinePattern) : fallback;

const obj = (v: unknown): Record<string, unknown> =>
  v && typeof v === 'object' && !Array.isArray(v) ? (v as Record<string, unknown>) : {};

/**
 * A stored document over the defaults: every known key taken when valid, clamped into its
 * range; unknown keys dropped; anything missing or invalid from the defaults.
 * @param stored - The document as stored (any shape)
 * @returns Whole, valid settings
 */
export const mergeUi = (stored: unknown): UiSettings => {
  const s = obj(stored);
  const d = DEFAULT_UI;
  const text = obj(s.text);
  const map = obj(s.map);
  const colors = obj(s.colors);
  const routes = obj(s.routes);
  const routeTypes = obj(routes.types);
  const charts = obj(s.charts);
  const zones = Array.isArray(charts.zones) ? charts.zones : [];
  const num = <K extends string>(
    group: Record<string, unknown>,
    prefix: string,
    keys: readonly K[],
    def: Record<K, number>,
  ) =>
    Object.fromEntries(keys.map((k) => [k, clamp(group[k], `${prefix}.${k}`, def[k])])) as Record<
      K,
      number
    >;
  const size = obj(s.pointer).size;
  return {
    version: 1,
    text: num(
      text,
      'text',
      ['body', 'small', 'headings', 'charts', 'map', 'navigation'] as const,
      d.text,
    ),
    pointer: {
      size: POINTER_SIZES.includes(size as PointerSize) ? (size as PointerSize) : d.pointer.size,
    },
    map: num(
      map,
      'map',
      [
        'visit',
        'visitSelected',
        'photo',
        'photoPin',
        'trackPoint',
        'hover',
        'airport',
        'waypoint',
        'grab',
      ] as const,
      d.map,
    ),
    colors: Object.fromEntries(
      Object.keys(d.colors).map((k) => [
        k,
        colour(colors[k], d.colors[k as keyof UiSettings['colors']]),
      ]),
    ) as UiSettings['colors'],
    routes: {
      types: Object.fromEntries(
        STYLED_TYPES.map((t) => {
          const v = obj(routeTypes[t]);
          const def = d.routes.types[t];
          return [
            t,
            {
              width: clamp(v.width, 'routes.width', def.width),
              opacity: clamp(v.opacity, 'routes.opacity', def.opacity),
              pattern: pattern(v.pattern, def.pattern),
            },
          ];
        }),
      ) as Record<StyledType, LineStyle>,
      selectedWidth: clamp(routes.selectedWidth, 'routes.selectedWidth', d.routes.selectedWidth),
      dimmedOpacity: clamp(routes.dimmedOpacity, 'routes.dimmedOpacity', d.routes.dimmedOpacity),
      crowdedWidth: clamp(routes.crowdedWidth, 'routes.crowdedWidth', d.routes.crowdedWidth),
      observedWidth: clamp(routes.observedWidth, 'routes.observedWidth', d.routes.observedWidth),
      inferred: {
        width: clamp(obj(routes.inferred).width, 'routes.inferred.width', d.routes.inferred.width),
        pattern: pattern(obj(routes.inferred).pattern, d.routes.inferred.pattern),
      },
      gap: {
        width: clamp(obj(routes.gap).width, 'routes.gap.width', d.routes.gap.width),
        pattern: pattern(obj(routes.gap).pattern, d.routes.gap.pattern),
      },
    },
    charts: {
      speed: colour(charts.speed, d.charts.speed),
      elevation: colour(charts.elevation, d.charts.elevation),
      cadence: colour(charts.cadence, d.charts.cadence),
      heartRate: colour(charts.heartRate, d.charts.heartRate),
      zones: d.charts.zones.map((z, i) => colour(zones[i], z)) as UiSettings['charts']['zones'],
    },
  };
};

/**
 * A pattern as a Leaflet dash array, growing with the width so a wider line keeps its look,
 * and never finer than today's (dashed max(8, 2w) max(6, 1.5w): "8 6" up to 4 px; dotted
 * max(1, 0.6w) max(5, 2w): "1 5" at 1.5 px); none for solid.
 * @param p - The pattern
 * @param width - The line's width
 * @returns The dash array, or undefined
 */
export const dashArray = (p: LinePattern, width: number): string | undefined => {
  const r = (x: number) => Math.round(x * 10) / 10;
  if (p === 'dashed') {
    return `${r(Math.max(8, 2 * width))} ${r(Math.max(6, 1.5 * width))}`;
  }
  if (p === 'dotted') {
    return `${r(Math.max(1, 0.6 * width))} ${r(Math.max(5, 2 * width))}`;
  }
  return undefined;
};

/** The CSS variables of the text sizes, set on the document's root */
export const textVariables = (t: UiSettings['text']): Record<string, string> => ({
  '--ui-small': String(t.small / DEFAULT_UI.text.small),
  '--ui-body': String(t.body / DEFAULT_UI.text.body),
  '--ui-heading': String(t.headings / 100),
  '--ui-chart': String(t.charts / DEFAULT_UI.text.charts),
  '--ui-map': String(t.map / DEFAULT_UI.text.map),
  '--ui-nav': String(t.navigation / DEFAULT_UI.text.navigation),
});

const scaled = (px: number, variable: string): string => `calc(${px}px * var(${variable}, 1))`;

/**
 * A font size set outside the theme, following the kind of text it is by its default size:
 * up to 13.5 px small text, up to 16.5 px body text, larger headings (AD-41 rule 5).
 * @param px - The size it had (today's Echo)
 * @returns A CSS size that follows the Owner's settings
 */
export const fs = Object.assign(
  (px: number): string =>
    scaled(px, px <= 13.5 ? '--ui-small' : px <= 16.5 ? '--ui-body' : '--ui-heading'),
  {
    /** Chart and timeline text */
    chart: (px: number): string => scaled(px, '--ui-chart'),
    /** Text on the map */
    map: (px: number): string => scaled(px, '--ui-map'),
    /** The navigation rail's labels */
    nav: (px: number): string => scaled(px, '--ui-nav'),
  },
);

/** The settings now in effect, for code outside React (kept by the SettingsProvider) */
let current: UiSettings = DEFAULT_UI;
const listeners = new Set<(s: UiSettings) => void>();

/** The settings now in effect */
export const currentUi = (): UiSettings => current;

/**
 * Makes these the settings in effect (the provider calls it before it renders them).
 * @param s - The settings
 */
export const setCurrentUi = (s: UiSettings): void => {
  current = s;
  listeners.forEach((l) => l(s));
};

/**
 * Called whenever the settings in effect change.
 * @param l - The listener
 * @returns Unsubscribe
 */
export const onUiChange = (l: (s: UiSettings) => void): (() => void) => {
  listeners.add(l);
  return () => {
    listeners.delete(l);
  };
};
