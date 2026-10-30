/**
 * @fileoverview Insights beside the map for the current filters: proposals,
 * totals against the period before, types, trend, training load, time in
 * heart-rate zones, trends per sport, when, records, places, streaks and
 * travel, data quality, and "Explain with AI"
 * @module Contacts/atlas/InsightsPanel
 */

import React, { useEffect, useMemo, useRef, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  ButtonBase,
  Chip,
  LinearProgress,
  Link,
  Skeleton,
  ToggleButton,
  ToggleButtonGroup,
  Tooltip,
  Typography,
} from '@mui/material';
import {
  AutoAwesome,
  InfoOutlined,
  LightbulbOutlined,
  Place,
  WarningAmber,
} from '@mui/icons-material';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import type {
  AtlasQuery,
  Insights,
  InsightsProposal,
  InsightsProposalAction,
  InsightsSeries,
  InsightsTotals,
  TypeTrend,
  WeekLoad,
} from './types';
import { ACTIVITY_TYPES, HEALTH_COLOR, VISIT_COLOR, isSet, sourceLabel, typeColor } from './types';
import { formatTypesFilter } from './filters';
import {
  WEEKDAYS,
  bucketLabels,
  explainPrompt,
  formatChange,
  formatHours,
  formatKm,
  formatPeriod,
  formatShare,
  labelStep,
  loadSummary,
  sparkPath,
  trendBars,
  type TrendBar,
  type TrendMetric,
} from './insights';
import { formatSignedPct } from './analysis';
import HrZonesBar from './HrZonesBar';
import ReviewsSection from './ReviewsSection';
import { tokens } from '../theme/contactsTheme';
import { fs } from '../settings/uiSettings';

/** A filter a proposal or a link applies */
export interface InsightsFilter {
  types?: string[];
  conflictsOnly?: boolean;
}

/** Props for InsightsPanel */
export interface InsightsPanelProps {
  /** The current filters (the same as the search, without a limit) */
  query: AtlasQuery;
  /** The panel is on screen: it loads only then */
  active: boolean;
  /** Bumped when the data changed (imports, edits) */
  reloadKey: number;
  /** Shows "Explain with AI" */
  assistantEnabled: boolean;
  onOpenActivity: (id: string) => void;
  /** Shows a place: the area filter around it, with the results */
  onShowPlace: (lat: number, lon: number) => void;
  onApplyFilter: (filter: InsightsFilter) => void;
  onOpenFusion: () => void;
  onOpenImport: () => void;
  /** Opens the assistant with a question */
  onExplain: (prompt: string) => void;
}

/** More than before */
const UP_COLOR = '#5fd38d';

/** Icon and colour per proposal severity */
const SEVERITY: Record<InsightsProposal['severity'], { color: string; icon: React.ReactElement }> =
  {
    info: { color: tokens.accent, icon: <InfoOutlined fontSize="small" /> },
    tip: { color: UP_COLOR, icon: <LightbulbOutlined fontSize="small" /> },
    warning: { color: tokens.attention, icon: <WarningAmber fontSize="small" /> },
  };

/** "3 Sep 2026" */
const formatDate = (iso: string): string =>
  new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });

/** Label of a proposal's button */
const actionLabel = (action: InsightsProposalAction): string => {
  switch (action.kind) {
    case 'activity':
      return 'Open';
    case 'place':
      return 'Show on map';
    case 'filter':
      if (action.conflictsOnly) {
        return 'Show them';
      }
      if (action.types?.length === 1) {
        return `Show ${formatTypesFilter(action.types).toLowerCase()}s`;
      }
      return action.types?.length ? `Show ${formatTypesFilter(action.types)}` : 'Show';
    case 'fusion':
      return 'Fusion settings';
    case 'import':
      return 'Import';
    default:
      return 'Show';
  }
};

/** A titled block */
const Section: React.FC<{ title: string; extra?: React.ReactNode; children: React.ReactNode }> = ({
  title,
  extra,
  children,
}) => (
  <Box component="section" sx={{ px: 1.5, pt: 1.5 }}>
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 0.5, minHeight: 24 }}>
      <Typography variant="subtitle2" sx={{ color: tokens.textSecondary }}>
        {title}
      </Typography>
      <Box sx={{ flex: 1 }} />
      {extra}
    </Box>
    {children}
  </Box>
);

