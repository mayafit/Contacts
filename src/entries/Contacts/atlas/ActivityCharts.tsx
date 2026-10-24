/**
 * @fileoverview The activity's charts (heart rate with its zones, speed or
 * pace, elevation, cadence) over time or distance, synced to one cursor
 * @module Contacts/atlas/ActivityCharts
 */

import React, { useMemo } from 'react';
import { Box, Typography } from '@mui/material';
import { Favorite } from '@mui/icons-material';
import type { ActivityAnalysis, ActivityDetail } from './types';
import { HEALTH_COLOR, isSet } from './types';
import {
  ZONE_COLORS,
  cadenceUnit,
  chartPath,
  chartX,
  chartY,
  distanceAtTime,
  ensureSpan,
  formatClock,
  formatPace,
  speedRange,
  speedSeries,
  valueAt,
  valueRange,
  type ChartAxis,
  type ChartFrame,
  type ChartPoint,
  type TrackSample,
} from './analysis';
import { formatDistance } from './geo';
import { tokens } from '../theme/contactsTheme';

/** Width of the charts' coordinate space (they stretch to the panel) */
const VB_W = 1000;
/** Height of a chart in pixels */
const CHART_H = 76;
/** Room above and below the line */
const PAD = 5;

const SPEED_COLOR = '#3fb6ff';
const ELEVATION_COLOR = '#5bd16b';
const CADENCE_COLOR = '#c9a7ff';

/** A horizontal background stripe of a chart (heart-rate zones) */
interface ChartBand {
  from: number;
  to: number;
  color: string;
}

/** Props of one synced chart */
interface SyncedChartProps {
  label: string;
  color: string;
  series: ChartPoint[];
  range: [number, number];
  /** A value with its unit */
  format: (v: number) => string;
  axis: ChartAxis;
  /** End of the x axis (seconds or meters) */
  maxX: number;
  cursorT: number | null;
  /** The cursor on this axis (seconds or meters) */
  cursorX: number | null;
  onHoverX: (x: number | null) => void;
  /** Smaller values at the top (pace: faster is up) */
  invert?: boolean;
  /** Fill below the line */
  area?: boolean;
  bands?: ChartBand[];
  /** The header's value while there is no cursor, e.g. "avg 142 bpm" */
  idle?: string;
}

/**
 * One chart of the stack: a line over time or distance, min/max labels, the
 * value under the cursor in its header.
 * @param props - Component props
 * @returns Chart element
 */
