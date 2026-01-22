/**
 * @fileoverview Axios response interceptor for 401 authentication error handling
 * @module Contacts/services/api/interceptors/authInterceptor
 */

import axios from 'axios';
import { getGlobals } from '../../../../../GLOBALS';
import { logout, setAuthError } from '../../../redux/slices/auth/authSlice';
import { logger } from '../../../../../shared/logger';
import { ErrorCode } from '../../../types';

/**
 * Configure axios response interceptor for 401 handling
 * Automatically logs out user and sets error when token expires during API calls
 */
export const setupAuthInterceptor = (): void => {
  axios.interceptors.response.use(
    (response) => response,
    (error) => {
      if (error.response?.status === 401) {
        logger.warn(
          {
            context: 'authInterceptor',
            metadata: {
              url: error.config?.url,
              method: error.config?.method,
            },
          },
          '401 Unauthorized - token expired or invalid',
        );

        // Get global dispatch from WebKit framework
        const dispatch = getGlobals().dispatch;

        if (dispatch) {
          // Dispatch logout to clear auth state
          dispatch(logout());

          // Set error for user feedback
          dispatch(
            setAuthError({
              code: ErrorCode.TOKEN_EXPIRED,
              message: 'Your session has expired. Please sign in again.',
              timestamp: new Date().toISOString(),
            }),
          );
        }
      }
      return Promise.reject(error);
    },
  );
};
