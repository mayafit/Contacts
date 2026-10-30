/**
 * @fileoverview The roads a matched route follows (AD-37), under the panel's route line:
 * route numbers as small road signs and street names, in travel order. Each one finds
 * every activity whose route follows it.
 * @module Contacts/atlas/RouteRoads
 */

import React, { useState } from 'react';
import { Box, ButtonBase, Link, Tooltip, Typography } from '@mui/material';
import type { RouteRoad } from './types';
import { visibleRoads } from './filters';
import { formatDistance } from './geo';
import { tokens } from '../theme/contactsTheme';
import { fs } from '../settings/uiSettings';

/** Props for RouteRoads */
export interface RouteRoadsProps {
  roads: RouteRoad[];
  /** Finds every activity on a road */
  onRoad: (road: string) => void;
}

/** A route number drawn like a road sign */
const signSx = {
  px: 0.6,
  height: 17,
  minWidth: 22,
  borderRadius: '3px',
  border: `1.5px solid ${tokens.text}`,
  bgcolor: tokens.surface,
  color: tokens.text,
  fontSize: fs(11),
  fontWeight: 700,
  lineHeight: 1,
  '&:hover': { bgcolor: 'rgba(57, 135, 229, 0.16)' },
  '&.Mui-focusVisible': { outline: `2px solid ${tokens.text}`, outlineOffset: 1 },
} as const;

/**
 * The roads line.
 * @param props - Component props
 * @returns The line, or nothing without roads
 */
const RouteRoads: React.FC<RouteRoadsProps> = ({ roads, onRoad }) => {
  const [all, setAll] = useState(false);
  if (roads.length === 0) {
    return null;
  }
  const { shown, hidden } = all ? { shown: roads, hidden: 0 } : visibleRoads(roads);

  return (
    <Typography
      variant="caption"
      component="div"
      role="group"
      aria-label="Roads"
      sx={{
        color: tokens.textMuted,
        ml: 5,
        mt: 0.25,
        display: 'flex',
        alignItems: 'center',
        flexWrap: 'wrap',
        columnGap: 0.6,
        rowGap: 0.25,
      }}
    >
      <span>Via</span>
      {shown.map((r, i) => (
        <React.Fragment key={r.name}>
          {i > 0 && (
            <Box component="span" aria-hidden sx={{ color: tokens.textMuted }}>
              ›
            </Box>
          )}
          <Tooltip
            describeChild
            title={`${formatDistance(r.m)} on it — show every route on ${r.ref ? `road ${r.name}` : r.name}`}
          >
            {r.ref ? (
              <ButtonBase onClick={() => onRoad(r.name)} sx={signSx} aria-label={`Road ${r.name}`}>
                {r.name}
              </ButtonBase>
            ) : (
              <Link
                component="button"
                type="button"
                variant="inherit"
                underline="hover"
                onClick={() => onRoad(r.name)}
                sx={{ color: tokens.textSecondary, fontSize: 'inherit', verticalAlign: 'baseline' }}
              >
                <bdi>{r.name}</bdi>
              </Link>
            )}
          </Tooltip>
        </React.Fragment>
      ))}
      {hidden > 0 && (
        <Link
          component="button"
          type="button"
          variant="inherit"
          underline="hover"
          onClick={() => setAll(true)}
          sx={{ color: 'inherit', fontSize: 'inherit' }}
        >
          +{hidden} more
        </Link>
      )}
    </Typography>
  );
};

export default RouteRoads;
