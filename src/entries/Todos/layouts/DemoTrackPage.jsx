import React, { useState } from 'react';

import { Typography, GlobalStyles } from '@mui/material';

import DemoTrackInfo from '../components/DemoTrackInfo';

const DemoTrackPage = () => {
  const [boolValue, setBoolValue] = useState(true);
  return (
    <div>
      <GlobalStyles
        styles={{
          '#MyMap': {
            position: 'fixed',
            width: `${boolValue ? '80%' : '20%'} !important`,
            [boolValue ? 'right' : 'left']: 0,
          },
        }}
      />
      <DemoTrackInfo />
      <div
        style={{
          width: boolValue ? '20%' : '80%',
          height: '100%',
          background: 'blue',
          position: 'fixed',
          top: '115px',
          [boolValue ? 'left' : 'right']: 0,
          zIndex: 1,
        }}
      >
        <Typography>Video</Typography>
      </div>
      <button
        onClick={() => {
          setBoolValue(!boolValue);
        }}
        style={{ position: 'fixed', top: '50%', right: '50%', zIndex: 1 }}
      >
        Click to toggle view
      </button>
    </div>
  );
};

export default DemoTrackPage;
