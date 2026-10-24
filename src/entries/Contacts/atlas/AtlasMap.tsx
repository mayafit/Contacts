/**
 * @fileoverview World map with activity tracks, visits, the selected item and
 * an area selection tool
 * @module Contacts/atlas/AtlasMap
 */

import React, { useEffect, useMemo, useRef, useState } from 'react';
import {
  CircleMarker,
  MapContainer,
  Marker,
  Polyline,
  Rectangle,
  TileLayer,
  Tooltip,
  useMap,
  useMapEvents,
} from 'react-leaflet';
import L from 'leaflet';
import type { LatLngBoundsExpression, LeafletMouseEvent } from 'leaflet';
import './leaflet.css';
import type { ActivitySummary, PointOutput, VisitSummary } from './types';
import { FILTER_COLOR, HEALTH_COLOR, VISIT_COLOR, typeColor, isSet } from './types';
import { Box } from '@mui/material';
import AtlasLegend from './AtlasLegend';
import { decodePolyline, describeActivity, greatCircle } from './geo';
import {
  ATTRIBUTION,
  MAP_STYLES,
  englishLabels,
  loadMapStyle,
  saveMapStyle,
  type MapStyle,
} from './mapStyle';
import { IconButton, MenuItem, TextField, Tooltip as MuiTooltip } from '@mui/material';
import { CenterFocusStrong } from '@mui/icons-material';
import { logger } from '../../../shared/logger';

/** A rectangle on the map */
export interface Box {
  minLat: number;
  minLon: number;
  maxLat: number;
  maxLon: number;
}

/** Props for AtlasMap */
export interface AtlasMapProps {
  activities: ActivitySummary[];
  visits: VisitSummary[];
  /** Full track of the selected activity */
  selectedTrack?: PointOutput[];
  selectedId?: string | null;
  /** The visit open in the side panel */
  selectedVisitId?: string | null;
  /** Area filter */
  area: Box | null;
  /** Drawing a new area */
  drawing: boolean;
  onAreaDrawn: (box: Box) => void;
  onSelectActivity: (id: string) => void;
  onSelectVisit: (v: VisitSummary) => void;
  /** Fit the view to the results when this changes (bumped when results arrive) */
  fitKey: string;
  /** Highlighted point on the selected track (from the chart) */
  cursor?: [number, number] | null;
  /** Fit the view to the area filter when this changes (0: never) */
  focusArea?: number;
}

/** The small permanent label at the area filter's top-left corner */
const AREA_LABEL = L.divIcon({
  className: 'atlas-area-label',
  html: `<div style="display:inline-block;white-space:nowrap;padding:1px 6px;border-radius:4px;background:${FILTER_COLOR};color:#fff;font:600 11px/16px system-ui,sans-serif;box-shadow:0 1px 3px rgba(0,0,0,.4)">Area filter</div>`,
  iconSize: [70, 18],
  iconAnchor: [-4, -4],
});

/** Fits the map to the area filter when asked (focus changes) */
const FocusArea: React.FC<{ area: Box | null; focus: number }> = ({ area, focus }) => {
  const map = useMap();
  useEffect(() => {
    if (!focus || !area) {
      return;
    }
    map.fitBounds(
      [
        [area.minLat, area.minLon],
        [area.maxLat, area.maxLon],
      ],
      { padding: [30, 30] },
    );
    // Only when asked, not when the area changes
  }, [focus]);
  return null;
};

/**
 * Moves the map to what matters, without taking the view away from the user:
 * - the first results: fit to them;
 * - new results (a filter changed): only when none of them is in view;
 * - a selected activity: only when its track is not fully in view;
 * - "Show all results" (force): fit to the results.
 */
