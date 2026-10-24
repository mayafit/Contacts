/**
 * @fileoverview Texts and chart data of the Atlas insights: changes against the
 * previous period, the period's caption, series buckets and their labels,
 * the training load summary and sparklines (en-GB, local time, the same on
 * every machine)
 * @module Contacts/atlas/insights
 */

import type { InsightsBucket, InsightsSeriesPoint, WeekLoad } from './types';
import { ACTIVITY_TYPES, isSet } from './types';
import type { AreaBox } from './filters';

const DAY_MS = 86_400_000;
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** Monday first, as Insights.byWeekday */
export const WEEKDAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];

/** What the trend chart shows */
export type TrendMetric = 'distance' | 'time' | 'count';

/** A change against the previous period */
export interface Change {
  /** e.g. "+23%", "−12%", "±0%", "new" */
  text: string;
  /** More than before */
  up: boolean;
}

/**
 * The change of a total against the previous period.
 * @param now - This period's value
 * @param previous - The previous period's value (null or undefined when unknown)
 * @returns The change, or null when there is nothing to compare
 */
export const formatChange = (now: number, previous: number | null | undefined): Change | null => {
  if (!isSet(previous) || (previous === 0 && now === 0)) {
    return null;
  }
  if (previous === 0) {
    return { text: 'new', up: true };
  }
  const pct = Math.round(((now - previous) / previous) * 100);
  if (pct === 0) {
    return { text: '±0%', up: false };
  }
  return pct > 0 ? { text: `+${pct}%`, up: true } : { text: `−${Math.abs(pct)}%`, up: false };
};

/** "3 Sep 2026" (local time) */
const day = (d: Date): string => `${d.getDate()} ${MONTHS[d.getMonth()]} ${d.getFullYear()}`;

/**
 * The period of the insights as a caption. An end at exactly midnight is the day
 * before (the range ends there).
 * @param from - Start, ISO (null when open)
 * @param to - End, ISO (null when open)
 * @returns e.g. "24 Jul – 24 Oct 2026", "24 Jul 2025 – 24 Oct 2026", "Since 3 Sep 2026", "All time"
 */
export const formatPeriod = (from: string | null, to: string | null): string => {
  const a = from ? new Date(from) : null;
  let b = to ? new Date(to) : null;
  if (b && b.getHours() === 0 && b.getMinutes() === 0 && b.getSeconds() === 0) {
    const before = new Date(b.getTime() - 1);
    if (!a || before.getTime() >= a.getTime()) {
      b = before;
    }
  }
  if (a && b) {
    if (day(a) === day(b)) {
      return day(a);
    }
    if (a.getFullYear() === b.getFullYear()) {
      return `${a.getDate()} ${MONTHS[a.getMonth()]} – ${day(b)}`;
    }
    return `${day(a)} – ${day(b)}`;
  }
  if (a) {
    return `Since ${day(a)}`;
  }
  if (b) {
    return `Until ${day(b)}`;
  }
  return 'All time';
};

/**
 * The question "Explain with AI" asks.
 * @param period - The period's caption (formatPeriod)
 * @returns The question
 */
export const explainPrompt = (period: string): string =>
  `Explain my Atlas insights for ${period === 'All time' ? 'all time' : period}: what stands out, what improved or declined, and what you propose.`;

/**
 * Kilometres, rounded to suit their size.
 * @param km - Distance
 * @returns e.g. "1,234 km", "87.4 km", "0 km"
 */
export const formatKm = (km: number): string =>
  km >= 100
    ? `${Math.round(km).toLocaleString('en-GB')} km`
    : `${String(Math.round(km * 10) / 10)} km`;

/**
 * Hours, rounded to suit their size.
 * @param hours - Time
 * @returns e.g. "124 h", "5.5 h", "45 min"
 */
export const formatHours = (hours: number): string => {
  if (hours >= 10) {
    return `${Math.round(hours).toLocaleString('en-GB')} h`;
  }
  if (hours >= 1) {
    return `${String(Math.round(hours * 10) / 10)} h`;
  }
  return `${Math.round(hours * 60)} min`;
};

/** Local calendar day of a moment, the key of a bucket */
const dayKey = (d: Date): string => `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`;

/**
 * The next bucket's start (local calendar).
 * @param d - A bucket's start
 * @param bucket - Bucket size
 * @returns The next bucket's start
 */
const nextBucket = (d: Date, bucket: InsightsBucket): Date => {
  const n = new Date(d.getTime());
  if (bucket === 'month') {
    n.setMonth(n.getMonth() + 1);
  } else {
    n.setDate(n.getDate() + (bucket === 'week' ? 7 : 1));
  }
  return n;
};

/** One type's part of a bucket */
export interface TrendSegment {
  type: string;
  value: number;
}

/** One bar of the trend chart */
export interface TrendBar {
  /** Start of the bucket (ms since the epoch) */
  t: number;
  /** Types in ACTIVITY_TYPES order; empty for a bucket without activities */
  segments: TrendSegment[];
  total: number;
}

/** The value a point contributes to a metric */
const valueOf = (p: InsightsSeriesPoint, metric: TrendMetric): number =>
  metric === 'distance' ? p.distanceKm : metric === 'time' ? p.hours : p.count;

/** Order of the types in a stack: as ACTIVITY_TYPES, unknown types last */
const typeOrder = (type: string): number => {
  const i = Object.keys(ACTIVITY_TYPES).indexOf(type);
  return i < 0 ? Number.MAX_SAFE_INTEGER : i;
};

/**
 * The series as stacked bars, one per bucket from the first to the last, empty
 * buckets in between included (at most 400).
 * @param points - Series points (one per bucket and type)
 * @param bucket - Bucket size
 * @param metric - What the bars measure
 * @returns Bars, oldest first
 */
