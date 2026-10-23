/**
 * @fileoverview The search results beside the map: activities and visits
 * @module Contacts/atlas/ResultsList
 */

import React from 'react';
import { Box, ButtonBase, Chip, Typography } from '@mui/material';
import { Place } from '@mui/icons-material';
import type { SearchResult, VisitSummary } from './types';
import { ACTIVITY_TYPES, VISIT_COLOR, typeColor } from './types';
import { formatDistance, formatDuration, formatWhen } from './geo';
import { tokens } from '../theme/contactsTheme';

/** Props for ResultsList */
export interface ResultsListProps {
  result: SearchResult | null;
  loading: boolean;
  types: string[];
  onToggleType: (type: string) => void;
  onSelectActivity: (id: string) => void;
  onSelectVisit: (v: VisitSummary) => void;
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
      <Typography variant="caption" sx={{ px: 1.5, color: tokens.textMuted }}>
        {loading
          ? 'Searching…'
          : result
            ? `${result.totalActivities} activities · ${result.totalVisits} visits${
                result.totalActivities > result.activities.length ||
                result.totalVisits > result.visits.length
                  ? ' (newest shown)'
                  : ''
              }`
            : ''}
      </Typography>
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
              <Box sx={{ minWidth: 0 }}>
                <Typography variant="body2" dir="auto" noWrap sx={{ fontWeight: 600 }}>
                  {it.a.name || ACTIVITY_TYPES[it.a.type]?.label || it.a.type}
                </Typography>
                <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                  {formatWhen(it.a.startTime)} ·{' '}
                  {[
                    formatDistance(it.a.distanceM),
                    formatDuration(it.a.durationS),
                    it.a.avgHeartRate ? `${it.a.avgHeartRate} bpm` : '',
                  ]
                    .filter(Boolean)
                    .join(' · ')}
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
                <Typography variant="body2" dir="auto" noWrap sx={{ fontWeight: 600 }}>
                  {it.v.placeName || 'Visit'}
                </Typography>
                <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                  {formatWhen(it.v.startTime)} ·{' '}
                  {formatDuration((Date.parse(it.v.endTime) - Date.parse(it.v.startTime)) / 1000)}
                </Typography>
              </Box>
            </ButtonBase>
          ),
        )}
        {!loading && result && items.length === 0 && (
          <Typography variant="body2" sx={{ p: 2, color: tokens.textMuted }}>
            Nothing here. Widen the time range or the area, or import data.
          </Typography>
        )}
      </Box>
    </Box>
  );
};

export default ResultsList;
