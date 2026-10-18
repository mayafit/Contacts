/**
 * @fileoverview Vertical A–Z / א–ת index beside the contacts table.
 * Shows only letters that occur in the current list, in list order, grouped by
 * script; clicking a letter jumps to its first contact.
 * @module Contacts/components/common/AlphabetRail
 */

import React from 'react';
import { Box, ButtonBase } from '@mui/material';
import { tokens } from '../../theme/contactsTheme';

/** One letter and the first row index filed under it */
export interface RailEntry {
  /** Index letter */
  letter: string;
  /** First row index for the letter */
  index: number;
}

/** Props for AlphabetRail */
export interface AlphabetRailProps {
  /** Letters present in the list, in list order */
  entries: RailEntry[];
  /** Letter of the first visible row */
  activeLetter: string | null;
  /** Called with the row index to jump to */
  onJump: (index: number) => void;
}

const scriptOfLetter = (letter: string): string => {
  if (/[א-ת]/.test(letter)) {
    return 'he';
  }
  if (/[A-Z]/.test(letter)) {
    return 'latin';
  }
  return 'other';
};

/**
 * Alphabet rail.
 * @param props - Component props
 * @returns Rail element, or null when there is nothing to index
 */
const AlphabetRail: React.FC<AlphabetRailProps> = ({ entries, activeLetter, onJump }) => {
  if (entries.length < 2) {
    return null;
  }
  return (
    <Box
      component="nav"
      aria-label="Jump to letter"
      sx={{
        width: 28,
        flexShrink: 0,
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        justifyContent: 'center',
        gap: '1px',
        py: 1,
        borderLeft: `1px solid ${tokens.border}`,
        overflowY: 'auto',
        scrollbarWidth: 'none',
        '&::-webkit-scrollbar': { display: 'none' },
      }}
    >
      {entries.map((entry, i) => {
        const startsGroup =
          i > 0 && scriptOfLetter(entries[i - 1].letter) !== scriptOfLetter(entry.letter);
        const active = entry.letter === activeLetter;
        return (
          <React.Fragment key={entry.letter}>
            {startsGroup && (
              <Box
                aria-hidden
                sx={{
                  width: 4,
                  height: 4,
                  borderRadius: '50%',
                  bgcolor: tokens.textMuted,
                  my: 0.5,
                }}
              />
            )}
            <ButtonBase
              onClick={() => onJump(entry.index)}
              aria-label={`Jump to ${entry.letter}`}
              aria-current={active ? 'true' : undefined}
              sx={{
                width: 22,
                minHeight: 16,
                borderRadius: '6px',
                fontSize: 11,
                lineHeight: '16px',
                fontWeight: active ? 700 : 500,
                color: active ? '#fff' : tokens.textSecondary,
                bgcolor: active ? tokens.accent : 'transparent',
                '&:hover': { bgcolor: active ? tokens.accent : tokens.hover, color: tokens.text },
              }}
            >
              {entry.letter}
            </ButtonBase>
          </React.Fragment>
        );
      })}
    </Box>
  );
};

export default React.memo(AlphabetRail);
