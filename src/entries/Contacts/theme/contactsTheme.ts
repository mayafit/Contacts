/**
 * @fileoverview Theme for the Contacts app.
 * Independent of the host template theme (MuiTheme.js sets a 25px base font for
 * its 62.5% root trick, which made every input in this app oversized).
 * @module Contacts/theme/contactsTheme
 */

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
 * Creates the Contacts theme.
 * @returns MUI theme
 */
export const createContactsTheme = () =>
  createTheme({
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
      fontSize: 15,
      // The host page sets html { font-size: 62.5% } (10px). MUI must know, or
      // every rem it computes comes out at 60% of the intended size.
      htmlFontSize: 10,
      h1: { fontSize: '25px', fontWeight: 600, letterSpacing: '-0.01em' },
      h2: { fontSize: '21px', fontWeight: 600 },
      h3: { fontSize: '18px', fontWeight: 600 },
      subtitle1: { fontSize: '16px', fontWeight: 600 },
      subtitle2: {
        fontSize: '13px',
        fontWeight: 600,
        letterSpacing: '0.04em',
        textTransform: 'uppercase',
      },
      body1: { fontSize: '15px' },
      body2: { fontSize: '14.5px' },
      caption: { fontSize: '13px' },
      button: { textTransform: 'none', fontWeight: 600, fontSize: '15px' },
    },
    components: {
      MuiCssBaseline: {
        styleOverrides: {
          body: { backgroundColor: tokens.page, colorScheme: 'dark', fontSize: '15px' },
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
            fontSize: '13px',
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
