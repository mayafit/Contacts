/**
 * @fileoverview GPX and TCX workouts (Sports Tracker, Garmin, Strava, Google Fit…)
 * @module Contacts/atlas/importers/tracks
 */

import { isSet } from '../types';

import type { ActivityInput, ParsedData, PointInput } from '../types';

/** Activity type from a sport name in a file ("Cycling", "Mountain biking", "Hiking"…) */
export const typeFromSport = (sport: string | null | undefined): string => {
  const s = (sport ?? '').toLowerCase();
  if (/bik|cycl|ride|ciclis|velo/.test(s)) {
    return 'ride';
  }
  if (/hik|trek|mountain(?!.*bik)/.test(s)) {
    return 'hike';
  }
  if (/run|jog/.test(s)) {
    return 'run';
  }
  if (/walk|nordic/.test(s)) {
    return 'walk';
  }
  if (/ski|snowboard/.test(s)) {
    return 'ski';
  }
  if (/swim/.test(s)) {
    return 'swim';
  }
  if (/driv|car|motor/.test(s)) {
    return 'drive';
  }
  return 'other';
};

/** First descendant element by local name (namespaces vary between apps) */
const child = (el: Element, name: string): Element | undefined =>
  Array.from(el.getElementsByTagName('*')).find((e) => e.localName === name);

const childText = (el: Element, name: string): string | undefined =>
  child(el, name)?.textContent?.trim() || undefined;

const num = (v: string | undefined | null): number | undefined => {
  if (!isSet(v) || v === '') {
    return undefined;
  }
  const n = Number(v);
  return Number.isFinite(n) ? n : undefined;
};

const parseXml = (text: string): Document => {
  const doc = new DOMParser().parseFromString(text, 'application/xml');
  if (doc.getElementsByTagName('parsererror').length) {
    throw new Error('Not a valid XML file');
  }
  return doc;
};

/**
 * A GPX file: each track becomes an activity, with heart rate and cadence
 * from Garmin's TrackPointExtension when present.
 * @param text - File content
 * @param fileName - File name (fallback name and id)
 * @param source - Source name
 * @returns Parsed data
 */
export const parseGpx = (text: string, fileName: string, source = 'gpx'): ParsedData => {
  const doc = parseXml(text);
  const activities: ActivityInput[] = [];
  const tracks = Array.from(doc.getElementsByTagName('*')).filter((e) => e.localName === 'trk');
  tracks.forEach((trk, ti) => {
    const points: PointInput[] = [];
    Array.from(trk.getElementsByTagName('*'))
      .filter((e) => e.localName === 'trkpt')
      .forEach((pt) => {
        const lat = num(pt.getAttribute('lat'));
        const lon = num(pt.getAttribute('lon'));
        const time = childText(pt, 'time');
        if (lat === undefined || lon === undefined || !time) {
          return;
        }
        points.push({
          t: new Date(time).toISOString(),
          lat,
          lon,
          ele: num(childText(pt, 'ele')) ?? null,
          hr: num(childText(pt, 'hr')) ?? null,
          cad: num(childText(pt, 'cad')) ?? null,
          speed: num(childText(pt, 'speed')) ?? null,
        });
      });
    if (!points.length) {
      return;
    }
    const name =
      childText(trk, 'name') ??
      (doc.documentElement ? childText(doc.documentElement, 'name') : undefined);
    const typeText = childText(trk, 'type') ?? name ?? fileName;
    activities.push({
      externalId: `${fileName}#${ti}|${points[0].t}`,
      type: typeFromSport(typeText),
      name: name ?? fileName.replace(/\.[^.]+$/, ''),
      startTime: points[0].t,
      endTime: points[points.length - 1].t,
      points,
    });
  });
  return { source, activities, places: [], visits: [], health: [] };
};

/**
 * A TCX file: each Activity becomes an activity, laps joined.
 * @param text - File content
 * @param fileName - File name
 * @param source - Source name
 * @returns Parsed data
 */
export const parseTcx = (text: string, fileName: string, source = 'tcx'): ParsedData => {
  const doc = parseXml(text);
  const activities: ActivityInput[] = [];
  Array.from(doc.getElementsByTagName('*'))
    .filter((e) => e.localName === 'Activity')
    .forEach((act) => {
      const points: PointInput[] = [];
      let distance: number | undefined;
      Array.from(act.getElementsByTagName('*'))
        .filter((e) => e.localName === 'Trackpoint')
        .forEach((tp) => {
          const time = childText(tp, 'Time');
          const lat = num(childText(tp, 'LatitudeDegrees'));
          const lon = num(childText(tp, 'LongitudeDegrees'));
          const d = num(childText(tp, 'DistanceMeters'));
          if (d !== undefined) {
            distance = d;
          }
          if (!time || lat === undefined || lon === undefined) {
            return;
          }
          const hrEl = child(tp, 'HeartRateBpm');
          points.push({
            t: new Date(time).toISOString(),
            lat,
            lon,
            ele: num(childText(tp, 'AltitudeMeters')) ?? null,
            hr: hrEl ? (num(childText(hrEl, 'Value')) ?? null) : null,
            cad: num(childText(tp, 'Cadence')) ?? null,
          });
        });
      const id = childText(act, 'Id');
      if (!points.length && !id) {
        return;
      }
      activities.push({
        externalId: `${id ?? fileName}`,
        type: typeFromSport(act.getAttribute('Sport') ?? fileName),
        name: childText(act, 'Notes') ?? fileName.replace(/\.[^.]+$/, ''),
        startTime: points[0]?.t ?? (id ? new Date(id).toISOString() : null),
        endTime: points[points.length - 1]?.t ?? null,
        distanceM: distance ?? null,
        points,
      });
    });
  return { source, activities, places: [], visits: [], health: [] };
};
