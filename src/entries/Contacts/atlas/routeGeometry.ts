/**
 * @fileoverview Road-matched routes (Epic 12): cutting a matched line into its pieces,
 * how each piece is drawn, and the panel's and status area's short texts
 * @module Contacts/atlas/routeGeometry
 */

import { currentUi, dashArray } from '../settings/uiSettings';
import type { UiSettings } from '../settings/uiSettings';
import type {
  ActivityRouteInfo,
  ActivitySummary,
  RebuildSuggestion,
  RouteJob,
  RoutePiece,
  RouteStatus,
} from './types';
import { formatDistance } from './geo';
import { INFERRED_CASING, INFERRED_COLOR, isSet } from './types';

/** A drawable run of one kind: its positions on the map */
export interface PieceLine {
  kind: RoutePiece['kind'];
  positions: [number, number][];
}

/** Metres between two [lat, lon] points (haversine) */
const metres = (a: [number, number], b: [number, number]): number => {
  const rad = Math.PI / 180;
  const h =
    Math.sin(((b[0] - a[0]) * rad) / 2) ** 2 +
    Math.cos(a[0] * rad) * Math.cos(b[0] * rad) * Math.sin(((b[1] - a[1]) * rad) / 2) ** 2;
  return 2 * 6_371_000 * Math.asin(Math.sqrt(h));
};

/** The point at a distance along a line, given its cumulative distances */
const pointAt = (
  path: [number, number][],
  cum: number[],
  m: number,
): { at: [number, number]; i: number } => {
  if (m <= 0) {
    return { at: path[0], i: 0 };
  }
  const last = path.length - 1;
  if (m >= cum[last]) {
    return { at: path[last], i: last };
  }
  // Binary search for the segment that holds m
  let lo = 0;
  let hi = last;
  while (hi - lo > 1) {
    const mid = (lo + hi) >> 1;
    if (cum[mid] <= m) {
      lo = mid;
    } else {
      hi = mid;
    }
  }
  const span = cum[hi] - cum[lo];
  const f = span > 0 ? (m - cum[lo]) / span : 0;
  return {
    at: [
      path[lo][0] + (path[hi][0] - path[lo][0]) * f,
      path[lo][1] + (path[hi][1] - path[lo][1]) * f,
    ],
    i: lo,
  };
};

/**
 * Cuts a matched line into runs of one kind. The pieces' metres were measured on the
 * server; they are scaled to this line's own length, so a rounding difference in the
 * two distance formulas never shifts a cut. Neighbouring pieces of one kind are joined;
 * a gap is drawn as the straight leg from its start to its end.
 * @param path - The decoded matched line
 * @param pieces - The pieces in metres along it
 * @returns Runs in order; the whole line as one observed run when there are no pieces
 */
export const splitPieces = (
  path: [number, number][],
  pieces: RoutePiece[] | null | undefined,
): PieceLine[] => {
  if (path.length < 2) {
    return [];
  }
  if (!pieces?.length) {
    return [{ kind: 'observed', positions: path }];
  }
  const cum = [0];
  for (let i = 1; i < path.length; i++) {
    cum.push(cum[i - 1] + metres(path[i - 1], path[i]));
  }
  const end = Math.max(...pieces.map((p) => p.toM));
  const scale = end > 0 ? cum[cum.length - 1] / end : 1;
  const sorted = [...pieces].sort((a, b) => a.fromM - b.fromM);
  // Neighbours of one kind are one run (fewer canvas paths)
  const merged: RoutePiece[] = [];
  sorted.forEach((p) => {
    const prev = merged[merged.length - 1];
    if (prev && prev.kind === p.kind && p.fromM - prev.toM < 1) {
      prev.toM = Math.max(prev.toM, p.toM);
    } else {
      merged.push({ ...p });
    }
  });
  const runs: PieceLine[] = [];
  merged.forEach((p) => {
    if (p.toM <= p.fromM) {
      return;
    }
    const from = pointAt(path, cum, p.fromM * scale);
    const to = pointAt(path, cum, p.toM * scale);
    const positions: [number, number][] =
      p.kind === 'gap' ? [from.at, to.at] : [from.at, ...path.slice(from.i + 1, to.i + 1), to.at];
    if (positions.length >= 2) {
      runs.push({ kind: p.kind, positions });
    }
  });
  return runs;
};

/**
 * Leaflet path options of a piece, from the Owner's route styles (AD-41): observed solid,
 * inferred dashed, gaps faint dotted by default
 */
export const pieceStyle = (
  kind: RoutePiece['kind'],
  color: string,
  r: UiSettings['routes'] = currentUi().routes,
): { color: string; weight: number; opacity: number; dashArray?: string } => {
  switch (kind) {
    case 'inferred':
      return {
        color: INFERRED_COLOR,
        weight: r.inferred.width,
        opacity: 1,
        dashArray: dashArray(r.inferred.pattern, r.inferred.width),
      };
    case 'gap':
      return {
        color: currentUi().colors.gap,
        weight: r.gap.width,
        opacity: 0.55,
        dashArray: dashArray(r.gap.pattern, r.gap.width),
      };
    default:
      return { color, weight: r.observedWidth, opacity: 0.95 };
  }
};

