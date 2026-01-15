import React from 'react';
import { Fab, Typography, useTheme } from '@mui/material';

export interface ExternalFabProps {
  /**
   * The label of the fab
   */
  label?: string;

  /**
   * The color of the fab
   */
  color?: string;

  onClick?: React.MouseEventHandler<HTMLButtonElement>;
}

const ExternalFab: React.FC<ExternalFabProps> = (
  { label, onClick, color }: ExternalFabProps = { label: '?' },
) => {
  const theme = useTheme();
  return (
    <Fab sx={{ backgroundColor: color ?? theme.palette.primary.main }} onClick={onClick}>
      <Typography variant="h6" color="textPrimary">
        {label}
      </Typography>
    </Fab>
  );
};

export default ExternalFab;
