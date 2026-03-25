/**
 * @fileoverview Contacts application root component with routing
 * @module Contacts/ContactsApp
 *
 * Updated for Story 2.1B: Simplified to work with backend cookie-based authentication
 * OAuth flow now handled server-side
 */

import React, { useEffect } from 'react';
import { BrowserRouter, Routes, Route, Navigate } from 'react-router';
import { useDispatch } from 'react-redux';
import LoginPage from './features/auth/components/LoginPage';
import AuthCallback from './routes/AuthCallback';
import ProtectedRoute from './routes/ProtectedRoute';
import ContactsHome from './components/ContactsHome';
import { checkAuthStatus } from './redux/slices/auth/authSlice';
import type { AppDispatch } from './types/store';
import { logger } from '../../shared/logger';

/**
 * Contacts application component
 * Sets up routing and checks authentication status with backend on load
 */
const ContactsApp: React.FC = () => {
  const dispatch = useDispatch<AppDispatch>();
  const [isCheckingAuth, setIsCheckingAuth] = React.useState(true);

  /**
   * Check authentication status with backend on app mount
   * Backend will verify session cookie and return auth status
   */
  useEffect(() => {
    logger.info(
      {
        context: 'Contacts/ContactsApp',
      },
      'Checking authentication status with backend',
    );

    dispatch(checkAuthStatus())
      .then((result) => {
        logger.info(
          {
            context: 'Contacts/ContactsApp',
            metadata: {
              type: result.type,
              payload: result.payload,
            },
          },
          'Auth check completed',
        );
      })
      .catch((error) => {
        logger.error(
          {
            context: 'Contacts/ContactsApp',
            metadata: {
              error: error.message,
            },
          },
          'Auth check failed',
          error,
        );
      })
      .finally(() => {
        logger.info(
          {
            context: 'Contacts/ContactsApp',
          },
          'Setting isCheckingAuth to false',
        );
        setIsCheckingAuth(false);
      });
  }, [dispatch]);

  // Show loading state while checking authentication
  // This prevents ProtectedRoute from redirecting before auth check completes
  if (isCheckingAuth) {
    return (
      <div style={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: '100vh' }}>
        <div>Checking authentication...</div>
      </div>
    );
  }

  return (
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
  );
};

export default ContactsApp;
