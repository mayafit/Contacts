/**
 * @fileoverview World map with activity tracks, visits, the selected item and
 * an area selection tool; or, in density mode, how often each way and place was visited
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
import type {
  ActivitySummary,
  AtlasQuery,
  DensityData,
  PhotoMapData,
  PhotoSummary,
  PointOutput,
  RoutePiece,
  VisitSummary,
} from './types';
import { PHOTO_COLOR, photoCountText, spread, takenText } from './photos';
import { FILTER_COLOR, HEALTH_COLOR, VISIT_COLOR, typeColor, isSet } from './types';
import { Box } from '@mui/material';
import AtlasLegend from './AtlasLegend';
import { decodePolyline, describeActivity, greatCircle } from './geo';
import {
  ATTRIBUTION,
  MAP_STYLES,
  englishLabels,
  loadMapMode,
  loadMapPhotos,
  loadMapStyle,
  saveMapMode,
  saveMapPhotos,
  saveMapStyle,
  type MapMode,
  type MapStyle,
} from './mapStyle';
import {
  CircularProgress,
  IconButton,
  MenuItem,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Tooltip as MuiTooltip,
  Typography,
} from '@mui/material';
import { CenterFocusStrong, PhotoCamera } from '@mui/icons-material';
import { useUi } from '../settings/SettingsProvider';
import { dashArray } from '../settings/uiSettings';
import type { StyledType, UiSettings } from '../settings/uiSettings';
import { logger } from '../../../shared/logger';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import DensityLayer from './DensityLayer';
import { trackTypes } from './densityGeo';
import { identifyMark } from './placeText';
import { tokens } from '../theme/contactsTheme';
import {
  hasInferred,
  inferredCasingStyle,
  pieceStyle,
  splitPieces,
  type PieceLine,
} from './routeGeometry';
import { fs } from '../settings/uiSettings';

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
  /**
   * The selected activity's matched route, drawn instead of its track (the track's
   * points then show as small dots when zoomed in); null draws the recorded track
   */
  selectedRoute?: { id: string; polyline: string; pieces?: RoutePiece[] | null } | null;
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
  /** The filters, for the density map (fetched only in density mode) */
  query?: AtlasQuery;
  /** Bumped when the data changed (the density map loads again) */
  reloadKey?: number;
  /** Photos of the open activity or place, shown as thumbnails */
  photos?: PhotoSummary[];
  /** Opens the photo viewer on a list at a photo */
  onOpenPhoto?: (photos: PhotoSummary[], index: number) => void;
  /** Points being marked for a rebuild (AD-38), [lat, lon] in order; null when not marking */
  marking?: [number, number][] | null;
  /** Adds a marked point (a click on the map while marking) */
  onMark?: (lat: number, lon: number) => void;
  /** Removes a marked point (a click on it) */
  onUnmark?: (index: number) => void;
  /** The selected flight's path: its flown track or the great circle between its airports (AD-42) */
  flightPath?: [number, number][] | null;
}

/** A numbered marked point, at the Owner's size and colour (AD-41) */
const markIcon = (n: number, size: number, color: string) =>
  L.divIcon({
    className: '',
    iconSize: [size, size],
    iconAnchor: [size / 2, size / 2],
    html: `<div style="width:${size}px;height:${size}px;border-radius:50%;background:${color};color:#fff;border:2px solid #fff;box-shadow:0 0 2px rgba(0,0,0,.6);font:600 calc(11px * var(--ui-map, 1))/${size - 4}px sans-serif;text-align:center;box-sizing:border-box">${n}</div>`,
  });

/**
 * Marking the way for a rebuild (AD-38): a click on the map adds a point, a click on a
 * point removes it; the points are joined in order by a thin dashed line
 */