const SyncedChart: React.FC<SyncedChartProps> = ({
  label,
  color,
  series,
  range,
  format,
  axis,
  maxX,
  cursorT,
  cursorX,
  onHoverX,
  invert = false,
  area = false,
  bands = [],
  idle,
}) => {
  const [min, max] = range;
  const frame = useMemo<ChartFrame>(
    () => ({ width: VB_W, height: CHART_H, pad: PAD, maxX, range: [min, max], invert }),
    [maxX, min, max, invert],
  );
  const xOf = (x: number) => chartX(x, frame);
  const yOf = (v: number) => chartY(v, frame);
  const key = axis === 'time' ? 't' : 'd';
  const path = useMemo(() => chartPath(series, key, frame), [series, key, frame]);
  const fill = useMemo(() => {
    if (!area || series.length < 2) {
      return '';
    }
    const first = chartX(series[0][key], frame).toFixed(1);
    const last = chartX(series[series.length - 1][key], frame).toFixed(1);
    return `${path}L${last},${CHART_H}L${first},${CHART_H}Z`;
  }, [path, area, series, key, frame]);

  const value = isSet(cursorT) ? valueAt(series, cursorT) : null;
  const handle = (e: React.PointerEvent<SVGSVGElement>) => {
    const rect = e.currentTarget.getBoundingClientRect();
    const f = Math.min(1, Math.max(0, (e.clientX - rect.left) / Math.max(1, rect.width)));
    onHoverX(f * maxX);
  };
  const labelSx = {
    position: 'absolute',
    left: 2,
    px: 0.25,
    fontSize: 11,
    lineHeight: '13px',
    color: tokens.textMuted,
    bgcolor: `${tokens.surface}b8`,
    borderRadius: '3px',
    pointerEvents: 'none',
  } as const;
  const top = invert ? min : max;
  const bottom = invert ? max : min;

  return (
    <Box sx={{ mt: 1 }}>
      <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 1 }}>
        <Typography variant="caption" sx={{ color: tokens.textSecondary, flex: 1 }} noWrap>
          {label}
        </Typography>
        {isSet(value) ? (
          <Typography variant="caption" sx={{ fontWeight: 600, color }} noWrap>
            {format(value)}
          </Typography>
        ) : (
          idle && (
            <Typography variant="caption" sx={{ color: tokens.textMuted }} noWrap>
              {idle}
            </Typography>
          )
        )}
      </Box>
      <Box sx={{ position: 'relative', height: CHART_H }}>
        <svg
          width="100%"
          height={CHART_H}
          viewBox={`0 0 ${VB_W} ${CHART_H}`}
          preserveAspectRatio="none"
          style={{ display: 'block', cursor: 'crosshair', touchAction: 'pan-y' }}
          role="img"
          aria-label={`${label} over ${axis}`}
          onPointerMove={handle}
          onPointerDown={handle}
          onPointerLeave={(e) => {
            if (e.pointerType === 'mouse') {
              onHoverX(null);
            }
          }}
        >
          {bands.map((b) => {
            const y1 = yOf(invert ? b.from : b.to);
            const y2 = yOf(invert ? b.to : b.from);
            return Math.abs(y2 - y1) > 0.5 ? (
              <rect
                key={`${b.from}-${b.to}`}
                x={0}
                y={Math.min(y1, y2)}
                width={VB_W}
                height={Math.abs(y2 - y1)}
                fill={b.color}
                opacity={0.09}
              />
            ) : null;
          })}
          <line
            x1={0}
            x2={VB_W}
            y1={CHART_H - 0.5}
            y2={CHART_H - 0.5}
            stroke={tokens.border}
            vectorEffect="non-scaling-stroke"
          />
          {fill && <path d={fill} fill={color} opacity={0.18} stroke="none" />}
          <path
            d={path}
            fill="none"
            stroke={color}
            strokeWidth={1.5}
            strokeLinejoin="round"
            vectorEffect="non-scaling-stroke"
          />
          {isSet(cursorX) && (
            <line
              x1={xOf(cursorX)}
              x2={xOf(cursorX)}
              y1={0}
              y2={CHART_H}
              stroke="#fff"
              opacity={0.55}
              vectorEffect="non-scaling-stroke"
            />
          )}
        </svg>
        {isSet(cursorX) && isSet(value) && (
          <Box
            sx={{
              position: 'absolute',
              left: `${(cursorX / Math.max(1e-9, maxX)) * 100}%`,
              top: yOf(value),
              width: 7,
              height: 7,
              ml: '-3.5px',
              mt: '-3.5px',
              borderRadius: '50%',
              bgcolor: color,
              border: `1px solid ${tokens.surface}`,
              pointerEvents: 'none',
            }}
          />
        )}
        <Box sx={{ ...labelSx, top: 1 }}>{format(top)}</Box>
        <Box sx={{ ...labelSx, bottom: 1 }}>{format(bottom)}</Box>
      </Box>
    </Box>
  );
};

/** Props for ActivityCharts */
export interface ActivityChartsProps {
  detail: ActivityDetail;
  analysis: ActivityAnalysis | null;
  /** The track relative to the start (trackSamples) */
  samples: TrackSample[];
  axis: ChartAxis;
  /** Length of the time axis in seconds */
  durationS: number;
  /** The cursor, seconds from the start */
  cursorT: number | null;
  /** Hovering a chart: a position on the current axis (seconds or meters); null leaves */
  onHoverX: (x: number | null) => void;
}

/**
 * The stack of synced charts with the x axis below.
 * @param props - Component props
 * @returns Charts element
 */
