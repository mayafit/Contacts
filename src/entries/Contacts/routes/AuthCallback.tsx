/**
 * @fileoverview OAuth callback handler for Google authentication
 * @module Contacts/routes/AuthCallback
 */

import React, { useEffect } from 'react';
import { useNavigate } from 'react-router';
import { Box, CircularProgress, Typography } from '@mui/material';

/**
 * Auth callback component
 * Handles OAuth redirect from Google with authorization code
 * Note: Using @react-oauth/google with implicit flow, so this is primarily
 * a placeholder for future authorization code flow implementation
 */
const AuthCallback: React.FC = () => {
  const navigate = useNavigate();

  useEffect(() => {
    // For @react-oauth/google, the token exchange happens automatically
    // This callback is mainly for handling potential errors or edge cases
    // In a full OAuth 2.0 authorization code flow, this would:
    // 1. Extract authorization code from URL
    // 2. Exchange code for access token
    // 3. Store tokens securely
    // 4. Redirect to main app

    // For now, redirect to login if we end up here
    const timer = setTimeout(() => {
      navigate('/login');
    }, 2000);

    return () => clearTimeout(timer);
  }, [navigate]);

  return (
    <Box
      sx={{
        minHeight: '100vh',
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        justifyContent: 'center',
        gap: 2,
      }}
    >
      <CircularProgress />
      <Typography variant="body1" color="text.secondary">
        Completing authentication...
      </Typography>
    </Box>
  );
};

export default AuthCallback;
