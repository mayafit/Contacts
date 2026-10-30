/**
 * @fileoverview Recognizes export files (also inside zips) and parses them in the browser
 * @module Contacts/atlas/importers
 */

import { isSet } from '../types';

import { Unzip, UnzipInflate } from 'fflate';
import type { ParsedData } from '../types';
import { parseGpx, parseTcx } from './tracks';
import { samsungKind } from './samsung';
import {
  isGoogleReviews,
  parseFitbitHeartRate,
  parseGoogleFit,
  parseGoogleReviews,
  parseTimelineDevice,
  parseTimelineTakeout,
} from './google';

/** A file to look at: its path (inside a zip or folder) and how to read it */
export interface ImportFile {
  path: string;
  text: () => Promise<string>;
}

/** What a path is, from its name and folder */
export type FileKind = 'gpx' | 'tcx' | 'json' | 'samsung' | null;

/**
 * Whether a file can hold importable data.
 * @param path - File path
 * @returns Kind, or null to skip
 */
export const kindOf = (path: string): FileKind => {
  const p = path.toLowerCase();
  if (samsungKind(path)) {
    return 'samsung';
  }
  if (p.endsWith('.gpx')) {
    return 'gpx';
  }
  if (p.endsWith('.tcx')) {
    return 'tcx';
  }
  if (!p.endsWith('.json')) {
    return null;
  }
  // The raw location log (Records.json) can be gigabytes; Timeline covers it
  if (/(^|\/)records\.json$/.test(p)) {
    return null;
  }
  // JSON files worth reading: Timeline, Fit data points, Fitbit heart rate
  if (
    /timeline|semantic location history|location history|fit\/all data|heart_rate/.test(p) ||
    // Takeout "Maps (your places)/Reviews.json" (AD-39)
    /(^|\/)reviews\.json$/.test(p) ||
    !p.includes('/')
  ) {
    return 'json';
  }
  return null;
};

/**
 * Parses one file, recognizing the JSON format by its content.
 * @param file - File
 * @param sourceHint - Source for GPX/TCX (sports-tracker when chosen)
 * @returns Parsed data, or null when the file is not a known format
 */
export const parseFile = async (
  file: ImportFile,
  sourceHint?: string,
): Promise<ParsedData | null> => {
  const kind = kindOf(file.path);
  const name = file.path.split('/').pop() ?? file.path;
  if (kind === 'gpx') {
    return parseGpx(await file.text(), name, sourceHint ?? 'gpx');
  }
  if (kind === 'tcx') {
    return parseTcx(
      await file.text(),
      name,
      sourceHint ?? (/fit\//i.test(file.path) ? 'google-fit' : 'tcx'),
    );
  }
  if (kind !== 'json') {
    return null;
  }
  const text = await file.text();
  let json: unknown;
  try {
    json = JSON.parse(text);
  } catch {
    return null;
  }
  if (isGoogleReviews(json)) {
    return parseGoogleReviews(json);
  }
  if (json && typeof json === 'object' && 'semanticSegments' in (json as object)) {
    return parseTimelineDevice(json);
  }
  if (
    Array.isArray(json) &&
    json.length &&
    (json[0]?.visit || json[0]?.activity || json[0]?.timelinePath)
  ) {
    return parseTimelineDevice(json);
  }
  if (json && typeof json === 'object' && 'timelineObjects' in (json as object)) {
    return parseTimelineTakeout(json);
  }
  if (
    json &&
    typeof json === 'object' &&
    ('Data Points' in (json as object) || 'dataPoints' in (json as object))
  ) {
    return parseGoogleFit(json);
  }
  if (Array.isArray(json) && json.length && json[0]?.dateTime && isSet(json[0]?.value?.bpm)) {
    return parseFitbitHeartRate(json);
  }
  return null;
};

const concat = (chunks: Uint8Array[]): Uint8Array => {
  const out = new Uint8Array(chunks.reduce((s, c) => s + c.length, 0));
  let offset = 0;
  chunks.forEach((c) => {
    out.set(c, offset);
    offset += c.length;
  });
  return out;
};

/** Entries larger than this are skipped (decoded as one string in memory) */
export const MAX_ENTRY_BYTES = 256 * 1024 * 1024;

/**
 * The importable files inside a zip, read as a stream (Takeout zips can be
 * several GB; only the wanted entries are kept in memory). Entries are handed
 * to onEntry one at a time, and reading waits while one is being handled.
 * @param zip - The zip file
 * @param onEntry - Called with each wanted entry
 * @param onSkip - Called with the path of an entry too large to read
 */
export const readZip = async (
  zip: File,
  onEntry: (file: ImportFile) => Promise<void>,
  onSkip?: (path: string) => void,
): Promise<void> => {
  let chain = Promise.resolve();
  let failure: unknown = null;
  const unzip = new Unzip((entry) => {
    if (!kindOf(entry.name)) {
      return;
    }
    if ((entry.originalSize ?? 0) > MAX_ENTRY_BYTES) {
      onSkip?.(entry.name);
      return;
    }
    const chunks: Uint8Array[] = [];
    let bytes = 0;
    let dropped = false;
    entry.ondata = (err, data, final) => {
      if (err) {
        failure ??= err;
        return;
      }
      if (dropped) {
        return;
      }
      bytes += data.length;
      if (bytes > MAX_ENTRY_BYTES) {
        // Size unknown up front (streamed zip): give up once it is too large
        dropped = true;
        chunks.length = 0;
        onSkip?.(entry.name);
        return;
      }
      chunks.push(data);
      if (final) {
        const text = new TextDecoder().decode(concat(chunks));
        chunks.length = 0;
        chain = chain.then(() => onEntry({ path: entry.name, text: async () => text }));
      }
    };
    entry.start();
  });
  unzip.register(UnzipInflate);
  const reader = zip.stream().getReader();
  for (;;) {
    const { done, value } = await reader.read();
    if (done) {
      unzip.push(new Uint8Array(0), true);
      break;
    }
    unzip.push(value);
    if (failure) {
      throw failure;
    }
    // Back-pressure: finish the entries read so far before reading on
    await chain;
  }
  if (failure) {
    throw failure;
  }
  await chain;
};
