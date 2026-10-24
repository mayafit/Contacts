import {
  bucketFor,
  describeActivity,
  greatCircle,
  decodePolyline,
  formatDistance,
  formatDuration,
  MAX_SPAN_MS,
  MIN_SPAN_MS,
  panRange,
  thinTrack,
  trackLength,
  zoomRange,
} from '../geo';

describe('atlas geo helpers', () => {
  it('decodes Google polylines', () => {
    expect(decodePolyline('_p~iF~ps|U_ulLnnqC_mqNvxq`@')).toEqual([
      [38.5, -120.2],
      [40.7, -120.95],
      [43.252, -126.453],
    ]);
  });

  it('picks a bucket for the span', () => {
    expect(bucketFor(2 * 86_400_000)).toBe('hour');
    expect(bucketFor(60 * 86_400_000)).toBe('day');
    expect(bucketFor(400 * 86_400_000)).toBe('week');
    expect(bucketFor(5 * 365 * 86_400_000)).toBe('month');
    expect(bucketFor(20 * 365 * 86_400_000)).toBe('year');
  });

  it('formats distance and duration', () => {
    expect(formatDistance(850)).toBe('850 m');
    expect(formatDistance(13240)).toBe('13.2 km');
    expect(formatDuration(42 * 60)).toBe('42 min');
    expect(formatDuration(65 * 60)).toBe('1:05 h');
  });

  it('zooms around a moment and moves along time', () => {
    const day = 86_400_000;
    const view = { from: 0, to: 10 * day };
    // Zooming in around day 2 keeps day 2 at the same place on the strip
    const zoomed = zoomRange(view, 2 * day, 0.5);
    expect(zoomed).toEqual({ from: day, to: 6 * day });
    expect(zoomRange(view, 0, 1e-9).to - zoomRange(view, 0, 1e-9).from).toBe(MIN_SPAN_MS);
    expect(zoomRange(view, 0, 1e9).to - zoomRange(view, 0, 1e9).from).toBe(MAX_SPAN_MS);
    expect(panRange(view, -0.5)).toEqual({ from: -5 * day, to: 5 * day });
  });

  it('thins long tracks and measures them', () => {
    const track = Array.from({ length: 60_001 }, (_, i) => ({ lat: 32 + i * 1e-6, lon: 35 }));
    const thin = thinTrack(track, 25_000);
    expect(thin).toHaveLength(25_000);
    expect(thin[0]).toBe(track[0]);
    expect(thin[thin.length - 1]).toBe(track[track.length - 1]);
    expect(thinTrack(track.slice(0, 10), 25_000)).toHaveLength(10);
    // 0.06 degrees of latitude ≈ 6.67 km
    expect(trackLength(track)).toBeGreaterThan(6600);
    expect(trackLength(track)).toBeLessThan(6700);
  });

  it('draws flights as great-circle arcs', () => {
    // Tel Aviv to San Francisco flies far north, without jumping across the map
    const arc = greatCircle([32.0, 34.9], [37.6, -122.4]);
    expect(arc[0][0]).toBeCloseTo(32.0, 5);
    expect(arc[0][1]).toBeCloseTo(34.9, 5);
    expect(arc[arc.length - 1][0]).toBeCloseTo(37.6, 5);
    expect(arc[arc.length - 1][1]).toBeCloseTo(-122.4, 5);
    expect(Math.max(...arc.map((p) => p[0]))).toBeGreaterThan(65);
    arc.slice(1).forEach((p, i) => expect(Math.abs(p[1] - arc[i][1])).toBeLessThan(30));
    expect(greatCircle([32, 35], [32, 35])).toHaveLength(2);
  });

  it('describes an activity for its tooltip', () => {
    const text = describeActivity({
      id: 'f1',
      type: 'flight',
      name: null,
      startTime: '2026-08-26T20:12:00Z',
      endTime: '2026-08-27T02:29:00Z',
      distanceM: 5_794_000,
      source: 'google-timeline',
    } as Parameters<typeof describeActivity>[0]);
    expect(text).toMatch(
      /^Flight · .+ · 6:17 h · 5794(\.0)? km$|^Flight · .+ · 6:17 h · 5,794 km$/,
    );
  });
});
