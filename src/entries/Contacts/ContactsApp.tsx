/**
 * @fileoverview Contacts application root component with routing
 * @module Contacts/ContactsApp
 *
 * Updated for Story 2.1B: Simplified to work with backend cookie-based authentication
 * OAuth flow now handled server-side
 */

import React, { useEffect, useMemo } from 'react';
import { BrowserRouter, Routes, Route, Navigate, useLocation, useNavigate } from 'react-router';
import { SESSION_EXPIRED_EVENT } from '../../services/api/BackendApiClient';
import { logout, logoutUser } from './redux/slices/auth/authSlice';
import { Box, CircularProgress, CssBaseline, ThemeProvider } from '@mui/material';
import { createContactsTheme } from './theme/contactsTheme';
import { useDispatch, useSelector } from 'react-redux';
import SettingsProvider, { useUi } from './settings/SettingsProvider';
import { selectIsAuthenticated } from './redux/slices/auth/selectors';
import SiteShell from './components/SiteShell';
import { checkAuthStatus } from './redux/slices/auth/authSlice';
import type { AppDispatch } from './types/store';
import { logger } from '../../shared/logger';

/**
 * Sends the user to sign in when an API call reports the Google session ended
 * (instead of leaving a page full of failed requests).
 * @returns Nothing visible
 */
const SessionWatcher: React.FC = () => {
  const navigate = useNavigate();
  const dispatch = useDispatch<AppDispatch>();
  useEffect(() => {
    const onExpired = () => {
      dispatch(logout());
      // Also end the server session, so a reload cannot look signed in
      void dispatch(logoutUser());
      navigate('/?error=session_expired', { replace: true });
    };
    window.addEventListener(SESSION_EXPIRED_EVENT, onExpired);
    return () => window.removeEventListener(SESSION_EXPIRED_EVENT, onExpired);
  }, [dispatch, navigate]);
  return null;
};

/**
 * Contacts application component
 * Sets up routing and checks authentication status with backend on load
 */
/** Redirects to the home page, keeping the query (e.g. ?error=session_expired) */
const ToHome: React.FC = () => {
  const { search } = useLocation();
  return <Navigate to={`/${search}`} replace />;
};

/** The theme from the Owner's text sizes (AD-41 rule 5), rebuilt when they change */
const Themed: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const t = useUi().text;
  // Rebuilt only when a text size changes (not on every map or colour change)
  const theme = useMemo(
    () =>
      createContactsTheme({
        body: t.body,
        small: t.small,
        headings: t.headings,
        charts: t.charts,
        map: t.map,
        navigation: t.navigation,
      }),
    [t.body, t.small, t.headings, t.charts, t.map, t.navigation],
  );
  return (
    <ThemeProvider theme={theme}>
      <CssBaseline />
      {children}
    </ThemeProvider>
  );
};

const ContactsApp: React.FC = () => {
  const dispatch = useDispatch<AppDispatch>();
  const [isCheckingAuth, setIsCheckingAuth] = React.useState(true);
  const isAuthenticated = useSelector(selectIsAuthenticated);

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
      <SettingsProvider signedIn={false}>
        <Themed>
          <Box
            sx={{ display: 'grid', placeItems: 'center', height: '100vh' }}
            aria-busy="true"
            aria-label="Checking sign-in"
          >
            <CircularProgress />
          </Box>
        </Themed>
      </SettingsProvider>
    );
  }

  return (
    <SettingsProvider signedIn={isAuthenticated}>
      <Themed>
        <BrowserRouter>
          <SessionWatcher />
          <Routes>
            {/* Old links (and the backend's error redirects) land on the home page */}
            <Route path="/login" element={<ToHome />} />
            <Route path="/auth/callback" element={<ToHome />} />
            {/* The site: Contacts and Atlas areas (or the welcome page when signed out) */}
            <Route path="/*" element={<SiteShell />} />
          </Routes>
        </BrowserRouter>
      </Themed>
    </SettingsProvider>
  );
};

export default ContactsApp;
