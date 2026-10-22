/**
 * @fileoverview Google Timeline (on-device export and older Takeout), Google Fit
 * and Fitbit heart-rate exports
 * @module Contacts/atlas/importers/google
 */

/* eslint-disable @typescript-eslint/no-explicit-any -- the export formats are loosely typed JSON */

import { isSet } from '../types';

import type {
  ActivityInput,
  HealthInput,
  ParsedData,
  PlaceInput,
  PointInput,
  VisitInput,
} from '../types';

/** "32.0853°, 34.7818°", "geo:32.0853,34.7818" or E7 integers → coordinates */
export const parseLatLng = (value: unknown): { lat: number; lon: number } | null => {
  if (typeof value === 'string') {
    const m = /(-?\d+(?:\.\d+)?)°?\s*,\s*(-?\d+(?:\.\d+)?)/.exec(value.replace(/^geo:/, ''));
    return m ? { lat: Number(m[1]), lon: Number(m[2]) } : null;
  }
  if (value && typeof value === 'object') {
    const o = value as Record<string, unknown>;
    if (typeof o.latitudeE7 === 'number' && typeof o.longitudeE7 === 'number') {
      return { lat: o.latitudeE7 / 1e7, lon: o.longitudeE7 / 1e7 };
    }
    if (typeof o.latE7 === 'number' && typeof o.lngE7 === 'number') {
      return { lat: o.latE7 / 1e7, lon: o.lngE7 / 1e7 };
    }
    if (o.latLng) {
      return parseLatLng(o.latLng);
    }
  }
  return null;
};

const iso = (v: unknown): string | null => {
  if (typeof v !== 'string' && typeof v !== 'number') {
    return null;
  }
  // Older Takeout writes epoch milliseconds as digit strings ("startTimestampMs")
  const d = new Date(typeof v === 'string' && /^\d+$/.test(v) ? Number(v) : v);
  return Number.isNaN(d.getTime()) ? null : d.toISOString();
};

const SEMANTIC_NAMES: Record<string, string> = {
  HOME: 'Home',
  INFERRED_HOME: 'Home',
  WORK: 'Work',
  INFERRED_WORK: 'Work',
  SEARCHED_ADDRESS: 'Searched place',
  ALIASED_LOCATION: 'Saved place',
};

const empty = (source: string): ParsedData => ({
  source,
  activities: [],
  places: [],
  visits: [],
  health: [],
});

/** The points of a time-sorted path within [from, to] (binary search) */
const slice = (path: PointInput[], from: string, to: string): PointInput[] => {
  let lo = 0;
  let hi = path.length;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (path[mid].t < from) {
      lo = mid + 1;
    } else {
      hi = mid;
    }
  }
  let end = lo;
  while (end < path.length && path[end].t <= to) {
    end++;
  }
  return path.slice(lo, end);
};

/**
 * Google Timeline exported from the phone (2024+): `semanticSegments` (also
 * the iPhone variant: a top-level array with "geo:" strings).
 * @param json - Parsed JSON
 * @returns Parsed data
 */
