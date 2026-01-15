import React from 'react';
import { Fab, Typography, Tooltip } from '@mui/material';

import { toast } from 'react-toastify';

const ExternalAction = () => {
  return (
    <Tooltip title="i'm a tooltip" disableInteractive>
      <Fab
        color="primary"
        onClick={() => {
          toast('Hello');
        }}
      >
        <Typography variant="h6" color="textPrimary">
          M
        </Typography>
      </Fab>
    </Tooltip>
  );
};
export default ExternalAction;
