/**
 * @fileoverview Colour key shared by the map and the timeline
 * @module Contacts/atlas/AtlasLegend
 */

import React from 'react';
import { Box, Typography } from '@mui/material';
import { kindColor, kindLabel } from './types';
import { tokens } from '../theme/contactsTheme';

/** Props for AtlasLegend */
export interface AtlasLegendProps {
  /** Kinds to show: activity types, 'visit', 'health' */
  kinds: string[];
  /** Health data is drawn as a strip, not a bar */
  healthAsStrip?: boolean;
}

/** Activity types first (by name), then visits, then health */
export const legendOrder = (kinds: Iterable<string>): string[] => {
  const rank = (k: string) => (k === 'health' ? 2 : k === 'visit' ? 1 : 0);
  return [...new Set(kinds)].sort((a, b) => rank(a) - rank(b) || a.localeCompare(b));
};

/**
 * A swatch and a name per kind: a line for tracks, a dot for visits, a strip
 * for health data, as drawn on the map and the timeline.
 * @param props - Component props
 * @returns Legend, or nothing when there is nothing to explain
 */
const AtlasLegend: React.FC<AtlasLegendProps> = ({ kinds, healthAsStrip }) =>
  kinds.length ? (
    <Box
      sx={{ display: 'flex', flexWrap: 'wrap', columnGap: 1.5, rowGap: 0.25 }}
      aria-label="Colour legend"
    >
      {legendOrder(kinds).map((k) => (
        <Box key={k} sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }}>
          <Box
            sx={{
              width: k === 'visit' ? 9 : 12,
              height: k === 'visit' ? 9 : k === 'health' ? 4 : 4,
              borderRadius: k === 'visit' ? '50%' : '2px',
              bgcolor: kindColor(k),
            }}
          />
          <Typography variant="caption" sx={{ color: tokens.textSecondary, lineHeight: 1.4 }}>
            {k === 'health' && healthAsStrip ? `${kindLabel(k)} (top strip)` : kindLabel(k)}
          </Typography>
        </Box>
      ))}
    </Box>
  ) : null;

export default AtlasLegend;
