import React from 'react';

import { withPerformance } from 'storybook-addon-performance';
import { merge } from 'lodash-es';
// import '../blueFiberSrc/styles/styles.less';
// import '../src/styles/styles.less';

import { createTheme } from '@mui/material/styles';

import { CssBaseline, Paper } from '@mui/material';

import { withThemeFromJSXProvider } from '@storybook/addon-themes';

import { Preview } from '@storybook/react';
import ProviderFn from './ProviderFN';

const darkTheme = merge({}, { themeName: 'dark' }, { palette: { mode: 'dark' } });
const lightTheme = merge({}, { themeName: 'light' }, { palette: { mode: 'light' } });

const darkThemeRTL = merge(
  {},
  { themeName: 'darkRTL' },
  { palette: { mode: 'dark' }, direction: 'rtl' },
);

const lightThemeRTL = merge(
  {},
  { themeName: 'lightRTL' },
  { palette: { mode: 'light' }, direction: 'rtl' },
);

const preview: Preview = {
  parameters: {
    // layout: 'centered',
    actions: { argTypesRegex: '^on[A-Z].*' },
    controls: {
      matchers: {
        color: /(background|color)$/i,
        date: /Date$/,
      },
    },
  },
};

export const decorators = [
  withThemeFromJSXProvider({
    themes: {
      dark: JSON.parse(JSON.stringify(createTheme(darkTheme))),
      darkRtl: JSON.parse(JSON.stringify(createTheme(darkThemeRTL))),
      light: JSON.parse(JSON.stringify(createTheme(lightTheme))),
      lightRtl: JSON.parse(JSON.stringify(createTheme(lightThemeRTL))),
    },
    defaultTheme: 'dark',
    Provider: ProviderFn,
    GlobalStyles: CssBaseline,
  }),
  withPerformance(),
];
export default preview;