/** The solid dark casing drawn under an inferred stretch's dashes (2.5 px wider than them) */
export const inferredCasingStyle = (
  r: UiSettings['routes'] = currentUi().routes,
): { color: string; weight: number; opacity: number } => ({
  color: INFERRED_CASING,
  weight: r.inferred.width + 2.5,
  opacity: 0.75,
});

/** Whether any run is inferred (the legend then explains the dashes) */
export const hasInferred = (runs: PieceLine[]): boolean => runs.some((r) => r.kind === 'inferred');

/** Flag names as the panel shows them */
const FLAG_TEXT: Record<string, string> = {
  sparse: 'few points',
  straight: 'straight legs',
  implausible_speed: 'impossible speeds',
  endpoint_far: 'ends far from the stops',
};

/** Why a route cannot be matched */
const MATCHABLE_TEXT: Record<string, string> = {
  no_track: 'no track',
  type_never: 'not matched for this type',
  outside_coverage: 'outside the mapped regions',
  rail: 'rail is not matched',
  unknown: 'unknown transit',
};

/** What the panel says about an activity's route */
export interface RouteCaption {
  /** The short line, e.g. "Route: matched to roads · 92%" */
  text: string;
  /** More on hover (the flags, the shares) */
  hint?: string;
  /** The matched line is drawn: the Roads / Recorded toggle and "Revert to recorded" */
  matched: boolean;
  /** The Owner kept the recorded line: "Try again" */
  rejected: boolean;
  /** "Fix this route" makes sense */
  canFix: boolean;
}

/** A share as a whole percent */
const pct = (x: number): string => `${Math.round(x * 100)}%`;

/**
 * The panel's route line for an activity.
 * @param summary - The activity (its geometry)
 * @param route - The detail's route section; null or missing when route quality is off
 * @returns Null when there is nothing to say (route quality off, or no track)
 */
export const routeCaption = (
  summary: Pick<ActivitySummary, 'geometry' | 'onRoadsM' | 'pointCount'>,
  route: ActivityRouteInfo | null | undefined,
): RouteCaption | null => {
  if (!route) {
    return null;
  }
  const d = route.description;
  const matchable = !d?.matchable || d.matchable === 'yes';
  const flags = (d?.flags ?? []).map((f) => FLAG_TEXT[f]).filter(Boolean);
  if (summary.geometry === 'matched') {
    const hint = [
      isSet(route.observedShare) ? `${pct(route.observedShare)} along recorded points` : '',
      route.inferredShare ? `${pct(route.inferredShare)} inferred between them` : '',
      summary.onRoadsM ? `${formatDistance(summary.onRoadsM)} on roads` : '',
    ]
      .filter(Boolean)
      .join(' · ');
    const how =
      d?.method === 'similar'
        ? 'rebuilt from similar drives'
        : d?.method === 'waypoints'
          ? 'through your marked points'
          : 'matched to roads';
    return {
      text: `Route: ${how}${isSet(d?.confidence) ? ` · ${pct(d.confidence)}` : ''}`,
      hint: hint || undefined,
      matched: true,
      rejected: false,
      canFix: false,
    };
  }
  if (d?.decision === 'reject') {
    return {
      text: 'Route: kept as recorded',
      hint: 'You chose the recorded line; it is not matched again automatically',
      matched: false,
      rejected: true,
      canFix: matchable,
    };
  }
  if (summary.pointCount < 2 || d?.matchable === 'no_track') {
    return null;
  }
  const why = !matchable && d?.matchable ? (MATCHABLE_TEXT[d.matchable] ?? d.matchable) : '';
  const hint =
    [
      flags.length ? flags.join(', ') : '',
      why ? `Not matched: ${why}` : '',
      d?.status === 'low_confidence' && isSet(d.confidence)
        ? `Match uncertain (${pct(d.confidence)})`
        : '',
      d?.status === 'failed' ? 'Matching found no roads' : '',
    ]
      .filter(Boolean)
      .join(' · ') || undefined;
  return {
    text: d?.grade ? `Route quality: ${d.grade}` : 'Route: not assessed yet',
    hint,
    matched: false,
    rejected: false,
    canFix: matchable,
  };
};

/**
 * Why "Fix this route" found no confident match: the confidence and the main reason the
 * route service gave (AD-35 rule 6), e.g. "No confident road match (65%): a 6 km
 * stretch with no road that fits the 4 min elapsed"
 * @param route - The activity's route info after the job
 * @returns The note
 */