/** One proposal */
const ProposalCard: React.FC<{
  proposal: InsightsProposal;
  onAction: (action: InsightsProposalAction) => void;
}> = ({ proposal, onAction }) => {
  const sev = SEVERITY[proposal.severity] ?? SEVERITY.info;
  return (
    <Box
      sx={{
        display: 'flex',
        gap: 1,
        p: 1,
        mb: 0.75,
        borderRadius: '8px',
        border: `1px solid ${tokens.border}`,
        borderLeft: `3px solid ${sev.color}`,
        bgcolor: tokens.raised,
      }}
    >
      <Box sx={{ color: sev.color, mt: 0.125, display: 'flex' }}>{sev.icon}</Box>
      <Box sx={{ minWidth: 0, flex: 1 }}>
        <Typography variant="body2" sx={{ fontWeight: 600 }}>
          {proposal.title}
        </Typography>
        <Typography variant="caption" component="p" sx={{ color: tokens.textSecondary }}>
          {proposal.text}
        </Typography>
        {proposal.action && (
          <Button
            size="small"
            onClick={() => proposal.action && onAction(proposal.action)}
            sx={{ mt: 0.5, ml: -0.75, minWidth: 0, py: 0 }}
          >
            {actionLabel(proposal.action)}
          </Button>
        )}
      </Box>
    </Box>
  );
};

/** One total with its change against the period before */
const TotalTile: React.FC<{ label: string; value: string; now: number; previous?: number }> = ({
  label,
  value,
  now,
  previous,
}) => {
  const change = formatChange(now, previous);
  return (
    <Box sx={{ minWidth: 0 }}>
      <Typography variant="body1" noWrap sx={{ fontWeight: 600 }}>
        {value}
      </Typography>
      <Typography variant="caption" component="div" noWrap sx={{ color: tokens.textMuted }}>
        {label}
        {change && (
          <Box
            component="span"
            title="Against the period before"
            sx={{ ml: 0.75, color: change.up ? UP_COLOR : tokens.textMuted, fontWeight: 600 }}
          >
            {change.text}
          </Box>
        )}
      </Typography>
    </Box>
  );
};

/** The totals as tiles */
const TotalsGrid: React.FC<{ totals: InsightsTotals; previous: InsightsTotals | null }> = ({
  totals,
  previous,
}) => (
  <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(3, minmax(0, 1fr))', gap: 1 }}>
    <TotalTile
      label="activities"
      value={totals.activities.toLocaleString('en-GB')}
      now={totals.activities}
      previous={previous?.activities}
    />
    <TotalTile
      label="distance"
      value={formatKm(totals.distanceKm)}
      now={totals.distanceKm}
      previous={previous?.distanceKm}
    />
    <TotalTile
      label="time"
      value={formatHours(totals.hours)}
      now={totals.hours}
      previous={previous?.hours}
    />
    <TotalTile
      label="climb"
      value={`${Math.round(totals.climbM).toLocaleString('en-GB')} m`}
      now={totals.climbM}
      previous={previous?.climbM}
    />
    <TotalTile
      label="visits"
      value={totals.visits.toLocaleString('en-GB')}
      now={totals.visits}
      previous={previous?.visits}
    />
    <TotalTile
      label="places"
      value={totals.places.toLocaleString('en-GB')}
      now={totals.places}
      previous={previous?.places}
    />
  </Box>
);

const TREND_W = 340;
const TREND_H = 84;
const LABEL_ROW = 14;
const YEAR_ROW = 13;

/** A value of the trend chart with its unit */
const formatMetric = (v: number, metric: TrendMetric): string =>
  metric === 'distance' ? formatKm(v) : metric === 'time' ? formatHours(v) : String(v);

/** Tooltip of a trend bar: "Mar 2026: 120 km (Ride 100 km, Run 20 km)" */
const barTitle = (bar: TrendBar, label: string, metric: TrendMetric): string => {
  const total = `${label}: ${formatMetric(Math.round(bar.total * 10) / 10, metric)}`;
  if (bar.segments.length < 2) {
    return total;
  }
  const parts = bar.segments.map(
    (s) =>
      `${ACTIVITY_TYPES[s.type]?.label ?? s.type} ${formatMetric(Math.round(s.value * 10) / 10, metric)}`,
  );
  return `${total} (${parts.join(', ')})`;
};

