import { alsoNamedText, defaultNearbySelection, identifyMark, identifyNote } from '../placeText';
import type { NearbyPlace } from '../types';

const nearby = (id: string): NearbyPlace => ({
  placeId: id,
  name: 'Place',
  lat: 31.8,
  lon: 34.7,
  distanceM: 40,
  visits: 3,
  identifyState: 'pending',
});

describe('place identity text', () => {
  it('says how a place got its identity', () => {
    expect(identifyNote('auto', 'lookup')).toBe('Identified automatically');
    expect(identifyNote('auto', null)).toBe('Identified automatically');
    expect(identifyNote('auto', 'nearby')).toBe('Identified automatically from a nearby place');
    expect(identifyNote('ambiguous')).toBe('Which one is it?');
    expect(identifyNote('pending')).toBe('Not identified yet');
    expect(identifyNote('user')).toBeNull();
    expect(identifyNote('none')).toBeNull();
    expect(identifyNote(undefined)).toBeNull();
  });

  it('marks ambiguous and automatic places', () => {
    expect(identifyMark('ambiguous')).toBe('?');
    expect(identifyMark('auto')).toBe('auto');
    expect(identifyMark('user')).toBeNull();
    expect(identifyMark(null)).toBeNull();
  });

  it('checks a few nearby suggestions, none of many', () => {
    expect(defaultNearbySelection([])).toEqual([]);
    expect(defaultNearbySelection([nearby('a'), nearby('b'), nearby('c')])).toEqual([
      'a',
      'b',
      'c',
    ]);
    expect(defaultNearbySelection([nearby('a'), nearby('b'), nearby('c'), nearby('d')])).toEqual(
      [],
    );
  });

  it('counts the nearby places named', () => {
    expect(alsoNamedText(1)).toBe('Also named 1 nearby place');
    expect(alsoNamedText(2)).toBe('Also named 2 nearby places');
  });
});

describe('map mode choice', () => {
  it('defaults to items and remembers density', () => {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const m = require('../mapStyle');
    localStorage.clear();
    expect(m.loadMapMode()).toBe('items');
    m.saveMapMode('density');
    expect(m.loadMapMode()).toBe('density');
    localStorage.setItem('atlas.mapMode', 'nonsense');
    expect(m.loadMapMode()).toBe('items');
  });
});