export const parseTimelineDevice = (json: unknown): ParsedData => {
  const out = empty('google-timeline');
  const segments =
    (Array.isArray(json) ? json : (json as { semanticSegments?: unknown[] })?.semanticSegments) ??
    [];
  // Paths are separate segments covering time windows; collect all their points
  const path: PointInput[] = [];
  segments.forEach((seg: any) => {
    const start = Date.parse(seg?.startTime);
    (seg?.timelinePath ?? []).forEach((p: any) => {
      const ll = parseLatLng(p.point);
      const t =
        iso(p.time) ??
        (Number.isFinite(start) && isSet(p.durationMinutesOffsetFromStartTime)
          ? new Date(start + Number(p.durationMinutesOffsetFromStartTime) * 60_000).toISOString()
          : null);
      if (ll && t) {
        path.push({ t, lat: ll.lat, lon: ll.lon });
      }
    });
  });
  path.sort((a, b) => a.t.localeCompare(b.t));

  const places = new Map<string, PlaceInput>();
  segments.forEach((seg: any) => {
    const startTime = iso(seg?.startTime);
    const endTime = iso(seg?.endTime);
    if (!startTime || !endTime) {
      return;
    }
    if (seg.visit) {
      const top = seg.visit.topCandidate ?? {};
      const ll = parseLatLng(top.placeLocation ?? top.placeLocation?.latLng);
      if (!ll) {
        return;
      }
      const placeId: string | undefined = top.placeId ?? top.placeID;
      const key = placeId ?? `${ll.lat.toFixed(4)},${ll.lon.toFixed(4)}`;
      if (!places.has(key)) {
        places.set(key, {
          externalId: key,
          name: SEMANTIC_NAMES[top.semanticType] ?? 'Place',
          lat: ll.lat,
          lon: ll.lon,
          category:
            top.semanticType && top.semanticType !== 'UNKNOWN'
              ? String(top.semanticType).toLowerCase()
              : null,
          googlePlaceId: placeId ?? null,
        });
      }
      out.visits.push({
        externalId: `${startTime}|${key}`,
        startTime,
        endTime,
        lat: ll.lat,
        lon: ll.lon,
        placeExternalId: key,
        confidence: isSet(top.probability) ? Number(top.probability) : null,
      });
    } else if (seg.activity) {
      const a = seg.activity;
      const s = parseLatLng(a.start);
      const e = parseLatLng(a.end);
      const points = slice(path, startTime, endTime);
      const all = [
        ...(s ? [{ t: startTime, ...s }] : []),
        ...points,
        ...(e ? [{ t: endTime, ...e }] : []),
      ];
      if (all.length < 2) {
        return;
      }
      out.activities.push({
        externalId: `${startTime}|${a.topCandidate?.type ?? 'unknown'}`,
        type: String(a.topCandidate?.type ?? 'other'),
        name: null,
        startTime,
        endTime,
        distanceM: isSet(a.distanceMeters) ? Number(a.distanceMeters) : null,
        points: all,
      });
    }
  });
  out.places = [...places.values()];
  return out;
};

/**
 * Google Timeline from older Takeout: `Semantic Location History/YYYY/*.json`
 * with `timelineObjects` (placeVisit / activitySegment).
 * @param json - Parsed JSON
 * @returns Parsed data
 */
export const parseTimelineTakeout = (json: unknown): ParsedData => {
  const out = empty('google-timeline');
  const places = new Map<string, PlaceInput>();
  ((json as { timelineObjects?: any[] })?.timelineObjects ?? []).forEach((obj) => {
    if (obj.placeVisit) {
      const v = obj.placeVisit;
      const ll = parseLatLng(v.location);
      const startTime = iso(v.duration?.startTimestamp ?? v.duration?.startTimestampMs);
      const endTime = iso(v.duration?.endTimestamp ?? v.duration?.endTimestampMs);
      if (!ll || !startTime || !endTime) {
        return;
      }
      const key = v.location?.placeId ?? `${ll.lat.toFixed(4)},${ll.lon.toFixed(4)}`;
      if (!places.has(key)) {
        places.set(key, {
          externalId: key,
          name:
            v.location?.name ??
            SEMANTIC_NAMES[v.location?.semanticType] ??
            v.location?.address?.split('\n')[0] ??
            'Place',
          lat: ll.lat,
          lon: ll.lon,
          address: v.location?.address ?? null,
          googlePlaceId: v.location?.placeId ?? null,
        });
      }
      out.visits.push({
        externalId: `${startTime}|${key}`,
        startTime,
        endTime,
        lat: ll.lat,
        lon: ll.lon,
        placeExternalId: key,
        confidence: isSet(v.visitConfidence) ? Number(v.visitConfidence) / 100 : null,
      });
    } else if (obj.activitySegment) {
      const a = obj.activitySegment;
      const startTime = iso(a.duration?.startTimestamp ?? a.duration?.startTimestampMs);
      const endTime = iso(a.duration?.endTimestamp ?? a.duration?.endTimestampMs);
      if (!startTime || !endTime) {
        return;
      }
      const raw = (a.simplifiedRawPath?.points ?? [])
        .map((p: any) => {
          const ll = parseLatLng(p);
          const t = iso(p.timestamp ?? p.timestampMs);
          return ll && t ? { t, lat: ll.lat, lon: ll.lon } : null;
        })
        .filter(Boolean) as PointInput[];
      let points = raw;
      if (points.length < 2) {
        // Waypoints have no times: spread them over the segment
        const ways = [a.startLocation, ...(a.waypointPath?.waypoints ?? []), a.endLocation]
          .map(parseLatLng)
          .filter(Boolean) as { lat: number; lon: number }[];
        const s = Date.parse(startTime);
        const span = Date.parse(endTime) - s;
        points = ways.map((w, i) => ({
          t: new Date(s + (ways.length > 1 ? (span * i) / (ways.length - 1) : 0)).toISOString(),
          ...w,
        }));
      }
      if (points.length < 2) {
        return;
      }
      out.activities.push({
        externalId: `${startTime}|${a.activityType ?? 'unknown'}`,
        type: String(a.activityType ?? 'other'),
        startTime,
        endTime,
        distanceM: isSet(a.distance) ? Number(a.distance) : null,
        points,
      });
    }
  });
  out.places = [...places.values()];
  return out;
};

