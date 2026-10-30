import {
  hasInferred,
  inferredCasingStyle,
  jobText,
  noMatchText,
  noSuggestionText,
  pieceStyle,
  rebuildText,
  routeCaption,
  routeStatusText,
  splitPieces,
  suggestionVerdict,
} from '../routeGeometry';
import {
  INFERRED_COLOR,
  type ActivityRouteInfo,
  type RebuildSuggestion,
  type RouteDescription,
} from '../types';

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

describe('noMatchText', () => {
  it('gives the confidence and the main reason of a match below the threshold', () => {
    expect(
      noMatchText(
        route({
          status: 'low_confidence',
          confidence: 0.65,
          reason: 'low confidence 0.65: a 6 km stretch with no road that fits the 4 min elapsed',
        }),
      ),
    ).toBe(
      'No confident road match (65%): a 6 km stretch with no road that fits the 4 min elapsed',
    );
  });

  it('reads a failure without a confidence, and says little when nothing is known', () => {
    expect(
      noMatchText(
        route({
          status: 'failed',
          confidence: null,
          reason: 'no_match: the engine matched no point',
        }),
      ),
    ).toBe('No confident road match: the engine matched no point');
    expect(noMatchText(null)).toBe('No confident road match');
  });

  it('drops the engine detail after the first ";" and the retry count', () => {
    expect(
      noMatchText(
        route({
          status: 'low_confidence',
          confidence: 0.5,
          reason:
            'low confidence 0.5: a 2 km straight jump that no road explains; engine 400 (442): No path could be found',
        }),
      ),
    ).toBe('No confident road match (50%): a 2 km straight jump that no road explains');
    expect(
      noMatchText(
        route({
          status: 'failed',
          confidence: null,
          reason: 'the engine did not answer within 15 s (gave up after 4 attempts)',
        }),
      ),
    ).toBe('No confident road match: the engine did not answer within 15 s');
    expect(
      noMatchText(
        route({
          status: 'low_confidence',
          confidence: 0.6,
          reason: 'a bug (gave up after 4 attempts; the routes.v1 line is kept)',
        }),
      ),
    ).toBe('No confident road match (60%): a bug');
    expect(
      noMatchText(
        route({
          status: 'failed',
          confidence: null,
          reason: 'the engine did not answer within 15 s (attempt 2; retried after 10 min)',
        }),
      ),
    ).toBe('No confident road match: the engine did not answer within 15 s');
  });
});

describe('rebuild texts (AD-38)', () => {
  const suggestion = (s: Partial<RebuildSuggestion> = {}): RebuildSuggestion => ({
    method: 'similar',
    outcome: 'suggested',
    activityId: 'a1',
    confidence: 0.85,
    previousConfidence: 0.52,
    basis: {
      activityId: 'b1',
      start: '2026-09-03T05:12:00Z',
      reversed: true,
      fit: 0.9,
      candidates: 14,
      agreeing: 14,
      sameWay: 6,
      examples: [],
    },
    ...s,
  });

  it('says what a rebuild was based on', () => {
    expect(rebuildText(suggestion())).toMatch(
      /^From 14 similar drives \(6 take this way\), the closest on 3 Sept? at \d\d:12, the other way$/,
    );
    expect(
      rebuildText(suggestion({ basis: { ...suggestion().basis!, sameWay: 14, reversed: false } })),
    ).not.toMatch(/take this way|other way/);
    expect(
      rebuildText(suggestion({ basis: { ...suggestion().basis!, sameWay: 1, reversed: false } })),
    ).toMatch(/\(1 takes this way\)/);
    // A basis stored before ways were counted
    expect(
      rebuildText(suggestion({ basis: { ...suggestion().basis!, agreeing: 1, sameWay: undefined } })),
    ).toMatch(/^From 1 similar drive, the closest/);
    expect(rebuildText(suggestion({ method: 'waypoints', waypoints: [[32, 34.8]] }))).toBe(
      'Through the 1 point you marked',
    );
  });

  it('compares the suggestion with the current match', () => {
    expect(suggestionVerdict(suggestion())).toBe(
      '85% confidence (now 52%) · shown once you use it',
    );
    expect(suggestionVerdict(suggestion({ previousConfidence: null }))).toBe(
      '85% confidence · shown once you use it',
    );
    expect(suggestionVerdict(suggestion({ previousConfidence: 0.9 }))).toBe(
      '85% confidence: no better than the current match (90%)',
    );
    expect(suggestionVerdict(suggestion({ confidence: 0.2, previousConfidence: null }))).toBe(
      '20% confidence · too weak to show',
    );
  });

  it('explains a rebuild without a suggestion', () => {
    expect(noSuggestionText(suggestion({ outcome: 'no_match', message: null }))).toBe(
      'The engine placed none of the points on a road',
    );
    expect(
      noSuggestionText(
        suggestion({ outcome: 'no_similar', message: 'None of the 3 drives follows this one.' }),
      ),
    ).toBe('No similar drive found: None of the 3 drives follows this one.');
    expect(
      noSuggestionText(
        suggestion({
          outcome: 'invalid',
          message: 'A marked point is more than 5 km from the recorded track.',
        }),
      ),
    ).toBe('A marked point is more than 5 km from the recorded track.');
  });

  it('names a rebuilt route in the caption', () => {
    const matched = { geometry: 'matched' as const, pointCount: 12, onRoadsM: null };
    expect(
      routeCaption(matched, route({ confidence: 0.8, status: 'matched', method: 'similar' }))!.text,
    ).toBe('Route: rebuilt from similar drives · 80%');
    expect(
      routeCaption(
        matched,
        route({ confidence: 0.4, status: 'low_confidence', method: 'waypoints' }),
      )!.text,
    ).toBe('Route: through your marked points · 40%');
  });
});
