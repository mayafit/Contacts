/**
 * @fileoverview Contacts application root component with routing
 * @module Contacts/ContactsApp
 */

import React, { useEffect } from 'react';
import { GoogleOAuthProvider } from '@react-oauth/google';
import { BrowserRouter, Routes, Route, Navigate } from 'react-router';
import { useDispatch } from 'react-redux';
import LoginPage from './features/auth/components/LoginPage';
import AuthCallback from './routes/AuthCallback';
import ProtectedRoute from './routes/ProtectedRoute';
import ContactsHome from './components/ContactsHome';
import { restoreAuthState } from './redux/slices/auth/authSlice';
import type { User } from './types';
import { logger } from '../../shared/logger';

/**
 * Contacts application component
 * Sets up OAuth provider, routing, and session restoration
 */
const ContactsApp: React.FC = () => {
  const dispatch = useDispatch();
  const clientId = process.env.REACT_APP_GOOGLE_CLIENT_ID;

  if (!clientId) {
    logger.error(
      {
        context: 'Contacts/ContactsApp',
      },
      'REACT_APP_GOOGLE_CLIENT_ID environment variable is not defined',
    );
    return (
      <div>
        <h1>Configuration Error</h1>
        <p>Google OAuth Client ID is not configured. Please check your .env file.</p>
      </div>
    );
  }

  /**
   * Restore authentication state from sessionStorage on app load
   */
  useEffect(() => {
    const accessToken = sessionStorage.getItem('access_token');
    const tokenExpiry = sessionStorage.getItem('token_expiry');
    const userJson = sessionStorage.getItem('user');

    if (accessToken && tokenExpiry && userJson) {
      try {
        const user: User = JSON.parse(userJson);
        const expiry = new Date(tokenExpiry);

        // Only restore if token hasn't expired
        if (expiry > new Date()) {
          dispatch(
            restoreAuthState({
              user,
              token: accessToken,
              tokenExpiry,
            }),
          );
        } else {
          // Clear expired tokens
          sessionStorage.removeItem('access_token');
          sessionStorage.removeItem('token_expiry');
          sessionStorage.removeItem('user');
        }
      } catch (error) {
        logger.error(
          {
            context: 'Contacts/ContactsApp',
            metadata: {
              hasAccessToken: !!accessToken,
              hasTokenExpiry: !!tokenExpiry,
              hasUserJson: !!userJson,
            },
          },
          'Failed to restore authentication state from sessionStorage',
          error instanceof Error ? error : new Error(String(error)),
        );
      }
    }
  }, [dispatch]);

  return (
    <GoogleOAuthProvider clientId={clientId}>
      <BrowserRouter>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route path="/auth/callback" element={<AuthCallback />} />
          <Route
            path="/"
            element={
              <ProtectedRoute>
                <ContactsHome />
              </ProtectedRoute>
            }
          />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </BrowserRouter>
    </GoogleOAuthProvider>
  );
};

export default ContactsApp;
