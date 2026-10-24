/**
 * @fileoverview What time Atlas opens with: last week, month, 3 months… or
 * everything (the user's choice, remembered in this browser)
 * @module Contacts/atlas/startRange
 */

/** The choices */
export type StartRange = 'week' | 'month' | '3months' | '6months' | 'ytd' | 'year' | 'all';

/** Choices in menu order, with their labels */
export const START_RANGES: { value: StartRange; label: string }[] = [
  { value: 'week', label: 'Last week' },
  { value: 'month', label: 'Last month' },
  { value: '3months', label: 'Last 3 months' },
  { value: '6months', label: 'Last 6 months' },
  { value: 'ytd', label: 'This year so far' },
  { value: 'year', label: 'Last 12 months' },
  { value: 'all', label: 'Everything' },
];

/** Used until the user picks one */
export const DEFAULT_START_RANGE: StartRange = '3months';

const KEY = 'atlas.startRange';

/**
 * The time a choice covers, ending now; null for everything.
 * @param choice - The choice
 * @param now - Current time (ms)
 * @returns { from, to } in ms, or null
 */
export const startRangeOf = (
  choice: StartRange,
  now: number,
): { from: number; to: number } | null => {
  const d = new Date(now);
  switch (choice) {
    case 'week':
      return { from: now - 7 * 86_400_000, to: now };
    case 'month':
      d.setMonth(d.getMonth() - 1);
      return { from: d.getTime(), to: now };
    case '3months':
      d.setMonth(d.getMonth() - 3);
      return { from: d.getTime(), to: now };
    case '6months':
      d.setMonth(d.getMonth() - 6);
      return { from: d.getTime(), to: now };
    case 'ytd':
      return { from: new Date(d.getFullYear(), 0, 1).getTime(), to: now };
    case 'year':
      d.setFullYear(d.getFullYear() - 1);
      return { from: d.getTime(), to: now };
    default:
      return null;
  }
};

/** The remembered choice (the default when none, or storage is unavailable) */
export const loadStartRange = (): StartRange => {
  try {
    const v = localStorage.getItem(KEY);
    return START_RANGES.some((r) => r.value === v) ? (v as StartRange) : DEFAULT_START_RANGE;
  } catch {
    return DEFAULT_START_RANGE;
  }
};

/** Remembers the choice in this browser */
export const saveStartRange = (choice: StartRange): void => {
  try {
    localStorage.setItem(KEY, choice);
  } catch {
    // private window or blocked storage: the choice applies to this visit only
  }
};
