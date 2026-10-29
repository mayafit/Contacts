/**
 * @fileoverview Colour key shared by the map and the timeline
 * @module Contacts/atlas/AtlasLegend
 */

import React from 'react';
import { Box, Typography } from '@mui/material';
import { kindColor, kindLabel } from './types';
import { PHOTO_COLOR } from './photos';
import { tokens } from '../theme/contactsTheme';

/** Props for AtlasLegend */
export interface AtlasLegendProps {
  /**
   * Kinds to show: activity types, 'visit', 'health', 'photos' (the "has photos" dot),
   * 'inferred' (a matched route's dashed stretch)
   */
  kinds: string[];
  /** Health data is drawn as a strip, not a bar */
  healthAsStrip?: boolean;
}

/** Activity types first (by name), then visits, health, and the photos dot */
export const legendOrder = (kinds: Iterable<string>): string[] => {
  const ranks: Record<string, number> = { visit: 1, health: 2, photos: 3, inferred: 4 };
  const rank = (k: string) => ranks[k] ?? 0;
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
          {k === 'inferred' ? (
            <Box
              data-testid="legend-dashed"
              sx={{
                width: 14,
                height: 0,
                borderTop: `2px dashed ${tokens.textSecondary}`,
                opacity: 0.8,
              }}
            />
          ) : (
            <Box
              sx={{
                width: k === 'visit' ? 9 : k === 'photos' ? 7 : 12,
                height: k === 'visit' ? 9 : k === 'photos' ? 7 : 4,
                borderRadius: k === 'visit' || k === 'photos' ? '50%' : '2px',
                bgcolor: k === 'photos' ? PHOTO_COLOR : kindColor(k),
              }}
            />
          )}
          <Typography variant="caption" sx={{ color: tokens.textSecondary, lineHeight: 1.4 }}>
            {k === 'photos'
              ? 'Has photos'
              : k === 'inferred'
                ? 'Inferred road (no GPS)'
                : k === 'health' && healthAsStrip
                  ? `${kindLabel(k)} (top strip)`
                  : kindLabel(k)}
          </Typography>
        </Box>
      ))}
    </Box>
  ) : null;

export default AtlasLegend;
