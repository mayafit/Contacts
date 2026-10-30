/**
 * @fileoverview Theme for the Contacts app.
 * Independent of the host template theme (MuiTheme.js sets a 25px base font for
 * its 62.5% root trick, which made every input in this app oversized).
 * @module Contacts/theme/contactsTheme
 */

import { DEFAULT_UI } from '../settings/uiSettings';
import type { UiSettings } from '../settings/uiSettings';
import { alpha, createTheme } from '@mui/material/styles';

/** Design tokens shared by components that style outside the MUI palette */
export const tokens = {
  /** Page background */
  page: '#0e1116',
  /** Card / panel surface */
  surface: '#151a21',
  /** Raised surface: menus, drawer, table header */
  raised: '#1b212a',
  /** Row hover */
  hover: '#1f2630',
  /** Hairline border */
  border: 'rgba(255,255,255,0.08)',
  /** Stronger border for inputs */
  borderStrong: 'rgba(255,255,255,0.16)',
  /** Primary ink */
  text: '#e6e9ef',
  /** Secondary ink */
  textSecondary: '#a3acb9',
  /** Muted ink: placeholders, axis labels */
  textMuted: '#6f7a89',
  /** Single accent (validated chart blue, dark step) */
  accent: '#3987e5',
  /** Chart track behind bars */
  track: '#232a34',
  /** Attention (missing phonetic etc.) */
  attention: '#e8a23a',
} as const;

/** UI font stack with strong Hebrew coverage on every OS */
export const fontFamily =
  'system-ui, -apple-system, "Segoe UI", Roboto, "Noto Sans Hebrew", "Arial Hebrew", Arial, sans-serif';

/**
 * Creates the Contacts theme, its typography from the Owner's text sizes (AD-41 rule 5).
 * @param text - The text sizes (today's Echo when not given)
 * @returns MUI theme
 */
export const createContactsTheme = (text: UiSettings['text'] = DEFAULT_UI.text) => {
  const px = (n: number) => `${Math.round(n * 10) / 10}px`;
  const heading = (n: number) => px((n * text.headings) / 100);
  return createTheme({
    palette: {
      mode: 'dark',
      primary: { main: tokens.accent },
      background: { default: tokens.page, paper: tokens.surface },
      text: { primary: tokens.text, secondary: tokens.textSecondary, disabled: tokens.textMuted },
      divider: tokens.border,
      warning: { main: tokens.attention },
    },
    shape: { borderRadius: 10 },
    typography: {
      fontFamily,
      fontSize: text.body,
      // The host page sets html { font-size: 62.5% } (10px). MUI must know, or
      // every rem it computes comes out at 60% of the intended size.
      htmlFontSize: 10,
      h1: { fontSize: heading(25), fontWeight: 600, letterSpacing: '-0.01em' },
      h2: { fontSize: heading(21), fontWeight: 600 },
      h3: { fontSize: heading(18), fontWeight: 600 },
      subtitle1: { fontSize: heading(16), fontWeight: 600 },
      subtitle2: {
        fontSize: px(text.small),
        fontWeight: 600,
        letterSpacing: '0.04em',
        textTransform: 'uppercase',
      },
      body1: { fontSize: px(text.body) },
      body2: { fontSize: px(text.body - 0.5) },
      caption: { fontSize: px(text.small) },
      button: { textTransform: 'none', fontWeight: 600, fontSize: px(text.body) },
    },
    components: {
      MuiCssBaseline: {
        styleOverrides: {
          body: { backgroundColor: tokens.page, colorScheme: 'dark', fontSize: px(text.body) },
          '*::-webkit-scrollbar': { width: 10, height: 10 },
          '*::-webkit-scrollbar-thumb': {
            backgroundColor: 'rgba(255,255,255,0.14)',
            borderRadius: 8,
            border: `2px solid ${tokens.surface}`,
          },
          '*::-webkit-scrollbar-track': { backgroundColor: 'transparent' },
        },
      },
      MuiPaper: { styleOverrides: { root: { backgroundImage: 'none' } } },
      MuiButton: {
        defaultProps: { disableElevation: true },
        styleOverrides: { root: { borderRadius: 8 } },
      },
      MuiChip: {
        styleOverrides: {
          root: { borderRadius: 8, fontWeight: 500 },
          sizeSmall: { height: 26 },
        },
      },
      MuiTooltip: {
        defaultProps: { arrow: true },
        styleOverrides: {
          tooltip: {
            backgroundColor: '#2a313c',
            fontSize: px(text.small),
            border: `1px solid ${tokens.border}`,
          },
          arrow: { color: '#2a313c' },
        },
      },
      MuiOutlinedInput: {
        styleOverrides: {
          root: {
            backgroundColor: alpha('#ffffff', 0.02),
            '& .MuiOutlinedInput-notchedOutline': { borderColor: tokens.borderStrong },
          },
        },
      },
      MuiDrawer: {
        styleOverrides: {
          paper: { backgroundColor: tokens.raised, borderLeft: `1px solid ${tokens.border}` },
        },
      },
      MuiMenu: {
        styleOverrides: {
          paper: { backgroundColor: tokens.raised, border: `1px solid ${tokens.border}` },
        },
      },
      MuiDialog: { styleOverrides: { paper: { backgroundColor: tokens.raised } } },
    },
  });
};