/** Stacked bars per bucket, coloured by type */
const TrendChart: React.FC<{ series: InsightsSeries; metric: TrendMetric }> = ({
  series,
  metric,
}) => {
  const bars = useMemo(() => trendBars(series.points, series.bucket, metric), [series, metric]);
  const labels = useMemo(
    () =>
      bucketLabels(
        bars.map((b) => b.t),
        series.bucket,
      ),
    [bars, series.bucket],
  );
  if (bars.length === 0) {
    return null;
  }
  const max = Math.max(...bars.map((b) => b.total), 1e-9);
  const slot = TREND_W / bars.length;
  const barW = Math.max(1, slot * 0.75);
  const step = labelStep(bars.length, 6);
  // Years where a new one starts, without overlapping the next
  const years = labels
    .map((l, i) => ({ x: i * slot, year: l.year }))
    .filter((y): y is { x: number; year: string } => isSet(y.year))
    .filter((y, i, all) => i === all.length - 1 || all[i + 1].x - y.x > 30);
  const height = TREND_H + LABEL_ROW + YEAR_ROW;
  return (
    <svg
      width="100%"
      viewBox={`0 0 ${TREND_W} ${height}`}
      style={{ display: 'block' }}
      role="img"
      aria-label={`Trend by ${series.bucket}`}
    >
      {bars.map((b, i) => {
        let y = TREND_H;
        const l = labels[i];
        return (
          <g key={b.t}>
            <title>{barTitle(b, `${l.label} ${new Date(b.t).getFullYear()}`, metric)}</title>
            {/* A transparent column so the whole slot shows the tooltip */}
            <rect x={i * slot} y={0} width={slot} height={TREND_H} fill="transparent" />
            {b.segments.map((s) => {
              const h = (s.value / max) * (TREND_H - 4);
              y -= h;
              return (
                <rect
                  key={s.type}
                  x={i * slot + (slot - barW) / 2}
                  y={y}
                  width={barW}
                  height={h}
                  fill={typeColor(s.type)}
                  opacity={0.9}
                />
              );
            })}
          </g>
        );
      })}
      <line x1={0} x2={TREND_W} y1={TREND_H} y2={TREND_H} stroke={tokens.border} />
      {labels.map((l, i) =>
        i % step === 0 ? (
          <text
            key={bars[i].t}
            x={i * slot + 1}
            y={TREND_H + LABEL_ROW - 3}
            style={{ fontSize: fs.chart(11) }}
            fill={tokens.textMuted}
          >
            {l.label}
          </text>
        ) : null,
      )}
      {years.map((y) => (
        <text
          key={y.year}
          x={y.x + 1}
          y={height - 2}
          style={{ fontSize: fs.chart(11) }}
          fontWeight={600}
          fill={tokens.textSecondary}
        >
          {y.year}
        </text>
      ))}
    </svg>
  );
};

const COUNT_W = 340;
const COUNT_H = 54;

/** Simple bars of counts (weekdays, hours) */
const CountBars: React.FC<{
  label: string;
  values: number[];
  /** Axis label of each bar ('' for none) */
  names: string[];
  /** Tooltip of each bar */
  titles: string[];
}> = ({ label, values, names, titles }) => {
  const max = Math.max(1, ...values);
  const slot = COUNT_W / Math.max(1, values.length);
  const barW = Math.max(1, slot * 0.7);
  return (
    <svg
      width="100%"
      viewBox={`0 0 ${COUNT_W} ${COUNT_H + LABEL_ROW}`}
      style={{ display: 'block' }}
      role="img"
      aria-label={label}
    >
      {values.map((v, i) => {
        const h = (v / max) * (COUNT_H - 4);
        return (
          <g key={i}>
            <title>{titles[i]}</title>
            <rect x={i * slot} y={0} width={slot} height={COUNT_H} fill="transparent" />
            <rect
              x={i * slot + (slot - barW) / 2}
              y={COUNT_H - h}
              width={barW}
              height={h}
              rx={1}
              fill={tokens.accent}
              opacity={0.85}
            />
            {names[i] && (
              <text
                x={i * slot + slot / 2}
                y={COUNT_H + LABEL_ROW - 3}
                style={{ fontSize: fs.chart(11) }}
                textAnchor="middle"
                fill={tokens.textMuted}
              >
                {names[i]}
              </text>
            )}
          </g>
        );
      })}
      <line x1={0} x2={COUNT_W} y1={COUNT_H} y2={COUNT_H} stroke={tokens.border} />
    </svg>
  );
};

