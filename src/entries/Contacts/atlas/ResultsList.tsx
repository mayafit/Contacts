/**
 * @fileoverview The search results beside the map: activities and visits
 * @module Contacts/atlas/ResultsList
 */

import React from 'react';
import { Box, ButtonBase, Chip, Link, Tooltip, Typography } from '@mui/material';
import { PhotoCameraOutlined, Place, WarningAmber } from '@mui/icons-material';
import type { SearchResult, VisitSummary } from './types';
import { ACTIVITY_TYPES, VISIT_COLOR, sourceLabel, typeColor } from './types';
import { formatDistance, formatDuration, formatWhen } from './geo';
import { identifyMark } from './placeText';
import { photoCountText } from './photos';
import { tokens } from '../theme/contactsTheme';

/** A quiet "📷 12" at the end of a row's details, when it has photos */
const PhotoMark: React.FC<{ count?: number }> = ({ count }) =>
  count ? (
    <Box
      component="span"
      aria-label={photoCountText(count) ?? undefined}
      title={photoCountText(count) ?? undefined}
      sx={{ whiteSpace: 'nowrap' }}
    >
      {' · '}
      <PhotoCameraOutlined sx={{ fontSize: 12, verticalAlign: '-2px' }} /> {count}
    </Box>
  ) : null;

/** Props for ResultsList */
export interface ResultsListProps {
  result: SearchResult | null;
  loading: boolean;
  types: string[];
  onToggleType: (type: string) => void;
  onSelectActivity: (id: string) => void;
  onSelectVisit: (v: VisitSummary) => void;
  /** Any filter (time, area, types, text) is active */
  filtered?: boolean;
  /** Removes every filter */
  onClearFilters?: () => void;
}

/**
 * Results list.
 * @param props - Component props
 * @returns List element
 */
