/**
 * @fileoverview The density map: every matching track as faint, blurred lines that build
 * up where they overlap, and visits as a heat map — drawn by MapLibre (WebGL) on a
 * transparent layer above any base map
 * @module Contacts/atlas/DensityLayer
 */

import React, { useEffect, useRef, useState } from 'react';
import { useMap } from 'react-leaflet';
import L from 'leaflet';
import type { GeoJSONSource, Map as GlMap } from 'maplibre-gl';
import type { DensityData } from './types';
import { tracksGeoJson, typeColorExpression, visitHeatColor, visitsGeoJson } from './densityGeo';
import { useUi } from '../settings/SettingsProvider';
import { logger } from '../../../shared/logger';

/** Props for DensityLayer */
export interface DensityLayerProps {
  /** What to draw; null draws nothing */
  data: DensityData | null;
}

/** Above the base map, below Leaflet's own shapes and markers; clicks pass through */
const PANE = 'atlasDensity';
const TRACKS = 'density-tracks';
const VISITS = 'density-visits';

/** A style with nothing in it: the layer is transparent except for what is added */
const EMPTY_STYLE = { version: 8, sources: {}, layers: [] };

/** Adds the sources and the layers (empty) to the MapLibre map */
const addLayers = (gl: GlMap): void => {
  gl.addSource(TRACKS, { type: 'geojson', data: tracksGeoJson([]) });
  gl.addSource(VISITS, { type: 'geojson', data: visitsGeoJson([]) });
  gl.addLayer({
    id: TRACKS,
    type: 'line',
    source: TRACKS,
    layout: { 'line-cap': 'round', 'line-join': 'round' },
    paint: {
      'line-color': typeColorExpression() as never,
      'line-opacity': 0.16,
      'line-width': ['interpolate', ['linear'], ['zoom'], 3, 0.8, 10, 1.6, 15, 3.5],
      'line-blur': ['interpolate', ['linear'], ['zoom'], 3, 0.4, 15, 2],
    },
  });
  gl.addLayer({
    id: VISITS,
    type: 'heatmap',
    source: VISITS,
    paint: {
      'heatmap-weight': ['get', 'w'],
      'heatmap-intensity': ['interpolate', ['linear'], ['zoom'], 0, 0.6, 9, 1.2, 15, 2.5],
      'heatmap-radius': ['interpolate', ['linear'], ['zoom'], 0, 4, 9, 12, 15, 28],
      'heatmap-color': visitHeatColor() as never,
      'heatmap-opacity': 0.85,
    },
  });
};

/**
 * Draws the density map while mounted (lazy-loads MapLibre). Without WebGL it draws
 * nothing and logs why.
 * @param props - Component props
 * @returns Nothing (draws on the map)
 */
const DensityLayer: React.FC<DensityLayerProps> = ({ data }) => {
  const map = useMap();
  const glRef = useRef<GlMap | null>(null);
  const [ready, setReady] = useState(false);

  useEffect(() => {
    let layer: L.Layer | null = null;
    let cancelled = false;
    if (!map.getPane(PANE)) {
      const pane = map.createPane(PANE);
      pane.style.zIndex = '350';
      pane.style.pointerEvents = 'none';
    }
    (async () => {
      await Promise.all([import('maplibre-gl'), import('@maplibre/maplibre-gl-leaflet')]);
      if (cancelled) {
        return;
      }
      // The plugin adds L.maplibreGL to the shared Leaflet instance
      const create = (L as unknown as { maplibreGL?: (o: object) => L.Layer }).maplibreGL;
      if (!create) {
        throw new Error('MapLibre layer missing');
      }
      layer = create({ style: EMPTY_STYLE, pane: PANE, interactive: false }).addTo(map);
      const gl = (layer as unknown as { getMaplibreMap: () => GlMap }).getMaplibreMap();
      const init = () => {
        if (cancelled) {
          return;
        }
        addLayers(gl);
        glRef.current = gl;
        setReady(true);
      };
      if (gl.isStyleLoaded()) {
        init();
      } else {
        gl.once('load', init);
      }
    })().catch((err) => {
      logger.warn(
        { context: 'AtlasMap/density', metadata: { message: (err as Error)?.message } },
        'Density map unavailable',
      );
    });
    return () => {
      cancelled = true;
      glRef.current = null;
      setReady(false);
      layer?.remove();
    };
  }, [map]);

  useEffect(() => {
    const gl = glRef.current;
    if (!ready || !gl) {
      return;
    }
    (gl.getSource(TRACKS) as GeoJSONSource | undefined)?.setData(tracksGeoJson(data?.tracks ?? []));
    (gl.getSource(VISITS) as GeoJSONSource | undefined)?.setData(visitsGeoJson(data?.visits ?? []));
  }, [ready, data]);

  // The Owner's colours (AD-41): the paint follows them
  const ui = useUi();
  useEffect(() => {
    const gl = glRef.current;
    if (!ready || !gl) {
      return;
    }
    gl.setPaintProperty(TRACKS, 'line-color', typeColorExpression() as never);
    gl.setPaintProperty(VISITS, 'heatmap-color', visitHeatColor() as never);
  }, [ready, ui.colors]);

  return null;
};

export default DensityLayer;
