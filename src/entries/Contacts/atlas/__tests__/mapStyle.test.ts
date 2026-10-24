import { ENGLISH_NAME, englishLabels } from '../mapStyle';

describe('map style', () => {
  it('shows names in English and leaves road numbers alone', () => {
    const style = englishLabels({
      layers: [
        {
          type: 'symbol',
          layout: {
            'text-field': [
              'case',
              ['has', 'name:nonlatin'],
              ['concat', ['get', 'name:latin'], '\n', ['get', 'name:nonlatin']],
              ['get', 'name'],
            ],
          },
        },
        { type: 'symbol', layout: { 'text-field': ['to-string', ['get', 'ref']] } },
        { type: 'line', layout: { 'line-cap': 'round' } },
      ],
    });
    expect(style.layers[0].layout!['text-field']).toBe(ENGLISH_NAME);
    expect(style.layers[1].layout!['text-field']).toEqual(['to-string', ['get', 'ref']]);
    expect(style.layers[2].layout).toEqual({ 'line-cap': 'round' });
  });
});

describe('base map choice', () => {
  it('defaults to streets and remembers a choice', () => {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const m = require('../mapStyle');
    localStorage.clear();
    expect(m.loadMapStyle()).toBe('streets');
    m.saveMapStyle('satellite');
    expect(m.loadMapStyle()).toBe('satellite');
    localStorage.setItem('atlas.mapStyle', 'nonsense');
    expect(m.loadMapStyle()).toBe('streets');
    expect(m.MAP_STYLES.find((s: { value: string }) => s.value === 'streets').light).toBe(true);
  });
});
