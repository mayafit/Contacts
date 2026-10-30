/**
 * @fileoverview Pure helpers of the activity analysis: pace and time texts,
 * track samples with distance, smoothing, cursor lookups by time or distance,
 * chart ranges and heart-rate zone shares
 * @module Contacts/atlas/analysis
 */

import { DEFAULT_UI, onUiChange } from '../settings/uiSettings';
import type { PointOutput } from './types';
import { ACTIVITY_TYPES, isSet } from './types';

/** Colours of heart-rate zones 1..5, cool to hot */
export const ZONE_COLORS: string[] = [...DEFAULT_UI.charts.zones];
// The Owner's zone colours (AD-41), in place
onUiChange((ui) => {
  ui.charts.zones.forEach((c, i) => {
    ZONE_COLORS[i] = c;
  });
});

/** What the activity charts are plotted against */
export type ChartAxis = 'time' | 'distance';

/** One point of the activity track, relative to the start */
export interface TrackSample {
  /** Seconds from the activity start */
  t: number;
  /** Meters from the start along the track */
  d: number;
  lat: number;
  lon: number;
  ele: number | null;
  hr: number | null;
  /** km/h (recorded, or derived from the distance) */
  speedKmh: number | null;
  cad: number | null;
}

/** One value of a chart series; v null leaves a gap */
export interface ChartPoint {
  /** Seconds from the activity start */
  t: number;
  /** Meters from the start */
  d: number;
  v: number | null;
}

/**
 * Seconds as a clock.
 * @param s - Seconds
 * @returns e.g. "4:05", "1:02:03"; '' when not set
 */
