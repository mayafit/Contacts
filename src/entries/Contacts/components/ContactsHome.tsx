/**
 * @fileoverview Main Contacts home page with contact grid
 * @module Contacts/components/ContactsHome
 */

import React, { useState } from 'react';
import { useSelector, useDispatch } from 'react-redux';
import { useNavigate } from 'react-router';
import { Container, Typography, Button, Box, Card, CardContent, Avatar, IconButton, Tooltip } from '@mui/material';
import { Logout as LogoutIcon, Settings as SettingsIcon } from '@mui/icons-material';
import { logout } from '../redux/slices/auth/authSlice';
import { selectUser } from '../redux/slices/auth/selectors';
import { logger } from '../../../shared/logger';
import VirtualizedContactsTable from './VirtualizedContactsTable';
import ColumnConfigDialog from '../features/columnConfig/components/ColumnConfigDialog';

/**
 * Contacts home page component
 * Displays authenticated user info, sign out button, and Google contacts table
 */
const ContactsHome: React.FC = () => {
  const dispatch = useDispatch();
  const navigate = useNavigate();
  const user = useSelector(selectUser);
  const [isColumnConfigOpen, setIsColumnConfigOpen] = useState(false);

  const handleSignOut = () => {
    logger.info(
      {
        context: 'ContactsHome/handleSignOut',
        metadata: { userId: user?.id, userEmail: user?.email },
      },
      'User initiated sign out',
    );

    dispatch(logout());
    navigate('/login');

    logger.info(
      {
        context: 'ContactsHome/handleSignOut',
      },
      'User signed out successfully and redirected to login',
    );
  };

  return (
    <Container maxWidth="lg">
      <Box sx={{ mt: 4, mb: 4 }}>
        <Card>
          <CardContent>
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, mb: 3 }}>
              {user?.profilePicture && (
                <Avatar src={user.profilePicture} alt={user.name} sx={{ width: 56, height: 56 }} />
              )}
              <Box>
                <Typography variant="h5" component="h1">
                  Welcome, {user?.name}
                </Typography>
                <Typography variant="body2" color="text.secondary">
                  {user?.email}
                </Typography>
              </Box>
              <Box sx={{ ml: 'auto' }}>
                <Button variant="outlined" startIcon={<LogoutIcon />} onClick={handleSignOut}>
                  Sign Out
                </Button>
              </Box>
            </Box>
            <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', mb: 2 }}>
              <Typography variant="h6" gutterBottom sx={{ mb: 0 }}>
                Your Contacts
              </Typography>
              <Tooltip title="Configure Columns">
                <IconButton
                  onClick={() => setIsColumnConfigOpen(true)}
                  aria-label="Configure columns"
                >
                  <SettingsIcon />
                </IconButton>
              </Tooltip>
            </Box>
          </CardContent>
        </Card>
        <Box sx={{ mt: 3 }}>
          <VirtualizedContactsTable />
        </Box>
      </Box>

      {/* Column Configuration Dialog */}
      <ColumnConfigDialog
        open={isColumnConfigOpen}
        onClose={() => setIsColumnConfigOpen(false)}
      />
    </Container>
  );
};

export default ContactsHome;
