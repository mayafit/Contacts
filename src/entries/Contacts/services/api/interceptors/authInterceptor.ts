/**
 * @fileoverview Axios response interceptor for 401 authentication error handling with token refresh
 * @module Contacts/services/api/interceptors/authInterceptor
 */

import axios, { AxiosError, InternalAxiosRequestConfig } from 'axios';
import { getGlobals } from '../../../../../GLOBALS';
import { logout, setAuthError, refreshAccessToken } from '../../../redux/slices/auth/authSlice';
import { logger } from '../../../../../shared/logger';
import { ErrorCode } from '../../../types';

/**
 * Flag to prevent concurrent token refresh attempts
 */
let isRefreshing = false;

/**
 * Queue of failed requests waiting for token refresh
 */
let failedQueue: Array<{
  resolve: (token: string) => void;
  reject: (error: unknown) => void;
}> = [];

/**
 * Process queued requests after token refresh completes
 * @param error - Error if refresh failed, null if successful
 * @param token - New access token if refresh successful
 */
const processQueue = (error: unknown, token: string | null = null): void => {
  failedQueue.forEach((prom) => {
    if (error) {
      prom.reject(error);
    } else {
      prom.resolve(token!);
    }
  });
  failedQueue = [];
};

/**
 * Configure axios response interceptor for 401 handling with automatic token refresh
 * Attempts to refresh the token when 401 is detected, retries the original request
 * Falls back to logout if refresh fails
 */
export const setupAuthInterceptor = (): void => {
  axios.interceptors.response.use(
    (response) => response,
    async (error: AxiosError) => {
      const originalRequest = error.config as InternalAxiosRequestConfig & { _retry?: boolean };

      // Only handle 401 errors
      if (error.response?.status !== 401) {
        return Promise.reject(error);
      }

      logger.warn(
        {
          context: 'authInterceptor',
          metadata: {
            url: originalRequest?.url,
            method: originalRequest?.method,
          },
        },
        '401 Unauthorized - attempting token refresh',
      );

      // Get global dispatch from WebKit framework
      const dispatch = getGlobals().dispatch;

      if (!dispatch) {
        logger.error(
          {
            context: 'authInterceptor',
          },
          'Global dispatch not available',
        );
        return Promise.reject(error);
      }

      // If already refreshing, queue this request
      if (isRefreshing) {
        logger.info(
          {
            context: 'authInterceptor',
            metadata: { url: originalRequest?.url },
          },
          'Token refresh in progress - queueing request',
        );

        return new Promise((resolve, reject) => {
          failedQueue.push({ resolve, reject });
        })
          .then((token) => {
            if (originalRequest) {
              originalRequest.headers['Authorization'] = `Bearer ${token}`;
            }
            return axios(originalRequest);
          })
          .catch((err) => {
            return Promise.reject(err);
          });
      }

      // Mark as refreshing to prevent concurrent refreshes
      isRefreshing = true;
      originalRequest._retry = true;

      try {
        logger.info(
          {
            context: 'authInterceptor',
          },
          'Attempting to refresh access token',
        );

        // Attempt token refresh
        const result = await dispatch(refreshAccessToken());

        if (refreshAccessToken.fulfilled.match(result)) {
          const newToken = result.payload.access_token;

          logger.info(
            {
              context: 'authInterceptor',
            },
            'Token refresh successful - retrying original request',
          );

          // Process queued requests with new token
          processQueue(null, newToken);

          // Retry original request with new token
          if (originalRequest) {
            originalRequest.headers['Authorization'] = `Bearer ${newToken}`;
          }
          isRefreshing = false;
          return axios(originalRequest);
        } else {
          // Refresh failed
          logger.error(
            {
              context: 'authInterceptor',
              metadata: { error: result.payload },
            },
            'Token refresh failed - logging out user',
          );

          // Process queued requests with error
          processQueue(result.payload, null);

          // Logout user
          dispatch(logout());

          // Set error for user feedback
          dispatch(
            setAuthError({
              code: ErrorCode.TOKEN_EXPIRED,
              message: 'Your session has expired. Please sign in again.',
              timestamp: new Date().toISOString(),
            }),
          );

          isRefreshing = false;
          return Promise.reject(error);
        }
      } catch (err) {
        logger.error(
          {
            context: 'authInterceptor',
          },
          'Unexpected error during token refresh',
          err instanceof Error ? err : new Error(String(err)),
        );

        // Process queued requests with error
        processQueue(err, null);

        // Logout user on unexpected errors
        dispatch(logout());

        dispatch(
          setAuthError({
            code: ErrorCode.TOKEN_EXPIRED,
            message: 'Your session has expired. Please sign in again.',
            timestamp: new Date().toISOString(),
          }),
        );

        isRefreshing = false;
        return Promise.reject(error);
      }
    },
  );
};
