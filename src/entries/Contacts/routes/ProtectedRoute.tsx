/**
 * @fileoverview Protected route component for authenticated views
 * @module Contacts/routes/ProtectedRoute
 */

import React, { useEffect } from 'react';
import { Navigate } from 'react-router';
import { useSelector, useDispatch } from 'react-redux';
import {
  selectIsAuthenticated,
  selectIsTokenExpired,
  selectAuthError,
} from '../redux/slices/auth/selectors';
import { logout } from '../redux/slices/auth/authSlice';
import { logger } from '../../../shared/logger';
import { ErrorCode } from '../types';

interface ProtectedRouteProps {
  /** Child components to render if authenticated */
  children: React.ReactNode;
}

/**
 * Protected route wrapper component
 * Redirects unauthenticated users to login page
 * Checks token expiry and clears expired tokens before redirect
 */
const ProtectedRoute: React.FC<ProtectedRouteProps> = ({ children }) => {
  const dispatch = useDispatch();
  const isAuthenticated = useSelector(selectIsAuthenticated);
  const isTokenExpired = useSelector(selectIsTokenExpired);
  const authError = useSelector(selectAuthError);

  // Clear expired tokens when detected
  useEffect(() => {
    if (isTokenExpired && isAuthenticated) {
      logger.warn(
        {
          context: 'auth/ProtectedRoute',
        },
        'Token expired - clearing authentication state and redirecting to login',
      );
      dispatch(logout());
    }
  }, [isTokenExpired, isAuthenticated, dispatch]);

  // Handle re-authentication prompt when 401 detected via interceptor
  useEffect(() => {
    if (authError?.code === ErrorCode.TOKEN_EXPIRED) {
      logger.info(
        {
          context: 'auth/ProtectedRoute',
          metadata: {
            errorCode: authError.code,
            errorMessage: authError.message,
          },
        },
        'Re-authentication required - token expired during API call',
      );
      // Logout is already dispatched by interceptor
      // Navigate component below will redirect to /login with state
    }
  }, [authError]);

  // Redirect to login if not authenticated or token expired
  if (!isAuthenticated || isTokenExpired) {
    // Pass error message as state for display on login page
    return (
      <Navigate
        to="/login"
        replace
        state={{
          message: authError?.message || 'Please sign in to continue.',
        }}
      />
    );
  }

  return <>{children}</>;
};

export default ProtectedRoute;
