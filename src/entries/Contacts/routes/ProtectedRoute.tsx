/**
 * @fileoverview Protected route component for authenticated views
 * @module Contacts/routes/ProtectedRoute
 */

import React, { useEffect } from 'react';
import { Navigate } from 'react-router';
import { useSelector, useDispatch } from 'react-redux';
import { selectIsAuthenticated, selectIsTokenExpired } from '../redux/slices/auth/selectors';
import { logout } from '../redux/slices/auth/authSlice';
import { logger } from '../../../shared/logger';

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

  // Redirect to login if not authenticated or token expired
  if (!isAuthenticated || isTokenExpired) {
    return <Navigate to="/login" replace />;
  }

  return <>{children}</>;
};

export default ProtectedRoute;
