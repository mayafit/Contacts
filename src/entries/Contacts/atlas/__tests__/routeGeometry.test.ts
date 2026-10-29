import {
  hasInferred,
  inferredCasingStyle,
  jobText,
  pieceStyle,
  routeCaption,
  routeStatusText,
  splitPieces,
} from '../routeGeometry';
import { INFERRED_COLOR, type ActivityRouteInfo, type RouteDescription } from '../types';

/** A straight north-south line of 11 points, ~111 m apart (1,112 m in all) */
const line: [number, number][] = Array.from({ length: 11 }, (_, i) => [32 + i * 0.001, 34.8]);
const total = 1111.95;

const description = (d: Partial<RouteDescription> = {}): RouteDescription => ({
  grade: 'poor',
  score: 30,
  flags: ['sparse', 'straight'],
  matchable: 'yes',
  needsImprovement: true,
  status: null,
  confidence: null,
  matchedDistanceM: null,
  current: false,
  decision: null,
  reason: null,
  ...d,
});

const route = (d: Partial<RouteDescription> | null = {}, shares = {}): ActivityRouteInfo => ({
  recordIds: ['r1'],
  description: d === null ? null : description(d),
  observedShare: null,
  inferredShare: null,
  ...shares,
});

describe('splitPieces', () => {
  it('draws the whole line as observed without pieces', () => {
    expect(splitPieces(line, null)).toEqual([{ kind: 'observed', positions: line }]);
    expect(splitPieces(line, [])).toHaveLength(1);
    expect(splitPieces(line.slice(0, 1), null)).toEqual([]);
  });

  it('cuts at the pieces’ metres, interpolating inside a segment', () => {
    const runs = splitPieces(line, [
      { fromM: 0, toM: 500, kind: 'observed' },
      { fromM: 500, toM: total, kind: 'inferred' },
    ]);
    expect(runs.map((r) => r.kind)).toEqual(['observed', 'inferred']);
    const cut = runs[0].positions[runs[0].positions.length - 1];
    // 500 m north of the start: between the 5th and 6th points
    expect(cut[0]).toBeCloseTo(32.0045, 4);
    expect(runs[1].positions[0]).toEqual(cut);
    expect(runs[1].positions[runs[1].positions.length - 1]).toEqual(line[10]);
  });

  it('scales the server’s metres to the decoded line (no drift from rounding)', () => {
    // The server measured 2% more: the cut still falls half way
    const runs = splitPieces(line, [
      { fromM: 0, toM: total * 1.02 * 0.5, kind: 'observed' },
      { fromM: total * 1.02 * 0.5, toM: total * 1.02, kind: 'inferred' },
    ]);
    expect(runs[0].positions[runs[0].positions.length - 1][0]).toBeCloseTo(32.005, 4);
  });

  it('draws a gap as the straight leg and joins neighbours of one kind', () => {
    const runs = splitPieces(line, [
      { fromM: 0, toM: 200, kind: 'observed' },
      { fromM: 200, toM: 400, kind: 'observed' },
      { fromM: 400, toM: 800, kind: 'gap' },
      { fromM: 800, toM: total, kind: 'observed' },
    ]);
    expect(runs.map((r) => r.kind)).toEqual(['observed', 'gap', 'observed']);
    expect(runs[1].positions).toHaveLength(2);
    expect(hasInferred(runs)).toBe(false);
    expect(hasInferred(splitPieces(line, [{ fromM: 0, toM: total, kind: 'inferred' }]))).toBe(true);
  });
});

describe('pieceStyle', () => {
  it('draws observed solid, inferred in distinct amber dashes on a casing, gaps faint grey dots', () => {
    const observed = pieceStyle('observed', '#f00');
    const inferred = pieceStyle('inferred', '#f00');
    const gap = pieceStyle('gap', '#f00');
    expect(observed.dashArray).toBeUndefined();
    expect(observed.color).toBe('#f00');
    expect(inferred.dashArray).toBeDefined();
    expect(inferred.color).toBe(INFERRED_COLOR);
    expect(inferred.opacity).toBe(1);
    expect(inferredCasingStyle().weight).toBeGreaterThan(inferred.weight);
    expect(gap.color).not.toBe('#f00');
    expect(gap.weight).toBeLessThan(inferred.weight);
    expect(gap.opacity).toBeLessThan(observed.opacity);
  });
});