const WaypointMarks: React.FC<{
  points: [number, number][];
  onMark: (lat: number, lon: number) => void;
  onUnmark: (index: number) => void;
}> = ({ points, onMark, onUnmark }) => {
  const map = useMap();
  const ui = useUi();
  useMapEvents({
    click(e: LeafletMouseEvent) {
      onMark(e.latlng.lat, e.latlng.lng);
    },
  });
  useEffect(() => {
    // The class carries the Owner's pointer (AD-41 rule 7); the inline cursor the system's
    const el = map.getContainer();
    el.style.cursor = 'crosshair';
    el.classList.add('atlas-crosshair');
    return () => {
      el.style.cursor = '';
      el.classList.remove('atlas-crosshair');
    };
  }, [map]);
  return (
    <>
      {points.length > 1 && (
        <Polyline
          positions={points}
          interactive={false}
          pathOptions={{ color: ui.colors.waypoint, weight: 2, opacity: 0.8, dashArray: '4 6' }}
        />
      )}
      {points.map((p, i) => (
        <Marker
          key={`${i}-${p[0]}-${p[1]}`}
          position={p}
          icon={markIcon(i + 1, ui.map.waypoint, ui.colors.waypoint)}
          title={`Point ${i + 1}: click to remove`}
          eventHandlers={{
            click: (e) => {
              L.DomEvent.stopPropagation(e.originalEvent);
              onUnmark(i);
            },
          }}
        />
      ))}
    </>
  );
};

/** Source points are shown on a matched route from this zoom on */
const SOURCE_DOTS_ZOOM = 14;

/**
 * The Owner's grab distance (AD-41 rule 6): the canvas renderer's tolerance, which circles
 * read at every hit test; lines keep their hit area in bounds made when drawn, so those are
 * made again
 */
const GrabDistance: React.FC<{ renderer: L.Canvas; tolerance: number }> = ({
  renderer,
  tolerance,
}) => {
  const map = useMap();
  useEffect(() => {
    if (renderer.options.tolerance === tolerance) {
      return;
    }
    renderer.options.tolerance = tolerance;
    map.eachLayer((layer) => {
      (layer as unknown as { _updateBounds?: () => void })._updateBounds?.();
    });
  }, [map, renderer, tolerance]);
  return null;
};

/** Props for SelectedRoute */
interface SelectedRouteProps {
  runs: PieceLine[];
  color: string;
  /** The Owner's route styles (AD-41) */
  styles: UiSettings['routes'];
}

/**
 * The selected activity's matched route: observed pieces solid, inferred amber dashes
 * on a dark casing, gaps faint dotted (memoised: hovering the charts does not redraw it)
 */
const SelectedRouteLines: React.FC<SelectedRouteProps> = ({ runs, color, styles }) => (
  <>
    {runs.map((r, i) =>
      r.kind === 'inferred' ? (
        <React.Fragment key={`${r.kind}-${i}`}>
          <Polyline
            positions={r.positions}
            interactive={false}
            pathOptions={inferredCasingStyle(styles)}
          />
          <Polyline
            positions={r.positions}
            interactive={false}
            pathOptions={pieceStyle(r.kind, color, styles)}
          />
        </React.Fragment>
      ) : (
        <Polyline
          key={`${r.kind}-${i}`}
          positions={r.positions}
          interactive={false}
          pathOptions={pieceStyle(r.kind, color, styles)}
        />
      ),
    )}
  </>
);
const SelectedRoute = React.memo(SelectedRouteLines);

/** Props for SourceDots */
interface SourceDotsProps {
  points: [number, number][];
  color: string;
  radius: number;
}

/**
 * The recorded points under a matched route, as small dots, only when zoomed in
 * (its own zoom state: zooming re-renders only the dots)
 */
const SourceDotsLayer: React.FC<SourceDotsProps> = ({ points, color, radius }) => {
  const map = useMap();
  const [zoom, setZoom] = useState(() => map.getZoom());
  useMapEvents({ zoomend: () => setZoom(map.getZoom()) });
  if (zoom < SOURCE_DOTS_ZOOM) {
    return null;
  }
  return (
    <>
      {points.map((p, i) => (
        <CircleMarker
          key={i}
          center={p}
          radius={radius}
          interactive={false}
          pathOptions={{ color, weight: 1, fillColor: color, fillOpacity: 0.9, opacity: 0.9 }}
        />
      ))}
    </>
  );
};
const SourceDots = React.memo(SourceDotsLayer);

/** Waits this long after a filter change before loading the density map (ms) */
const DENSITY_DEBOUNCE = 400;

/** A visit's name on the map, marked when its identity is automatic or uncertain */
const VisitLabel: React.FC<{ visit: VisitSummary }> = ({ visit }) => {
  const mark = identifyMark(visit.identifyState);
  const name = visit.placeName || 'Visit';
  const photos = photoCountText(visit.photoCount);
  const more = photos ? <span style={{ opacity: 0.7 }}> · {photos}</span> : null;
  if (mark === 'auto') {
    return (
      <span>
        <i>{name}</i> <span style={{ opacity: 0.7 }}>· auto</span>
        {more}
      </span>
    );
  }
  return (
    <span>
      {mark === '?' ? `${name} (?)` : name}
      {more}
    </span>
  );
};

