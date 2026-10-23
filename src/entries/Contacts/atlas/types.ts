/**
 * @fileoverview Atlas data shapes (match the backend's Atlas DTOs)
 * @module Contacts/atlas/types
 */

/** A track point to import */
export interface PointInput {
  t: string;
  lat: number;
  lon: number;
  ele?: number | null;
  hr?: number | null;
  speed?: number | null;
  cad?: number | null;
}

/** An activity to import */
export interface ActivityInput {
  externalId?: string | null;
  type: string;
  name?: string | null;
  startTime?: string | null;
  endTime?: string | null;
  distanceM?: number | null;
  durationS?: number | null;
  elevationGainM?: number | null;
  notes?: string | null;
  points?: PointInput[];
}

/** A place to import */
export interface PlaceInput {
  externalId?: string | null;
  name: string;
  lat: number;
  lon: number;
  address?: string | null;
  category?: string | null;
  googlePlaceId?: string | null;
}

/** A visit to import */
export interface VisitInput {
  externalId?: string | null;
  startTime: string;
  endTime: string;
  lat: number;
  lon: number;
  placeExternalId?: string | null;
  placeName?: string | null;
  confidence?: number | null;
}

/** A health sample to import */
export interface HealthInput {
  type: string;
  startTime: string;
  value: number;
  endTime?: string | null;
  unit?: string | null;
}

/** What one parsed file contributes */
export interface ParsedData {
  source: string;
  activities: ActivityInput[];
  places: PlaceInput[];
  visits: VisitInput[];
  health: HealthInput[];
}

/** An activity as listed (no points) */
export interface ActivitySummary {
  id: string;
  type: string;
  name?: string | null;
  startTime: string;
  endTime: string;
  distanceM?: number | null;
  durationS?: number | null;
  elevationGainM?: number | null;
  avgHeartRate?: number | null;
  maxHeartRate?: number | null;
  source: string;
  polyline?: string | null;
  minLat?: number | null;
  minLon?: number | null;
  maxLat?: number | null;
  maxLon?: number | null;
  pointCount: number;
  /** Sources of the fused activity's members, e.g. ["sports-tracker", "fitbit"] */
  sources: string[];
  /** Source records fused into this activity (1 when not fused) */
  memberCount: number;
  /** Fields the sources disagree on (0 when none) */
  conflictCount: number;
}

/** One source record contributing to a fused activity */
export interface ActivityMember {
  id: string;
  source: string;
  type: string;
  name: string | null;
  startTime: string;
  endTime: string;
  distanceM: number | null;
  durationS: number | null;
  avgHeartRate: number | null;
  maxHeartRate: number | null;
  pointCount: number;
  primary: boolean;
  importId: string | null;
}

/** A field the sources of a fused activity disagree on */
export interface FusionConflict {
  field: 'type' | 'distanceM' | 'startTime' | 'endTime' | 'track';
  /** Human text, e.g. "Sports Tracker says Ride, Google Timeline says Drive" */
  message: string;
  values: { source: string; value: string }[];
  /** The value used */
  chosen: string;
}

/** A place visit linked to an activity */
export interface LinkedPlace {
  visitId: string;
  placeName: string | null;
  address: string | null;
  role: 'start' | 'end' | 'during';
  startTime: string;
  endTime: string;
  lat: number;
  lon: number;
}

/** A connected account that Atlas syncs from */
export interface SourceConnection {
  provider: 'sports-tracker';
  /** Username or email as entered */
  account: string | null;
  status: 'idle' | 'syncing' | 'error' | 'expired';
  lastSyncAt: string | null;
  lastError: string | null;
  /** Workouts found in the last sync */
  itemsTotal: number;
  /** Workouts processed so far */
  itemsDone: number;
  /** New or updated in the last sync */
  itemsImported: number;
}

/** Result of re-running fusion over everything */
export interface FuseResult {
  activities: number;
  fused: number;
  conflicts: number;
}

/** A visit with its place */
export interface VisitSummary {
  id: string;
  placeId?: string | null;
  placeName?: string | null;
  address?: string | null;
  startTime: string;
  endTime: string;
  lat: number;
  lon: number;
  source: string;
}

/** Search filters */
export interface AtlasQuery {
  from?: string;
  to?: string;
  minLat?: number;
  minLon?: number;
  maxLat?: number;
  maxLon?: number;
  types?: string[];
  text?: string;
  activities?: boolean;
  visits?: boolean;
  limit?: number;
}

/** Search results */
export interface SearchResult {
  activities: ActivitySummary[];
  visits: VisitSummary[];
  totalActivities: number;
  totalVisits: number;
}

/** A track point as returned */
export interface PointOutput {
  t: string;
  lat: number;
  lon: number;
  ele?: number | null;
  hr?: number | null;
  speed?: number | null;
}

/** A value over time */
export interface SeriesPoint {
  t: string;
  v: number;
  min?: number | null;
  max?: number | null;
}

/** An activity with track and heart rate */
export interface ActivityDetail {
  summary: ActivitySummary;
  notes?: string | null;
  points: PointOutput[];
  heartRateSource?: 'track' | 'health' | 'none' | null;
  heartRate: SeriesPoint[];
  /** Every contributing source record, primary first */
  members: ActivityMember[];
  conflicts: FusionConflict[];
  /** Field → the source that supplied it, e.g. { distanceM: "sports-tracker" } */
  fieldSources: Record<string, string>;
  /** Visits linked to the activity */
  places: LinkedPlace[];
}