describe('routeCaption', () => {
  const recorded = { geometry: 'recorded' as const, pointCount: 12, onRoadsM: null };

  it('says nothing when route quality is off', () => {
    expect(routeCaption(recorded, null)).toBeNull();
    expect(routeCaption(recorded, undefined)).toBeNull();
  });

  it('shows a drawn match with its confidence and the shares on hover', () => {
    const c = routeCaption(
      { geometry: 'matched', pointCount: 12, onRoadsM: 12_300 },
      route({ confidence: 0.92, status: 'matched' }, { observedShare: 0.6, inferredShare: 0.4 }),
    )!;
    expect(c.text).toBe('Route: matched to roads · 92%');
    expect(c.hint).toBe('60% along recorded points · 40% inferred between them · 12.3 km on roads');
    expect(c.matched).toBe(true);
    expect(c.canFix).toBe(false);
  });

  it('shows the grade with the flags on hover, and offers a fix', () => {
    const c = routeCaption(recorded, route({ grade: 'fair', flags: ['sparse'] }))!;
    expect(c.text).toBe('Route quality: fair');
    expect(c.hint).toBe('few points');
    expect(c.canFix).toBe(true);
  });

  it('offers "Try again" after the Owner kept the recording', () => {
    const c = routeCaption(recorded, route({ decision: 'reject' }))!;
    expect(c.text).toBe('Route: kept as recorded');
    expect(c.rejected).toBe(true);
  });

  it('explains an unmatchable route and does not offer a fix', () => {
    const c = routeCaption(recorded, route({ matchable: 'outside_coverage', flags: [] }))!;
    expect(c.hint).toBe('Not matched: outside the mapped regions');
    expect(c.canFix).toBe(false);
  });

  it('says an unassessed route is not assessed yet, and nothing without a track', () => {
    expect(routeCaption(recorded, route(null))!.text).toBe('Route: not assessed yet');
    expect(routeCaption({ ...recorded, pointCount: 0 }, route(null))).toBeNull();
    expect(routeCaption(recorded, route({ matchable: 'no_track' }))).toBeNull();
  });

  it('mentions an uncertain match', () => {
    const c = routeCaption(
      recorded,
      route({ status: 'low_confidence', confidence: 0.45, flags: [] }),
    )!;
    expect(c.hint).toBe('Match uncertain (45%)');
  });
});

describe('jobText and routeStatusText', () => {
  it('describes a job', () => {
    expect(jobText(null)).toBe('Waiting for the route service…');
    expect(jobText({ state: 'running', done: 0, total: 1 })).toBe('Matching to roads…');
    expect(jobText({ state: 'running', done: 1, total: 3 })).toBe('Matching 1/3…');
    expect(jobText({ state: 'failed', done: 0, total: 1 })).toBe('Matching failed');
  });

  it('keeps the status to one line', () => {
    expect(routeStatusText(null)).toBeNull();
    expect(routeStatusText({ available: false, enabled: false })).toBeNull();
    expect(routeStatusText({ available: false })).toBe('Route matching unavailable');
    expect(
      routeStatusText({
        queued: { interactive: 0, batch: 2 },
        waitingForMatch: 1200,
        etaSeconds: 1500,
        engine: { up: true, breaker: { open: false } },
      }),
    ).toBe('Route matching: 1,200 waiting, 2 queued, about 25 min');
    expect(
      routeStatusText({
        queued: { interactive: 0, batch: 0 },
        waitingForMatch: 5,
        etaSeconds: 30,
        engine: { up: true, breaker: { open: true } },
      }),
    ).toBe('Route matching: paused after engine errors · 5 waiting, under 2 min');
    expect(
      routeStatusText({
        queued: { interactive: 0, batch: 0 },
        waitingForMatch: 0,
        backfilled: true,
      }),
    ).toBe('Route matching: up to date');
  });
});
