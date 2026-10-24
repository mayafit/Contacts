/**
 * @fileoverview Samsung Health export ("Download personal data"): workouts with
 * their heart rate and GPS files, and all-day heart rate
 * @module Contacts/atlas/importers/samsung
 */

import type { ActivityInput, HealthInput, ParsedData, PointInput } from '../types';
import { isSet } from '../types';

/** Source name of everything from Samsung Health */
export const SAMSUNG_SOURCE = 'samsung-health';

/** What a file of the export is */
export type SamsungKind = 'exercise' | 'heartRate' | 'live' | 'location' | 'binning' | null;

/**
 * Which part of a Samsung Health export a path is.
 * @param path - Path inside the export folder or zip
 * @returns Kind, or null when not used
 */
export const samsungKind = (path: string): SamsungKind => {
  const p = path.toLowerCase().replace(/\\/g, '/');
  const name = p.split('/').pop() ?? p;
  if (name.endsWith('.csv')) {
    if (
      name.startsWith('com.samsung.shealth.exercise.') ||
      name.startsWith('com.samsung.health.exercise.')
    ) {
      return 'exercise';
    }
    if (name.startsWith('com.samsung.shealth.tracker.heart_rate.')) {
      return 'heartRate';
    }
    return null;
  }
  if (name.endsWith('.live_data.json') && p.includes('exercise')) {
    return 'live';
  }
  if (name.endsWith('.location_data.json') && p.includes('exercise')) {
    return 'location';
  }
  if (name.endsWith('.binning_data.json') && p.includes('heart_rate')) {
    return 'binning';
  }
  return null;
};

/** CSV with quoted fields (commas and line breaks inside quotes) */
const parseCsv = (text: string): string[][] => {
  const rows: string[][] = [];
  let row: string[] = [];
  let cell = '';
  let quoted = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c === '"' && text[i + 1] === '"') {
        cell += '"';
        i++;
      } else if (c === '"') {
        quoted = false;
      } else {
        cell += c;
      }
    } else if (c === '"') {
      quoted = true;
    } else if (c === ',') {
      row.push(cell);
      cell = '';
    } else if (c === '\n' || c === '\r') {
      if (c === '\r' && text[i + 1] === '\n') {
        i++;
      }
      row.push(cell);
      if (row.some((x) => x.length > 0)) {
        rows.push(row);
      }
      row = [];
      cell = '';
    } else {
      cell += c;
    }
  }
  row.push(cell);
  if (row.some((x) => x.length > 0)) {
    rows.push(row);
  }
  return rows;
};

/**
 * Rows of a Samsung Health CSV: the first line names the table, the second has
 * the columns ("com.samsung.health.exercise.start_time" is read as "start_time").
 * @param text - File content
 * @returns Rows by short column name
 */
export const parseSamsungCsv = (text: string): Record<string, string>[] => {
  // Without a byte-order mark
  const rows = parseCsv(text.charCodeAt(0) === 0xfeff ? text.slice(1) : text);
  if (rows.length < 2) {
    return [];
  }
  // The table-name line has few cells; the header is the next line
  const headerAt = rows[0].length <= 3 && rows.length > 2 ? 1 : 0;
  const names = rows[headerAt].map((h) => (h.trim().split('.').pop() ?? h.trim()).toLowerCase());
  return rows.slice(headerAt + 1).map((cells) => {
    const row: Record<string, string> = {};
    names.forEach((n, i) => {
      if (n && !(n in row)) {
        row[n] = (cells[i] ?? '').trim();
      }
    });
    return row;
  });
};

/**
 * A Samsung time: "2025-01-02 03:27:10.123" is UTC (the offset is a separate
 * column); epoch milliseconds in the JSON files.
 * @param v - Value
 * @returns ISO time, or null
 */
