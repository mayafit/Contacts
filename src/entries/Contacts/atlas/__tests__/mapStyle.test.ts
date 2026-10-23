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
