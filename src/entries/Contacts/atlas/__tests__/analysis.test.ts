import {
  chartPath,
  chartY,
  distanceAtTime,
  effortLabel,
  ensureSpan,
  formatClock,
  formatPace,
  formatSignedPct,
  formatSpeedOrPace,
  indexAt,
  paceOf,
  smoothSeries,
  speedRange,
  speedSeries,
  splitLabel,
  splitScale,
  timeAtDistance,
  trackSamples,
  typePlural,
  valueAt,
  valueRange,
  zoneShares,
  type ChartFrame,
  type ChartPoint,
  type TrackSample,
} from '../analysis';
import { loadSummary, sparkPath } from '../insights';
import type { PointOutput } from '../types';

const START = Date.UTC(2026, 9, 1, 8, 0, 0);
const at = (s: number) => new Date(START + s * 1000).toISOString();

const sample = (t: number, d: number, speedKmh: number | null = null): TrackSample => ({
  t,
  d,
  lat: 32,
  lon: 34,
  ele: null,
  hr: null,
  speedKmh,
  cad: null,
});

describe('atlas analysis: texts', () => {
  it('formats a pace as minutes per km', () => {
    expect(formatPace(312)).toBe('5:12 /km');
    expect(formatPace(299.6)).toBe('5:00 /km');
    expect(formatPace(65, false)).toBe('1:05');
  });

  it('leaves out a missing or impossible pace', () => {
    expect(formatPace(null)).toBe('');
    expect(formatPace(0)).toBe('');
    expect(formatPace(Infinity)).toBe('');
  });

  it('turns a speed into a pace', () => {
    expect(paceOf(12)).toBe(300);
    expect(paceOf(0)).toBeNull();
    expect(formatSpeedOrPace(12, true)).toBe('5:00 /km');
    expect(formatSpeedOrPace(24.25, false)).toBe('24.3 km/h');
    expect(formatSpeedOrPace(null, false)).toBe('');
  });

  it('formats seconds as a clock', () => {
    expect(formatClock(245)).toBe('4:05');
    expect(formatClock(3723)).toBe('1:02:03');
    expect(formatClock(0)).toBe('0:00');
    expect(formatClock(null)).toBe('');
  });

  it('formats a signed change', () => {
    expect(formatSignedPct(4.2)).toBe('+4%');
    expect(formatSignedPct(-2.4)).toBe('−2%');
    expect(formatSignedPct(0.3)).toBe('±0%');
    expect(formatSignedPct(null)).toBe('');
  });

  it('names best-effort distances and split ends', () => {
    expect(effortLabel(1000)).toBe('1 km');
    expect(effortLabel(21_097)).toBe('Half marathon');
    expect(effortLabel(400)).toBe('400 m');
    expect(splitLabel(3000, 1000)).toBe('3');
    expect(splitLabel(42_195, 1000)).toBe('42.2');
    expect(splitLabel(300, 100)).toBe('300');
  });

  it('says the plural of a type', () => {
    expect(typePlural('ride')).toBe('rides');
    expect(typePlural('ski')).toBe('ski days');
    expect(typePlural('other')).toBe('activities');
    expect(typePlural('unknown')).toBe('activities');
  });
});

describe('atlas analysis: heart-rate zones', () => {
  it('gives whole percentages that add up to 100', () => {
    const shares = zoneShares([600, 1200, 900, 300, 0]);
    expect(shares).toEqual([20, 40, 30, 10, 0]);
    const odd = zoneShares([1, 1, 1, 0, 0]);
    expect(odd.reduce((a, b) => a + b, 0)).toBe(100);
    expect(odd).toEqual([34, 33, 33, 0, 0]);
  });

  it('gives zeros without any time', () => {
    expect(zoneShares([0, 0, 0, 0, 0])).toEqual([0, 0, 0, 0, 0]);
    expect(zoneShares([NaN, -5, 0, 0, 0])).toEqual([0, 0, 0, 0, 0]);
  });
});

describe('atlas analysis: track samples', () => {
  it('uses the server distance', () => {
    const points: PointOutput[] = [
      { t: at(0), lat: 32, lon: 34, d: 0, speed: 2 },
      { t: at(10), lat: 32.001, lon: 34, d: 111, speed: 3 },
    ];
    const s = trackSamples(points, START);
    expect(s.map((x) => x.t)).toEqual([0, 10]);
    expect(s[1].d).toBe(111);
    expect(s[1].speedKmh).toBeCloseTo(10.8);
  });

  it('sums the distance up and derives the speed when missing', () => {
    const points: PointOutput[] = [
      { t: at(0), lat: 32, lon: 34 },
      { t: at(10), lat: 32.001, lon: 34 },
      { t: at(20), lat: 32.002, lon: 34 },
    ];
    const s = trackSamples(points, START);
    expect(s[1].d).toBeCloseTo(111.2, 0);
    expect(s[2].d).toBeCloseTo(222.4, 0);
    // 111 m in 10 s ≈ 40 km/h
    expect(s[1].speedKmh).toBeCloseTo(40, 0);
  });
});