export const samsungTime = (v: unknown): string | null => {
  if (typeof v === 'number' && Number.isFinite(v) && v > 0) {
    return new Date(v).toISOString();
  }
  if (typeof v !== 'string' || !v.trim()) {
    return null;
  }
  const s = v.trim();
  if (/^\d{12,}$/.test(s)) {
    return new Date(Number(s)).toISOString();
  }
  const zoned = /[zZ]|[+-]\d\d:?\d\d$/.test(s);
  const d = new Date(`${s.replace(' ', 'T')}${zoned ? '' : 'Z'}`);
  return Number.isNaN(d.getTime()) ? null : d.toISOString();
};

/** Samsung exercise type codes → Atlas types */
export const samsungType = (code: string | number): string => {
  const n = Number(code);
  if (n === 1001) {
    return 'walk';
  }
  if (n === 1002) {
    return 'run';
  }
  if (n >= 11000 && n < 11100) {
    return 'ride';
  }
  if (n === 13001) {
    return 'hike';
  }
  if (n >= 14000 && n < 14100) {
    return 'swim';
  }
  if (n >= 16000 && n < 16100) {
    return 'ski';
  }
  return 'other';
};

/** The data id a JSON file belongs to: the part of its name before ".com.samsung…" */
const uuidOf = (path: string): string => {
  const name = path.replace(/\\/g, '/').split('/').pop() ?? path;
  return name.split('.com.samsung')[0];
};

const num = (v: unknown): number | null => {
  const n = typeof v === 'number' ? v : Number(v);
  return Number.isFinite(n) && n !== 0 ? n : null;
};

const jsonArray = (text: string): Record<string, unknown>[] => {
  try {
    const v = JSON.parse(text) as unknown;
    return Array.isArray(v) ? (v as Record<string, unknown>[]) : [];
  } catch {
    return [];
  }
};

interface LiveSample {
  t: number;
  hr: number | null;
  speed: number | null;
  cad: number | null;
}

interface Workout {
  row?: Record<string, string>;
  live?: LiveSample[];
  location?: { t: number; lat: number; lon: number; ele: number | null }[];
}

/**
 * A workout as an Atlas activity: the GPS points with the heart rate, speed and
 * cadence of the nearest live sample (within 10 s).
 */
const toActivity = (id: string, w: Workout): ActivityInput | null => {
  const r = w.row!;
  const start = samsungTime(r.start_time);
  if (!start) {
    return null;
  }
  const live = w.live ?? [];
  let j = 0;
  const points: PointInput[] = (w.location ?? []).map((p) => {
    while (j < live.length - 1 && Math.abs(live[j + 1].t - p.t) <= Math.abs(live[j].t - p.t)) {
      j++;
    }
    const s = live.length && Math.abs(live[j].t - p.t) <= 10_000 ? live[j] : null;
    return {
      t: new Date(p.t).toISOString(),
      lat: p.lat,
      lon: p.lon,
      ele: p.ele,
      hr: s?.hr ? Math.round(s.hr) : null,
      speed: s?.speed ?? null,
      cad: s?.cad ? Math.round(s.cad) : null,
    };
  });
  const durationMs = num(r.duration);
  const meanHr = num(r.mean_heart_rate);
  const maxHr = num(r.max_heart_rate);
  return {
    externalId: id,
    type: samsungType(r.exercise_type),
    name: r.title || null,
    startTime: start,
    endTime: samsungTime(r.end_time),
    distanceM: num(r.distance),
    durationS: durationMs ? durationMs / 1000 : null,
    avgHeartRate: meanHr ? Math.round(meanHr) : null,
    maxHeartRate: maxHr ? Math.round(maxHr) : null,
    points: points.length > 1 ? points : undefined,
  };
};

/** Heart-rate values as health data */
const heartRates = (items: { t: string | null; v: number | null }[]): HealthInput[] =>
  items
    .filter((x): x is { t: string; v: number } => isSet(x.t) && isSet(x.v))
    .map((x) => ({ type: 'heart_rate', startTime: x.t, value: x.v, unit: 'bpm' }));