const FitResults: React.FC<{
  activities: ActivitySummary[];
  visits: VisitSummary[];
  fitKey: string;
  track?: PointOutput[];
  /** Bumped by "Show all results" */
  force: number;
}> = ({ activities, visits, fitKey, track, force }) => {
  const map = useMap();
  const fitted = useRef(false);
  const lastForce = useRef(force);
  useEffect(() => {
    const forced = force !== lastForce.current;
    lastForce.current = force;
    const view = map.getBounds();
    const boxes: L.LatLngBounds[] = [];
    activities.forEach((a) => {
      if (isSet(a.minLat) && isSet(a.maxLat) && isSet(a.minLon) && isSet(a.maxLon)) {
        boxes.push(L.latLngBounds([a.minLat, a.minLon], [a.maxLat, a.maxLon]));
      }
    });
    visits.forEach((v) => boxes.push(L.latLngBounds([v.lat, v.lon], [v.lat, v.lon])));
    const fit = (b: L.LatLngBounds) => {
      fitted.current = true;
      map.fitBounds(b, { padding: [30, 30], maxZoom: 15 });
    };

    if (!forced && track?.length) {
      const t = L.latLngBounds(track.map((p) => [p.lat, p.lon] as [number, number]));
      if (!view.contains(t)) {
        fit(t);
      }
      return;
    }
    if (boxes.length === 0) {
      return;
    }
    const all = boxes.reduce(
      (acc, b) => acc.extend(b),
      L.latLngBounds(boxes[0].getSouthWest(), boxes[0].getNorthEast()),
    );
    if (forced || !fitted.current || !boxes.some((b) => view.intersects(b))) {
      fit(all);
    }
    // Only when new results arrive, the selection changes or the user asks
  }, [fitKey, track, force]);
  return null;
};

/**
 * The base map: OpenFreeMap vector tiles drawn by MapLibre, with every label
 * in English. MapLibre is loaded only here (it is large). Without WebGL, or
 * when the style cannot be loaded, falls back to OpenStreetMap raster tiles
 * (labels in the local language).
 */
const BaseMap: React.FC<{ style: MapStyle }> = ({ style: choice }) => {
  const map = useMap();
  const [fallback, setFallback] = useState(false);
  const def = MAP_STYLES.find((m) => m.value === choice) ?? MAP_STYLES[0];
  useEffect(() => {
    let layer: L.Layer | null = null;
    let cancelled = false;
    setFallback(false);
    if (def.raster) {
      const tiles = L.tileLayer(def.url, { attribution: def.attribution, maxZoom: 19 }).addTo(map);
      tiles.bringToBack();
      layer = tiles;
      return () => {
        layer?.remove();
      };
    }
    (async () => {
      const [style] = await Promise.all([
        fetch(def.url).then((r) => {
          if (!r.ok) {
            throw new Error(`style ${r.status}`);
          }
          return r.json();
        }),
        import('maplibre-gl'),
        import('@maplibre/maplibre-gl-leaflet'),
      ]);
      if (cancelled) {
        return;
      }
      // The plugin adds L.maplibreGL to the shared Leaflet instance
      const create = (L as unknown as { maplibreGL?: (o: object) => L.Layer }).maplibreGL;
      if (!create) {
        throw new Error('MapLibre layer missing');
      }
      layer = create({ style: englishLabels(style), attribution: ATTRIBUTION }).addTo(map);
    })().catch((err) => {
      logger.warn(
        { context: 'AtlasMap/baseMap', metadata: { message: (err as Error)?.message } },
        'English base map unavailable, using OpenStreetMap tiles',
      );
      if (!cancelled) {
        setFallback(true);
      }
    });
    return () => {
      cancelled = true;
      layer?.remove();
    };
  }, [map, def]);
  return fallback ? (
    <TileLayer
      // OpenStreetMap tiles, darkened by CSS (see leaflet.css: .atlas-dark-tiles)
      url="https://tile.openstreetmap.org/{z}/{x}/{y}.png"
      attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
      className="atlas-dark-tiles"
      maxZoom={19}
    />
  ) : null;
};

