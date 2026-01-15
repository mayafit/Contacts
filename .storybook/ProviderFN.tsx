import React from 'react';
import DirectionWrapper from './_directionWrapper';

import { createTheme, ThemeProvider } from '@mui/material/styles';
import { Paper } from '@mui/material';
const ProviderFn = ({ theme, children }) => {
  const muTheme = createTheme(theme);
  // console.log(theme);
  // console.log(JSON.stringify(muTheme));
  return (
    // @ts-ignore
    <DirectionWrapper direction={theme.direction}>
      <ThemeProvider theme={muTheme}>
        {/* <Paper
            style={{
              width: '100%',
              height: '100%',
              position: 'absolute',
              padding: '20px',
            }}
          > */}
        {children}
        {/* </Paper> */}
      </ThemeProvider>
    </DirectionWrapper>
  );
};

export default ProviderFn;
