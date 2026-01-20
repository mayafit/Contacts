/**
 * @fileoverview OAuth callback handler for Google authentication
 * @module Contacts/routes/AuthCallback
 */

import React, { useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router';
import { Box, CircularProgress, Typography, Alert } from '@mui/material';
import { logger } from '../../../shared/logger';

/**
 * Auth callback component
 * Handles OAuth redirect from Google with authorization code or error
 * Note: Using @react-oauth/google with implicit flow, the token exchange
 * happens automatically via the GoogleLogin component. This route handles
 * edge cases, errors, and provides user feedback during authentication.
 */
const AuthCallback: React.FC = () => {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    // Check for OAuth error in URL parameters
    const oauthError = searchParams.get('error');
    const errorDescription = searchParams.get('error_description');

    if (oauthError) {
      const errorMessage = errorDescription || 'Authentication failed. Please try again.';

      logger.error(
        {
          context: 'auth/AuthCallback',
          metadata: {
            error: oauthError,
            errorDescription,
          },
        },
        'OAuth callback received error from Google',
      );

      setError(errorMessage);

      // Redirect to login after showing error
      const timer = setTimeout(() => {
        navigate('/login');
      }, 3000);

      return () => clearTimeout(timer);
    }

    // For @react-oauth/google implicit flow, if we reach this callback
    // without error but also without being authenticated, redirect to login
    logger.info(
      {
        context: 'auth/AuthCallback',
      },
      'Auth callback reached - redirecting to login for explicit authentication',
    );

    const timer = setTimeout(() => {
      navigate('/login');
    }, 1500);

    return () => clearTimeout(timer);
  }, [navigate, searchParams]);

  if (error) {
    return (
      <Box
        sx={{
          minHeight: '100vh',
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          justifyContent: 'center',
          gap: 2,
          p: 3,
        }}
      >
        <Alert severity="error" sx={{ maxWidth: 500 }}>
          <Typography variant="h6" gutterBottom>
            Authentication Error
          </Typography>
          <Typography variant="body2">{error}</Typography>
        </Alert>
        <Typography variant="body2" color="text.secondary">
          Redirecting to login page...
        </Typography>
      </Box>
    );
  }

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
