/**
 * @fileoverview Parts of the activity analysis below the charts: the moving
 * summary, splits, effort (drift, best efforts) and the comparison with the
 * user's other activities (same route, same type)
 * @module Contacts/atlas/AnalysisSections
 */

import React, { useState } from 'react';
import { Box, Button, ButtonBase, Chip, Tooltip, Typography } from '@mui/material';
import { InfoOutlined } from '@mui/icons-material';
import type { ActivityAnalysis, BestEffort, Drift, Similar, Split } from './types';
import { isSet } from './types';
import {
  cadenceUnit,
  effortLabel,
  formatClock,
  formatPace,
  formatSpeedOrPace,
  splitLabel,
  splitScale,
  typePlural,
} from './analysis';
import { formatDuration } from './geo';
import { tokens } from '../theme/contactsTheme';
import { fs } from '../settings/uiSettings';

/** Faster, better */
const GOOD_COLOR = '#5fd38d';
/** The bars of the splits */
const BAR_COLOR = '#3fb6ff';

/** "3 Sep 2026" */
const formatDate = (iso: string): string =>
  new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });

/** "< 1 min" below a minute, else as formatDuration */
const shortDuration = (s: number): string =>
  s > 0 && s < 60 ? '< 1 min' : formatDuration(s) || '—';

/** Props for AnalysisSummary */
export interface AnalysisSummaryProps {
  analysis: ActivityAnalysis;
  /** Activity type (cadence unit) */
  type: string;
}

/**
 * Moving time, stopped time, moving speed or pace, top speed and cadence as tiles.
 * @param props - Component props
 * @returns Tiles element
 */
export const AnalysisSummary: React.FC<AnalysisSummaryProps> = ({ analysis: a, type }) => {
  const tiles: { label: string; value: string; title?: string }[] = [
    { label: 'Moving', value: shortDuration(a.movingS), title: 'Time while moving' },
    {
      label: 'Stopped',
      value: a.stoppedS > 0 ? shortDuration(a.stoppedS) : 'none',
      title: 'Time standing still (left out of the moving averages)',
    },
    {
      label: a.pace ? 'Avg pace' : 'Moving speed',
      value: formatSpeedOrPace(a.avgMovingSpeedKmh, a.pace),
      title: 'Average while moving',
    },
    {
      label: a.pace ? 'Best pace' : 'Max speed',
      value: formatSpeedOrPace(a.maxSpeedKmh, a.pace),
      title: 'The fastest 30 seconds',
    },
    {
      label: 'Cadence',
      value: isSet(a.avgCadence) ? `${Math.round(a.avgCadence)} ${cadenceUnit(type)}` : '',
      title: 'Average cadence',
    },
  ].filter((t) => t.value);
  return (
    <Box
      sx={{ display: 'grid', gridTemplateColumns: 'repeat(3, minmax(0, 1fr))', gap: 0.75, mt: 1 }}
    >
      {tiles.map((t) => (
        <Box
          key={t.label}
          title={t.title}
          sx={{ px: 1, py: 0.5, borderRadius: '8px', bgcolor: tokens.page, minWidth: 0 }}
        >
          <Typography variant="caption" component="div" noWrap sx={{ color: tokens.textMuted }}>
            {t.label}
          </Typography>
          <Typography variant="body2" noWrap sx={{ fontWeight: 600 }}>
            {t.value}
          </Typography>
        </Box>
      ))}
    </Box>
  );
};

/** Rows of the splits table shown before "Show all" */
const SPLITS_SHOWN = 12;

/** Columns of the splits table: where, pace/speed with its bar, time, heart rate, climb */
const SPLIT_COLUMNS = '36px minmax(0, 1fr) 52px 34px 40px';

/** Props for SplitsTable */
export interface SplitsTableProps {
  splits: Split[];
  splitM: number;
  pace: boolean;
}

/**
 * The splits as a compact table with a bar per row (longer = faster); the
 * fastest split is highlighted.
 * @param props - Component props
 * @returns Table element
 */