/** Drag a rectangle to choose an area (while drawing) */
const AreaDraw: React.FC<{ drawing: boolean; onDone: (b: Box) => void }> = ({
  drawing,
  onDone,
}) => {
  const start = useRef<[number, number] | null>(null);
  const [current, setCurrent] = React.useState<Box | null>(null);
  const map = useMap();
  // The latest box, for finishing from a document-level mouseup
  const latest = useRef<Box | null>(null);
  latest.current = current;
  const finish = () => {
    if (!start.current) {
      return;
    }
    map.dragging.enable();
    start.current = null;
    const box = latest.current;
    if (box && box.maxLat - box.minLat > 1e-5) {
      onDone(box);
    }
    setCurrent(null);
  };
  useMapEvents({
    mousedown(e: LeafletMouseEvent) {
      if (!drawing) {
        return;
      }
      map.dragging.disable();
      start.current = [e.latlng.lat, e.latlng.lng];
    },
    mousemove(e: LeafletMouseEvent) {
      if (!drawing || !start.current) {
        return;
      }
      const [lat0, lon0] = start.current;
      setCurrent({
        minLat: Math.min(lat0, e.latlng.lat),
        maxLat: Math.max(lat0, e.latlng.lat),
        minLon: Math.min(lon0, e.latlng.lng),
        maxLon: Math.max(lon0, e.latlng.lng),
      });
    },
    mouseup: () => finish(),
    mouseout: () => finish(),
  });
  useEffect(() => {
    map.getContainer().style.cursor = drawing ? 'crosshair' : '';
    if (!drawing) {
      return undefined;
    }
    // Released outside the map (over the timeline or a panel)
    const up = () => finish();
    document.addEventListener('mouseup', up);
    return () => document.removeEventListener('mouseup', up);
  }, [drawing, map]);
  return current ? (
    <Rectangle
      bounds={[
        [current.minLat, current.minLon],
        [current.maxLat, current.maxLon],
      ]}
      pathOptions={{ color: FILTER_COLOR, weight: 2, dashArray: '6 4', fillOpacity: 0.08 }}
    />
  ) : null;
};

/**
 * The Atlas map (dark base map with English labels).
 * @param props - Component props
 * @returns Map element
 */