const LOAD_H = 70;

/** Weekly training load as bars, hours as a line; the latest week highlighted */
const LoadChart: React.FC<{ weekly: WeekLoad[] }> = ({ weekly }) => {
  const times = useMemo(() => weekly.map((w) => Date.parse(w.week)), [weekly]);
  const labels = useMemo(() => bucketLabels(times, 'week'), [times]);
  if (weekly.length === 0) {
    return null;
  }
  const maxLoad = Math.max(1e-9, ...weekly.map((w) => w.load));
  const maxHours = Math.max(1e-9, ...weekly.map((w) => w.hours));
  const slot = TREND_W / weekly.length;
  const barW = Math.max(1, slot * 0.7);
  const step = labelStep(weekly.length, 5);
  const hy = (h: number) => LOAD_H - 3 - (h / maxHours) * (LOAD_H - 8);
  const hoursLine = weekly
    .map((w, i) => `${i ? 'L' : 'M'}${(i * slot + slot / 2).toFixed(1)},${hy(w.hours).toFixed(1)}`)
    .join('');
  return (
    <svg
      width="100%"
      viewBox={`0 0 ${TREND_W} ${LOAD_H + LABEL_ROW}`}
      style={{ display: 'block' }}
      role="img"
      aria-label="Training load per week"
    >
      {weekly.map((w, i) => {
        const h = (w.load / maxLoad) * (LOAD_H - 4);
        const latest = i === weekly.length - 1;
        return (
          <g key={w.week}>
            <title>
              {`Week of ${labels[i].label} ${new Date(times[i]).getFullYear()}: load ${Math.round(w.load)} · ${formatHours(w.hours)} · ${formatKm(w.distanceKm)}`}
            </title>
            <rect x={i * slot} y={0} width={slot} height={LOAD_H} fill="transparent" />
            <rect
              x={i * slot + (slot - barW) / 2}
              y={LOAD_H - h}
              width={barW}
              height={h}
              rx={1}
              fill={latest ? UP_COLOR : tokens.accent}
              opacity={latest ? 1 : 0.6}
            />
          </g>
        );
      })}
      {weekly.length > 1 && (
        <path
          d={hoursLine}
          fill="none"
          stroke={tokens.textSecondary}
          strokeWidth={1.25}
          opacity={0.8}
          pointerEvents="none"
        />
      )}
      <line x1={0} x2={TREND_W} y1={LOAD_H} y2={LOAD_H} stroke={tokens.border} />
      {labels.map((l, i) =>
        i % step === 0 ? (
          <text
            key={weekly[i].week}
            // Labels near the right edge end at it instead of running off
            x={i * slot > TREND_W * 0.85 ? TREND_W - 1 : i * slot + 1}
            textAnchor={i * slot > TREND_W * 0.85 ? 'end' : 'start'}
            y={LOAD_H + LABEL_ROW - 3}
            style={{ fontSize: fs.chart(11) }}
            fill={tokens.textMuted}
          >
            {l.label}
          </text>
        ) : null,
      )}
    </svg>
  );
};

const SPARK_W = 1000;
const SPARK_H = 34;

