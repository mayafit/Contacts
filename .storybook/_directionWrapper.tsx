import React, { useEffect } from 'react';
// import { create } from 'jss';
// import rtl from 'jss-rtl';
import { CacheProvider } from '@emotion/react';
import createCache from '@emotion/cache';
import rtlPlugin from 'stylis-plugin-rtl';
// import { jssPreset, StylesProvider } from '@mui/styles';
import { StyledEngineProvider } from '@mui/material/styles';

const cacheRtl = createCache({
  key: 'muirtl',
  stylisPlugins: [rtlPlugin],
  // prepend: true,
});
const cacheLtr = createCache({
  key: 'muiltr',
  // prepend: true,
});
// import JssProvider from 'react-jss/lib/JssProvider';

const DirectionWrapper: React.FC<{
  direction?: string;
  className?: string;
  children?: React.ReactNode;
}> = ({ direction = 'ltr', children, className } = {}) => {
  // const jss =
  //   direction === 'ltr'
  //     ? create({ insertionPoint: 'jss-insertion-point', plugins: [...jssPreset().plugins] })
  //     : create({
  //         insertionPoint: 'jss-insertion-point',
  //         plugins: [...jssPreset().plugins, rtl()],
  //       });

  useEffect(() => {
    document.body.dir = direction;
  }, [direction]);
  return (
    // <StyledEngineProvider injectFirst>

    <StyledEngineProvider injectFirst>
      <CacheProvider value={direction === 'rtl' ? cacheRtl : cacheLtr}>
        {/* <StylesProvider jss={jss}> */}
        {/* <div className={className} dir={direction}> */}
        <div className={className}>{children}</div>
        {/* </StylesProvider> */}
      </CacheProvider>
    </StyledEngineProvider>
  );
};

export default DirectionWrapper;
