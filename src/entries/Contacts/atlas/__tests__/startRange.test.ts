import { DEFAULT_START_RANGE, loadStartRange, saveStartRange, startRangeOf } from '../startRange';

describe('what Atlas opens with', () => {
  it('covers the chosen time up to now', () => {
    const now = new Date(2026, 9, 24, 12, 0).getTime();
    expect(startRangeOf('week', now)).toEqual({ from: now - 7 * 86_400_000, to: now });
    expect(new Date(startRangeOf('3months', now)!.from).getMonth()).toBe(6);
    expect(new Date(startRangeOf('ytd', now)!.from)).toEqual(new Date(2026, 0, 1));
    expect(new Date(startRangeOf('year', now)!.from).getFullYear()).toBe(2025);
    expect(startRangeOf('all', now)).toBeNull();
  });

  it('remembers the choice, with last 3 months as the default', () => {
    localStorage.clear();
    expect(loadStartRange()).toBe(DEFAULT_START_RANGE);
    expect(DEFAULT_START_RANGE).toBe('3months');
    saveStartRange('ytd');
    expect(loadStartRange()).toBe('ytd');
    localStorage.setItem('atlas.startRange', 'nonsense');
    expect(loadStartRange()).toBe('3months');
  });
});
