/**
 * @fileoverview The active filters under the Atlas header: one removable chip per
 * filter (time, area, types, text, road, sources disagree), "Clear all" and the result count
 * @module Contacts/atlas/AtlasFilterBar
 */

import React from 'react';
import { Box, Button, Chip, Tooltip, Typography } from '@mui/material';
import {
  AltRoute,
  Category,
  CropFree,
  FilterAlt,
  Schedule,
  Search,
  WarningAmber,
} from '@mui/icons-material';
import type { SearchResult } from './types';
import { FILTER_COLOR } from './types';
import type { AreaBox } from './filters';
import {
  formatAreaFilter,
  formatResultCount,
  formatRoadFilter,
  formatTimeFilter,
  formatTypesFilter,
} from './filters';
import { tokens } from '../theme/contactsTheme';

/** Props for AtlasFilterBar */
export interface AtlasFilterBarProps {
  /** Time filter (ms since the epoch), null for none */
  selection: { from: number; to: number } | null;
  /** Area filter, null for none */
  area: AreaBox | null;
  /** Activity type filter (empty for all types) */
  types: string[];
  /** Text filter as applied to the search ('' for none) */
  text: string;
  /** Only activities whose sources disagree */
  conflictsOnly?: boolean;
  /** Only activities whose matched route follows this road ('' for none) */
  road?: string;
  /** The search result (for the count), null while none has arrived */
  result: SearchResult | null;
  loading: boolean;
  onClearTime: () => void;
  /** Zooms the timeline to the time filter */
  onFocusTime: () => void;
  onClearArea: () => void;
  /** Fits the map to the area filter */
  onFocusArea: () => void;
  onClearTypes: () => void;
  onClearText: () => void;
  onClearConflicts?: () => void;
  onClearRoad?: () => void;
  onClearAll: () => void;
}

/** Chip style of an active filter: the selection blue of the timeline and the area */
const chipSx = {
  maxWidth: '100%',
  height: 26,
  bgcolor: 'rgba(57, 135, 229, 0.16)',
  border: `1px solid ${FILTER_COLOR}`,
  color: tokens.text,
  fontWeight: 500,
  '& .MuiChip-icon': { color: FILTER_COLOR, fontSize: 16 },
  '& .MuiChip-deleteIcon': { color: tokens.textSecondary, fontSize: 16 },
  '& .MuiChip-deleteIcon:hover': { color: tokens.text },
  '&.MuiChip-clickable:hover': { bgcolor: 'rgba(57, 135, 229, 0.28)' },
} as const;

/**
 * Filter bar.
 * @param props - Component props
 * @returns Bar element
 */
const AtlasFilterBar: React.FC<AtlasFilterBarProps> = ({
  selection,
  area,
  types,
  text,
  conflictsOnly = false,
  road = '',
  result,
  loading,
  onClearTime,
  onFocusTime,
  onClearArea,
  onFocusArea,
  onClearTypes,
  onClearText,
  onClearConflicts,
  onClearRoad,
  onClearAll,
}) => {
  const active =
    !!selection || !!area || types.length > 0 || text !== '' || conflictsOnly || road !== '';
  const count = loading ? 'Searching…' : result ? formatResultCount(result) : '';

  return (
    <Box
      role="region"
      aria-label="Active filters"
      sx={{
        display: 'flex',
        alignItems: 'center',
        flexWrap: 'wrap',
        columnGap: 0.75,
        rowGap: 0.5,
        px: { xs: 1.5, md: 2.5 },
        py: 0.625,
        minHeight: 38,
        flexShrink: 0,
        borderBottom: `1px solid ${tokens.border}`,
        bgcolor: active ? 'rgba(57, 135, 229, 0.06)' : tokens.surface,
      }}
    >
      <FilterAlt sx={{ fontSize: 18, color: active ? FILTER_COLOR : tokens.textMuted }} />
      {active ? (
        <>
          {selection && (
            <Tooltip title="Time filter — click to zoom the timeline to it">
              <Chip
                size="small"
                icon={<Schedule />}
                label={formatTimeFilter(selection)}
                onClick={onFocusTime}
                onDelete={onClearTime}
                sx={chipSx}
              />
            </Tooltip>
          )}
          {area && (
            <Tooltip title="Area filter — click to show it on the map">
              <Chip
                size="small"
                icon={<CropFree />}
                label={formatAreaFilter(area)}
                onClick={onFocusArea}
                onDelete={onClearArea}
                sx={chipSx}
              />
            </Tooltip>
          )}
          {types.length > 0 && (
            <Tooltip title="Activity types">
              <Chip
                size="small"
                icon={<Category />}
                label={formatTypesFilter(types)}
                onDelete={onClearTypes}
                sx={chipSx}
              />
            </Tooltip>
          )}
          {text !== '' && (
            <Tooltip title="Text search">
              <Chip
                size="small"
                icon={<Search />}
                label={`“${text}”`}
                onDelete={onClearText}
                sx={chipSx}
              />
            </Tooltip>
          )}
          {road !== '' && (
            <Tooltip title="Only routes on this road (drives drawn on the roads)">
              <Chip
                size="small"
                icon={<AltRoute />}
                label={formatRoadFilter(road)}
                onDelete={onClearRoad}
                sx={chipSx}
              />
            </Tooltip>
          )}
          {conflictsOnly && (
            <Tooltip title="Only activities whose sources disagree">
              <Chip
                size="small"
                icon={<WarningAmber />}
                label="Sources disagree"
                onDelete={onClearConflicts}
                sx={chipSx}
              />
            </Tooltip>
          )}
          <Button size="small" onClick={onClearAll} sx={{ minWidth: 0, px: 1 }}>
            Clear all
          </Button>
        </>
      ) : (
        <Typography variant="caption" sx={{ color: tokens.textMuted, minWidth: 0 }}>
          <Box component="span" sx={{ display: { xs: 'none', sm: 'inline' } }}>
            Showing everything — drag on the timeline for a time range, use Area to choose a place
          </Box>
          <Box component="span" sx={{ display: { xs: 'inline', sm: 'none' } }}>
            Showing everything
          </Box>
        </Typography>
      )}
      {count && (
        <Typography
          variant="caption"
          aria-live="polite"
          sx={{ ml: 'auto', pl: 1, color: tokens.textSecondary, whiteSpace: 'nowrap' }}
        >
          {count}
        </Typography>
      )}
    </Box>
  );
};

export default AtlasFilterBar;
