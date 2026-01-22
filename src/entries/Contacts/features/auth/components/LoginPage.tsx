/**
 * @fileoverview Login page with Google OAuth button
 * @module Contacts/features/auth/components/LoginPage
 */

import React from 'react';
import { GoogleLogin, type CredentialResponse } from '@react-oauth/google';
import { useDispatch } from 'react-redux';
import { useNavigate, useLocation } from 'react-router';
import { Box, Card, CardContent, Typography, Container, Alert } from '@mui/material';
import { loginWithGoogle } from '../../../redux/slices/auth/authSlice';
import type { AppDispatch } from '../../../types/store';
import type { GoogleCredentialResponse } from '../../../types';
import { logger } from '../../../../../shared/logger';

/**
 * Login page component
 * Displays Google OAuth login button for user authentication
 */
const LoginPage: React.FC = () => {
  const dispatch = useDispatch<AppDispatch>();
  const navigate = useNavigate();
  const location = useLocation();
  const errorMessage = (location.state as { message?: string })?.message;

  /**
   * Handle successful Google OAuth authentication
   */
  const handleSuccess = async (credentialResponse: CredentialResponse) => {
    if (!credentialResponse.credential) {
      logger.error(
        {
          context: 'auth/LoginPage',
          metadata: { selectBy: credentialResponse.select_by },
        },
        'No credential received from Google OAuth',
      );
      return;
    }

    const googleCred: GoogleCredentialResponse = {
      credential: credentialResponse.credential,
      select_by: credentialResponse.select_by,
    };

    try {
      await dispatch(loginWithGoogle(googleCred)).unwrap();
      logger.info(
        {
          context: 'auth/LoginPage',
          metadata: { selectBy: credentialResponse.select_by },
        },
        'User successfully authenticated with Google',
      );
      // Redirect to main app on successful login
      navigate('/');
    } catch (error) {
      logger.error(
        {
          context: 'auth/LoginPage',
          metadata: {
            selectBy: credentialResponse.select_by,
          },
        },
        'Google OAuth login failed',
        error instanceof Error ? error : new Error(String(error)),
      );
    }
  };

  /**
   * Handle Google OAuth authentication error
   */
  const handleError = () => {
    logger.error(
      {
        context: 'auth/LoginPage',
      },
      'Google OAuth authentication failed',
    );
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
              <Alert severity="warning" sx={{ width: '100%' }}>
                {errorMessage}
              </Alert>
            )}
            <Typography variant="body1" color="text.secondary" align="center">
              Sign in with your Google account to access and manage your contacts
            </Typography>
            <Box sx={{ mt: 2 }}>
              <GoogleLogin onSuccess={handleSuccess} onError={handleError} />
              <Typography variant="caption" color="text.secondary" sx={{ mt: 1, display: 'block' }}>
                Note: Contacts scope will be requested during authorization
              </Typography>
            </Box>
          </CardContent>
        </Card>
      </Box>
    </Container>
  );
};

export default LoginPage;
