/**
 * @fileoverview World map with activity tracks, visits, the selected item and
 * an area selection tool
 * @module Contacts/atlas/AtlasMap
 */

import React, { useEffect, useMemo, useRef } from 'react';
import {
  CircleMarker,
  MapContainer,
  Polyline,
  Rectangle,
  TileLayer,
  Tooltip,
  useMap,
  useMapEvents,
} from 'react-leaflet';
import type { LatLngBoundsExpression, LeafletMouseEvent } from 'leaflet';
import './leaflet.css';
import type { ActivitySummary, PointOutput, VisitSummary } from './types';
import { typeColor, isSet } from './types';
import { decodePolyline } from './geo';

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
}

/** Fits the map to the results when fitKey changes, or to the selected track */
const FitResults: React.FC<{
  activities: ActivitySummary[];
  visits: VisitSummary[];
  fitKey: string;
  track?: PointOutput[];
}> = ({ activities, visits, fitKey, track }) => {
  const map = useMap();
  useEffect(() => {
    const lats: number[] = [];
    const lons: number[] = [];
    if (track?.length) {
      track.forEach((p) => {
        lats.push(p.lat);
        lons.push(p.lon);
      });
    } else {
      activities.forEach((a) => {
        if (isSet(a.minLat) && isSet(a.maxLat) && isSet(a.minLon) && isSet(a.maxLon)) {
          lats.push(a.minLat, a.maxLat);
          lons.push(a.minLon, a.maxLon);
        }
      });
      visits.forEach((v) => {
        lats.push(v.lat);
        lons.push(v.lon);
      });
    }
    if (lats.length) {
      map.fitBounds(
        [
          [Math.min(...lats), Math.min(...lons)],
          [Math.max(...lats), Math.max(...lons)],
        ],
        { padding: [30, 30], maxZoom: 15 },
      );
    }
    // Only when new results arrive or the selection changes, not on every render
  }, [fitKey, track]);
  return null;
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
      pathOptions={{ color: '#3987e5', weight: 1, dashArray: '4 4' }}
    />
  ) : null;
};

/**
 * The Atlas map (OpenStreetMap tiles, darkened).
 * @param props - Component props
 * @returns Map element
 */
const AtlasMap: React.FC<AtlasMapProps> = ({
  activities,
  visits,
  selectedTrack,
  selectedId,
  area,
  drawing,
  onAreaDrawn,
  onSelectActivity,
  onSelectVisit,
  fitKey,
  cursor,
}) => {
  const lines = useMemo(
    () =>
      activities.filter((a) => a.polyline).map((a) => ({ a, path: decodePolyline(a.polyline!) })),
    [activities],
  );
  const areaBounds: LatLngBoundsExpression | null = area
    ? [
        [area.minLat, area.minLon],
        [area.maxLat, area.maxLon],
      ]
    : null;

  return (
    <MapContainer
      center={[31.8, 35.0]}
      zoom={7}
      worldCopyJump
      style={{ height: '100%', width: '100%', background: '#0e1116' }}
      preferCanvas
    >
      <TileLayer
        // OpenStreetMap tiles, darkened by CSS (see leaflet.css: .atlas-dark-tiles)
        url="https://tile.openstreetmap.org/{z}/{x}/{y}.png"
        attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
        className="atlas-dark-tiles"
        maxZoom={19}
      />
      <FitResults activities={activities} visits={visits} fitKey={fitKey} track={selectedTrack} />
      <AreaDraw drawing={drawing} onDone={onAreaDrawn} />
      {areaBounds && (
        <Rectangle
          bounds={areaBounds}
          pathOptions={{ color: '#3987e5', weight: 1, fillOpacity: 0.06 }}
        />
      )}
      {lines.map(({ a, path }) => (
        <Polyline
          key={a.id}
          positions={path}
          pathOptions={{
            color: typeColor(a.type),
            weight: a.id === selectedId ? 5 : 2.5,
            opacity: selectedId && a.id !== selectedId ? 0.35 : 0.9,
          }}
          eventHandlers={{ click: () => onSelectActivity(a.id) }}
        >
          <Tooltip sticky>{a.name || a.type}</Tooltip>
        </Polyline>
      ))}
      {selectedTrack && selectedTrack.length > 1 && (
        <Polyline
          positions={selectedTrack.map((p) => [p.lat, p.lon] as [number, number])}
          pathOptions={{ color: '#ffffff', weight: 2, opacity: 0.9 }}
        />
      )}
      {visits.map((v) => (
        <CircleMarker
          key={v.id}
          center={[v.lat, v.lon]}
          radius={5}
          pathOptions={{ color: '#ffb547', fillColor: '#ffb547', fillOpacity: 0.85, weight: 1 }}
          eventHandlers={{ click: () => onSelectVisit(v) }}
        >
          <Tooltip>{v.placeName || 'Visit'}</Tooltip>
        </CircleMarker>
      ))}
      {cursor && (
        <CircleMarker
          center={cursor}
          radius={7}
          pathOptions={{ color: '#fff', fillColor: '#ff4d6d', fillOpacity: 1, weight: 2 }}
        />
      )}
    </MapContainer>
  );
};

export default AtlasMap;