/**
 * Collects a Samsung Health export file by file (in any order: the zip may
 * hold the JSON files before the CSV) and hands out workouts as soon as each
 * one has its heart-rate and GPS files. All-day heart rate is handed out per file.
 */
export class SamsungCollector {
  private readonly workouts = new Map<string, Workout>();

  private readonly done = new Set<string>();

  /** Workouts handed out so far */
  public activities = 0;

  /**
   * Takes one file of the export.
   * @param path - Path in the export
   * @param text - Content
   * @returns Data ready to import now (may be empty)
   */
  add(path: string, text: string): ParsedData {
    const out = SamsungCollector.empty();
    const kind = samsungKind(path);
    if (kind === 'heartRate') {
      out.health = heartRates(
        parseSamsungCsv(text).map((r) => ({ t: samsungTime(r.start_time), v: num(r.heart_rate) })),
      );
      return out;
    }
    if (kind === 'binning') {
      out.health = heartRates(
        jsonArray(text).map((b) => ({ t: samsungTime(b.start_time), v: num(b.heart_rate) })),
      );
      return out;
    }
    if (kind === 'exercise') {
      parseSamsungCsv(text).forEach((r) => {
        if (r.datauuid) {
          this.workout(r.datauuid).row = r;
        }
      });
    } else if (kind === 'live') {
      this.workout(uuidOf(path)).live = jsonArray(text)
        .map((s) => ({
          t: Number(s.start_time),
          hr: num(s.heart_rate),
          speed: num(s.speed),
          cad: num(s.cadence),
        }))
        .filter((s) => Number.isFinite(s.t) && s.t > 0)
        .sort((a, b) => a.t - b.t);
    } else if (kind === 'location') {
      this.workout(uuidOf(path)).location = jsonArray(text)
        .map((s) => ({
          t: Number(s.start_time),
          lat: Number(s.latitude),
          lon: Number(s.longitude),
          ele: num(s.altitude),
        }))
        .filter(
          (s) =>
            Number.isFinite(s.t) &&
            s.t > 0 &&
            Number.isFinite(s.lat) &&
            Number.isFinite(s.lon) &&
            (s.lat !== 0 || s.lon !== 0),
        )
        .sort((a, b) => a.t - b.t);
    }
    this.workouts.forEach((w, id) => {
      if (w.row && !this.done.has(id) && SamsungCollector.complete(w)) {
        this.emit(id, w, out);
      }
    });
    return out;
  }

  /**
   * What is left at the end: workouts whose heart-rate or GPS file never came.
   * @returns Data to import
   */
  finish(): ParsedData {
    const out = SamsungCollector.empty();
    this.workouts.forEach((w, id) => {
      if (w.row && !this.done.has(id)) {
        this.emit(id, w, out);
      }
    });
    return out;
  }

  private static empty(): ParsedData {
    return { source: SAMSUNG_SOURCE, activities: [], places: [], visits: [], health: [] };
  }

  /** The row names the files it has (live_data / location_data columns) */
  private static complete(w: Workout): boolean {
    const wantsLive = !!w.row?.live_data;
    const wantsLocation = !!w.row?.location_data;
    return (!wantsLive || !!w.live) && (!wantsLocation || !!w.location);
  }

  private workout(id: string): Workout {
    let w = this.workouts.get(id);
    if (!w) {
      w = {};
      this.workouts.set(id, w);
    }
    return w;
  }

  private emit(id: string, w: Workout, out: ParsedData): void {
    this.done.add(id);
    const activity = toActivity(id, w);
    if (activity) {
      out.activities.push(activity);
      this.activities++;
      // Indoors (no GPS): the heart rate goes in as health data, so it is not lost
      if (!activity.points?.length && w.live?.length) {
        out.health.push(
          ...heartRates(w.live.map((s) => ({ t: new Date(s.t).toISOString(), v: s.hr }))),
        );
      }
    }
    // Free the memory of a handed-out workout
    this.workouts.set(id, { row: w.row });
  }
}
