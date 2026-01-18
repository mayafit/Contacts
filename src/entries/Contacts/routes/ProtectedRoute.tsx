/**
 * @fileoverview Protected route component for authenticated views
 * @module Contacts/routes/ProtectedRoute
 */

import React from 'react';
import { Navigate } from 'react-router';
import { useSelector } from 'react-redux';
import { selectIsAuthenticated, selectIsTokenExpired } from '../redux/slices/auth/selectors';

interface ProtectedRouteProps {
  /** Child components to render if authenticated */
  children: React.ReactNode;
}

/**
 * Protected route wrapper component
 * Redirects unauthenticated users to login page
 * Checks token expiry and redirects if expired
 */
const ProtectedRoute: React.FC<ProtectedRouteProps> = ({ children }) => {
  const isAuthenticated = useSelector(selectIsAuthenticated);
  const isTokenExpired = useSelector(selectIsTokenExpired);

  // Redirect to login if not authenticated or token expired
  if (!isAuthenticated || isTokenExpired) {
    return <Navigate to="/login" replace />;
  }

  return <>{children}</>;
};

export default ProtectedRoute;
