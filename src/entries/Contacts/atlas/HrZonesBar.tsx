/**
 * @fileoverview Time in heart-rate zones Z1–Z5 as a stacked bar with minutes
 * and shares (an activity's zones and the insights' period)
 * @module Contacts/atlas/HrZonesBar
 */

import React from 'react';
import { Box, Tooltip, Typography } from '@mui/material';
import { ZONE_COLORS, zoneShares } from './analysis';
import { formatDuration } from './geo';
import { tokens } from '../theme/contactsTheme';

/** Props for HrZonesBar */
export interface HrZonesBarProps {
  /** 5 values: seconds in zone 1 … zone 5 */
  seconds: number[];
  /** 4 lower bounds (bpm) of zones 2..5, for the tooltips */
  bounds?: number[];
}

/** "< 1 min", "42 min", "1:05 h" */
const minutes = (s: number): string => (s > 0 && s < 60 ? '< 1 min' : formatDuration(s));

/** "< 112 bpm", "112–130 bpm", "≥ 168 bpm" */
const zoneRange = (bounds: number[] | undefined, i: number): string => {
  if (!bounds || bounds.length < 4) {
    return '';
  }
  if (i === 0) {
    return `< ${Math.round(bounds[0])} bpm`;
  }
  if (i === 4) {
    return `≥ ${Math.round(bounds[3])} bpm`;
  }
  return `${Math.round(bounds[i - 1])}–${Math.round(bounds[i]) - 1} bpm`;
};

/**
 * Stacked bar of the time in heart-rate zones, cool to hot, with a legend.
 * @param props - Component props
 * @returns Bar element; null without any time
 */
const HrZonesBar: React.FC<HrZonesBarProps> = ({ seconds, bounds }) => {
  const values = Array.from({ length: 5 }, (_, i) => seconds[i] ?? 0);
  const total = values.reduce((a, b) => a + Math.max(0, b), 0);
  if (total <= 0) {
    return null;
  }
  const shares = zoneShares(values);
  const title = (i: number) =>
    [`Zone ${i + 1}`, zoneRange(bounds, i), minutes(values[i]), `${shares[i]}%`]
      .filter(Boolean)
      .join(' · ');
  return (
    <Box>
      <Box
        role="img"
        aria-label={`Time in heart-rate zones: ${values
          .map((_, i) => `zone ${i + 1} ${shares[i]}%`)
          .join(', ')}`}
        sx={{ display: 'flex', height: 12, borderRadius: '6px', overflow: 'hidden', gap: '1px' }}
      >
        {values.map((s, i) =>
          s > 0 ? (
            <Tooltip key={i} title={title(i)}>
              <Box sx={{ flex: s, bgcolor: ZONE_COLORS[i], minWidth: 2 }} />
            </Tooltip>
          ) : null,
        )}
      </Box>
      <Box
        sx={{
          display: 'grid',
          gridTemplateColumns: 'repeat(5, minmax(0, 1fr))',
          gap: 0.5,
          mt: 0.75,
        }}
      >
        {values.map((s, i) => (
          <Tooltip key={i} title={title(i)}>
            <Box sx={{ minWidth: 0 }}>
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }}>
                <Box
                  sx={{
                    width: 8,
                    height: 8,
                    borderRadius: '2px',
                    bgcolor: ZONE_COLORS[i],
                    flexShrink: 0,
                  }}
                />
                <Typography variant="caption" sx={{ fontWeight: 600 }}>
                  Z{i + 1}
                </Typography>
                <Typography variant="caption" noWrap sx={{ color: tokens.textMuted }}>
                  {shares[i]}%
                </Typography>
              </Box>
              <Typography
                variant="caption"
                component="div"
                noWrap
                sx={{ color: tokens.textSecondary }}
              >
                {s > 0 ? minutes(s) : '—'}
              </Typography>
            </Box>
          </Tooltip>
        ))}
      </Box>
    </Box>
  );
};

export default HrZonesBar;