export const SplitsTable: React.FC<SplitsTableProps> = ({ splits, splitM, pace }) => {
  const [all, setAll] = useState(false);
  const scale = splitScale(splits, splitM);
  let end = 0;
  const rows = splits.map((s, i) => {
    end += s.distanceM;
    return { s, i, label: splitLabel(end, splitM) };
  });
  const shown = all ? rows : rows.slice(0, SPLITS_SHOWN);
  const cellSx = { fontSize: fs(12.5), textAlign: 'right', whiteSpace: 'nowrap' } as const;
  return (
    <Box role="table" aria-label="Splits">
      <Box
        role="row"
        sx={{
          display: 'grid',
          gridTemplateColumns: SPLIT_COLUMNS,
          gap: 0.75,
          pb: 0.25,
          color: tokens.textMuted,
          fontSize: fs(11),
        }}
      >
        <span role="columnheader">{splitM >= 1000 ? 'km' : 'm'}</span>
        <span role="columnheader">{pace ? 'pace' : 'speed'}</span>
        <Box component="span" role="columnheader" sx={{ textAlign: 'right' }}>
          time
        </Box>
        <Box component="span" role="columnheader" sx={{ textAlign: 'right' }}>
          bpm
        </Box>
        <Box component="span" role="columnheader" sx={{ textAlign: 'right' }}>
          climb
        </Box>
      </Box>
      {shown.map(({ s, i, label }) => {
        const fastest = i === scale.fastest;
        return (
          <Box
            key={s.index}
            role="row"
            title={fastest ? 'The fastest split' : undefined}
            sx={{
              display: 'grid',
              gridTemplateColumns: SPLIT_COLUMNS,
              gap: 0.75,
              alignItems: 'center',
              py: '2px',
              color: fastest ? tokens.text : tokens.textSecondary,
              fontWeight: fastest ? 600 : 400,
            }}
          >
            <Box role="cell" sx={{ fontSize: fs(12.5), color: tokens.textMuted }}>
              {label}
            </Box>
            <Box role="cell" sx={{ position: 'relative', height: 18, minWidth: 0 }}>
              <Box
                sx={{
                  position: 'absolute',
                  left: 0,
                  top: 2,
                  bottom: 2,
                  width: `${Math.round(scale.widths[i] * 100)}%`,
                  borderRadius: '3px',
                  bgcolor: fastest ? GOOD_COLOR : BAR_COLOR,
                  opacity: fastest ? 0.45 : 0.22,
                }}
              />
              <Box
                sx={{
                  position: 'relative',
                  px: 0.5,
                  fontSize: fs(12.5),
                  lineHeight: '18px',
                  whiteSpace: 'nowrap',
                }}
              >
                {pace ? formatPace(s.paceSPerKm) : `${s.speedKmh.toFixed(1)} km/h`}
              </Box>
            </Box>
            <Box role="cell" sx={cellSx}>
              {formatClock(s.timeS)}
            </Box>
            <Box role="cell" sx={cellSx}>
              {isSet(s.avgHr) ? Math.round(s.avgHr) : '—'}
            </Box>
            <Box role="cell" sx={cellSx}>
              {Math.round(s.climbM)} m
            </Box>
          </Box>
        );
      })}
      {rows.length > SPLITS_SHOWN && (
        <Button size="small" onClick={() => setAll((v) => !v)} sx={{ mt: 0.25, ml: -0.75 }}>
          {all ? 'Show fewer' : `Show all ${rows.length} splits`}
        </Button>
      )}
    </Box>
  );
};

/** Colour of a drift verdict */
const DRIFT_COLOR: Record<Drift['verdict'], string> = {
  steady: GOOD_COLOR,
  drifting: tokens.attention,
  fading: '#ff4d6d',
};

/** Props for EffortSection */
export interface EffortSectionProps {
  drift: Drift | null;
  bestEfforts: BestEffort[];
  pace: boolean;
  /** Moves the cursor to a moment (seconds from the start) */
  onSeek: (t: number) => void;
}

/**
 * Heart-rate drift and the best efforts.
 * @param props - Component props
 * @returns Section content
 */