const ActivityCharts: React.FC<ActivityChartsProps> = ({
  detail,
  analysis,
  samples,
  axis,
  durationS,
  cursorT,
  onHoverX,
}) => {
  const s = detail.summary;
  const pace = analysis?.pace ?? false;
  const start = Date.parse(s.startTime);
  const maxD = samples.length ? samples[samples.length - 1].d : 0;
  const maxX = axis === 'time' ? Math.max(1, durationS) : Math.max(1, maxD);
  const cursorD = isSet(cursorT) && samples.length > 1 ? distanceAtTime(samples, cursorT) : null;
  const cursorX = axis === 'time' ? cursorT : cursorD;
  const avgCadence = analysis?.avgCadence ?? null;

  const hr = useMemo<ChartPoint[]>(
    () =>
      detail.heartRate
        .map((p) => {
          const t = (Date.parse(p.t) - start) / 1000;
          return { t, d: distanceAtTime(samples, t), v: p.v };
        })
        // Outside the track there is no distance: they would pile up at the ends
        .filter(
          (p) =>
            axis === 'time' ||
            (samples.length > 0 && p.t >= samples[0].t && p.t <= samples[samples.length - 1].t),
        ),
    [detail, samples, start, axis],
  );
  const speed = useMemo(() => speedSeries(samples, pace), [samples, pace]);
  const ele = useMemo<ChartPoint[]>(
    () => samples.filter((p) => isSet(p.ele)).map((p) => ({ t: p.t, d: p.d, v: p.ele })),
    [samples],
  );
  const cad = useMemo<ChartPoint[]>(
    () =>
      samples.some((p) => isSet(p.cad) && p.cad > 0)
        ? samples.map((p) => ({ t: p.t, d: p.d, v: p.cad }))
        : [],
    [samples],
  );

  const hrRange = valueRange(hr.map((p) => p.v));
  const speedR = speedRange(speed, pace);
  const eleRange = valueRange(ele.map((p) => p.v));
  const cadRange = valueRange(
    cad.map((p) => (isSet(p.v) && p.v > 0 ? p.v : null)),
    0.02,
    0.98,
  );
  const zones = analysis?.zones ?? null;
  const bands: ChartBand[] =
    zones && zones.bounds.length >= 4
      ? [0, ...zones.bounds].map((from, i) => ({
          from,
          to: i < 4 ? zones.bounds[i] : 1000,
          color: ZONE_COLORS[i],
        }))
      : [];
  const common = { axis, maxX, cursorT, cursorX, onHoverX };
  const xLabel = (x: number) => (axis === 'time' ? formatClock(x) : formatDistance(x) || '0 m');
  const avgSpeed = analysis?.avgMovingSpeedKmh ?? null;
  const unit = cadenceUnit(s.type);

  return (
    <Box>
      {hr.length > 1 && hrRange ? (
        <SyncedChart
          {...common}
          label="Heart rate"
          color={HEALTH_COLOR}
          series={hr}
          range={ensureSpan(hrRange, 10)}
          format={(v) => `${Math.round(v)} bpm`}
          bands={bands}
          idle={s.avgHeartRate ? `avg ${s.avgHeartRate} bpm` : undefined}
        />
      ) : (
        <Typography
          variant="caption"
          sx={{ display: 'flex', alignItems: 'center', gap: 0.5, color: tokens.textMuted, mt: 1 }}
        >
          <Favorite sx={{ fontSize: 15 }} /> No heart rate in the track or in your health data for
          this time.
        </Typography>
      )}
      {detail.heartRateSource === 'health' && (
        <Typography variant="caption" component="div" sx={{ color: tokens.textMuted }}>
          From your health data (the track has none)
        </Typography>
      )}
      {speedR && speed.length > 1 && (
        <SyncedChart
          {...common}
          label={pace ? 'Pace' : 'Speed'}
          color={SPEED_COLOR}
          series={speed}
          range={pace ? ensureSpan(speedR, 30) : ensureSpan(speedR, 2)}
          format={pace ? (v) => formatPace(v) : (v) => `${v.toFixed(1)} km/h`}
          invert={pace}
          idle={
            isSet(avgSpeed)
              ? `avg ${pace ? formatPace(3600 / Math.max(0.1, avgSpeed)) : `${avgSpeed.toFixed(1)} km/h`} moving`
              : undefined
          }
        />
      )}
      {ele.length > 1 && eleRange && (
        <SyncedChart
          {...common}
          label="Elevation"
          color={ELEVATION_COLOR}
          series={ele}
          range={ensureSpan(eleRange, 10)}
          format={(v) => `${Math.round(v)} m`}
          area
          idle={isSet(s.elevationGainM) ? `+${Math.round(s.elevationGainM)} m` : undefined}
        />
      )}
      {cad.length > 1 && cadRange && (
        <SyncedChart
          {...common}
          label="Cadence"
          color={CADENCE_COLOR}
          series={cad}
          range={ensureSpan(cadRange, 10)}
          format={(v) => `${Math.round(v)} ${unit}`}
          idle={isSet(avgCadence) ? `avg ${Math.round(avgCadence)} ${unit}` : undefined}
        />
      )}
      <Box
        sx={{
          display: 'flex',
          justifyContent: 'space-between',
          mt: 0.25,
          fontSize: 11,
          color: tokens.textMuted,
        }}
      >
        <span>{xLabel(0)}</span>
        <Box component="span" sx={{ color: isSet(cursorT) ? tokens.text : undefined }}>
          {isSet(cursorT)
            ? [formatClock(cursorT), isSet(cursorD) ? formatDistance(cursorD) || '0 m' : '']
                .filter(Boolean)
                .join(' · ')
            : xLabel(maxX / 2)}
        </Box>
        <span>{xLabel(maxX)}</span>
      </Box>
    </Box>
  );
};

export default ActivityCharts;
