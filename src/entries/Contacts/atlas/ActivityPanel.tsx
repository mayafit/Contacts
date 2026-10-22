/**
 * @fileoverview Details of one activity: numbers, heart-rate and elevation
 * charts linked to the map, and editing
 * @module Contacts/atlas/ActivityPanel
 */

import React, { useMemo, useState } from 'react';
import { Box, Button, IconButton, MenuItem, TextField, Tooltip, Typography } from '@mui/material';
import { ArrowBack, DeleteOutline, Edit, Favorite } from '@mui/icons-material';
import type { ActivityDetail, PointOutput, SeriesPoint } from './types';
import { ACTIVITY_TYPES, typeColor, isSet } from './types';
import { formatDistance, formatDuration, formatWhen } from './geo';
import { tokens } from '../theme/contactsTheme';

/** Props for ActivityPanel */
export interface ActivityPanelProps {
  detail: ActivityDetail;
  onBack: () => void;
  onCursor: (pos: [number, number] | null) => void;
  onSave: (patch: { name?: string; type?: string; notes?: string }) => Promise<void>;
  onDelete: () => void;
}

/** Nearest track point to a time */
const pointAt = (points: PointOutput[], t: number): PointOutput | undefined => {
  let lo = 0;
  let hi = points.length - 1;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (Date.parse(points[mid].t) < t) {
      lo = mid + 1;
    } else {
      hi = mid;
    }
  }
  return points[lo];
};

/** A line chart over the activity's time span; hovering reports the time */
const Chart: React.FC<{
  label: string;
  unit: string;
  color: string;
  series: { t: number; v: number }[];
  from: number;
  to: number;
  onHover: (t: number | null) => void;
}> = ({ label, unit, color, series, from, to, onHover }) => {
  const [hover, setHover] = useState<{ t: number; v: number } | null>(null);
  const W = 340;
  const H = 90;
  if (series.length < 2) {
    return null;
  }
  const min = Math.min(...series.map((p) => p.v));
  const max = Math.max(...series.map((p) => p.v));
  const x = (t: number) => ((t - from) / Math.max(1, to - from)) * W;
  const y = (v: number) => H - 4 - ((v - min) / Math.max(1, max - min)) * (H - 10);
  const d = series
    .map((p, i) => `${i ? 'L' : 'M'}${x(p.t).toFixed(1)},${y(p.v).toFixed(1)}`)
    .join('');
  return (
    <Box sx={{ mt: 1.5 }}>
      <Typography variant="caption" sx={{ color: tokens.textSecondary }}>
        {label} · {Math.round(min)}–{Math.round(max)} {unit}
        {hover
          ? ` · ${Math.round(hover.v)} ${unit} at ${new Date(hover.t).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' })}`
          : ''}
      </Typography>
      <svg
        width="100%"
        viewBox={`0 0 ${W} ${H}`}
        style={{ display: 'block', cursor: 'crosshair' }}
        onMouseMove={(e) => {
          const rect = (e.currentTarget as SVGSVGElement).getBoundingClientRect();
          const t = from + ((e.clientX - rect.left) / rect.width) * (to - from);
          const near = series.reduce((a, b) => (Math.abs(b.t - t) < Math.abs(a.t - t) ? b : a));
          setHover(near);
          onHover(near.t);
        }}
        onMouseLeave={() => {
          setHover(null);
          onHover(null);
        }}
      >
        <path d={d} fill="none" stroke={color} strokeWidth={1.5} />
        {hover && (
          <line x1={x(hover.t)} x2={x(hover.t)} y1={0} y2={H} stroke="#fff" opacity={0.5} />
        )}
      </svg>
    </Box>
  );
};

/**
 * Activity details.
 * @param props - Component props
 * @returns Panel element
 */