/**
 * Data for the filters, loaded while a layer is on (the density map, the photos); a
 * newer filter cancels the previous load
 */
const useFiltered = <T,>(
  active: boolean,
  query: AtlasQuery | undefined,
  reloadKey: number,
  load: (q: AtlasQuery, signal: AbortSignal) => Promise<T>,
  what: string,
): { data: T | null; loading: boolean } => {
  const [data, setData] = useState<T | null>(null);
  const [loading, setLoading] = useState(false);
  const key = JSON.stringify(query ?? {});
  useEffect(() => {
    if (!active) {
      setLoading(false);
      return undefined;
    }
    let alive = true;
    // A newer filter cancels the previous load (each is heavy for the server)
    const abort = new AbortController();
    setLoading(true);
    const t = setTimeout(() => {
      load(JSON.parse(key) as AtlasQuery, abort.signal)
        .then((d) => {
          if (alive) {
            setData(d);
          }
        })
        .catch((err) => {
          if (!alive) {
            return;
          }
          // Not the previous filter's map under the new filters
          setData(null);
          logger.warn(
            { context: 'AtlasMap', metadata: { what, message: (err as Error)?.message } },
            'Loading a map layer failed',
          );
        })
        .finally(() => {
          if (alive) {
            setLoading(false);
          }
        });
    }, DENSITY_DEBOUNCE);
    return () => {
      alive = false;
      clearTimeout(t);
      abort.abort();
    };
  }, [active, key, reloadKey]);
  return { data, loading };
};

/** The density map's data for the filters, loaded while in density mode */
const useDensity = (active: boolean, query: AtlasQuery | undefined, reloadKey: number) =>
  useFiltered<DensityData>(
    active,
    query,
    reloadKey,
    (q, signal) => backendApiClient.atlasDensity(q, signal),
    'density',
  );

/** Photos for the filters, loaded while the photos layer is on */
const usePhotoLayer = (active: boolean, query: AtlasQuery | undefined, reloadKey: number) =>
  useFiltered<PhotoMapData>(
    active,
    query,
    reloadKey,
    (q, signal) => backendApiClient.atlasMapPhotos(q, signal),
    'photos',
  );

/** Thumbnails on the map for the open activity or place, at most this many */
const PHOTO_PINS = 40;

/** A photo's thumbnail as a map pin */
const photoPin = (p: PhotoSummary, size: number, color: string): L.DivIcon =>
  L.divIcon({
    className: 'atlas-photo-pin',
    html: `<img src="${backendApiClient.atlasPhotoUrl(p.id, 'small')}" alt="" loading="lazy" style="width:${size}px;height:${size}px;object-fit:cover;border-radius:6px;border:2px solid ${color};box-shadow:0 1px 4px rgba(0,0,0,.5);display:block" />`,
    iconSize: [size, size],
    iconAnchor: [size / 2, size / 2],
  });

/**
 * The small "has photos" dot beside a visit's circle (top-right, so the circle
 * stays clickable); tracks get theirs on the canvas
 */
const visitBadge = (color: string) =>
  L.divIcon({
    className: 'atlas-photo-badge',
    html: `<div style="width:7px;height:7px;border-radius:50%;background:${color};border:1px solid #0e1116"></div>`,
    iconSize: [9, 9],
    iconAnchor: [-2, 10],
  });