export const formatClock = (s: number | null | undefined): string => {
  if (!isSet(s) || !Number.isFinite(s)) {
    return '';
  }
  const total = Math.max(0, Math.round(s));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const sec = String(total % 60).padStart(2, '0');
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${sec}` : `${m}:${sec}`;
};

/**
 * Seconds per km of a speed.
 * @param kmh - Speed
 * @returns Seconds per km; null for no or zero speed
 */
export const paceOf = (kmh: number | null | undefined): number | null =>
  isSet(kmh) && kmh > 0 ? 3600 / kmh : null;

/**
 * A pace as minutes per km.
 * @param sPerKm - Seconds per km
 * @param unit - Append " /km" (default true)
 * @returns e.g. "5:12 /km"; '' when not set
 */
export const formatPace = (sPerKm: number | null | undefined, unit = true): string => {
  if (!isSet(sPerKm) || !Number.isFinite(sPerKm) || sPerKm <= 0) {
    return '';
  }
  const total = Math.round(sPerKm);
  const text = `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`;
  return unit ? `${text} /km` : text;
};

/**
 * A speed in km/h.
 * @param kmh - Speed
 * @returns e.g. "24.3 km/h"; '' when not set
 */
export const formatSpeed = (kmh: number | null | undefined): string =>
  isSet(kmh) && Number.isFinite(kmh) ? `${kmh.toFixed(1)} km/h` : '';

/**
 * A speed as the activity shows it: pace for run/walk/hike, km/h otherwise.
 * @param kmh - Speed
 * @param pace - Show pace
 * @returns e.g. "5:12 /km" or "24.3 km/h"; '' when not set
 */
export const formatSpeedOrPace = (kmh: number | null | undefined, pace: boolean): string =>
  pace ? formatPace(paceOf(kmh)) : formatSpeed(kmh);

/**
 * A signed percentage change.
 * @param pct - Change in percent
 * @returns e.g. "+4%", "−2%", "±0%"; '' when not set
 */
export const formatSignedPct = (pct: number | null | undefined): string => {
  if (!isSet(pct) || !Number.isFinite(pct)) {
    return '';
  }
  const r = Math.round(pct);
  if (r === 0) {
    return '±0%';
  }
  return r > 0 ? `+${r}%` : `−${Math.abs(r)}%`;
};

/**
 * Whole-number shares of the zones that add up to 100 (largest remainder).
 * @param seconds - Time per zone
 * @returns Percent per zone; all 0 when there is no time
 */
export const zoneShares = (seconds: number[]): number[] => {
  const clean = seconds.map((s) => (Number.isFinite(s) && s > 0 ? s : 0));
  const total = clean.reduce((a, b) => a + b, 0);
  if (total <= 0) {
    return clean.map(() => 0);
  }
  const exact = clean.map((s) => (s / total) * 100);
  const out = exact.map(Math.floor);
  let left = 100 - out.reduce((a, b) => a + b, 0);
  const order = exact
    .map((e, i) => ({ i, rest: e - Math.floor(e) }))
    .sort((a, b) => b.rest - a.rest);
  for (let k = 0; left > 0 && k < order.length; k++, left--) {
    out[order[k].i] += 1;
  }
  return out;
};

/** Distance between two positions in meters (haversine) */
const metersBetween = (a: { lat: number; lon: number }, b: { lat: number; lon: number }) => {
  const rad = Math.PI / 180;
  const h =
    Math.sin(((b.lat - a.lat) * rad) / 2) ** 2 +
    Math.cos(a.lat * rad) * Math.cos(b.lat * rad) * Math.sin(((b.lon - a.lon) * rad) / 2) ** 2;
  return 2 * 6_371_000 * Math.asin(Math.sqrt(h));
};

/**
 * The track relative to the start: distance from the server's `d` (or summed
 * up when missing), speed recorded or derived from the distance.
 * @param points - Track points, oldest first
 * @param startMs - Activity start (ms since the epoch)
 * @returns Samples, oldest first
 */
export const trackSamples = (points: PointOutput[], startMs: number): TrackSample[] => {
  const haveD = points.length > 0 && points.every((p) => isSet(p.d) && Number.isFinite(p.d));
  let sum = 0;
  const out: TrackSample[] = points.map((p, i) => {
    if (!haveD && i > 0) {
      sum += metersBetween(points[i - 1], p);
    }
    return {
      t: (Date.parse(p.t) - startMs) / 1000,
      d: haveD ? (p.d as number) : sum,
      lat: p.lat,
      lon: p.lon,
      ele: isSet(p.ele) ? p.ele : null,
      hr: isSet(p.hr) ? p.hr : null,
      speedKmh: isSet(p.speed) ? p.speed * 3.6 : null,
      cad: isSet(p.cad) ? p.cad : null,
    };
  });
  // Derive missing speeds from the distance over the neighbouring points
  out.forEach((s, i) => {
    if (isSet(s.speedKmh)) {
      return;
    }
    const a = out[Math.max(0, i - 1)];
    const b = out[Math.min(out.length - 1, i + 1)];
    const dt = b.t - a.t;
    s.speedKmh = dt > 0 ? ((b.d - a.d) / dt) * 3.6 : null;
  });
  return out;
};

/**
 * A centred moving average over a time window; gaps (null) stay gaps and are
 * left out of their neighbours' averages.
 * @param series - Points, by time
 * @param windowS - Width of the window in seconds (e.g. 15)
 * @returns The smoothed series (same length)
 */
export const smoothSeries = (series: ChartPoint[], windowS: number): ChartPoint[] => {
  const half = windowS / 2;
  let lo = 0;
  let hi = 0;
  let sum = 0;
  let count = 0;
  return series.map((p) => {
    while (hi < series.length && series[hi].t <= p.t + half) {
      const v = series[hi].v;
      if (isSet(v)) {
        sum += v;
        count += 1;
      }
      hi += 1;
    }
    while (lo < hi && series[lo].t < p.t - half) {
      const v = series[lo].v;
      if (isSet(v)) {
        sum -= v;
        count -= 1;
      }
      lo += 1;
    }
    return { ...p, v: isSet(p.v) && count > 0 ? sum / count : null };
  });
};

/**
 * Index of the point nearest to x on an ascending key (binary search).
 * @param series - Points, ascending by key
 * @param key - 't' (seconds) or 'd' (meters)
 * @param x - The wanted value
 * @returns Index; -1 for an empty series
 */
export const indexAt = <T extends { t: number; d: number }>(
  series: T[],
  key: 't' | 'd',
  x: number,
): number => {
  if (series.length === 0) {
    return -1;
  }
  let lo = 0;
  let hi = series.length - 1;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (series[mid][key] < x) {
      lo = mid + 1;
    } else {
      hi = mid;
    }
  }
  if (lo > 0 && Math.abs(series[lo - 1][key] - x) <= Math.abs(series[lo][key] - x)) {
    return lo - 1;
  }
  return lo;
};

/**
 * The value of a series at a time (its nearest point).
 * @param series - Points, by time
 * @param t - Seconds from the start
 * @returns The value; null for an empty series or a gap
 */
export const valueAt = (series: ChartPoint[], t: number): number | null => {
  const i = indexAt(series, 't', t);
  return i < 0 ? null : series[i].v;
};

/**
 * The distance covered at a time (linear between the samples, clamped).
 * @param samples - Track, by time
 * @param t - Seconds from the start
 * @returns Meters; 0 for an empty track
 */
export const distanceAtTime = (samples: { t: number; d: number }[], t: number): number => {
  if (samples.length === 0) {
    return 0;
  }
  if (t <= samples[0].t) {
    return samples[0].d;
  }
  const last = samples[samples.length - 1];
  if (t >= last.t) {
    return last.d;
  }
  let lo = 0;
  let hi = samples.length - 1;
  while (hi - lo > 1) {
    const mid = (lo + hi) >> 1;
    if (samples[mid].t <= t) {
      lo = mid;
    } else {
      hi = mid;
    }
  }
  const a = samples[lo];
  const b = samples[hi];
  const f = b.t > a.t ? (t - a.t) / (b.t - a.t) : 0;
  return a.d + (b.d - a.d) * f;
};

/**
 * The time at which a distance is first reached (the nearest sample).
 * @param samples - Track, by time (distance never decreases)
 * @param d - Meters from the start
 * @returns Seconds from the start; 0 for an empty track
 */
export const timeAtDistance = (samples: { t: number; d: number }[], d: number): number => {
  const i = indexAt(samples, 'd', d);
  return i < 0 ? 0 : samples[i].t;
};

/**
 * The value range of a chart, robust against spikes.
 * @param values - Values (null and non-finite ignored)
 * @param lowQ - Lower quantile (0 = the minimum)
 * @param highQ - Upper quantile (1 = the maximum)
 * @returns [min, max]; null when there are no values
 */
export const valueRange = (
  values: (number | null)[],
  lowQ = 0,
  highQ = 1,
): [number, number] | null => {
  const sorted = values
    .filter((v): v is number => isSet(v) && Number.isFinite(v))
    .sort((a, b) => a - b);
  if (sorted.length === 0) {
    return null;
  }
  const at = (q: number) =>
    sorted[Math.min(sorted.length - 1, Math.max(0, Math.round(q * (sorted.length - 1))))];
  return [at(lowQ), at(highQ)];
};

/**
 * Below this speed an activity counts as stopped (as the server's analysis).
 * @param pace - Run, walk or hike
 * @returns km/h
 */
export const movingThresholdKmh = (pace: boolean): number => (pace ? 1 : 3);

/**
 * The speed chart's series: smoothed to ~15 s; as pace (seconds per km) with
 * gaps where stopped, or as km/h.
 * @param samples - Track
 * @param pace - Show pace
 * @returns Series
 */
export const speedSeries = (samples: TrackSample[], pace: boolean): ChartPoint[] => {
  const smooth = smoothSeries(
    samples.map((s) => ({ t: s.t, d: s.d, v: s.speedKmh })),
    15,
  );
  if (!pace) {
    return smooth;
  }
  const min = movingThresholdKmh(true);
  return smooth.map((p) => ({ ...p, v: isSet(p.v) && p.v >= min ? 3600 / p.v : null }));
};

/**
 * The y range of the speed chart: moving stretches only, without the extremes.
 * @param series - speedSeries
 * @param pace - Values are pace
 * @returns [min, max]; null when never moving
 */
export const speedRange = (series: ChartPoint[], pace: boolean): [number, number] | null => {
  const min = movingThresholdKmh(pace);
  const values = series.map((p) => p.v).filter((v) => isSet(v) && (pace || v >= min));
  return valueRange(values, 0.02, 0.98);
};

/** How a chart maps values to its coordinate space */
export interface ChartFrame {
  /** Width of the coordinate space */
  width: number;
  /** Height of the coordinate space */
  height: number;
  /** Room above and below the line */
  pad: number;
  /** End of the x axis (seconds or meters; the axis starts at 0) */
  maxX: number;
  /** [min, max] of the values; values outside are clamped */
  range: [number, number];
  /** Smaller values at the top (pace: faster is up) */
  invert: boolean;
}

/**
 * The x coordinate of a position on the axis.
 * @param x - Seconds or meters
 * @param f - Chart frame
 * @returns x in the coordinate space
 */
export const chartX = (x: number, f: ChartFrame): number => (x / Math.max(1e-9, f.maxX)) * f.width;

/**
 * The y coordinate of a value.
 * @param v - Value
 * @param f - Chart frame
 * @returns y in the coordinate space (0 at the top)
 */
export const chartY = (v: number, f: ChartFrame): number => {
  const [min, max] = f.range;
  const share = Math.min(1, Math.max(0, (v - min) / Math.max(1e-9, max - min)));
  return f.pad + (f.invert ? share : 1 - share) * (f.height - 2 * f.pad);
};

/**
 * An SVG path of a series; gaps (null) start a new piece.
 * @param series - Points, by time
 * @param key - The axis: 't' or 'd'
 * @param f - Chart frame
 * @returns Path data ('' when empty)
 */
export const chartPath = (series: ChartPoint[], key: 't' | 'd', f: ChartFrame): string => {
  let d = '';
  let open = false;
  series.forEach((p) => {
    if (!isSet(p.v)) {
      open = false;
      return;
    }
    d += `${open ? 'L' : 'M'}${chartX(p[key], f).toFixed(1)},${chartY(p.v, f).toFixed(1)}`;
    open = true;
  });
  return d;
};

/**
 * Widens a range to at least a span, around its middle (a flat line stays flat).
 * @param range - [min, max]
 * @param span - Smallest span
 * @returns The range, widened when needed
 */
export const ensureSpan = (range: [number, number], span: number): [number, number] => {
  const [min, max] = range;
  if (max - min >= span) {
    return range;
  }
  const mid = (min + max) / 2;
  return [mid - span / 2, mid + span / 2];
};

/**
 * Unit of an activity type's cadence.
 * @param type - Activity type
 * @returns "rpm" for rides, "spm" (steps per minute) otherwise
 */
export const cadenceUnit = (type: string): string => (type === 'ride' ? 'rpm' : 'spm');

/** Plural nouns of the activity types, for "your rides" */
const PLURALS: Record<string, string> = {
  ride: 'rides',
  run: 'runs',
  hike: 'hikes',
  walk: 'walks',
  swim: 'swims',
  ski: 'ski days',
  drive: 'drives',
  flight: 'flights',
  transit: 'trips',
};

/**
 * The plural noun of an activity type.
 * @param type - Activity type
 * @returns e.g. "rides"; "activities" for unknown types
 */
export const typePlural = (type: string): string =>
  PLURALS[type] ?? (ACTIVITY_TYPES[type] && type !== 'other' ? `${type}s` : 'activities');

/**
 * Name of a best-effort distance.
 * @param m - Meters
 * @returns e.g. "1 km", "Half marathon", "400 m"
 */
export const effortLabel = (m: number): string => {
  if (Math.abs(m - 21_097) < 50) {
    return 'Half marathon';
  }
  if (Math.abs(m - 42_195) < 50) {
    return 'Marathon';
  }
  return m >= 1000 ? `${Number((m / 1000).toFixed(1))} km` : `${Math.round(m)} m`;
};

/**
 * Where a split ends, as its row label.
 * @param endM - Meters from the start at the split's end
 * @param splitM - The split length
 * @returns e.g. "3" (km), "42.2" (km), "300" (m)
 */
export const splitLabel = (endM: number, splitM: number): string =>
  splitM >= 1000 ? String(Number((endM / 1000).toFixed(1))) : String(Math.round(endM));

/** How fast each split was, for the inline bars */
export interface SplitScale {
  /** 0.3..1 per split (longer = faster) */
  widths: number[];
  /** Index of the fastest full-length split; -1 when none */
  fastest: number;
}

/**
 * The inline bars of the splits table; a short last split cannot be the fastest.
 * @param splits - Speeds and lengths
 * @param splitM - The split length
 * @returns Bar widths and the fastest split
 */
export const splitScale = (
  splits: { speedKmh: number; distanceM: number }[],
  splitM: number,
): SplitScale => {
  const speeds = splits.map((s) => s.speedKmh).filter((v) => Number.isFinite(v) && v > 0);
  if (speeds.length === 0) {
    return { widths: splits.map(() => 0.3), fastest: -1 };
  }
  const min = Math.min(...speeds);
  const max = Math.max(...speeds);
  let fastest = -1;
  splits.forEach((s, i) => {
    if (s.distanceM >= splitM * 0.5 && (fastest < 0 || s.speedKmh > splits[fastest].speedKmh)) {
      fastest = i;
    }
  });
  return {
    widths: splits.map((s) =>
      max > min ? 0.3 + (0.7 * (Math.max(min, s.speedKmh) - min)) / (max - min) : 1,
    ),
    fastest,
  };
};