export const EffortSection: React.FC<EffortSectionProps> = ({
  drift,
  bestEfforts,
  pace,
  onSeek,
}) => (
  <Box>
    {drift && (
      <Box sx={{ mb: bestEfforts.length ? 1 : 0 }}>
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }}>
          <Typography variant="body2">
            Heart-rate drift{' '}
            <Box component="span" sx={{ fontWeight: 600 }}>
              {drift.decouplingPct.toFixed(1)}%
            </Box>{' '}
            —{' '}
            <Box component="span" sx={{ color: DRIFT_COLOR[drift.verdict], fontWeight: 600 }}>
              {drift.verdict}
            </Box>
          </Typography>
          <Tooltip title="Speed per heartbeat in the second half against the first half. Under 5% is steady: your heart rate kept pace with your speed. 5–10% is drifting and over 10% fading: the same speed cost more later on (fatigue, heat, too fast a start).">
            <InfoOutlined sx={{ fontSize: fs(15), color: tokens.textMuted }} />
          </Tooltip>
        </Box>
        <Typography variant="caption" component="div" sx={{ color: tokens.textMuted }}>
          1st half {formatSpeedOrPace(drift.firstHalf.speedKmh, pace)} ·{' '}
          {Math.round(drift.firstHalf.avgHr)} bpm → 2nd half{' '}
          {formatSpeedOrPace(drift.secondHalf.speedKmh, pace)} ·{' '}
          {Math.round(drift.secondHalf.avgHr)} bpm
        </Typography>
      </Box>
    )}
    {bestEfforts.length > 0 && (
      <>
        <Typography variant="caption" component="div" sx={{ color: tokens.textMuted }}>
          Best efforts · click to show where
        </Typography>
        {bestEfforts.map((e) => (
          <ButtonBase
            key={e.distanceM}
            onClick={() => onSeek(e.startOffsetS)}
            sx={{
              display: 'flex',
              alignItems: 'center',
              gap: 1,
              width: '100%',
              justifyContent: 'flex-start',
              textAlign: 'left',
              py: 0.375,
              px: 0.5,
              borderRadius: '6px',
              '&:hover': { bgcolor: tokens.hover },
            }}
          >
            <Typography variant="body2" noWrap sx={{ flex: 1, minWidth: 0 }}>
              {effortLabel(e.distanceM)}
            </Typography>
            {e.allTimeRank === 1 ? (
              <Tooltip title="Your fastest of this distance">
                <Chip
                  size="small"
                  label="PB"
                  sx={{
                    height: 18,
                    fontSize: fs(12),
                    fontWeight: 700,
                    bgcolor: GOOD_COLOR,
                    color: tokens.page,
                  }}
                />
              </Tooltip>
            ) : (
              isSet(e.allTimeRank) && (
                <Typography
                  variant="caption"
                  title="Among all your efforts of this distance"
                  sx={{ color: tokens.textMuted }}
                >
                  #{e.allTimeRank}
                </Typography>
              )
            )}
            <Typography variant="body2" sx={{ fontWeight: 600, width: 64, textAlign: 'right' }}>
              {formatClock(e.timeS)}
            </Typography>
            <Typography
              variant="caption"
              noWrap
              sx={{ color: tokens.textSecondary, width: 72, textAlign: 'right' }}
            >
              {pace ? formatPace(e.paceSPerKm) : `${e.speedKmh.toFixed(1)} km/h`}
            </Typography>
          </ButtonBase>
        ))}
      </>
    )}
  </Box>
);

/** Route rows shown before "Show all" */
const ROUTE_SHOWN = 5;
const DOTS_W = 340;
const DOTS_H = 64;

/**
 * "#2 of 7 on this route", "Fastest of 7 on this route".
 * @param similar - The comparison
 * @returns Text; '' when there is no route to compare with
 */
export const routeRankText = (similar: Similar): string => {
  if (similar.ofRoute < 2 || !isSet(similar.rankBySpeed)) {
    return '';
  }
  return similar.rankBySpeed === 1
    ? `Fastest of ${similar.ofRoute} on this route`
    : `#${similar.rankBySpeed} of ${similar.ofRoute} on this route`;
};

/** Props for ComparedSection */
export interface ComparedSectionProps {
  similar: Similar;
  /** This activity */
  current: { id: string; startTime: string; speedKmh: number | null; durationS: number | null };
  type: string;
  pace: boolean;
  onOpenActivity: (id: string) => void;
}

/**
 * The activity against the same route and the same type.
 * @param props - Component props
 * @returns Section content
 */
