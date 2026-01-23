/**
 * @fileoverview Main Contacts home page (placeholder for contact grid)
 * @module Contacts/components/ContactsHome
 */

import React from 'react';
import { useSelector, useDispatch } from 'react-redux';
import { useNavigate } from 'react-router';
import { Container, Typography, Button, Box, Card, CardContent, Avatar } from '@mui/material';
import { Logout as LogoutIcon } from '@mui/icons-material';
import { logout } from '../redux/slices/auth/authSlice';
import { selectUser } from '../redux/slices/auth/selectors';
import { logger } from '../../../shared/logger';

/**
 * Contacts home page component
 * Placeholder for the main contact grid (Epic 2)
 * Currently displays authenticated user info and sign out button
 */
const ContactsHome: React.FC = () => {
  const dispatch = useDispatch();
  const navigate = useNavigate();
  const user = useSelector(selectUser);

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
            <Typography variant="h6" gutterBottom>
              Contacts
            </Typography>
            <Typography variant="body1" color="text.secondary">
              Contact grid will be implemented in Epic 2.
              <br />
              Authentication is now complete! ✅
            </Typography>
          </CardContent>
        </Card>
      </Box>
    </Container>
  );
};

export default ContactsHome;
