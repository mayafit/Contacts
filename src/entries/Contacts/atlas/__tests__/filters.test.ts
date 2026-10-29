import {
  formatAreaCentre,
  formatAreaFilter,
  formatAreaSize,
  formatResultCount,
  formatRoadFilter,
  formatSpan,
  formatTimeFilter,
  formatTypesFilter,
  parseRoadQuery,
  visibleRoads,
} from '../filters';
import type { SearchResult } from '../types';

const at = (y: number, mo: number, d: number, h = 0, mi = 0) =>
  new Date(y, mo - 1, d, h, mi).getTime();

const result = (shownA: number, totalA: number, shownV: number, totalV: number): SearchResult =>
  ({
    activities: Array.from({ length: shownA }, () => ({})),
    visits: Array.from({ length: shownV }, () => ({})),
    totalActivities: totalA,
    totalVisits: totalV,
  }) as unknown as SearchResult;

describe('atlas filter bar texts', () => {
  it('shows a range of days without times', () => {
    expect(formatTimeFilter({ from: at(2026, 9, 3), to: at(2026, 9, 10) })).toBe(
      '3 Sep 2026 – 10 Sep 2026 (7 days)',
    );
  });

  it('shows times for ranges under two days', () => {
    expect(formatTimeFilter({ from: at(2026, 9, 3, 14), to: at(2026, 9, 3, 16, 30) })).toBe(
      '3 Sep 2026 14:00–16:30 (2.5 h)',
    );
    expect(formatTimeFilter({ from: at(2026, 9, 3, 20), to: at(2026, 9, 4, 9, 5) })).toBe(
      '3 Sep 2026 20:00 – 4 Sep 2026 09:05 (13.1 h)',
    );
  });

  it('names the length of a range', () => {
    expect(formatSpan(45 * 60_000)).toBe('45 min');
    expect(formatSpan(10_000)).toBe('1 min');
    expect(formatSpan(5 * 3_600_000)).toBe('5 h');
    expect(formatSpan(36 * 3_600_000)).toBe('36 h');
    expect(formatSpan(2 * 86_400_000)).toBe('2 days');
  });

  it('describes an area by size and centre', () => {
    const area = { minLat: 32.8, maxLat: 32.9, minLon: 34.95, maxLon: 35.09 };
    expect(formatAreaSize(area)).toBe('13 × 11 km');
    expect(formatAreaCentre(area)).toBe('32.85°N 35.02°E');
    expect(formatAreaFilter(area)).toBe('Area 13 × 11 km · 32.85°N 35.02°E');
  });

  it('uses decimals for small areas and meters under a kilometre', () => {
    expect(formatAreaSize({ minLat: 0, maxLat: 0.01, minLon: 0, maxLon: 0.02 })).toBe(
      '2.2 × 1.1 km',
    );
    expect(formatAreaSize({ minLat: 0, maxLat: 0.005, minLon: 0, maxLon: 0.004 })).toBe(
      '445 × 553 m',
    );
  });

  it('marks the southern and western hemispheres', () => {
    expect(formatAreaCentre({ minLat: -34, maxLat: -33, minLon: -59, maxLon: -58 })).toBe(
      '33.50°S 58.50°W',
    );
  });

  it('lists the types by name', () => {
    expect(formatTypesFilter(['ride', 'hike'])).toBe('Ride, Hike');
    expect(formatTypesFilter(['kayak'])).toBe('kayak');
  });

  it('counts the results, with "of N" only when more were found than returned', () => {
    expect(formatResultCount(result(12, 12, 30, 30))).toBe('12 activities · 30 visits');
    expect(formatResultCount(result(500, 812, 30, 30))).toBe('500 of 812 activities · 30 visits');
    expect(formatResultCount(result(1, 1, 1, 1))).toBe('1 activity · 1 visit');
    expect(formatResultCount(result(0, 0, 0, 0))).toBe('0 activities · 0 visits');
  });
});

describe('road filter', () => {
  it('labels a route number as a road and a street by its name', () => {
    expect(formatRoadFilter('431')).toBe('Road 431');
    expect(formatRoadFilter(' 20A ')).toBe('Road 20A');
    expect(formatRoadFilter('איילון צפון')).toBe('איילון צפון');
  });

  it('reads "road 431" typed into the search box', () => {
    expect(parseRoadQuery('road 431')).toBe('431');
    expect(parseRoadQuery('  Route 4 ')).toBe('4');
    expect(parseRoadQuery('כביש 431')).toBe('431');
    expect(parseRoadQuery('highway 6')).toBe('6');
    expect(parseRoadQuery('דרך נמיר')).toBeNull();
    expect(parseRoadQuery('road אבן גבירול')).toBe('אבן גבירול');
    expect(parseRoadQuery('431')).toBeNull();
    expect(parseRoadQuery('Roadrunner')).toBeNull();
    expect(parseRoadQuery('cafe road')).toBeNull();
  });

  it('shows route numbers and the longest streets first, in travel order', () => {
    const r = (name: string, ref: boolean, m: number) => ({ name, ref, m });
    const roads = [
      r('a', false, 900),
      r('4', true, 100),
      r('b', false, 200),
      r('c', false, 950),
      r('431', true, 50),
    ];
    expect(visibleRoads(roads, 10)).toEqual({ shown: roads, hidden: 0 });
    const { shown, hidden } = visibleRoads(roads, 3);
    expect(shown.map((x) => x.name)).toEqual(['4', 'c', '431']);
    expect(hidden).toBe(2);
  });
});