/** One sport's trend: speed as a sparkline with average heart rate faintly behind */
const TypeTrendRow: React.FC<{ trend: TypeTrend }> = ({ trend }) => {
  const speed = useMemo(
    () =>
      sparkPath(
        trend.points.map((p) => ({ t: Date.parse(p.t), v: p.speedKmh })),
        SPARK_W,
        SPARK_H,
      ),
    [trend],
  );
  const hr = useMemo(
    () =>
      sparkPath(
        trend.points.map((p) => ({ t: Date.parse(p.t), v: p.avgHr })),
        SPARK_W,
        SPARK_H,
      ),
    [trend],
  );
  const speedChange = formatSignedPct(trend.speedChangePct);
  const hrChange = formatSignedPct(trend.hrChangePct);
  return (
    <Box sx={{ py: 0.75, borderBottom: `1px solid ${tokens.border}` }}>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
        <Box
          sx={{
            width: 10,
            height: 10,
            borderRadius: '50%',
            bgcolor: typeColor(trend.type),
            flexShrink: 0,
          }}
        />
        <Typography variant="body2" sx={{ fontWeight: 600 }}>
          {ACTIVITY_TYPES[trend.type]?.label ?? trend.type}
        </Typography>
        <Typography variant="caption" sx={{ color: tokens.textMuted }}>
          {trend.points.length}×
        </Typography>
        <Box sx={{ flex: 1 }} />
        <Typography
          variant="caption"
          noWrap
          title="Average of the last third of the period against the first third"
          sx={{ color: tokens.textSecondary, minWidth: 0 }}
        >
          {speedChange && (
            <Box
              component="span"
              sx={{
                color: (trend.speedChangePct ?? 0) >= 0.5 ? UP_COLOR : tokens.textSecondary,
                fontWeight: 600,
              }}
            >
              speed {speedChange}
            </Box>
          )}
          {speedChange && hrChange ? ' · ' : ''}
          {hrChange && `HR ${hrChange}`}
          {(speedChange || hrChange) && (
            <Box component="span" sx={{ color: tokens.textMuted }}>
              {' '}
              (last third vs first)
            </Box>
          )}
        </Typography>
      </Box>
      {(speed || hr) && (
        <svg
          width="100%"
          height={SPARK_H}
          viewBox={`0 0 ${SPARK_W} ${SPARK_H}`}
          preserveAspectRatio="none"
          style={{ display: 'block', marginTop: 2 }}
          role="img"
          aria-label={`${ACTIVITY_TYPES[trend.type]?.label ?? trend.type}: speed${hr ? ' and average heart rate' : ''} per activity`}
        >
          {hr && (
            <path
              d={hr}
              fill="none"
              stroke={HEALTH_COLOR}
              strokeWidth={1}
              opacity={0.35}
              vectorEffect="non-scaling-stroke"
            />
          )}
          {speed && (
            <path
              d={speed}
              fill="none"
              stroke={typeColor(trend.type)}
              strokeWidth={1.5}
              strokeLinejoin="round"
              vectorEffect="non-scaling-stroke"
            />
          )}
        </svg>
      )}
    </Box>
  );
};

/** A label and its value on one line */
const Fact: React.FC<{ label: string; children: React.ReactNode }> = ({ label, children }) => (
  <Box sx={{ display: 'flex', gap: 1, alignItems: 'baseline', py: 0.25 }}>
    <Typography variant="caption" sx={{ color: tokens.textMuted, width: 128, flexShrink: 0 }}>
      {label}
    </Typography>
    <Typography variant="body2" component="div" sx={{ minWidth: 0 }}>
      {children}
    </Typography>
  </Box>
);

/** Rows that open something */
const rowSx = {
  display: 'flex',
  justifyContent: 'flex-start',
  alignItems: 'flex-start',
  gap: 1,
  width: '100%',
  textAlign: 'left',
  py: 0.75,
  borderBottom: `1px solid ${tokens.border}`,
} as const;

/** "1 day", "12 days" */
const days = (n: number): string => `${n.toLocaleString('en-GB')} ${n === 1 ? 'day' : 'days'}`;

/**
 * Insights panel.
 * @param props - Component props
 * @returns Panel element
 */