const ActivityPanel: React.FC<ActivityPanelProps> = ({
  detail,
  onBack,
  onCursor,
  onSave,
  onDelete,
}) => {
  const s = detail.summary;
  const [editing, setEditing] = useState(false);
  const [name, setName] = useState(s.name ?? '');
  const [type, setType] = useState(s.type);
  const [notes, setNotes] = useState(detail.notes ?? '');
  const from = Date.parse(s.startTime);
  const to = Date.parse(s.endTime);
  const hr = useMemo(
    () => detail.heartRate.map((p: SeriesPoint) => ({ t: Date.parse(p.t), v: p.v })),
    [detail],
  );
  const ele = useMemo(
    () => detail.points.filter((p) => isSet(p.ele)).map((p) => ({ t: Date.parse(p.t), v: p.ele! })),
    [detail],
  );
  const hover = (t: number | null) => {
    const p = !isSet(t) ? undefined : pointAt(detail.points, t);
    onCursor(p ? [p.lat, p.lon] : null);
  };

  const stats = [
    ['Distance', formatDistance(s.distanceM)],
    ['Time', formatDuration(s.durationS)],
    ['Climb', isSet(s.elevationGainM) ? `${Math.round(s.elevationGainM)} m` : ''],
    [
      'Avg speed',
      s.distanceM && s.durationS ? `${((s.distanceM / s.durationS) * 3.6).toFixed(1)} km/h` : '',
    ],
    ['Heart rate', s.avgHeartRate ? `${s.avgHeartRate} avg · ${s.maxHeartRate} max` : ''],
  ].filter(([, v]) => v);

  return (
    <Box sx={{ p: 2 }}>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
        <IconButton size="small" onClick={onBack} aria-label="Back to results">
          <ArrowBack fontSize="small" />
        </IconButton>
        <Box sx={{ width: 10, height: 10, borderRadius: '50%', bgcolor: typeColor(s.type) }} />
        <Typography variant="subtitle1" dir="auto" sx={{ flex: 1, minWidth: 0 }} noWrap>
          {s.name || ACTIVITY_TYPES[s.type]?.label || s.type}
        </Typography>
        <Tooltip title="Edit">
          <IconButton size="small" onClick={() => setEditing((v) => !v)} aria-label="Edit activity">
            <Edit fontSize="small" />
          </IconButton>
        </Tooltip>
        <Tooltip title="Delete">
          <IconButton size="small" onClick={onDelete} aria-label="Delete activity">
            <DeleteOutline fontSize="small" />
          </IconButton>
        </Tooltip>
      </Box>
      <Typography variant="caption" sx={{ color: tokens.textMuted, display: 'block', ml: 5 }}>
        {formatWhen(s.startTime)} · {s.source}
      </Typography>

      {editing && (
        <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1.25, mt: 1.5 }}>
          <TextField
            size="small"
            label="Name"
            value={name}
            onChange={(e) => setName(e.target.value)}
          />
          <TextField
            size="small"
            select
            label="Type"
            value={type}
            onChange={(e) => setType(e.target.value)}
          >
            {Object.entries(ACTIVITY_TYPES).map(([k, v]) => (
              <MenuItem key={k} value={k}>
                {v.label}
              </MenuItem>
            ))}
          </TextField>
          <TextField
            size="small"
            label="Notes"
            value={notes}
            onChange={(e) => setNotes(e.target.value)}
            multiline
            minRows={2}
          />
          <Button
            variant="contained"
            size="small"
            onClick={async () => {
              try {
                await onSave({ name, type, notes });
                setEditing(false);
              } catch {
                // The page shows the error; keep the form open
              }
            }}
          >
            Save
          </Button>
        </Box>
      )}

      <Box sx={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 1, mt: 1.5 }}>
        {stats.map(([k, v]) => (
          <Box key={k} sx={{ p: 1, borderRadius: '8px', bgcolor: tokens.page }}>
            <Typography variant="caption" sx={{ color: tokens.textMuted }}>
              {k}
            </Typography>
            <Typography variant="body2" sx={{ fontWeight: 600 }}>
              {v}
            </Typography>
          </Box>
        ))}
      </Box>

      {hr.length > 1 ? (
        <Chart
          label="Heart rate"
          unit="bpm"
          color="#ff4d6d"
          series={hr}
          from={from}
          to={to}
          onHover={hover}
        />
      ) : (
        <Typography
          variant="caption"
          sx={{ display: 'flex', alignItems: 'center', gap: 0.5, color: tokens.textMuted, mt: 1.5 }}
        >
          <Favorite sx={{ fontSize: 14 }} /> No heart rate in the track or in your health data for
          this time.
        </Typography>
      )}
      {detail.heartRateSource === 'health' && (
        <Typography variant="caption" sx={{ color: tokens.textMuted }}>
          From your health data (the track has none)
        </Typography>
      )}
      <Chart
        label="Elevation"
        unit="m"
        color="#5bd16b"
        series={ele}
        from={from}
        to={to}
        onHover={hover}
      />
      {detail.notes && !editing && (
        <Typography
          variant="body2"
          dir="auto"
          sx={{ mt: 1.5, color: tokens.textSecondary, whiteSpace: 'pre-wrap' }}
        >
          {detail.notes}
        </Typography>
      )}
    </Box>
  );
};

export default ActivityPanel;
