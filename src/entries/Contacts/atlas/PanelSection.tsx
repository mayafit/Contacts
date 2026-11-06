/**
 * @fileoverview A light collapsible section of the activity panel: a header
 * row with a chevron, optional controls on the right, and its content
 * @module Contacts/atlas/PanelSection
 */

import React, { useId, useState } from 'react';
import { Box, ButtonBase, Collapse, Typography } from '@mui/material';
import { ExpandMore } from '@mui/icons-material';
import { tokens } from '../theme/contactsTheme';
import { fs } from '../settings/uiSettings';

/** Props for PanelSection */
export interface PanelSectionProps {
  title: string;
  /** Shown after the title while collapsed, e.g. "#2 of 7" */
  hint?: React.ReactNode;
  /** Controls on the right of the header (stay clickable, not part of the toggle) */
  extra?: React.ReactNode;
  /** Expanded at first (default true) */
  defaultOpen?: boolean;
  children: React.ReactNode;
}

/**
 * A collapsible section.
 * @param props - Component props
 * @returns Section element
 */
const PanelSection: React.FC<PanelSectionProps> = ({
  title,
  hint,
  extra,
  defaultOpen = true,
  children,
}) => {
  const [open, setOpen] = useState(defaultOpen);
  const id = useId();
  return (
    <Box component="section" sx={{ mt: 1.5, borderTop: `1px solid ${tokens.border}`, pt: 0.75 }}>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, minHeight: 28 }}>
        <ButtonBase
          onClick={() => setOpen((v) => !v)}
          aria-expanded={open}
          aria-controls={id}
          sx={{
            flex: 1,
            minWidth: 0,
            justifyContent: 'flex-start',
            gap: 0.5,
            py: 0.25,
            borderRadius: '6px',
            '&:hover': { bgcolor: tokens.hover },
          }}
        >
          <ExpandMore
            sx={{
              fontSize: fs(18),
              color: tokens.textMuted,
              transform: open ? 'none' : 'rotate(-90deg)',
              transition: 'transform 150ms',
            }}
          />
          <Typography variant="subtitle2" noWrap sx={{ color: tokens.textSecondary }}>
            {title}
          </Typography>
          {!open && hint && (
            <Typography variant="caption" noWrap sx={{ color: tokens.textMuted, minWidth: 0 }}>
              {hint}
            </Typography>
          )}
        </ButtonBase>
        {open && extra}
      </Box>
      <Collapse in={open} id={id}>
        <Box sx={{ pt: 0.5 }}>{children}</Box>
      </Collapse>
    </Box>
  );
};

export default PanelSection;
