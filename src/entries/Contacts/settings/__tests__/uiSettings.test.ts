import { createContactsTheme } from '../../theme/contactsTheme';
import { pointerCss, cursorValue } from '../pointer';
import {
  DEFAULT_UI,
  currentUi,
  dashArray,
  fs,
  mergeUi,
  onUiChange,
  setCurrentUi,
  textVariables,
} from '../uiSettings';
import { ACTIVITY_TYPES, VISIT_COLOR, typeColor } from '../../atlas/types';
import { pieceStyle } from '../../atlas/routeGeometry';

describe('the appearance settings', () => {
  afterEach(() => setCurrentUi(DEFAULT_UI));

  it('are today’s Echo by default, from nothing or from garbage', () => {
    expect(mergeUi(undefined)).toEqual(DEFAULT_UI);
    expect(mergeUi('nonsense')).toEqual(DEFAULT_UI);
    expect(mergeUi({ text: [1, 2], map: null })).toEqual(DEFAULT_UI);
  });

  it('take what is valid, clamp numbers into range, check colours and drop unknown keys', () => {
    const ui = mergeUi({
      text: { body: 18, small: 2, headings: '150', charts: 'big' },
      pointer: { size: 32 },
      map: { visit: 500, grab: 6 },
      colors: { ride: '#ABCDEF', hike: 'green', visit: '#123' },
      routes: {
        types: { drive: { width: 4, opacity: 7, pattern: 'dotted' }, flight: { pattern: 'wavy' } },
        inferred: { pattern: 'solid' },
      },
      charts: { zones: ['#000000'] },
      future: { x: 1 },
    });

    expect(ui.text).toMatchObject({ body: 18, small: 9, headings: 150, charts: 11 });
    expect(ui.pointer.size).toBe(32);
    expect(ui.map).toMatchObject({ visit: 25, grab: 6, photo: 4 });
    expect(ui.colors).toMatchObject({ ride: '#abcdef', hike: '#5bd16b', visit: '#e879f9' });
    expect(ui.routes.types.drive).toEqual({ width: 4, opacity: 1, pattern: 'dotted' });
    expect(ui.routes.types.flight.pattern).toBe('dashed');
    expect(ui.routes.inferred).toEqual({ width: 4, pattern: 'solid' });
    expect(ui.charts.zones).toEqual(['#000000', '#5bd16b', '#f2d15c', '#ff8a3d', '#ff4d6d']);
    expect((ui as unknown as Record<string, unknown>).future).toBeUndefined();
    expect(mergeUi({ pointer: { size: 99 } }).pointer.size).toBe('system');
  });

  it('draw patterns that keep today’s look at today’s widths and grow with wider lines', () => {
    expect(dashArray('dashed', 2)).toBe('8 6');
    expect(dashArray('dashed', 4)).toBe('8 6');
    expect(dashArray('dashed', 8)).toBe('16 12');
    expect(dashArray('dotted', 1.5)).toBe('1 5');
    expect(dashArray('dotted', 5)).toBe('3 10');
    expect(dashArray('solid', 5)).toBeUndefined();
  });

  it('size text outside the theme by its kind', () => {
    expect(fs(11)).toBe('calc(11px * var(--ui-small, 1))');
    expect(fs(13.5)).toBe('calc(13.5px * var(--ui-small, 1))');
    expect(fs(15)).toBe('calc(15px * var(--ui-body, 1))');
    expect(fs(20)).toBe('calc(20px * var(--ui-body, 1))');
    expect(fs(24)).toBe('calc(24px * var(--ui-heading, 1))');
    expect(fs.chart(11)).toBe('calc(11px * var(--ui-chart, 1))');
    expect(fs.map(11)).toBe('calc(11px * var(--ui-map, 1))');
    expect(fs.nav(11.5)).toBe('calc(11.5px * var(--ui-nav, 1))');
    expect(textVariables({ ...DEFAULT_UI.text, small: 19.5, headings: 150 })).toMatchObject({
      '--ui-small': '1.5',
      '--ui-body': '1',
      '--ui-heading': '1.5',
    });
  });

  it('give the theme its typography', () => {
    const t = createContactsTheme({
      ...DEFAULT_UI.text,
      body: 18,
      small: 15,
      headings: 120,
    }).typography;
    expect(t.body1.fontSize).toBe('18px');
    expect(t.body2.fontSize).toBe('17.5px');
    expect(t.caption.fontSize).toBe('15px');
    expect(t.h1.fontSize).toBe('30px');
    expect(createContactsTheme().typography.body1.fontSize).toBe('15px');
  });

  it('draw Echo’s own pointer, or leave the system’s', () => {
    expect(pointerCss('system')).toBeNull();
    const css = pointerCss(48)!;
    expect(css).toContain('html, body { cursor: url("data:image/svg+xml,');
    expect(css).toContain('.leaflet-interactive');
    expect(css).toContain('.atlas-crosshair');
    expect(cursorValue('arrow', 48, 'default')).toMatch(/ 6 5, default$/);
    expect(decodeURIComponent(cursorValue('hand', 32, 'pointer'))).toContain(
      'width="32" height="32"',
    );
  });

  it('reach code outside React: colours, styles and the store', () => {
    const seen: string[] = [];
    const off = onUiChange((s) => seen.push(s.colors.ride));
    setCurrentUi(
      mergeUi({
        colors: { ride: '#010203', visit: '#0a0b0c', gap: '#111111' },
        routes: { gap: { width: 3 } },
      }),
    );
    off();

    expect(seen).toEqual(['#010203']);
    expect(currentUi().colors.ride).toBe('#010203');
    expect(typeColor('ride')).toBe('#010203');
    expect(ACTIVITY_TYPES.ride.color).toBe('#010203');
    expect(VISIT_COLOR).toBe('#0a0b0c');
    expect(pieceStyle('gap', '#fff')).toEqual({
      color: '#111111',
      weight: 3,
      opacity: 0.55,
      dashArray: '1.8 6',
    });
  });
});
