/**
 * @fileoverview Texts and input conversion of the fusion service: tolerances,
 * match evidence, the service status and the settings form's limits
 * @module Contacts/atlas/fusionText
 */

import type { FusionStatus, MatchEvidence } from './types';
import { isSet } from './types';

/** Limits of a source's tolerances (the server clamps to these) */
export const TIME_TOLERANCE_S = { min: 1, max: 21_600 };
export const LOCATION_TOLERANCE_M = { min: 1, max: 20_000 };
/** Range of the match threshold slider */
export const MATCH_THRESHOLD = { min: 0.2, max: 0.95, step: 0.05 };

const MINUS = '−';

/** A number with at most one decimal, without a trailing ".0" */
const oneDecimal = (n: number): string => String(Math.round(n * 10) / 10);

/**
 * Seconds as the minutes the settings form shows.
 * @param seconds - Tolerance in seconds
 * @returns e.g. "0.5" for 30 s, "10" for 600 s, "0.333" for 20 s
 */
export const secondsToMinutesText = (seconds: number): string =>
  String(Math.round((seconds / 60) * 1000) / 1000);

/**
 * Reads the minutes typed in the settings form.
 * Accepts decimal minutes ("0.5", "10", "1,5") and minutes:seconds ("1:30").
 * @param text - What the user typed
 * @returns Whole seconds, or null when it is not a positive number
 */
export const minutesTextToSeconds = (text: string): number | null => {
  const t = text.trim().replace(',', '.');
  const clockMatch = /^(\d+):([0-5]?\d)$/.exec(t);
  if (clockMatch) {
    const s = Number(clockMatch[1]) * 60 + Number(clockMatch[2]);
    return s > 0 ? s : null;
  }
  if (!/^\d*\.?\d+$/.test(t)) {
    return null;
  }
  const s = Math.round(Number(t) * 60);
  return s > 0 ? s : null;
};

/**
 * Checks a time tolerance typed in minutes.
 * @param text - What the user typed
 * @returns An error message, or null when valid
 */
export const timeToleranceError = (text: string): string | null => {
  const s = minutesTextToSeconds(text);
  if (!isSet(s)) {
    return 'Minutes, e.g. 0.5 or 1:30';
  }
  if (s < TIME_TOLERANCE_S.min || s > TIME_TOLERANCE_S.max) {
    return `1 s to ${TIME_TOLERANCE_S.max / 3600} h`;
  }
  return null;
};

/**
 * Checks a location tolerance typed in meters.
 * @param text - What the user typed
 * @returns An error message, or null when valid
 */
export const locationToleranceError = (text: string): string | null => {
  const t = text.trim();
  if (!/^\d*\.?\d+$/.test(t)) {
    return 'Meters, e.g. 100';
  }
  const m = Number(t);
  if (m < LOCATION_TOLERANCE_M.min || m > LOCATION_TOLERANCE_M.max) {
    return `${LOCATION_TOLERANCE_M.min} to ${LOCATION_TOLERANCE_M.max.toLocaleString('en-GB')} m`;
  }
  return null;
};

/**
 * A length of time, unsigned.
 * @param seconds - Seconds
 * @returns e.g. "40 s", "2 min", "10.5 min", "1.5 h"
 */
export const formatSeconds = (seconds: number): string => {
  const s = Math.abs(seconds);
  if (s < 60) {
    return `${Math.round(s)} s`;
  }
  if (s < 3600) {
    return `${oneDecimal(s / 60)} min`;
  }
  return `${oneDecimal(s / 3600)} h`;
};

/**
 * A signed time difference.
 * @param seconds - Seconds (negative = earlier)
 * @returns e.g. "+40 s", "−2 min", "0 s"
 */
export const formatDelta = (seconds: number): string => {
  const text = formatSeconds(seconds);
  if (text === '0 s') {
    return text;
  }
  return `${seconds < 0 ? MINUS : '+'}${text}`;
};

/**
 * A distance for the evidence line.
 * @param meters - Meters
 * @returns e.g. "18 m", "1.2 km"
 */
export const formatMeters = (meters: number): string =>
  meters < 1000 ? `${Math.round(meters)} m` : `${oneDecimal(meters / 1000)} km`;

/**
 * A confidence as a percentage.
 * @param confidence - 0..1
 * @returns e.g. "94%"
 */
export const formatPercent = (confidence: number): string => `${Math.round(confidence * 100)}%`;

/**
 * How a member of a fused activity matches the primary, as one line.
 * @param e - The member's evidence
 * @returns e.g. "Start +40 s · end −2 min (±10.5 min) · track 18 m apart (±325 m) · match 94%"
 */
export const describeEvidence = (e: MatchEvidence): string => {
  if (e.userDecision) {
    return 'merged by you';
  }
  if (e.segment) {
    return 'the next part of the recording: started again where it stopped';
  }
  if (e.contained) {
    return 'a piece of it as another source saw it, within the recording';
  }
  const track = isSet(e.trackDeviationM)
    ? `track ${formatMeters(e.trackDeviationM)} apart (±${formatMeters(e.locationToleranceM)})`
    : 'no track to compare';
  return [
    `Start ${formatDelta(e.startDeltaS)}`,
    `end ${formatDelta(e.endDeltaS)} (±${formatSeconds(e.timeToleranceS)})`,
    track,
    `match ${formatPercent(e.confidence)}`,
  ].join(' · ');
};

/**
 * Whether the fusion service has work queued or running.
 * @param status - The service status
 * @returns True while pending > 0 or running
 */
export const fusionBusy = (status: FusionStatus | null | undefined): boolean =>
  // Queued work after a failure waits for its retry: not "busy"
  !!status && (status.running || (status.pending > 0 && !status.lastError));

/** "10:32" today, "3 Sep 10:32" on other days (local time) */
const when = (iso: string, now: Date): string => {
  const d = new Date(iso);
  const time = `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
  if (d.toDateString() === now.toDateString()) {
    return time;
  }
  return `${d.getDate()} ${d.toLocaleString('en-GB', { month: 'short' }).slice(0, 3)} ${time}`;
};

/**
 * The fusion service's status as one line.
 * @param status - The service status
 * @param now - The current time (for "today")
 * @returns e.g. "Up to date · last run 10:32 · 174 activities, 12 fused, 3 conflicts" or "Working… 4 windows queued"
 */
export const describeFusionStatus = (status: FusionStatus, now: Date = new Date()): string => {
  if (status.lastError && !status.running) {
    return status.pending > 0
      ? `${status.lastError} (${status.pending} ${status.pending === 1 ? 'window' : 'windows'} waiting)`
      : status.lastError;
  }
  if (fusionBusy(status)) {
    if (status.pending > 0) {
      return `Working… ${status.pending} ${status.pending === 1 ? 'window' : 'windows'} queued`;
    }
    return 'Working…';
  }
  const parts = ['Up to date'];
  if (status.lastRunAt) {
    parts.push(`last run ${when(status.lastRunAt, now)}`);
  }
  const r = status.lastResult;
  if (r) {
    parts.push(
      `${r.activities} ${r.activities === 1 ? 'activity' : 'activities'}, ${r.fused} fused, ${r.conflicts} ${
        r.conflicts === 1 ? 'conflict' : 'conflicts'
      }`,
    );
  }
  return parts.join(' · ');
};
