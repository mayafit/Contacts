import { SOURCE_LABELS, SPORTS_TRACKER_ERRORS, describeFieldSources, sourceLabel } from '../types';

describe('atlas fusion helpers', () => {
  it('names the known sources', () => {
    expect(sourceLabel('sports-tracker')).toBe('Sports Tracker');
    expect(sourceLabel('google-timeline')).toBe('Google Timeline');
    expect(sourceLabel('google-fit')).toBe('Google Fit');
    expect(sourceLabel('fitbit')).toBe('Fitbit');
    expect(sourceLabel('gpx')).toBe('GPX file');
    expect(sourceLabel('tcx')).toBe('TCX file');
    expect(sourceLabel('manual')).toBe('Manual');
    expect(Object.keys(SOURCE_LABELS)).toHaveLength(7);
  });

  it('shows unknown sources as they are', () => {
    expect(sourceLabel('strava')).toBe('strava');
    expect(sourceLabel('')).toBe('Unknown');
    expect(sourceLabel(null)).toBe('Unknown');
    expect(sourceLabel(undefined)).toBe('Unknown');
  });

  it('describes where the values come from', () => {
    expect(
      describeFieldSources({
        distanceM: 'sports-tracker',
        type: 'sports-tracker',
        heartRate: 'fitbit',
      }),
    ).toBe('Distance: Sports Tracker · Type: Sports Tracker · Heart rate: Fitbit');
  });

  it('lists a label once and keeps unknown fields', () => {
    expect(describeFieldSources({ heartRate: 'fitbit', avgHeartRate: 'google-fit' })).toBe(
      'Heart rate: Fitbit',
    );
    expect(describeFieldSources({ cadence: 'gpx' })).toBe('cadence: GPX file');
  });

  it('is empty without field sources', () => {
    expect(describeFieldSources({})).toBe('');
    expect(describeFieldSources(null)).toBe('');
    expect(describeFieldSources(undefined)).toBe('');
  });

  it('knows the Sports Tracker error codes', () => {
    expect(SPORTS_TRACKER_ERRORS).toEqual({
      loginRefused: 'SPORTS_TRACKER_LOGIN_REFUSED',
      keyInvalid: 'SPORTS_TRACKER_KEY_INVALID',
      keyExpired: 'SPORTS_TRACKER_KEY_EXPIRED',
    });
  });
});
