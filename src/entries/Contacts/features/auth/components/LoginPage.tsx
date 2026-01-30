/**
 * @fileoverview Login page with backend OAuth redirect
 * @module Contacts/features/auth/components/LoginPage
 *
 * Updated for Story 2.1B: Now redirects to backend OAuth endpoint
 * OAuth flow handled server-side
 */

import React from 'react';
import { useLocation } from 'react-router';
import { Box, Card, CardContent, Typography, Container, Alert, Button } from '@mui/material';
import { Login as LoginIcon } from '@mui/icons-material';
import { GoogleContactsService } from '../../../services/GoogleContactsService';
import { logger } from '../../../../../shared/logger';

/**
 * Login page component
 * Displays sign-in button that redirects to backend OAuth flow
 */
const LoginPage: React.FC = () => {
  const location = useLocation();
  const searchParams = new URLSearchParams(location.search);
  const errorParam = searchParams.get('error');

  const errorMessages: Record<string, string> = {
    invalid_state: 'Authentication failed: invalid state parameter',
    no_code: 'Authentication failed: no authorization code received',
    config_error: 'Server configuration error. Please contact support.',
    auth_failed: 'Authentication failed. Please try again.',
  };

  const errorMessage = errorParam ? errorMessages[errorParam] || 'An unknown error occurred' : null;

  /**
   * Handle sign in button click
   * Redirects to backend OAuth login endpoint
   */
  const handleSignIn = () => {
    logger.info(
      {
        context: 'auth/LoginPage',
      },
      'User initiated backend OAuth login',
    );

    GoogleContactsService.login();
  };

  return (
    <Container maxWidth="sm">
      <Box
        sx={{
          minHeight: '100vh',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
        }}
      >
        <Card sx={{ width: '100%', maxWidth: 400 }}>
          <CardContent
            sx={{
              display: 'flex',
              flexDirection: 'column',
              alignItems: 'center',
              gap: 3,
              py: 4,
            }}
          >
            <Typography variant="h4" component="h1" gutterBottom>
              Contacts
            </Typography>
            {errorMessage && (
              <Alert severity="error" sx={{ width: '100%' }}>
                {errorMessage}
              </Alert>
            )}
            <Typography variant="body1" color="text.secondary" align="center">
              Sign in with your Google account to access and manage your contacts
            </Typography>
            <Box sx={{ mt: 2 }}>
              <Button
                variant="contained"
                size="large"
                startIcon={<LoginIcon />}
                onClick={handleSignIn}
                sx={{
                  textTransform: 'none',
                  px: 4,
                  py: 1.5,
                }}
              >
                Sign in with Google
              </Button>
              <Typography variant="caption" color="text.secondary" sx={{ mt: 2, display: 'block', textAlign: 'center' }}>
                You will be redirected to Google to authorize access to your contacts
              </Typography>
            </Box>
          </CardContent>
        </Card>
      </Box>
    </Container>
  );
};

export default LoginPage;
