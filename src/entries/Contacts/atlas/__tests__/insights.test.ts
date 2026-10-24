import {
  areaAround,
  bucketLabels,
  explainPrompt,
  formatChange,
  formatHours,
  formatKm,
  formatPeriod,
  formatShare,
  labelStep,
  trendBars,
} from '../insights';
import type { InsightsSeriesPoint } from '../types';

const iso = (y: number, mo: number, d: number, h = 0, mi = 0) =>
  new Date(y, mo - 1, d, h, mi).toISOString();

const at = (y: number, mo: number, d: number) => new Date(y, mo - 1, d).getTime();

const point = (
  t: string,
  type: string,
  distanceKm: number,
  hours = 1,
  count = 1,
): InsightsSeriesPoint => ({ t, type, distanceKm, hours, count });

describe('atlas insights: change against the period before', () => {
  it('shows a rise as a positive percentage', () => {
    expect(formatChange(123, 100)).toEqual({ text: '+23%', up: true });
  });

  it('shows a fall with a minus sign', () => {
    expect(formatChange(88, 100)).toEqual({ text: '−12%', up: false });
  });

  it('shows no change', () => {
    expect(formatChange(100, 100)).toEqual({ text: '±0%', up: false });
  });

  it('is new when there was nothing before', () => {
    expect(formatChange(5, 0)).toEqual({ text: 'new', up: true });
  });

  it('has nothing to compare without a previous value or with zeros', () => {
    expect(formatChange(5, null)).toBeNull();
    expect(formatChange(5, undefined)).toBeNull();
    expect(formatChange(0, 0)).toBeNull();
  });
});

describe('atlas insights: period caption', () => {
  it('names an open period', () => {
    expect(formatPeriod(null, null)).toBe('All time');
    expect(formatPeriod(iso(2026, 9, 3), null)).toBe('Since 3 Sep 2026');
    expect(formatPeriod(null, iso(2026, 9, 3, 12))).toBe('Until 3 Sep 2026');
  });

  it('writes the year once within one year', () => {
    expect(formatPeriod(iso(2026, 7, 24), iso(2026, 10, 24, 18))).toBe('24 Jul – 24 Oct 2026');
  });

  it('writes both years across years', () => {
    expect(formatPeriod(iso(2025, 7, 24), iso(2026, 10, 24, 18))).toBe('24 Jul 2025 – 24 Oct 2026');
  });

  it('ends a range ending at midnight on the day before', () => {
    expect(formatPeriod(iso(2026, 7, 1), iso(2026, 8, 1))).toBe('1 Jul – 31 Jul 2026');
  });

  it('shows one day once', () => {
    expect(formatPeriod(iso(2026, 9, 3, 8), iso(2026, 9, 3, 20))).toBe('3 Sep 2026');
    expect(formatPeriod(iso(2026, 9, 3), iso(2026, 9, 4))).toBe('3 Sep 2026');
  });

  it('asks the assistant about the period', () => {
    expect(explainPrompt('24 Jul – 24 Oct 2026')).toBe(
      'Explain my Atlas insights for 24 Jul – 24 Oct 2026: what stands out, what improved or declined, and what you propose.',
    );
    expect(explainPrompt('All time')).toContain('for all time:');
  });
});

describe('atlas insights: numbers', () => {
  it('rounds kilometres to their size', () => {
    expect(formatKm(0)).toBe('0 km');
    expect(formatKm(87.44)).toBe('87.4 km');
    expect(formatKm(1234.4)).toBe('1,234 km');
  });

  it('rounds hours to their size', () => {
    expect(formatHours(0.75)).toBe('45 min');
    expect(formatHours(5.46)).toBe('5.5 h');
    expect(formatHours(124.4)).toBe('124 h');
  });

  it('writes a share', () => {
    expect(formatShare(3, 4)).toBe('75%');
    expect(formatShare(1, 0)).toBe('');
  });
});

describe('atlas insights: trend bars', () => {
  it('stacks the types per bucket in the legend order and fills empty buckets', () => {
    const bars = trendBars(
      [
        point(iso(2026, 1, 1), 'run', 10),
        point(iso(2026, 1, 1), 'ride', 40),
        point(iso(2026, 3, 1), 'hike', 12),
      ],
      'month',
      'distance',
    );
    expect(bars.map((b) => b.t)).toEqual([at(2026, 1, 1), at(2026, 2, 1), at(2026, 3, 1)]);
    expect(bars[0].segments).toEqual([
      { type: 'ride', value: 40 },
      { type: 'run', value: 10 },
    ]);
    expect(bars[0].total).toBe(50);
    expect(bars[1]).toEqual({ t: at(2026, 2, 1), segments: [], total: 0 });
    expect(bars[2].total).toBe(12);
  });

  it('measures time or counts', () => {
    const points = [
      point(iso(2026, 1, 5), 'ride', 40, 2, 1),
      point(iso(2026, 1, 12), 'ride', 20, 1.5, 2),
    ];
    expect(trendBars(points, 'week', 'time').map((b) => b.total)).toEqual([2, 1.5]);
    expect(trendBars(points, 'week', 'count').map((b) => b.total)).toEqual([1, 2]);
  });

  it('is empty without points', () => {
    expect(trendBars([], 'day', 'distance')).toEqual([]);
  });
});

describe('atlas insights: bucket labels', () => {
  it('names months with the year where it starts', () => {
    expect(bucketLabels([at(2025, 11, 1), at(2025, 12, 1), at(2026, 1, 1)], 'month')).toEqual([
      { label: 'Nov', year: '2025' },
      { label: 'Dec', year: null },
      { label: 'Jan', year: '2026' },
    ]);
  });

  it('names days and weeks by their date', () => {
    expect(bucketLabels([at(2026, 9, 3), at(2026, 9, 10)], 'week')).toEqual([
      { label: '3 Sep', year: '2026' },
      { label: '10 Sep', year: null },
    ]);
  });

  it('thins labels to what fits', () => {
    expect(labelStep(5, 6)).toBe(1);
    expect(labelStep(12, 6)).toBe(2);
    expect(labelStep(53, 6)).toBe(9);
  });
});

describe('atlas insights: area around a place', () => {
  it('is a square of about 600 m around the point', () => {
    const a = areaAround(32, 35);
    expect((a.maxLat - a.minLat) * 110_574).toBeCloseTo(600, 0);
    expect((a.maxLon - a.minLon) * 111_320 * Math.cos((32 * Math.PI) / 180)).toBeCloseTo(600, 0);
    expect((a.minLat + a.maxLat) / 2).toBeCloseTo(32, 6);
    expect((a.minLon + a.maxLon) / 2).toBeCloseTo(35, 6);
  });
});
