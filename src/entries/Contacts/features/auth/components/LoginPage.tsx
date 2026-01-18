/**
 * @fileoverview Login page with Google OAuth button
 * @module Contacts/features/auth/components/LoginPage
 */

import React from 'react';
import { GoogleLogin, type CredentialResponse } from '@react-oauth/google';
import { useDispatch } from 'react-redux';
import { useNavigate } from 'react-router';
import { Box, Card, CardContent, Typography, Container } from '@mui/material';
import { loginWithGoogle } from '../../../redux/slices/auth/authSlice';
import type { AppDispatch } from '../../../types/store';
import type { GoogleCredentialResponse } from '../../../types';

/**
 * Login page component
 * Displays Google OAuth login button for user authentication
 */
const LoginPage: React.FC = () => {
  const dispatch = useDispatch<AppDispatch>();
  const navigate = useNavigate();

  /**
   * Handle successful Google OAuth authentication
   */
  const handleSuccess = async (credentialResponse: CredentialResponse) => {
    if (!credentialResponse.credential) {
      console.error('No credential received from Google');
      return;
    }

    const googleCred: GoogleCredentialResponse = {
      credential: credentialResponse.credential,
      select_by: credentialResponse.select_by,
    };

    try {
      await dispatch(loginWithGoogle(googleCred)).unwrap();
      // Redirect to main app on successful login
      navigate('/');
    } catch (error) {
      console.error('Login failed:', error);
    }
  };

  /**
   * Handle Google OAuth authentication error
   */
  const handleError = () => {
    console.error('Google OAuth authentication failed');
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
            <Typography variant="body1" color="text.secondary" align="center">
              Sign in with your Google account to access and manage your contacts
            </Typography>
            <Box sx={{ mt: 2 }}>
              <GoogleLogin onSuccess={handleSuccess} onError={handleError} />
            </Box>
          </CardContent>
        </Card>
      </Box>
    </Container>
  );
};

export default LoginPage;