/** Timeline histogram row */
export interface HistogramRow {
  bucket: string;
  kind: string;
  count: number;
}

/** One import run */
export interface ImportRun {
  id: string;
  source: string;
  fileName?: string | null;
  createdAt: string;
  activities: number;
  places: number;
  visits: number;
  healthSamples: number;
}

/** Totals */
export interface AtlasSummary {
  activities: number;
  places: number;
  visits: number;
  healthSamples: number;
  first?: string | null;
  last?: string | null;
  byType: Record<string, { count: number; distanceKm: number }>;
  imports: ImportRun[];
}

/** Visits and activities near a point */
export interface NearResult {
  visits: VisitSummary[];
  activities: ActivitySummary[];
}

/** An MCP access token as listed */
export interface AccessTokenInfo {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
  lastUsedAt?: string | null;
}

/** Activity types with their colour on the map */
export const ACTIVITY_TYPES: Record<string, { label: string; color: string }> = {
  ride: { label: 'Ride', color: '#3fb6ff' },
  hike: { label: 'Hike', color: '#5bd16b' },
  run: { label: 'Run', color: '#ff8a3d' },
  walk: { label: 'Walk', color: '#c9a7ff' },
  drive: { label: 'Drive', color: '#8b95a5' },
  transit: { label: 'Transit', color: '#6f7d92' },
  flight: { label: 'Flight', color: '#f2d15c' },
  ski: { label: 'Ski', color: '#9fe7ff' },
  swim: { label: 'Swim', color: '#3ee0c5' },
  other: { label: 'Other', color: '#b0b7c3' },
};

/** Colour of an activity type */
export const typeColor = (type: string): string =>
  ACTIVITY_TYPES[type]?.color ?? ACTIVITY_TYPES.other.color;

/** Place visits: a colour no activity type uses */
export const VISIT_COLOR = '#e879f9';
/** Health data (heart rate) */
export const HEALTH_COLOR = '#ff4d6d';

/** Colour of a timeline/map kind: an activity type, 'visit' or 'health' */
export const kindColor = (kind: string): string =>
  kind === 'visit' ? VISIT_COLOR : kind === 'health' ? HEALTH_COLOR : typeColor(kind);

/** Name of a timeline/map kind */
export const kindLabel = (kind: string): string =>
  kind === 'visit'
    ? 'Place visits'
    : kind === 'health'
      ? 'Health data'
      : (ACTIVITY_TYPES[kind]?.label ?? kind);

/** Friendly names of the data sources */
export const SOURCE_LABELS: Record<string, string> = {
  'sports-tracker': 'Sports Tracker',
  'google-timeline': 'Google Timeline',
  'google-fit': 'Google Fit',
  fitbit: 'Fitbit',
  gpx: 'GPX file',
  tcx: 'TCX file',
  manual: 'Manual',
  user: 'You',
};

/**
 * Friendly name of a data source; unknown sources are shown as they are.
 * @param source - Source id, e.g. "sports-tracker"
 * @returns Display name, e.g. "Sports Tracker"
 */
export const sourceLabel = (source: string | null | undefined): string =>
  !source ? 'Unknown' : (SOURCE_LABELS[source] ?? source);

/** Friendly names of the fused fields (keys of ActivityDetail.fieldSources) */
export const FIELD_LABELS: Record<string, string> = {
  type: 'Type',
  name: 'Name',
  notes: 'Notes',
  startTime: 'Start',
  endTime: 'End',
  distanceM: 'Distance',
  durationS: 'Time',
  time: 'Times',
  elevationGainM: 'Climb',
  track: 'Track',
  heartRate: 'Heart rate',
  avgHeartRate: 'Heart rate',
  maxHeartRate: 'Max heart rate',
};

/**
 * Where a fused activity's values come from, as one line.
 * Fields with the same label (e.g. heartRate and avgHeartRate) are listed once.
 * @param fieldSources - Field → source
 * @returns e.g. "Distance: Sports Tracker · Heart rate: Fitbit"; '' when empty
 */
export const describeFieldSources = (fieldSources: Record<string, string> | null | undefined) => {
  const seen = new Set<string>();
  const parts: string[] = [];
  Object.entries(fieldSources ?? {}).forEach(([field, source]) => {
    const label = FIELD_LABELS[field] ?? field;
    if (seen.has(label)) {
      return;
    }
    seen.add(label);
    parts.push(`${label}: ${sourceLabel(source)}`);
  });
  return parts.join(' · ');
};

/** Error codes of the Sports Tracker connection (ApiRequestError.code) */
export const SPORTS_TRACKER_ERRORS = {
  /** Sports Tracker refused the server's sign-in: offer the session-key route */
  loginRefused: 'SPORTS_TRACKER_LOGIN_REFUSED',
  /** The pasted session key does not work */
  keyInvalid: 'SPORTS_TRACKER_KEY_INVALID',
  /** The stored session key has expired: sign in again */
  keyExpired: 'SPORTS_TRACKER_KEY_EXPIRED',
} as const;

/** Neither null nor undefined (a type guard) */
export const isSet = <T>(value: T | null | undefined): value is T =>
  value !== null && value !== undefined;
