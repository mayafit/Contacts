import {
  bucketFor,
  decodePolyline,
  formatDistance,
  formatDuration,
  thinTrack,
  trackLength,
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
});