/** The small permanent label at the area filter's top-left corner */
const areaLabel = (color: string) =>
  L.divIcon({
    className: 'atlas-area-label',
    html: `<div style="display:inline-block;white-space:nowrap;padding:1px 6px;border-radius:4px;background:${color};color:#fff;font:600 calc(11px * var(--ui-map, 1))/1.45 system-ui,sans-serif;box-shadow:0 1px 3px rgba(0,0,0,.4)">Area filter</div>`,
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
    map.getContainer().classList.toggle('atlas-crosshair', drawing);
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
  selectedRoute,
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
  query,
  reloadKey = 0,
  photos,
  onOpenPhoto,
  marking,
  onMark,
  onUnmark,
  flightPath,
}) => {
  const ui = useUi();
  // One canvas renderer, made once; GrabDistance keeps its tolerance (AD-41 rule 6)
  const [renderer] = useState(() => L.canvas({ tolerance: ui.map.grab }));
  const visitBadgeIcon = useMemo(() => visitBadge(ui.colors.photo), [ui.colors.photo]);
  const areaLabelIcon = useMemo(() => areaLabel(ui.colors.area), [ui.colors.area]);
  const [mapStyle, setMapStyle] = useState<MapStyle>(loadMapStyle);
  const [mode, setMode] = useState<MapMode>(loadMapMode);
  const density = mode === 'density';
  const { data: densityData, loading: densityLoading } = useDensity(density, query, reloadKey);
  const [photoLayer, setPhotoLayer] = useState(loadMapPhotos);
  const { data: layerPhotos, loading: photosLoading } = usePhotoLayer(photoLayer, query, reloadKey);
  // The open activity's or place's photos that have a position, a few spread over its time
  const pins = useMemo(() => {
    const placed = (photos ?? []).filter((p) => isSet(p.lat) && isSet(p.lon));
    // Icons made once per list: a new icon on each render would reload every thumbnail
    return spread(placed, PHOTO_PINS).map((p) => ({
      p,
      index: (photos ?? []).indexOf(p),
      icon: photoPin(p, ui.map.photoPin, ui.colors.photo),
    }));
  }, [photos, ui.map.photoPin, ui.colors.photo]);
  // The layer's markers are built when its photos change, not on every render (thousands)
  const openPhotoRef = useRef(onOpenPhoto);
  openPhotoRef.current = onOpenPhoto;
  const layerMarkers = useMemo(
    () =>
      photoLayer && layerPhotos
        ? layerPhotos.photos.map((p, i) =>
            isSet(p.lat) && isSet(p.lon) ? (
              <CircleMarker
                key={p.id}
                center={[p.lat, p.lon]}
                radius={ui.map.photo}
                pathOptions={{
                  color: '#0e1116',
                  weight: 1,
                  fillColor: PHOTO_COLOR,
                  fillOpacity: 0.9,
                }}
                eventHandlers={{ click: () => openPhotoRef.current?.(layerPhotos.photos, i) }}
              >
                <Tooltip>{takenText(p.takenAt)}</Tooltip>
              </CircleMarker>
            ) : null,
          )
        : null,
    [photoLayer, layerPhotos, ui.map.photo, ui.colors.photo],
  );
  // Bumped by the "Show all results" button
  const [fitAll, setFitAll] = useState(0);
  const styleDef = MAP_STYLES.find((m) => m.value === mapStyle) ?? MAP_STYLES[0];
  // On a light map the selected track is drawn dark (white would vanish)
  const highlightColor = styleDef.light ? ui.colors.highlightLight : ui.colors.highlightDark;

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
  // The selected flight's real path (AD-42) replaces its arc, so the flight is one line
  // and its airport dots are the real airports
  const shown = useMemo(
    () =>
      flightPath && flightPath.length > 1
        ? lines.map((l) => (l.flight && l.a.id === selectedId ? { ...l, path: flightPath } : l))
        : lines,
    [lines, flightPath, selectedId],
  );
  const selectedFlight = shown.find((l) => l.a.id === selectedId && l.flight);
  // The selected matched route's runs, decoded once per route (not on hover)
  const routeRuns = useMemo(
    () =>
      selectedRoute
        ? splitPieces(decodePolyline(selectedRoute.polyline), selectedRoute.pieces)
        : null,
    [selectedRoute],
  );
  const routeColor = useMemo(() => {
    const a = activities.find((x) => x.id === selectedId);
    return a ? typeColor(a.type) : highlightColor;
  }, [activities, selectedId, highlightColor, ui.colors]);
  const sourceDots = useMemo(
    () =>
      routeRuns && selectedTrack
        ? selectedTrack.map((p) => [p.lat, p.lon] as [number, number])
        : null,
    [routeRuns, selectedTrack],
  );
  // Items with photos get a small dot (the open one's panel shows its photos instead):
  // at the middle of a track, beside a visit. Positions made once per result list.
  const trackBadges = useMemo(
    () =>
      lines
        .filter(({ a }) => a.photoCount)
        .map(({ a, path }) => ({ id: a.id, at: path[Math.floor(path.length / 2)] })),
    [lines],
  );
  const visitBadges = useMemo(
    () =>
      visits
        .filter((v) => v.photoCount)
        .map((v) => ({ id: v.id, at: [v.lat, v.lon] as [number, number] })),
    [visits],
  );
  const crowded = lines.length > 150;
  const areaBounds: LatLngBoundsExpression | null = area
    ? [
        [area.minLat, area.minLon],
        [area.maxLat, area.maxLon],
      ]
    : null;

  const kinds = useMemo(() => {
    if (density) {
      return densityData
        ? [...trackTypes(densityData.tracks), ...(densityData.visits.length ? ['visit'] : [])]
        : [];
    }
    return [
      ...activities.map((a) => a.type),
      ...(visits.length ? ['visit'] : []),
      ...(trackBadges.length || visitBadges.length ? ['photos'] : []),
      ...(routeRuns && hasInferred(routeRuns) ? ['inferred'] : []),
    ];
  }, [density, densityData, activities, visits, trackBadges, visitBadges, routeRuns]);

  return (
    <Box sx={{ position: 'relative', height: '100%', width: '100%' }}>
      <MapContainer
        center={[31.8, 35.0]}
        zoom={7}
        worldCopyJump
        style={{ height: '100%', width: '100%', background: '#0e1116' }}
        preferCanvas
        renderer={renderer}
      >
        <GrabDistance renderer={renderer} tolerance={ui.map.grab} />
        <BaseMap style={mapStyle} />
        <FitResults
          activities={activities}
          visits={visits}
          fitKey={fitKey}
          track={selectedTrack}
          force={fitAll}
        />
        <AreaDraw drawing={drawing} onDone={onAreaDrawn} />
        {marking && onMark && onUnmark && (
          <WaypointMarks points={marking} onMark={onMark} onUnmark={onUnmark} />
        )}
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
              icon={areaLabelIcon}
              interactive={false}
              keyboard={false}
            />
          </>
        )}
        {density && <DensityLayer data={densityData} />}
        {!density &&
          shown
            // The selected matched route is drawn by its pieces (dashes would be hidden under it)
            .filter(({ a }) => !(routeRuns && a.id === selectedId))
            .map(({ a, path }) => {
              // The Owner's style of the type (AD-41): many tracks at once are thinner and
              // lighter, so the streets stay readable
              const st = ui.routes.types[a.type as StyledType] ?? ui.routes.types.other;
              const weight =
                a.id === selectedId
                  ? ui.routes.selectedWidth
                  : crowded
                    ? ui.routes.crowdedWidth
                    : st.width;
              return (
                <Polyline
                  key={a.id}
                  positions={path}
                  pathOptions={{
                    color: typeColor(a.type),
                    weight,
                    opacity:
                      selectedId && a.id !== selectedId
                        ? ui.routes.dimmedOpacity
                        : crowded
                          ? Math.min(st.opacity, 0.5)
                          : st.opacity,
                    dashArray: dashArray(st.pattern, weight),
                  }}
                  eventHandlers={{ click: () => onSelectActivity(a.id) }}
                >
                  <Tooltip sticky>{describeActivity(a)}</Tooltip>
                </Polyline>
              );
            })}
        {!density &&
          shown
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
                radius={ui.map.airport}
                interactive={false}
                pathOptions={{ color: '#0e1116', weight: 1, fillColor: m.color, fillOpacity: 1 }}
              />
            ))}
        {density ? null : selectedFlight ? (
          <Polyline
            positions={selectedFlight.path}
            pathOptions={{
              color: highlightColor,
              weight: 2.5,
              opacity: 0.95,
              dashArray: dashArray(ui.routes.types.flight.pattern, 2.5),
            }}
          />
        ) : routeRuns ? (
          <>
            <SelectedRoute runs={routeRuns} color={routeColor} styles={ui.routes} />
            {sourceDots && (
              <SourceDots points={sourceDots} color={highlightColor} radius={ui.map.trackPoint} />
            )}
          </>
        ) : (
          selectedTrack &&
          selectedTrack.length > 1 && (
            <Polyline
              positions={selectedTrack.map((p) => [p.lat, p.lon] as [number, number])}
              pathOptions={{ color: highlightColor, weight: 2.5, opacity: 0.95 }}
            />
          )
        )}
        {!density &&
          visits.map((v) => (
            <CircleMarker
              key={v.id}
              center={[v.lat, v.lon]}
              radius={v.id === selectedVisitId ? ui.map.visitSelected : ui.map.visit}
              pathOptions={{
                color: v.id === selectedVisitId ? '#ffffff' : VISIT_COLOR,
                fillColor: VISIT_COLOR,
                fillOpacity: 0.85,
                weight: v.id === selectedVisitId ? 2 : 1,
              }}
              eventHandlers={{ click: () => onSelectVisit(v) }}
            >
              <Tooltip>
                <VisitLabel visit={v} />
              </Tooltip>
            </CircleMarker>
          ))}
        {!density &&
          trackBadges
            .filter((b) => b.id !== selectedId)
            .map((b) => (
              <CircleMarker
                key={`badge-${b.id}`}
                center={b.at}
                radius={3.5}
                interactive={false}
                pathOptions={{
                  color: '#0e1116',
                  weight: 1,
                  fillColor: PHOTO_COLOR,
                  fillOpacity: 1,
                }}
              />
            ))}
        {!density &&
          visitBadges
            .filter((b) => b.id !== selectedVisitId)
            .map((b) => (
              <Marker
                key={`badge-${b.id}`}
                position={b.at}
                icon={visitBadgeIcon}
                interactive={false}
                keyboard={false}
              />
            ))}
        {layerMarkers}
        {pins.map(({ p, index, icon }) => (
          <Marker
            key={`pin-${p.id}`}
            position={[p.lat!, p.lon!]}
            icon={icon}
            eventHandlers={{ click: () => onOpenPhoto?.(photos ?? [], index) }}
          >
            <Tooltip>{takenText(p.takenAt)}</Tooltip>
          </Marker>
        ))}
        {cursor && (
          <CircleMarker
            center={cursor}
            radius={ui.map.hover}
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
      <Box
        sx={{
          position: 'absolute',
          top: 10,
          right: 10,
          zIndex: 1000,
          display: 'flex',
          alignItems: 'center',
          gap: 1,
        }}
      >
        {((density && densityLoading) || (photoLayer && photosLoading)) && (
          <CircularProgress size={16} aria-label="Loading the map" />
        )}
        <MuiTooltip
          title={
            photoLayer
              ? layerPhotos?.truncated
                ? `Showing the newest ${layerPhotos.photos.length.toLocaleString()} photos · click to hide`
                : 'Hide photos'
              : 'Show the photos taken in the filters (OneDrive)'
          }
        >
          <ToggleButton
            size="small"
            value="photos"
            selected={photoLayer}
            aria-label="Photos on the map"
            onChange={() => {
              saveMapPhotos(!photoLayer);
              setPhotoLayer(!photoLayer);
            }}
            sx={{
              bgcolor: 'rgba(14, 17, 22, 0.85)',
              py: 0.4,
              px: 1,
              fontSize: fs.map(13),
              textTransform: 'none',
              gap: 0.5,
              '&.Mui-selected': { color: PHOTO_COLOR },
            }}
          >
            <PhotoCamera sx={{ fontSize: fs.map(16) }} />
            Photos
          </ToggleButton>
        </MuiTooltip>
        <ToggleButtonGroup
          size="small"
          exclusive
          value={mode}
          onChange={(_, v: MapMode | null) => {
            if (v) {
              saveMapMode(v);
              setMode(v);
            }
          }}
          aria-label="Map mode"
          sx={{
            bgcolor: 'rgba(14, 17, 22, 0.85)',
            '& .MuiToggleButton-root': {
              py: 0.4,
              px: 1,
              fontSize: fs.map(13),
              textTransform: 'none',
            },
          }}
        >
          <ToggleButton value="items">Items</ToggleButton>
          <MuiTooltip title="How often you went where (selecting items needs Items)">
            <ToggleButton value="density">Density</ToggleButton>
          </MuiTooltip>
        </ToggleButtonGroup>
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
            '& .MuiSelect-select': { fontSize: fs.map(13), py: 0.6 },
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
          {density && (
            <Typography
              variant="caption"
              component="div"
              sx={{ color: tokens.textMuted, lineHeight: 1.4 }}
            >
              Brighter = more often
              {densityData?.truncated &&
                ` · showing the newest ${densityData.tracks.length.toLocaleString()} tracks and ${densityData.visits.length.toLocaleString()} visits`}
            </Typography>
          )}
        </Box>
      )}
    </Box>
  );
};

export default AtlasMap;
