/**
 * @fileoverview Details of one activity: numbers, the analysis (moving
 * summary; heart-rate, speed/pace, elevation and cadence charts synced to one
 * cursor that the map follows; zones, splits, effort, comparison with the
 * user's other activities), editing, and the fused activity's sources,
 * conflicts and places (split, merge)
 * @module Contacts/atlas/ActivityPanel
 */

import React, { useMemo, useRef, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  ButtonBase,
  Chip,
  IconButton,
  MenuItem,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Tooltip,
  Typography,
} from '@mui/material';
import {
  ArrowBack,
  CallMerge,
  CallSplit,
  DeleteOutline,
  Edit,
  Place,
  SportsScore,
  TripOrigin,
} from '@mui/icons-material';
import type {
  ActivityDetail,
  ActivityMember,
  LinkedPlace,
  MatchEvidence,
  PhotoSummary,
} from './types';
import {
  ACTIVITY_TYPES,
  VISIT_COLOR,
  describeFieldSources,
  sourceLabel,
  typeColor,
  isSet,
} from './types';
import { formatDistance, formatDuration, formatWhen } from './geo';
import { describeEvidence, formatPercent } from './fusionText';
import { indexAt, timeAtDistance, trackSamples, type ChartAxis } from './analysis';
import ActivityCharts from './ActivityCharts';
import HrZonesBar from './HrZonesBar';
import PanelSection from './PanelSection';
import PhotoStrip, { PhotosLink } from './PhotoStrip';
import {
  AnalysisSummary,
  ComparedSection,
  EffortSection,
  SplitsTable,
  routeRankText,
} from './AnalysisSections';
import { tokens } from '../theme/contactsTheme';
import RouteLine, { type RouteView } from './RouteLine';
import RouteRoads from './RouteRoads';

/** Props for ActivityPanel */
export interface ActivityPanelProps {
  detail: ActivityDetail;
  onBack: () => void;
  onCursor: (pos: [number, number] | null) => void;
  onSave: (patch: { name?: string; type?: string; notes?: string }) => Promise<void>;
  onDelete: () => void;
  /** Splits a source record off this activity ("not the same activity") */
  onSplit: (memberId: string) => Promise<void>;
  /** Starts picking another activity to merge with this one */
  onStartMerge: () => void;
  /** Shows a linked place (its visit, or its position on the map) */
  onShowPlace: (lat: number, lon: number, visitId: string) => void;
  /** Opens another activity (e.g. an earlier one on the same route) */
  onOpenActivity: (id: string) => void;
  /** Photos taken during it (OneDrive) */
  photos?: PhotoSummary[];
  /** Opens the viewer at one of the photos */
  onOpenPhoto?: (index: number) => void;
  /** The route drawn as matched (roads) or recorded */
  routeView?: RouteView;
  onRouteView?: (view: RouteView) => void;
  /** Loads the activity again after a route change (revert, fix) */
  onRouteChanged?: () => Promise<ActivityDetail | null>;
  /** Finds every activity whose route follows a road */
  onRoad?: (road: string) => void;
}

/** Icon and text of a linked place's role */
const ROLE: Record<LinkedPlace['role'], { label: string; icon: React.ReactElement }> = {
  start: { label: 'Start', icon: <TripOrigin sx={{ fontSize: 15 }} /> },
  end: { label: 'End', icon: <SportsScore sx={{ fontSize: 15 }} /> },
  during: { label: 'Stop', icon: <Place sx={{ fontSize: 15 }} /> },
};

/** Time of day, e.g. 14:05 */
const clock = (iso: string): string =>
  new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });

/** One source record of a fused activity */
const MemberRow: React.FC<{
  member: ActivityMember;
  /** How it matches the primary (non-primary members) */
  evidence?: MatchEvidence;
  busy: boolean;
  onSplit: () => void;
}> = ({ member: m, evidence, busy, onSplit }) => (
  <Box
    sx={{
      display: 'flex',
      alignItems: 'flex-start',
      gap: 1,
      py: 0.75,
      borderBottom: `1px solid ${tokens.border}`,
    }}
  >
    <Box
      sx={{
        width: 8,
        height: 8,
        mt: 0.75,
        borderRadius: '50%',
        bgcolor: typeColor(m.type),
        flexShrink: 0,
      }}
    />
    <Box sx={{ flex: 1, minWidth: 0 }}>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5, flexWrap: 'wrap' }}>
        <Typography variant="body2" sx={{ fontWeight: 600 }}>
          {sourceLabel(m.source)}
        </Typography>
        <Typography variant="body2" dir="auto" noWrap sx={{ color: tokens.textSecondary }}>
          · {ACTIVITY_TYPES[m.type]?.label ?? m.type}
          {m.name ? ` · ${m.name}` : ''}
        </Typography>
        {m.primary && (
          <Chip size="small" label="primary" color="primary" sx={{ height: 18, fontSize: 12 }} />
        )}
        {m.pointCount > 0 && (
          <Chip size="small" label="track" variant="outlined" sx={{ height: 18, fontSize: 12 }} />
        )}
      </Box>
      <Typography variant="caption" sx={{ color: tokens.textMuted }}>
        {[
          `${clock(m.startTime)}–${clock(m.endTime)}`,
          formatDistance(m.distanceM),
          formatDuration(m.durationS),
          m.avgHeartRate ? `${m.avgHeartRate} bpm` : '',
        ]
          .filter(Boolean)
          .join(' · ')}
      </Typography>
      {!m.primary && evidence && (
        <Typography
          variant="caption"
          sx={{ display: 'block', color: tokens.textSecondary }}
          title="How this record matches the primary one: time differences (combined tolerance), distance between the tracks (combined tolerance) and the match confidence"
        >
          {describeEvidence(evidence)}
        </Typography>
      )}
    </Box>
    {!m.primary && (
      <Tooltip title="Not the same activity — split it off">
        <span>
          <IconButton
            size="small"
            disabled={busy}
            onClick={onSplit}
            aria-label={`Split off the ${sourceLabel(m.source)} record`}
          >
            <CallSplit fontSize="small" />
          </IconButton>
        </span>
      </Tooltip>
    )}
  </Box>
);

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
  onSplit,
  onStartMerge,
  onShowPlace,
  onOpenActivity,
  photos,
  onOpenPhoto,
  routeView = 'roads',
  onRouteView,
  onRouteChanged,
  onRoad,
}) => {
  const s = detail.summary;
  const [editing, setEditing] = useState(false);
  const [splitting, setSplitting] = useState(false);
  const members = detail.members ?? [];
  const conflicts = detail.conflicts ?? [];
  const places = detail.places ?? [];
  const evidence = detail.evidence ?? [];
  const matchConfidence = detail.matchConfidence ?? null;
  const provenance = describeFieldSources(detail.fieldSources);
  const analysis = detail.analysis ?? null;
  const [name, setName] = useState(s.name ?? '');
  const [type, setType] = useState(s.type);
  const [notes, setNotes] = useState(detail.notes ?? '');
  const [axis, setAxis] = useState<ChartAxis>('time');
  // The charts' shared cursor, seconds from the start
  const [cursorT, setCursorT] = useState<number | null>(null);
  const photosRef = useRef<HTMLDivElement>(null);
  const from = Date.parse(s.startTime);
  const to = Date.parse(s.endTime);
  const samples = useMemo(() => trackSamples(detail.points, from), [detail.points, from]);
  const durationS = Math.max(
    (to - from) / 1000,
    samples.length ? samples[samples.length - 1].t : 0,
  );
  const hasDistance = samples.length > 1 && samples[samples.length - 1].d > 0;
  const trackOffsetS = detail.points.length ? (Date.parse(detail.points[0].t) - from) / 1000 : 0;
  const pace = analysis?.pace ?? false;

  /** Moves the cursor (charts and map) to a moment; null hides it */
  const seek = (t: number | null) => {
    setCursorT(t);
    if (!isSet(t)) {
      onCursor(null);
      return;
    }
    const i = indexAt(samples, 't', t);
    onCursor(i < 0 ? null : [samples[i].lat, samples[i].lon]);
  };
  const hoverX = (x: number | null) =>
    seek(!isSet(x) ? null : axis === 'time' || !hasDistance ? x : timeAtDistance(samples, x));

  // The source's distance stays; the matched length is shown beside it (AD-34 rule 5)
  const onRoads =
    s.geometry === 'matched' &&
    isSet(s.onRoadsM) &&
    detail.fieldSources?.distance !== 'track' &&
    Math.abs(s.onRoadsM - (s.distanceM ?? 0)) >= 100
      ? ` · ${formatDistance(s.onRoadsM)} on roads`
      : '';
  const stats = [
    ['Distance', s.distanceM ? `${formatDistance(s.distanceM)}${onRoads}` : ''],
    ['Time', formatDuration(s.durationS)],
    ['Climb', isSet(s.elevationGainM) ? `${Math.round(s.elevationGainM)} m` : ''],
    [
      'Avg speed',
      s.distanceM && s.durationS ? `${((s.distanceM / s.durationS) * 3.6).toFixed(1)} km/h` : '',
    ],
    ['Heart rate', s.avgHeartRate ? `${s.avgHeartRate} avg · ${s.maxHeartRate} max` : ''],
  ].filter(([, v]) => v);

  const zones = analysis?.zones ?? null;
  const splits = analysis?.splits ?? [];
  const bestEfforts = analysis?.bestEfforts ?? [];
  const drift = analysis?.drift ?? null;
  const similar = analysis?.similar ?? null;
  const hasCompared =
    !!similar &&
    (similar.sameRoute.length > 0 ||
      !!routeRankText(similar) ||
      (similar.sameType.count >= 3 &&
        (isSet(similar.sameType.speedPercentile) || isSet(similar.sameType.distancePercentile))));
  const showSources = members.length > 1 || conflicts.length > 0;

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
        {formatWhen(s.startTime)} ·{' '}
        {(s.sources?.length ? s.sources : [s.source]).map((x) => sourceLabel(x)).join(', ')}
        {photos && photos.length > 0 && onOpenPhoto && (
          <>
            {' · '}
            <PhotosLink count={photos.length} target={photosRef} />
          </>
        )}
      </Typography>
      {onRouteView && onRouteChanged && (
        <RouteLine
          detail={detail}
          view={routeView}
          onView={onRouteView}
          onChanged={onRouteChanged}
        />
      )}
      {onRoad && s.geometry === 'matched' && detail.route?.roads && (
        <RouteRoads roads={detail.route.roads} onRoad={onRoad} />
      )}

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
      {analysis && <AnalysisSummary analysis={analysis} type={s.type} />}

      {detail.notes && !editing && (
        <Typography
          variant="body2"
          dir="auto"
          sx={{ mt: 1.5, color: tokens.textSecondary, whiteSpace: 'pre-wrap' }}
        >
          {detail.notes}
        </Typography>
      )}

      {photos && photos.length > 0 && onOpenPhoto && (
        <Box ref={photosRef} sx={{ scrollMarginTop: 8 }}>
          <PanelSection title="Photos" hint={photos.length}>
            <PhotoStrip photos={photos} onOpen={onOpenPhoto} />
          </PanelSection>
        </Box>
      )}

      <PanelSection
        title="Charts"
        extra={
          hasDistance ? (
            <ToggleButtonGroup
              size="small"
              exclusive
              value={axis}
              onChange={(_, v: ChartAxis | null) => v && setAxis(v)}
              aria-label="Charts over"
              sx={{ '& .MuiToggleButton-root': { py: 0, px: 0.75, fontSize: 12, height: 22 } }}
            >
              <ToggleButton value="time">Time</ToggleButton>
              <ToggleButton value="distance">Distance</ToggleButton>
            </ToggleButtonGroup>
          ) : undefined
        }
      >
        <ActivityCharts
          detail={detail}
          analysis={analysis}
          samples={samples}
          axis={hasDistance ? axis : 'time'}
          durationS={durationS}
          cursorT={cursorT}
          onHoverX={hoverX}
        />
      </PanelSection>

      {zones && zones.seconds.some((x) => x > 0) && (
        <PanelSection title="Heart-rate zones">
          <HrZonesBar seconds={zones.seconds} bounds={zones.bounds} />
          <Typography variant="caption" component="div" sx={{ mt: 0.5, color: tokens.textMuted }}>
            Max HR {Math.round(zones.maxHr)} bpm (
            {zones.maxHrSource === 'history' ? 'your history' : 'this activity'}) · avg{' '}
            {Math.round(zones.avgHr)} · highest here {Math.round(zones.maxHrHere)}
          </Typography>
        </PanelSection>
      )}

      {analysis && splits.length > 1 && (
        <PanelSection title="Splits" hint={String(splits.length)}>
          <SplitsTable splits={splits} splitM={analysis.splitM} pace={pace} />
        </PanelSection>
      )}

      {(drift || bestEfforts.length > 0) && (
        <PanelSection
          title="Effort"
          hint={drift ? `drift ${drift.decouplingPct.toFixed(1)}%` : undefined}
        >
          <EffortSection
            drift={drift}
            bestEfforts={bestEfforts}
            pace={pace}
            // The server counts from the first track point; the charts from the activity start
            onSeek={(t) => seek(t + trackOffsetS)}
          />
        </PanelSection>
      )}

      {similar && hasCompared && (
        <PanelSection title="Compared with you" hint={routeRankText(similar)}>
          <ComparedSection
            similar={similar}
            current={{
              id: s.id,
              startTime: s.startTime,
              durationS: s.durationS ?? null,
              // Like the server: elapsed time when the source gave no duration
              speedKmh:
                s.distanceM && (s.durationS || to > from)
                  ? (s.distanceM / (s.durationS || (to - from) / 1000)) * 3.6
                  : null,
            }}
            type={s.type}
            pace={pace}
            onOpenActivity={onOpenActivity}
          />
        </PanelSection>
      )}

      <PanelSection
        title="Sources"
        defaultOpen={showSources}
        hint={
          conflicts.length > 0
            ? 'the sources disagree'
            : `${Math.max(1, members.length)} ${members.length > 1 ? 'records' : 'record'}`
        }
        extra={
          isSet(matchConfidence) ? (
            <Typography
              variant="caption"
              title="The lowest match confidence of the combined records"
              sx={{ color: tokens.textMuted }}
            >
              match {formatPercent(matchConfidence)}
            </Typography>
          ) : undefined
        }
      >
        {conflicts.length > 0 && (
          <Alert severity="warning" sx={{ mb: 1, '& .MuiAlert-message': { minWidth: 0 } }}>
            <Typography variant="body2" sx={{ fontWeight: 600 }}>
              The sources disagree
            </Typography>
            {conflicts.map((c, i) => (
              <Typography key={`${c.field}-${i}`} variant="caption" sx={{ display: 'block' }}>
                {c.message}
                {c.chosen ? ` — using ${c.chosen}` : ''}
              </Typography>
            ))}
          </Alert>
        )}
        {showSources &&
          members.map((m) => (
            <MemberRow
              key={m.id}
              member={m}
              evidence={evidence.find((e) => e.memberId === m.id)}
              busy={splitting}
              onSplit={async () => {
                setSplitting(true);
                try {
                  await onSplit(m.id);
                } catch {
                  // The page shows the error
                } finally {
                  setSplitting(false);
                }
              }}
            />
          ))}
        {showSources && provenance && (
          <Typography
            variant="caption"
            sx={{ display: 'block', mt: 0.75, color: tokens.textMuted }}
          >
            Where values come from: {provenance}
          </Typography>
        )}
        <Button size="small" startIcon={<CallMerge />} onClick={onStartMerge} sx={{ mt: 0.5 }}>
          Merge with another activity…
        </Button>
      </PanelSection>

      {places.length > 0 && (
        <PanelSection title="Places" hint={String(places.length)}>
          {places.map((p) => (
            <ButtonBase
              key={`${p.visitId}-${p.role}`}
              onClick={() => onShowPlace(p.lat, p.lon, p.visitId)}
              sx={{
                display: 'flex',
                justifyContent: 'flex-start',
                alignItems: 'center',
                gap: 1,
                width: '100%',
                textAlign: 'left',
                py: 0.5,
                borderRadius: '6px',
                '&:hover': { bgcolor: tokens.hover },
              }}
            >
              <Tooltip title={ROLE[p.role]?.label ?? p.role}>
                <Box sx={{ display: 'flex', color: VISIT_COLOR }}>
                  {ROLE[p.role]?.icon ?? ROLE.during.icon}
                </Box>
              </Tooltip>
              <Typography variant="body2" dir="auto" noWrap sx={{ flex: 1, minWidth: 0 }}>
                {p.placeName || 'Unnamed place'}
              </Typography>
              <Typography variant="caption" sx={{ color: tokens.textMuted, flexShrink: 0 }}>
                {clock(p.startTime)}
                {p.role === 'during' ? `–${clock(p.endTime)}` : ''}
              </Typography>
            </ButtonBase>
          ))}
        </PanelSection>
      )}
    </Box>
  );
};

export default ActivityPanel;