const FIT_TYPES: Record<string, { type: string; unit: string }> = {
  'com.google.heart_rate.bpm': { type: 'heart_rate', unit: 'bpm' },
  'com.google.step_count.delta': { type: 'steps', unit: 'steps' },
  'com.google.weight': { type: 'weight', unit: 'kg' },
  'com.google.calories.expended': { type: 'calories', unit: 'kcal' },
};

/**
 * Google Fit Takeout "All Data" file: `{ "Data Points": [...] }`.
 * @param json - Parsed JSON
 * @returns Parsed data
 */
export const parseGoogleFit = (json: unknown): ParsedData => {
  const out = empty('google-fit');
  const points =
    (json as Record<string, any[]>)?.['Data Points'] ??
    (json as Record<string, any[]>)?.dataPoints ??
    [];
  points.forEach((p) => {
    const kind = FIT_TYPES[p.dataTypeName];
    const v = p.fitValue?.[0]?.value;
    const value = v?.fpVal ?? v?.intVal;
    if (!kind || !isSet(value) || !isSet(p.startTimeNanos)) {
      return;
    }
    const start = new Date(Number(BigInt(p.startTimeNanos) / 1_000_000n)).toISOString();
    const end = isSet(p.endTimeNanos)
      ? new Date(Number(BigInt(p.endTimeNanos) / 1_000_000n)).toISOString()
      : null;
    out.health.push({
      type: kind.type,
      startTime: start,
      endTime: end !== start ? end : null,
      value: Number(value),
      unit: kind.unit,
    });
  });
  return out;
};

/**
 * Fitbit export `heart_rate-YYYY-MM-DD.json`: `[{ dateTime: "10/22/26 06:00:00", value: { bpm } }]`
 * (times are the device's local time).
 * @param json - Parsed JSON
 * @param utcOffsetMinutes - The device's offset from UTC; without it each time is
 * read in this browser's time zone, so summer and winter time are both right
 * @returns Parsed data
 */
export const parseFitbitHeartRate = (json: unknown, utcOffsetMinutes?: number): ParsedData => {
  const out = empty('fitbit');
  (Array.isArray(json) ? json : []).forEach((row: any) => {
    const m = /^(\d{2})\/(\d{2})\/(\d{2}) (\d{2}):(\d{2}):(\d{2})$/.exec(row?.dateTime ?? '');
    const bpm = row?.value?.bpm;
    if (!m || !isSet(bpm)) {
      return;
    }
    const [y, mo, d, h, mi, s] = [
      2000 + Number(m[3]),
      Number(m[1]) - 1,
      Number(m[2]),
      Number(m[4]),
      Number(m[5]),
      Number(m[6]),
    ];
    const utc = !isSet(utcOffsetMinutes)
      ? new Date(y, mo, d, h, mi, s).getTime()
      : Date.UTC(y, mo, d, h, mi, s) - utcOffsetMinutes * 60_000;
    out.health.push({
      type: 'heart_rate',
      startTime: new Date(utc).toISOString(),
      value: Number(bpm),
      unit: 'bpm',
    });
  });
  return out;
};

/** Every visit, activity and health sample of several parsed files */
export const merge = (parts: ParsedData[], source: string): ParsedData => ({
  source,
  activities: parts.flatMap((p) => p.activities) as ActivityInput[],
  places: parts.flatMap((p) => p.places) as PlaceInput[],
  visits: parts.flatMap((p) => p.visits) as VisitInput[],
  health: parts.flatMap((p) => p.health) as HealthInput[],
});
