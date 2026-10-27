import {
  trackTypes,
  tracksGeoJson,
  typeColorExpression,
  visitHeatColor,
  visitWeight,
  visitsGeoJson,
  withAlpha,
} from '../densityGeo';
import { VISIT_COLOR, typeColor } from '../types';

describe('density map data', () => {
  it('decodes tracks into GeoJSON lines ([lon, lat]) with their type', () => {
    const fc = tracksGeoJson([
      { type: 'ride', polyline: '_p~iF~ps|U_ulLnnqC_mqNvxq`@' },
      { type: 'walk', polyline: '_p~iF~ps|U' },
      { type: 'run', polyline: '' },
    ]);
    expect(fc.type).toBe('FeatureCollection');
    expect(fc.features).toHaveLength(1);
    expect(fc.features[0].properties).toEqual({ type: 'ride' });
    expect(fc.features[0].geometry).toEqual({
      type: 'LineString',
      coordinates: [
        [-120.2, 38.5],
        [-120.95, 40.7],
        [-126.453, 43.252],
      ],
    });
  });

  it('draws flights as the arc from departure to arrival', () => {
    const fc = tracksGeoJson([{ type: 'flight', polyline: '_p~iF~ps|U_ulLnnqC_mqNvxq`@' }]);
    const coords = fc.features[0].geometry.coordinates;
    expect(coords.length).toBeGreaterThan(3);
    expect(coords[0][0]).toBeCloseTo(-120.2);
    expect(coords[0][1]).toBeCloseTo(38.5);
    expect(coords[coords.length - 1][0]).toBeCloseTo(-126.453);
    expect(coords[coords.length - 1][1]).toBeCloseTo(43.252);
  });

  it('weighs visits by time spent, log-scaled and bounded', () => {
    expect(visitWeight(0)).toBe(0.05);
    expect(visitWeight(-3)).toBe(0.05);
    expect(visitWeight(Number.NaN)).toBe(0.05);
    expect(visitWeight(24)).toBeCloseTo(1);
    expect(visitWeight(500)).toBe(1);
    const hour = visitWeight(1);
    const eight = visitWeight(8);
    expect(hour).toBeGreaterThan(0.05);
    expect(eight).toBeGreaterThan(hour);
    // Log-scaled: 8 h weighs far less than 8 times an hour
    expect(eight).toBeLessThan(hour * 4);
  });

  it('makes weighted GeoJSON points of visits', () => {
    const fc = visitsGeoJson([
      [31.8, 34.7, 2],
      [Number.NaN, 34.7, 1],
    ]);
    expect(fc.features).toHaveLength(1);
    expect(fc.features[0].geometry).toEqual({ type: 'Point', coordinates: [34.7, 31.8] });
    expect(fc.features[0].properties.w).toBeCloseTo(visitWeight(2));
  });

  it('colours each type as on the map', () => {
    const expr = typeColorExpression();
    expect(expr.slice(0, 2)).toEqual(['match', ['get', 'type']]);
    const i = expr.indexOf('ride');
    expect(expr[i + 1]).toBe(typeColor('ride'));
    expect(expr[expr.length - 1]).toBe(typeColor('other'));
    // label/value pairs plus the input, the operator and the fallback
    expect(expr.length % 2).toBe(1);
  });

  it('ramps the heat map from transparent to the visit colour', () => {
    expect(withAlpha('#e879f9', 0.5)).toBe('rgba(232,121,249,0.5)');
    const ramp = visitHeatColor();
    expect(ramp[0]).toBe('interpolate');
    expect(ramp[4]).toBe(withAlpha(VISIT_COLOR, 0));
  });

  it('lists the track types once', () => {
    expect(
      trackTypes([
        { type: 'ride', polyline: '' },
        { type: 'ride', polyline: '' },
        { type: '', polyline: '' },
      ]),
    ).toEqual(['ride', 'other']);
  });
});