describe('atlas analysis: smoothing', () => {
  it('averages over a centred time window', () => {
    const series: ChartPoint[] = [0, 5, 10, 15, 20].map((t, i) => ({
      t,
      d: t,
      v: [10, 20, 30, 40, 50][i],
    }));
    const s = smoothSeries(series, 10);
    // Window ±5 s: 0 → (10,20); 10 → (20,30,40); 20 → (40,50)
    expect(s[0].v).toBe(15);
    expect(s[2].v).toBe(30);
    expect(s[4].v).toBe(45);
  });

  it('keeps gaps and leaves them out of the averages', () => {
    const series: ChartPoint[] = [
      { t: 0, d: 0, v: 10 },
      { t: 1, d: 0, v: null },
      { t: 2, d: 0, v: 30 },
    ];
    const s = smoothSeries(series, 10);
    expect(s[1].v).toBeNull();
    expect(s[0].v).toBe(20);
  });

  it('shows pace with gaps where stopped and ranges over the moving part', () => {
    const samples = [0, 10, 20, 30, 40].map((t, i) => sample(t, t * 3, [12, 12, 0, 0, 12][i]));
    const pace = speedSeries(samples, true);
    expect(pace[0].v).toBe(300);
    // Stopped (the 15-second window around t=20 holds only the stop): a gap
    expect(pace[2].v).toBeNull();
    const stopped = speedSeries(
      [0, 100, 200].map((t) => sample(t, 0, 0)),
      true,
    );
    expect(stopped.every((p) => p.v === null)).toBe(true);
    expect(speedRange(stopped, true)).toBeNull();
    const speed = speedSeries(
      [0, 100, 200, 300].map((t, i) => sample(t, 0, [0, 20, 30, 0][i])),
      false,
    );
    expect(speedRange(speed, false)).toEqual([20, 30]);
  });
});

describe('atlas analysis: cursor lookups', () => {
  const track = [sample(0, 0), sample(10, 50), sample(20, 50), sample(30, 150)];

  it('finds the nearest point by time or distance', () => {
    expect(indexAt(track, 't', 14)).toBe(1);
    expect(indexAt(track, 't', 16)).toBe(2);
    expect(indexAt(track, 't', -5)).toBe(0);
    expect(indexAt(track, 't', 99)).toBe(3);
    expect(indexAt(track, 'd', 120)).toBe(3);
    expect(indexAt([], 't', 1)).toBe(-1);
  });

  it('gives the value under the cursor', () => {
    const series: ChartPoint[] = [
      { t: 0, d: 0, v: 100 },
      { t: 60, d: 200, v: 140 },
    ];
    expect(valueAt(series, 20)).toBe(100);
    expect(valueAt(series, 40)).toBe(140);
    expect(valueAt([], 10)).toBeNull();
  });

  it('converts between time and distance', () => {
    expect(distanceAtTime(track, 5)).toBe(25);
    expect(distanceAtTime(track, 15)).toBe(50);
    expect(distanceAtTime(track, 25)).toBe(100);
    expect(distanceAtTime(track, 99)).toBe(150);
    expect(distanceAtTime([], 5)).toBe(0);
    // The first moment the distance is reached (not the stop after it)
    expect(timeAtDistance(track, 50)).toBe(10);
    expect(timeAtDistance(track, 140)).toBe(30);
  });
});

describe('atlas analysis: chart geometry', () => {
  const frame: ChartFrame = {
    width: 100,
    height: 50,
    pad: 5,
    maxX: 10,
    range: [0, 10],
    invert: false,
  };

  it('maps values to y, inverted for pace', () => {
    expect(chartY(10, frame)).toBe(5);
    expect(chartY(0, frame)).toBe(45);
    expect(chartY(99, frame)).toBe(5);
    expect(chartY(10, { ...frame, invert: true })).toBe(45);
  });

  it('breaks the path at gaps', () => {
    const d = chartPath(
      [
        { t: 0, d: 0, v: 0 },
        { t: 5, d: 0, v: 10 },
        { t: 6, d: 0, v: null },
        { t: 10, d: 0, v: 10 },
      ],
      't',
      frame,
    );
    expect(d).toBe('M0.0,45.0L50.0,5.0M100.0,5.0');
  });

  it('finds robust ranges and widens flat ones', () => {
    expect(valueRange([3, null, 1, 2])).toEqual([1, 3]);
    expect(valueRange([null])).toBeNull();
    expect(ensureSpan([100, 102], 10)).toEqual([96, 106]);
    expect(ensureSpan([0, 50], 10)).toEqual([0, 50]);
  });
});

describe('atlas analysis: splits', () => {
  it('scales the bars by speed and picks the fastest full split', () => {
    const s = splitScale(
      [
        { speedKmh: 10, distanceM: 1000 },
        { speedKmh: 12, distanceM: 1000 },
        { speedKmh: 15, distanceM: 200 },
      ],
      1000,
    );
    expect(s.fastest).toBe(1);
    expect(s.widths[0]).toBeCloseTo(0.3);
    expect(s.widths[2]).toBeCloseTo(1);
  });
});

describe('atlas insights: training load and sparklines', () => {
  const week = (i: number, load: number) => ({
    week: new Date(Date.UTC(2026, 0, 5 + i * 7)).toISOString(),
    hours: 1,
    distanceKm: 10,
    load,
  });

  it('compares the latest week with the 4 before', () => {
    const s = loadSummary([
      week(0, 999),
      week(1, 100),
      week(2, 200),
      week(3, 300),
      week(4, 400),
      week(5, 50),
    ]);
    expect(s?.latest.load).toBe(50);
    expect(s?.avgBefore).toBe(250);
    expect(loadSummary([week(0, 10)])?.avgBefore).toBeNull();
    expect(loadSummary([])).toBeNull();
  });

  it('draws a sparkline that fills the box', () => {
    expect(
      sparkPath(
        [
          { t: 0, v: 10 },
          { t: 5, v: null },
          { t: 10, v: 20 },
        ],
        100,
        20,
        0,
      ),
    ).toBe('M0.0,20.0M100.0,0.0');
    expect(sparkPath([{ t: 0, v: 1 }], 100, 20)).toBe('');
  });
});