const ResultsList: React.FC<ResultsListProps> = ({
  result,
  loading,
  types,
  onToggleType,
  onSelectActivity,
  onSelectVisit,
  filtered = false,
  onClearFilters,
}) => {
  const items = [
    ...(result?.activities ?? []).map((a) => ({ kind: 'a' as const, t: a.startTime, a })),
    ...(result?.visits ?? []).map((v) => ({ kind: 'v' as const, t: v.startTime, v })),
  ].sort((x, y) => y.t.localeCompare(x.t));

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', minHeight: 0, flex: 1 }}>
      <Box sx={{ display: 'flex', gap: 0.5, flexWrap: 'wrap', p: 1.5, pb: 1 }}>
        {Object.entries(ACTIVITY_TYPES).map(([k, v]) => (
          <Chip
            key={k}
            size="small"
            label={v.label}
            onClick={() => onToggleType(k)}
            variant={types.includes(k) ? 'filled' : 'outlined'}
            sx={{
              borderColor: v.color,
              bgcolor: types.includes(k) ? v.color : undefined,
              color: types.includes(k) ? '#111' : tokens.textSecondary,
            }}
          />
        ))}
      </Box>
      {!loading &&
        result &&
        (result.totalActivities > result.activities.length ||
          result.totalVisits > result.visits.length) && (
          // The count is in the filter bar; say which part of a long result is listed
          <Typography variant="caption" sx={{ px: 1.5, color: tokens.textMuted }}>
            Newest shown
          </Typography>
        )}
      <Box sx={{ overflowY: 'auto', flex: 1, minHeight: 0 }}>
        {items.map((it) =>
          it.kind === 'a' ? (
            <ButtonBase
              key={it.a.id}
              onClick={() => onSelectActivity(it.a.id)}
              sx={{
                display: 'flex',
                justifyContent: 'flex-start',
                alignItems: 'flex-start',
                gap: 1,
                width: '100%',
                textAlign: 'left',
                px: 1.5,
                py: 1,
                borderBottom: `1px solid ${tokens.border}`,
              }}
            >
              <Box
                sx={{
                  width: 10,
                  height: 10,
                  mt: 0.75,
                  borderRadius: '50%',
                  bgcolor: typeColor(it.a.type),
                  flexShrink: 0,
                }}
              />
              <Box sx={{ minWidth: 0, flex: 1 }}>
                <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.75, minWidth: 0 }}>
                  <Typography
                    variant="body2"
                    dir="auto"
                    noWrap
                    sx={{ fontWeight: 600, minWidth: 0, flexShrink: 1 }}
                  >
                    {it.a.name || ACTIVITY_TYPES[it.a.type]?.label || it.a.type}
                  </Typography>
                  {(it.a.memberCount ?? 1) > 1 && (
                    <Tooltip
                      title={`Fused from ${(it.a.sources ?? []).map((x) => sourceLabel(x)).join(', ')}`}
                    >
                      <Chip
                        size="small"
                        label={`${it.a.memberCount} sources`}
                        variant="outlined"
                        sx={{ height: 18, fontSize: 12, flexShrink: 0 }}
                      />
                    </Tooltip>
                  )}
                  {(it.a.conflictCount ?? 0) > 0 && (
                    <Tooltip title="The sources disagree on some values">
                      <WarningAmber
                        aria-label="The sources disagree"
                        sx={{ fontSize: 16, color: tokens.attention, flexShrink: 0 }}
                      />
                    </Tooltip>
                  )}
                </Box>
                <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                  {formatWhen(it.a.startTime)} ·{' '}
                  {[
                    formatDistance(it.a.distanceM),
                    formatDuration(it.a.durationS),
                    it.a.avgHeartRate ? `${it.a.avgHeartRate} bpm` : '',
                  ]
                    .filter(Boolean)
                    .join(' · ')}
                  <PhotoMark count={it.a.photoCount} />
                </Typography>
              </Box>
            </ButtonBase>
          ) : (
            <ButtonBase
              key={it.v.id}
              onClick={() => onSelectVisit(it.v)}
              sx={{
                display: 'flex',
                justifyContent: 'flex-start',
                alignItems: 'flex-start',
                gap: 1,
                width: '100%',
                textAlign: 'left',
                px: 1.5,
                py: 1,
                borderBottom: `1px solid ${tokens.border}`,
              }}
            >
              <Place sx={{ fontSize: 16, mt: 0.25, color: VISIT_COLOR }} />
              <Box sx={{ minWidth: 0 }}>
                <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.75, minWidth: 0 }}>
                  <Typography
                    variant="body2"
                    dir="auto"
                    noWrap
                    sx={{
                      fontWeight: 600,
                      minWidth: 0,
                      flexShrink: 1,
                      fontStyle: identifyMark(it.v.identifyState) === 'auto' ? 'italic' : undefined,
                    }}
                  >
                    {it.v.placeName || 'Visit'}
                  </Typography>
                  {identifyMark(it.v.identifyState) === 'auto' && (
                    <Tooltip title="Identified automatically">
                      <Typography
                        variant="caption"
                        sx={{ color: tokens.textMuted, flexShrink: 0, lineHeight: 1 }}
                      >
                        auto
                      </Typography>
                    </Tooltip>
                  )}
                  {identifyMark(it.v.identifyState) === '?' && (
                    <Tooltip title="Several places here — open it to pick which one">
                      <Typography
                        variant="caption"
                        aria-label="Not sure which place"
                        sx={{ color: tokens.attention, fontWeight: 700, flexShrink: 0 }}
                      >
                        ?
                      </Typography>
                    </Tooltip>
                  )}
                </Box>
                <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                  {formatWhen(it.v.startTime)} ·{' '}
                  {formatDuration((Date.parse(it.v.endTime) - Date.parse(it.v.startTime)) / 1000)}
                  <PhotoMark count={it.v.photoCount} />
                </Typography>
              </Box>
            </ButtonBase>
          ),
        )}
        {!loading && result && items.length === 0 && (
          <Typography variant="body2" sx={{ p: 2, color: tokens.textMuted }}>
            {filtered ? (
              <>
                Nothing matches the filters.{' '}
                {onClearFilters && (
                  <Link component="button" variant="body2" onClick={onClearFilters}>
                    Clear all
                  </Link>
                )}
              </>
            ) : (
              'Nothing here yet. Import data to see it on the map.'
            )}
          </Typography>
        )}
      </Box>
    </Box>
  );
};

export default ResultsList;
