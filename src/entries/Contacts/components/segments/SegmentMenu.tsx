/**
 * @fileoverview The segments menu beside the label filter (AD-45 rule 7): segments with their
 * live counts and their linked labels' differences, new segment, manage labels
 * @module Contacts/components/segments/SegmentMenu
 */

import React, { useState } from 'react';
import { Box, Button, Chip, Divider, IconButton, Menu, MenuItem, Tooltip } from '@mui/material';
import { Add, Edit, Label as LabelIcon, Segment as SegmentIcon } from '@mui/icons-material';
import type { Segment } from '../../model/segments';
import { tokens } from '../../theme/contactsTheme';
import { fs } from '../../settings/uiSettings';

/** A segment's figures for the menu */
export interface SegmentFigures {
  count: number;
  /** The linked label's difference, when linked */
  diff?: { add: number; remove: number };
}

const diffText = (d?: { add: number; remove: number }): string =>
  d && d.add + d.remove > 0 ? `+${d.add} −${d.remove}` : '';

/**
 * The segments menu, and the active segment's chip.
 * @param props - Segments and their figures, the active one, actions
 * @returns Menu button and chip
 */
const SegmentMenu: React.FC<{
  segments: Segment[];
  figures: Map<string, SegmentFigures>;
  active: Segment | null;
  onSelect: (id: string | null) => void;
  onNew: () => void;
  onEdit: (segment: Segment) => void;
  onManageLabels: () => void;
  onUpdateLabel: (segment: Segment) => void;
}> = ({ segments, figures, active, onSelect, onNew, onEdit, onManageLabels, onUpdateLabel }) => {
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const close = () => setAnchor(null);
  const activeDiff = active ? figures.get(active.id)?.diff : undefined;
  return (
    <>
      <Button
        size="small"
        variant="outlined"
        color="inherit"
        startIcon={<SegmentIcon fontSize="small" />}
        onClick={(e) => setAnchor(e.currentTarget)}
        aria-haspopup="menu"
        sx={{ height: 32, borderRadius: '8px', borderColor: tokens.border, fontSize: fs(14) }}
      >
        Segments
      </Button>
      {active && (
        <Chip
          label={`${active.name} · ${figures.get(active.id)?.count ?? 0}`}
          onDelete={() => onSelect(null)}
          onClick={() => onEdit(active)}
          color="primary"
          variant="outlined"
          size="small"
        />
      )}
      {active && diffText(activeDiff) && (
        <Tooltip title="The linked label differs from the segment">
          <Button size="small" onClick={() => onUpdateLabel(active)}>
            Update label ({diffText(activeDiff)})
          </Button>
        </Tooltip>
      )}
      <Menu anchorEl={anchor} open={!!anchor} onClose={close}>
        {segments.map((s) => {
          const f = figures.get(s.id);
          return (
            <MenuItem
              key={s.id}
              selected={active?.id === s.id}
              onClick={() => {
                onSelect(s.id);
                close();
              }}
              sx={{ minWidth: 280, gap: 1 }}
            >
              <Box component="span" dir="auto" sx={{ flex: 1 }}>
                {s.name}
              </Box>
              {diffText(f?.diff) && (
                <Box component="span" sx={{ color: tokens.attention, fontSize: fs(12) }}>
                  {diffText(f?.diff)}
                </Box>
              )}
              <Box component="span" sx={{ color: tokens.textMuted }}>
                {f?.count ?? 0}
              </Box>
              <IconButton
                size="small"
                aria-label={`Edit ${s.name}`}
                onClick={(e) => {
                  e.stopPropagation();
                  onEdit(s);
                  close();
                }}
              >
                <Edit fontSize="small" />
              </IconButton>
            </MenuItem>
          );
        })}
        {segments.length > 0 && <Divider />}
        <MenuItem
          onClick={() => {
            onNew();
            close();
          }}
        >
          <Add fontSize="small" sx={{ mr: 1 }} /> New segment…
        </MenuItem>
        <MenuItem
          onClick={() => {
            onManageLabels();
            close();
          }}
        >
          <LabelIcon fontSize="small" sx={{ mr: 1 }} /> Manage labels…
        </MenuItem>
      </Menu>
    </>
  );
};

export default SegmentMenu;