export const noMatchText = (route: ActivityRouteInfo | null | undefined): string => {
  const d = route?.description;
  const confidence = isSet(d?.confidence) ? ` (${pct(d.confidence)})` : '';
  // The main reason only: no retry bookkeeping ("(gave up after N attempts)", "(attempt 1;
  // retried after 1 min)"), and no engine detail after the first ";"
  const why = d?.reason
    ?.replace(/\s*\((?:gave up after \d+ attempts?|[^()]*;)[^()]*\)/g, '')
    .split(';')[0]
    .replace(/^low confidence [\d.]+:\s*/, '')
    .replace(/^no_match: /, '')
    .replace(/^no_call: /, '')
    .trim();
  return `No confident road match${confidence}${why ? `: ${why}` : ''}`;
};

/** A job's progress as a short text */
export const jobText = (job: Pick<RouteJob, 'state' | 'done' | 'total'> | null): string => {
  if (!job || job.state === 'queued') {
    return 'Waiting for the route service…';
  }
  if (job.state === 'running') {
    return job.total > 1 ? `Matching ${job.done}/${job.total}…` : 'Matching to roads…';
  }
  return job.state === 'done' ? 'Done' : 'Matching failed';
};

/** Minutes or hours, e.g. "about 3 min", "about 2 h" */
const eta = (s: number): string =>
  s < 90
    ? 'under 2 min'
    : s < 5400
      ? `about ${Math.round(s / 60)} min`
      : `about ${Math.round(s / 3600)} h`;

/**
 * The route service's state in one short line (the import dialog's status area).
 * @param status - GET /api/routes/status, or null while loading
 * @returns The line, or null when there is nothing to show
 */
export const routeStatusText = (status: RouteStatus | null): string | null => {
  if (!status || status.enabled === false) {
    return null;
  }
  if (status.available === false) {
    return 'Route matching unavailable';
  }
  const queued = (status.queued?.interactive ?? 0) + (status.queued?.batch ?? 0);
  const waiting = status.waitingForMatch ?? 0;
  const parts: string[] = [];
  if (status.engine?.breaker?.open) {
    parts.push('paused after engine errors');
  } else if (status.engine && !status.engine.up) {
    parts.push('engine down');
  }
  if (waiting > 0 || queued > 0 || (status.running ?? 0) > 0) {
    parts.push(
      [
        waiting > 0 ? `${waiting.toLocaleString()} waiting` : '',
        queued > 0 ? `${queued} queued` : '',
        status.etaSeconds ? eta(status.etaSeconds) : '',
      ]
        .filter(Boolean)
        .join(', '),
    );
  } else if (status.backfilled) {
    parts.push('up to date');
  }
  return `Route matching: ${parts.filter(Boolean).join(' · ') || 'idle'}`;
};

/** "3 Sep at 08:12" (local time) */
const dayAndTime = (iso: string): string => {
  const d = new Date(iso);
  const day = d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short' });
  const time = d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
  return `${day} at ${time}`;
};

/**
 * What a rebuild was based on, in a line (AD-38).
 * @param s - The suggestion
 * @returns e.g. "From 14 similar drives (6 take this way), the closest on 3 Sep at 08:12, the other way"
 */
export const rebuildText = (s: RebuildSuggestion): string => {
  if (s.method === 'waypoints') {
    const n = s.waypoints?.length ?? 0;
    return `Through the ${n} point${n === 1 ? '' : 's'} you marked`;
  }
  const b = s.basis;
  if (!b) {
    return '';
  }
  const similar = `${b.agreeing} similar drive${b.agreeing === 1 ? '' : 's'}`;
  // How many of them take the basis's way (absent on bases stored before ways were counted)
  const way =
    typeof b.sameWay === 'number' && b.sameWay < b.agreeing
      ? ` (${b.sameWay} take${b.sameWay === 1 ? 's' : ''} this way)`
      : '';
  return `From ${similar}${way}, the closest on ${dayAndTime(b.start)}${b.reversed ? ', the other way' : ''}`;
};

/**
 * How a suggestion compares with what is stored now.
 * @param s - The suggestion
 * @returns e.g. "On roads, 85% confidence (now 52%)", or that it is no better
 */
export const suggestionVerdict = (s: RebuildSuggestion): string => {
  if (!isSet(s.confidence)) {
    return '';
  }
  const now = isSet(s.previousConfidence) ? s.previousConfidence : null;
  const shown = s.confidence >= 0.3 ? 'shown once you use it' : 'too weak to show';
  if (now !== null && s.confidence <= now) {
    return `${pct(s.confidence)} confidence: no better than the current match (${pct(now)})`;
  }
  return `${pct(s.confidence)} confidence${now !== null ? ` (now ${pct(now)})` : ''} · ${shown}`;
};

/**
 * Why a rebuild gave no suggestion.
 * @param s - The result
 * @returns A sentence for the panel
 */
export const noSuggestionText = (s: RebuildSuggestion): string => {
  switch (s.outcome) {
    case 'no_similar':
      return s.message ? `No similar drive found: ${s.message}` : 'No similar drive found';
    case 'gone':
      return 'The activity no longer exists';
    case 'no_match':
      return s.message ?? 'The engine placed none of the points on a road';
    default:
      return s.message ?? 'The route could not be rebuilt';
  }
};