const InsightsPanel: React.FC<InsightsPanelProps> = ({
  query,
  active,
  reloadKey,
  assistantEnabled,
  onOpenActivity,
  onShowPlace,
  onApplyFilter,
  onOpenFusion,
  onOpenImport,
  onExplain,
}) => {
  const [data, setData] = useState<Insights | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [metric, setMetric] = useState<TrendMetric>('distance');
  // What the shown insights were loaded for, and the latest request (older answers are dropped)
  const loaded = useRef<string | null>(null);
  const latest = useRef(0);
  const key = JSON.stringify(query);

  useEffect(() => {
    const want = `${key}#${reloadKey}`;
    if (!active || loaded.current === want) {
      return undefined;
    }
    const id = ++latest.current;
    const t = setTimeout(() => {
      // Loading starts with the request: a cancelled wait never leaves the bar spinning
      setLoading(true);
      backendApiClient
        .atlasInsights(
          JSON.parse(key) as AtlasQuery,
          Intl.DateTimeFormat().resolvedOptions().timeZone,
        )
        .then((r) => {
          if (id === latest.current) {
            loaded.current = want;
            setData(r);
            setError(null);
          }
        })
        .catch((e) => {
          if (id === latest.current) {
            setError((e as Error).message || 'Insights failed');
          }
        })
        .finally(() => {
          if (id === latest.current) {
            setLoading(false);
          }
        });
    }, 300);
    return () => {
      clearTimeout(t);
      if (id === latest.current) {
        setLoading(false);
      }
    };
  }, [key, active, reloadKey]);

  const act = (action: InsightsProposalAction) => {
    switch (action.kind) {
      case 'activity':
        onOpenActivity(action.activityId);
        break;
      case 'place':
        onShowPlace(action.lat, action.lon);
        break;
      case 'filter':
        onApplyFilter({ types: action.types, conflictsOnly: action.conflictsOnly });
        break;
      case 'fusion':
        onOpenFusion();
        break;
      case 'import':
        onOpenImport();
        break;
      default:
        break;
    }
  };

  const period = data ? formatPeriod(data.from, data.to) : '';
  const weekly = data?.weekly ?? [];
  const load = loadSummary(weekly);
  const zoneSeconds = data?.zoneSeconds ?? null;
  const trends = data?.trends ?? [];
  const hasActivities = !!data && data.totals.activities > 0;

  return (
    <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', position: 'relative', pb: 2 }}>
      <Box sx={{ position: 'sticky', top: 0, zIndex: 1, height: 2 }}>
        {loading && <LinearProgress sx={{ height: 2 }} aria-label="Loading insights" />}
      </Box>
      {error && (
        <Alert severity="error" sx={{ m: 1.5, mb: 0 }} onClose={() => setError(null)}>
          {error}
        </Alert>
      )}
      {!data && !error && (
        <Box sx={{ p: 1.5 }}>
          <Skeleton variant="rounded" height={56} sx={{ mb: 1 }} />
          <Skeleton variant="rounded" height={96} sx={{ mb: 1 }} />
          <Skeleton variant="rounded" height={120} />
        </Box>
      )}
      {data && (
        <>
          {data.proposals.length > 0 && (
            <Box sx={{ px: 1.5, pt: 1.5 }}>
              {data.proposals.map((p, i) => (
                <ProposalCard key={`${p.kind}-${i}`} proposal={p} onAction={act} />
              ))}
            </Box>
          )}

          <Section title="Totals">
            <Typography
              variant="caption"
              component="div"
              sx={{ color: tokens.textMuted, mt: -0.5, mb: 1 }}
            >
              {period}
              {data.previous ? ' · change against the period before' : ''}
            </Typography>
            <TotalsGrid totals={data.totals} previous={data.previous} />
          </Section>

          {!hasActivities && data.totals.visits === 0 && (
            <Typography variant="body2" sx={{ px: 1.5, pt: 1.5, color: tokens.textMuted }}>
              Nothing in this period matches the filters.
            </Typography>
          )}

          {data.byType.length > 0 && (
            <Section title="By type">
              {data.byType.map((s) => (
                <Box
                  key={s.type}
                  sx={{
                    display: 'flex',
                    gap: 1,
                    py: 0.5,
                    borderBottom: `1px solid ${tokens.border}`,
                  }}
                >
                  <Box
                    sx={{
                      width: 10,
                      height: 10,
                      mt: 0.75,
                      borderRadius: '50%',
                      bgcolor: typeColor(s.type),
                      flexShrink: 0,
                    }}
                  />
                  <Box sx={{ minWidth: 0, flex: 1 }}>
                    <Box sx={{ display: 'flex', gap: 1 }}>
                      <Typography variant="body2" sx={{ fontWeight: 600, flex: 1 }}>
                        {ACTIVITY_TYPES[s.type]?.label ?? s.type}
                      </Typography>
                      <Typography variant="body2" sx={{ color: tokens.textSecondary }}>
                        {s.count.toLocaleString('en-GB')}×
                      </Typography>
                    </Box>
                    <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                      {[
                        s.distanceKm > 0 ? formatKm(s.distanceKm) : '',
                        formatHours(s.hours),
                        isSet(s.avgSpeedKmh) ? `${s.avgSpeedKmh.toFixed(1)} km/h` : '',
                        isSet(s.avgHeartRate) ? `${Math.round(s.avgHeartRate)} bpm` : '',
                        isSet(s.longestKm) ? `longest ${formatKm(s.longestKm)}` : '',
                      ]
                        .filter(Boolean)
                        .join(' · ')}
                    </Typography>
                  </Box>
                </Box>
              ))}
            </Section>
          )}

          {data.series.points.length > 0 && (
            <Section
              title={`Trend by ${data.series.bucket}`}
              extra={
                <ToggleButtonGroup
                  size="small"
                  exclusive
                  value={metric}
                  onChange={(_, v: TrendMetric | null) => v && setMetric(v)}
                  aria-label="Trend measure"
                  sx={{
                    '& .MuiToggleButton-root': { py: 0, px: 0.75, fontSize: fs(12), height: 22 },
                  }}
                >
                  <ToggleButton value="distance">km</ToggleButton>
                  <ToggleButton value="time">hours</ToggleButton>
                  <ToggleButton value="count">count</ToggleButton>
                </ToggleButtonGroup>
              }
            >
              <TrendChart series={data.series} metric={metric} />
            </Section>
          )}

          {load && weekly.some((w) => w.load > 0 || w.hours > 0) && (
            <Section
              title="Training load"
              extra={
                <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                  latest week{' '}
                  <Box component="span" sx={{ color: UP_COLOR, fontWeight: 600 }}>
                    {Math.round(load.latest.load)}
                  </Box>
                  {isSet(load.avgBefore) ? ` · before ${Math.round(load.avgBefore)}` : ''}
                </Typography>
              }
            >
              <LoadChart weekly={weekly} />
              <Typography variant="caption" component="p" sx={{ color: tokens.textMuted, mt: 0.5 }}>
                Bars: load per week = minutes × intensity (average heart rate ÷ your max, or 0.6
                without heart rate). Line: hours. The latest week is green
                {isSet(load.avgBefore) ? '; "before" is the average of the 4 weeks before it' : ''}.
              </Typography>
            </Section>
          )}

          {zoneSeconds && zoneSeconds.some((x) => x > 0) && (
            <Section title="Time in heart-rate zones">
              <HrZonesBar seconds={zoneSeconds} />
              <Typography variant="caption" component="p" sx={{ color: tokens.textMuted, mt: 0.5 }}>
                Estimated from each activity&apos;s average heart rate against your max.
              </Typography>
            </Section>
          )}

          {trends.length > 0 && (
            <Section title="Trends per sport">
              {trends.map((t) => (
                <TypeTrendRow key={t.type} trend={t} />
              ))}
              <Typography variant="caption" component="p" sx={{ color: tokens.textMuted, mt: 0.5 }}>
                Speed per activity; the faint red line is the average heart rate. Faster at a lower
                heart rate means better fitness.
              </Typography>
            </Section>
          )}

          {hasActivities && (
            <Section title="When">
              <CountBars
                label="Activities per weekday"
                values={data.byWeekday}
                names={WEEKDAYS}
                titles={data.byWeekday.map((n, i) => `${WEEKDAYS[i]}: ${n}`)}
              />
              <Box sx={{ mt: 1 }}>
                <CountBars
                  label="Activity starts per hour"
                  values={data.byHour}
                  names={data.byHour.map((_, h) => (h % 6 === 0 ? `${h}:00` : ''))}
                  titles={data.byHour.map((n, h) => `${h}:00–${h + 1}:00: ${n}`)}
                />
              </Box>
            </Section>
          )}

          {data.records.length > 0 && (
            <Section title="Records">
              {data.records.map((r) => (
                <ButtonBase
                  key={`${r.kind}-${r.type}`}
                  onClick={() => onOpenActivity(r.activityId)}
                  sx={rowSx}
                >
                  <Box
                    sx={{
                      width: 10,
                      height: 10,
                      mt: 0.75,
                      borderRadius: '50%',
                      bgcolor: typeColor(r.type),
                      flexShrink: 0,
                    }}
                  />
                  <Box sx={{ minWidth: 0, flex: 1 }}>
                    <Typography variant="body2">{r.label}</Typography>
                    <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                      {formatDate(r.date)}
                    </Typography>
                  </Box>
                  <Typography variant="body2" sx={{ fontWeight: 600, whiteSpace: 'nowrap' }}>
                    {r.value}
                  </Typography>
                </ButtonBase>
              ))}
            </Section>
          )}

          {data.topPlaces.length > 0 && (
            <Section title="Places">
              {data.topPlaces.map((p) => (
                <ButtonBase
                  key={p.placeId ?? `${p.lat},${p.lon}`}
                  onClick={() => onShowPlace(p.lat, p.lon)}
                  sx={rowSx}
                >
                  <Place sx={{ fontSize: fs(16), mt: 0.25, color: VISIT_COLOR }} />
                  <Box sx={{ minWidth: 0, flex: 1 }}>
                    <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.75, minWidth: 0 }}>
                      <Typography
                        variant="body2"
                        dir="auto"
                        noWrap
                        sx={{
                          minWidth: 0,
                          fontStyle: p.unnamed ? 'italic' : undefined,
                          color: p.unnamed ? tokens.textSecondary : undefined,
                        }}
                      >
                        {p.unnamed ? 'Unnamed place' : p.name || 'Unnamed place'}
                      </Typography>
                      {p.isNew && (
                        <Chip
                          size="small"
                          label="new"
                          sx={{ height: 18, fontSize: fs(12), flexShrink: 0 }}
                          color="primary"
                          variant="outlined"
                        />
                      )}
                    </Box>
                    <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                      {p.visits} {p.visits === 1 ? 'visit' : 'visits'} · {formatHours(p.hours)}
                      {p.unnamed && (
                        <Tooltip title="You come here often, but the place has no name. Label it in Google Maps and import your Timeline again, so searches and insights can use the name.">
                          <Box
                            component="span"
                            sx={{ ml: 0.75, color: tokens.attention, fontWeight: 600 }}
                          >
                            name it
                          </Box>
                        </Tooltip>
                      )}
                    </Typography>
                  </Box>
                </ButtonBase>
              ))}
            </Section>
          )}

          <ReviewsSection onShowPlace={onShowPlace} />

          {(hasActivities || data.travel.flights > 0 || data.travel.daysAway > 0) && (
            <Section title="Streaks & travel">
              <Fact label="Longest streak">
                {days(data.streaks.longestActiveDays)}
                {data.streaks.longestActiveFrom &&
                  ` from ${formatDate(data.streaks.longestActiveFrom)}`}
              </Fact>
              <Fact label="Since last activity">
                {!isSet(data.streaks.daysSinceLast)
                  ? '—'
                  : data.streaks.daysSinceLast === 0
                    ? 'today'
                    : days(data.streaks.daysSinceLast)}
              </Fact>
              <Fact label="Active days">{data.streaks.activeDays.toLocaleString('en-GB')}</Fact>
              <Fact label="Flights">
                {data.travel.flights}
                {data.travel.flights > 0 ? ` · ${formatKm(data.travel.flightKm)}` : ''}
              </Fact>
              <Fact label="Days away">{days(data.travel.daysAway)}</Fact>
            </Section>
          )}

          {hasActivities && (
            <Section title="Data quality">
              <Fact label="Heart rate">
                {data.coverage.withHeartRate} of {data.coverage.sports} sports
                {data.coverage.sports > 0
                  ? ` (${formatShare(data.coverage.withHeartRate, data.coverage.sports)})`
                  : ''}
              </Fact>
              <Fact label="Recorded">
                {data.coverage.recorded} recorded · {data.coverage.inferredOnly} only inferred
              </Fact>
              <Fact label="Fused">{data.coverage.fused} from several sources</Fact>
              {(data.coverage.implausible ?? 0) > 0 && (
                <Fact label="Impossible speed">
                  {data.coverage.implausible} probably misclassified · left out of records
                </Fact>
              )}
              <Fact label="Sources disagree">
                {data.coverage.withConflicts}
                {data.coverage.withConflicts > 0 && (
                  <>
                    {' · '}
                    <Link
                      component="button"
                      variant="body2"
                      onClick={() => onApplyFilter({ conflictsOnly: true })}
                    >
                      show them
                    </Link>
                  </>
                )}
              </Fact>
              {data.coverage.sources.length > 0 && (
                <Fact label="Sources">
                  {data.coverage.sources
                    .map((s) => `${sourceLabel(s.source)} ${s.activities.toLocaleString('en-GB')}`)
                    .join(' · ')}
                </Fact>
              )}
            </Section>
          )}

          {assistantEnabled && (
            <Box sx={{ px: 1.5, pt: 2 }}>
              <Button
                variant="outlined"
                size="small"
                startIcon={<AutoAwesome />}
                onClick={() => onExplain(explainPrompt(period))}
              >
                Explain with AI
              </Button>
            </Box>
          )}
        </>
      )}
    </Box>
  );
};

export default InsightsPanel;