const AtlasMap: React.FC<AtlasMapProps> = ({
  activities,
  visits,
  selectedTrack,
  selectedId,
  selectedVisitId,
  area,
  drawing,
  onAreaDrawn,
  onSelectActivity,
  onSelectVisit,
  fitKey,
  cursor,
  focusArea = 0,
}) => {
  const [mapStyle, setMapStyle] = useState<MapStyle>(loadMapStyle);
  // Bumped by the "Show all results" button
  const [fitAll, setFitAll] = useState(0);
  const styleDef = MAP_STYLES.find((m) => m.value === mapStyle) ?? MAP_STYLES[0];
  // On a light map the selected track is drawn dark (white would vanish)
  const highlightColor = styleDef.light ? '#111827' : '#ffffff';

  // Flights as the arc from departure to arrival: the positions a phone records
  // in the air are rough guesses, and a straight line through them misleads
  const lines = useMemo(
    () =>
      activities
        .filter((a) => a.polyline)
        .map((a) => {
          const raw = decodePolyline(a.polyline!);
          const flight = a.type === 'flight' && raw.length > 1;
          return { a, flight, path: flight ? greatCircle(raw[0], raw[raw.length - 1]) : raw };
        }),
    [activities],
  );
  const selectedFlight = lines.find((l) => l.a.id === selectedId && l.flight);
  const crowded = lines.length > 150;
  const areaBounds: LatLngBoundsExpression | null = area
    ? [
        [area.minLat, area.minLon],
        [area.maxLat, area.maxLon],
      ]
    : null;

  const kinds = useMemo(
    () => [...activities.map((a) => a.type), ...(visits.length ? ['visit'] : [])],
    [activities, visits],
  );

  return (
    <Box sx={{ position: 'relative', height: '100%', width: '100%' }}>
      <MapContainer
        center={[31.8, 35.0]}
        zoom={7}
        worldCopyJump
        style={{ height: '100%', width: '100%', background: '#0e1116' }}
        preferCanvas
      >
        <BaseMap style={mapStyle} />
        <FitResults
          activities={activities}
          visits={visits}
          fitKey={fitKey}
          track={selectedTrack}
          force={fitAll}
        />
        <AreaDraw drawing={drawing} onDone={onAreaDrawn} />
        <FocusArea area={area} focus={focusArea} />
        {areaBounds && area && (
          <>
            <Rectangle
              bounds={areaBounds}
              pathOptions={{
                color: FILTER_COLOR,
                weight: 3,
                opacity: 0.95,
                fillColor: FILTER_COLOR,
                fillOpacity: 0.1,
                interactive: false,
              }}
            />
            <Marker
              position={[area.maxLat, area.minLon]}
              icon={AREA_LABEL}
              interactive={false}
              keyboard={false}
            />
          </>
        )}
        {lines.map(({ a, path, flight }) => (
          <Polyline
            key={a.id}
            positions={path}
            pathOptions={{
              color: typeColor(a.type),
              // Many tracks at once: thinner and lighter, so the streets stay readable
              weight: a.id === selectedId ? 5 : crowded ? 1.5 : flight ? 2 : 2.5,
              opacity: selectedId && a.id !== selectedId ? 0.3 : crowded ? 0.5 : 0.9,
              dashArray: flight ? '8 6' : undefined,
            }}
            eventHandlers={{ click: () => onSelectActivity(a.id) }}
          >
            <Tooltip sticky>{describeActivity(a)}</Tooltip>
          </Polyline>
        ))}
        {lines
          .filter((l) => l.flight)
          .flatMap(({ a, path }) => [
            { key: `${a.id}-from`, at: path[0], color: typeColor(a.type) },
            { key: `${a.id}-to`, at: path[path.length - 1], color: typeColor(a.type) },
          ])
          .map((m) => (
            // Airports: where one flight ends and the next begins
            <CircleMarker
              key={m.key}
              center={m.at}
              radius={3.5}
              interactive={false}
              pathOptions={{ color: '#0e1116', weight: 1, fillColor: m.color, fillOpacity: 1 }}
            />
          ))}
        {selectedFlight ? (
          <Polyline
            positions={selectedFlight.path}
            pathOptions={{ color: highlightColor, weight: 2.5, opacity: 0.95, dashArray: '8 6' }}
          />
        ) : (
          selectedTrack &&
          selectedTrack.length > 1 && (
            <Polyline
              positions={selectedTrack.map((p) => [p.lat, p.lon] as [number, number])}
              pathOptions={{ color: highlightColor, weight: 2.5, opacity: 0.95 }}
            />
          )
        )}
        {visits.map((v) => (
          <CircleMarker
            key={v.id}
            center={[v.lat, v.lon]}
            radius={v.id === selectedVisitId ? 8 : 5}
            pathOptions={{
              color: v.id === selectedVisitId ? '#ffffff' : VISIT_COLOR,
              fillColor: VISIT_COLOR,
              fillOpacity: 0.85,
              weight: v.id === selectedVisitId ? 2 : 1,
            }}
            eventHandlers={{ click: () => onSelectVisit(v) }}
          >
            <Tooltip>{v.placeName || 'Visit'}</Tooltip>
          </CircleMarker>
        ))}
        {cursor && (
          <CircleMarker
            center={cursor}
            radius={7}
            pathOptions={{ color: '#fff', fillColor: HEALTH_COLOR, fillOpacity: 1, weight: 2 }}
          />
        )}
      </MapContainer>
      <MuiTooltip title="Show all results">
        <IconButton
          size="small"
          aria-label="Show all results"
          onClick={() => setFitAll((n) => n + 1)}
          sx={{
            position: 'absolute',
            top: 84,
            left: 10,
            zIndex: 1000,
            width: 34,
            height: 34,
            borderRadius: '4px',
            bgcolor: '#ffffff',
            color: '#111827',
            border: '2px solid rgba(0,0,0,0.2)',
            '&:hover': { bgcolor: '#f3f4f6' },
          }}
        >
          <CenterFocusStrong fontSize="small" />
        </IconButton>
      </MuiTooltip>
      <Box sx={{ position: 'absolute', top: 10, right: 10, zIndex: 1000 }}>
        <TextField
          select
          size="small"
          value={mapStyle}
          onChange={(e) => {
            const next = e.target.value as MapStyle;
            saveMapStyle(next);
            setMapStyle(next);
          }}
          aria-label="Map style"
          SelectProps={{
            renderValue: (v) => `Map: ${MAP_STYLES.find((m) => m.value === v)?.label}`,
          }}
          sx={{
            bgcolor: 'rgba(14, 17, 22, 0.85)',
            borderRadius: 1,
            '& .MuiSelect-select': { fontSize: 12, py: 0.6 },
          }}
        >
          {MAP_STYLES.map((m) => (
            <MenuItem key={m.value} value={m.value} dense>
              {m.label}
            </MenuItem>
          ))}
        </TextField>
      </Box>
      {kinds.length > 0 && (
        <Box
          sx={{
            position: 'absolute',
            left: 10,
            bottom: 24,
            zIndex: 1000,
            maxWidth: 'calc(100% - 80px)',
            px: 1,
            py: 0.5,
            borderRadius: 1,
            bgcolor: 'rgba(14, 17, 22, 0.85)',
            pointerEvents: 'none',
          }}
        >
          <AtlasLegend kinds={kinds} />
        </Box>
      )}
    </Box>
  );
};

export default AtlasMap;