export const trendBars = (
  points: InsightsSeriesPoint[],
  bucket: InsightsBucket,
  metric: TrendMetric,
): TrendBar[] => {
  if (points.length === 0) {
    return [];
  }
  const byKey = new Map<string, TrendSegment[]>();
  points.forEach((p) => {
    const key = dayKey(new Date(p.t));
    const list = byKey.get(key) ?? [];
    const v = valueOf(p, metric);
    const same = list.find((s) => s.type === p.type);
    if (same) {
      same.value += v;
    } else {
      list.push({ type: p.type, value: v });
    }
    byKey.set(key, list);
  });
  const times = points.map((p) => Date.parse(p.t));
  const last = Math.max(...times);
  const out: TrendBar[] = [];
  for (
    let d = new Date(Math.min(...times));
    d.getTime() <= last + DAY_MS / 2 && out.length < 400;
    d = nextBucket(d, bucket)
  ) {
    const segments = (byKey.get(dayKey(d)) ?? [])
      .filter((s) => s.value > 0)
      .sort((x, y) => typeOrder(x.type) - typeOrder(y.type));
    out.push({ t: d.getTime(), segments, total: segments.reduce((s, x) => s + x.value, 0) });
  }
  return out;
};

/** A bucket's label on the trend chart */
export interface BucketLabel {
  /** e.g. "Mar" (months) or "3 Sep" (weeks, days) */
  label: string;
  /** The year, on the first bucket and where a new year starts; null elsewhere */
  year: string | null;
}

/**
 * Labels of the trend chart's buckets, with the year where it is needed.
 * @param times - Bucket starts (ms since the epoch), oldest first
 * @param bucket - Bucket size
 * @returns One label per bucket
 */
export const bucketLabels = (times: number[], bucket: InsightsBucket): BucketLabel[] =>
  times.map((t, i) => {
    const d = new Date(t);
    const prev = i > 0 ? new Date(times[i - 1]) : null;
    const newYear = !prev || prev.getFullYear() !== d.getFullYear();
    return {
      label: bucket === 'month' ? MONTHS[d.getMonth()] : `${d.getDate()} ${MONTHS[d.getMonth()]}`,
      year: newYear ? String(d.getFullYear()) : null,
    };
  });

/**
 * Which labels fit: every n-th, so that at most max are shown.
 * @param count - Number of buckets
 * @param max - Most labels that fit
 * @returns n (1 shows every label)
 */
export const labelStep = (count: number, max: number): number =>
  Math.max(1, Math.ceil(count / Math.max(1, max)));

/**
 * A square area around a point, for the area filter.
 * @param lat - Latitude of the centre
 * @param lon - Longitude of the centre
 * @param sizeM - Side of the square in metres (600 by default)
 * @returns The area
 */
export const areaAround = (lat: number, lon: number, sizeM = 600): AreaBox => {
  const dLat = sizeM / 2 / 110_574;
  const dLon = sizeM / 2 / (111_320 * Math.max(0.01, Math.cos((lat * Math.PI) / 180)));
  return { minLat: lat - dLat, minLon: lon - dLon, maxLat: lat + dLat, maxLon: lon + dLon };
};

/**
 * A share as a whole percentage.
 * @param part - Part
 * @param whole - Whole
 * @returns e.g. "42%"; '' when the whole is 0
 */
export const formatShare = (part: number, whole: number): string =>
  whole > 0 ? `${Math.round((part / whole) * 100)}%` : '';

/** The latest week of the training load against the weeks before */
export interface LoadSummary {
  latest: WeekLoad;
  /** Average load of up to 4 weeks before the latest; null when there are none */
  avgBefore: number | null;
}

/**
 * The latest week's load and the average of the (up to) 4 weeks before it.
 * @param weekly - Weeks, oldest first
 * @returns The summary; null without weeks
 */
export const loadSummary = (weekly: WeekLoad[]): LoadSummary | null => {
  if (weekly.length === 0) {
    return null;
  }
  const before = weekly.slice(Math.max(0, weekly.length - 5), weekly.length - 1);
  return {
    latest: weekly[weekly.length - 1],
    avgBefore: before.length ? before.reduce((s, w) => s + w.load, 0) / before.length : null,
  };
};

/**
 * A sparkline: the values over time, scaled to fill the box; gaps (null) break the line.
 * @param points - Times (ms) and values, oldest first
 * @param width - Width of the box
 * @param height - Height of the box
 * @param pad - Room above and below
 * @returns SVG path data ('' with fewer than 2 values)
 */
export const sparkPath = (
  points: { t: number; v: number | null }[],
  width: number,
  height: number,
  pad = 3,
): string => {
  const set = points.filter((p): p is { t: number; v: number } => isSet(p.v));
  if (set.length < 2) {
    return '';
  }
  const t0 = Math.min(...set.map((p) => p.t));
  const t1 = Math.max(...set.map((p) => p.t));
  const min = Math.min(...set.map((p) => p.v));
  const max = Math.max(...set.map((p) => p.v));
  const x = (t: number) => (t1 > t0 ? ((t - t0) / (t1 - t0)) * width : width / 2);
  const y = (v: number) =>
    max > min ? pad + (1 - (v - min) / (max - min)) * (height - 2 * pad) : height / 2;
  let d = '';
  let open = false;
  points.forEach((p) => {
    if (!isSet(p.v)) {
      open = false;
      return;
    }
    d += `${open ? 'L' : 'M'}${x(p.t).toFixed(1)},${y(p.v).toFixed(1)}`;
    open = true;
  });
  return d;
};