export const ComparedSection: React.FC<ComparedSectionProps> = ({
  similar,
  current,
  type,
  pace,
  onOpenActivity,
}) => {
  const [all, setAll] = useState(false);
  const route = similar.sameRoute;
  const rank = routeRankText(similar);
  const dots = [
    ...route
      .filter((r) => isSet(r.speedKmh))
      .map((r) => ({ id: r.id, t: Date.parse(r.startTime), v: r.speedKmh as number, me: false })),
    ...(isSet(current.speedKmh)
      ? [{ id: current.id, t: Date.parse(current.startTime), v: current.speedKmh, me: true }]
      : []),
  ];
  const tMin = Math.min(...dots.map((d) => d.t));
  const tMax = Math.max(...dots.map((d) => d.t));
  const vMin = Math.min(...dots.map((d) => d.v));
  const vMax = Math.max(...dots.map((d) => d.v));
  const dx = (t: number) => 8 + ((t - tMin) / Math.max(1, tMax - tMin)) * (DOTS_W - 16);
  const dy = (v: number) => DOTS_H - 8 - ((v - vMin) / Math.max(0.1, vMax - vMin)) * (DOTS_H - 16);
  const st = similar.sameType;
  const plural = typePlural(type);
  return (
    <Box>
      {rank && (
        <Typography variant="body2" sx={{ fontWeight: 600 }}>
          {rank}
        </Typography>
      )}
      {route.length > 0 && (isSet(similar.avgSpeedKmh) || isSet(similar.avgHr)) && (
        <Typography variant="caption" component="div" sx={{ color: tokens.textMuted }}>
          Route average{' '}
          {[
            formatSpeedOrPace(similar.avgSpeedKmh, pace),
            isSet(similar.avgHr) ? `${Math.round(similar.avgHr)} bpm` : '',
          ]
            .filter(Boolean)
            .join(' · ')}
        </Typography>
      )}
      {dots.length >= 2 && (
        <svg
          width="100%"
          viewBox={`0 0 ${DOTS_W} ${DOTS_H}`}
          style={{ display: 'block', marginTop: 4 }}
          role="img"
          aria-label="Your times on this route: faster is higher, newer is to the right"
        >
          {isSet(similar.avgSpeedKmh) && (
            <line
              x1={0}
              x2={DOTS_W}
              y1={dy(similar.avgSpeedKmh)}
              y2={dy(similar.avgSpeedKmh)}
              stroke={tokens.borderStrong}
              strokeDasharray="3 3"
            />
          )}
          {dots.map((d) => (
            <g
              key={d.id}
              style={{ cursor: d.me ? 'default' : 'pointer' }}
              onClick={() => {
                if (!d.me) {
                  onOpenActivity(d.id);
                }
              }}
            >
              <title>
                {`${d.me ? 'This one' : formatDate(new Date(d.t).toISOString())}: ${formatSpeedOrPace(d.v, pace)}`}
              </title>
              <circle
                cx={dx(d.t)}
                cy={dy(d.v)}
                r={d.me ? 5 : 3.5}
                fill={d.me ? tokens.accent : tokens.textSecondary}
                stroke={d.me ? '#fff' : 'none'}
                strokeWidth={1.5}
                opacity={d.me ? 1 : 0.75}
              />
            </g>
          ))}
        </svg>
      )}
      {route.length > 0 && (
        <Box sx={{ mt: 0.5 }}>
          {(all ? route : route.slice(0, ROUTE_SHOWN)).map((r) => (
            <ButtonBase
              key={r.id}
              onClick={() => onOpenActivity(r.id)}
              sx={{
                display: 'flex',
                alignItems: 'center',
                gap: 1,
                width: '100%',
                justifyContent: 'flex-start',
                textAlign: 'left',
                py: 0.375,
                px: 0.5,
                borderRadius: '6px',
                '&:hover': { bgcolor: tokens.hover },
              }}
            >
              <Typography variant="body2" noWrap sx={{ flex: 1, minWidth: 0 }}>
                {formatDate(r.startTime)}
              </Typography>
              <Typography variant="caption" noWrap sx={{ color: tokens.textSecondary }}>
                {[
                  formatDuration(r.durationS),
                  formatSpeedOrPace(r.speedKmh, pace),
                  isSet(r.avgHr) ? `${Math.round(r.avgHr)} bpm` : '',
                ]
                  .filter(Boolean)
                  .join(' · ')}
              </Typography>
            </ButtonBase>
          ))}
          {route.length > ROUTE_SHOWN && (
            <Button size="small" onClick={() => setAll((v) => !v)} sx={{ ml: -0.75 }}>
              {all ? 'Show fewer' : `Show all ${route.length}`}
            </Button>
          )}
        </Box>
      )}
      {st.count >= 3 && (isSet(st.speedPercentile) || isSet(st.distancePercentile)) && (
        <Typography
          variant="body2"
          sx={{ mt: route.length > 0 || rank ? 1 : 0, color: tokens.textSecondary }}
        >
          {isSet(st.speedPercentile) && (
            <>
              Faster than{' '}
              <Box component="span" sx={{ fontWeight: 600, color: tokens.text }}>
                {Math.round(st.speedPercentile)}%
              </Box>{' '}
              of your {plural} over the past year
              {isSet(st.distancePercentile) ? '; ' : '.'}
            </>
          )}
          {isSet(st.distancePercentile) && (
            <>
              {isSet(st.speedPercentile) ? 'longer than ' : 'Longer than '}
              <Box component="span" sx={{ fontWeight: 600, color: tokens.text }}>
                {Math.round(st.distancePercentile)}%
              </Box>
              {isSet(st.speedPercentile) ? '.' : ` of your ${plural} over the past year.`}
            </>
          )}
          <Box component="span" sx={{ color: tokens.textMuted }}>
            {' '}
            ({st.count} {plural})
          </Box>
        </Typography>
      )}
    </Box>
  );
};
