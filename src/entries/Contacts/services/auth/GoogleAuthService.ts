/**
 * @fileoverview Google OAuth authentication service for token management
 * @module Contacts/services/auth/GoogleAuthService
 */

import axios from 'axios';
import { logger } from '../../../../shared/logger';

/**
 * API Response wrapper type for service methods
 */
interface APIResponse<T> {
  success: boolean;
  data?: T;
  error?: {
    code: string;
    message: string;
    details?: unknown;
  };
}

/**
 * Token refresh response from Google OAuth endpoint
 */
interface TokenRefreshResponse {
  access_token: string;
  expires_in: number;
  scope: string;
  token_type: string;
}

/**
 * Google Authentication Service
 * Handles OAuth token refresh and other auth-related API calls
 */
export class GoogleAuthService {
  private static readonly TOKEN_ENDPOINT = 'https://oauth2.googleapis.com/token';

  /**
   * Refresh access token using refresh token
   * @returns APIResponse with new access token or error
   */
  static async refreshAccessToken(): Promise<
    APIResponse<{ access_token: string; expires_in: number }>
  > {
    try {
      const refreshToken = sessionStorage.getItem('refresh_token');
      const clientId = process.env.REACT_APP_GOOGLE_CLIENT_ID;

      if (!refreshToken) {
        logger.error(
          {
            context: 'GoogleAuthService/refreshAccessToken',
          },
          'No refresh token found in sessionStorage',
        );
        return {
          success: false,
          error: {
            code: 'NO_REFRESH_TOKEN',
            message: 'No refresh token available',
          },
        };
      }

      if (!clientId) {
        logger.error(
          {
            context: 'GoogleAuthService/refreshAccessToken',
          },
          'REACT_APP_GOOGLE_CLIENT_ID not configured',
        );
        return {
          success: false,
          error: {
            code: 'MISSING_CLIENT_ID',
            message: 'OAuth client ID not configured',
          },
        };
      }

      logger.info(
        {
          context: 'GoogleAuthService/refreshAccessToken',
        },
        'Attempting to refresh access token',
      );

      // POST request with application/x-www-form-urlencoded content type
      const response = await axios.post<TokenRefreshResponse>(
        this.TOKEN_ENDPOINT,
        new URLSearchParams({
          client_id: clientId,
          grant_type: 'refresh_token',
          refresh_token: refreshToken,
        }),
        {
          headers: {
            'Content-Type': 'application/x-www-form-urlencoded',
          },
        },
      );

      logger.info(
        {
          context: 'GoogleAuthService/refreshAccessToken',
          metadata: {
            expires_in: response.data.expires_in,
          },
        },
        'Access token refreshed successfully',
      );

      return {
        success: true,
        data: {
          access_token: response.data.access_token,
          expires_in: response.data.expires_in,
        },
      };
    } catch (error) {
      if (axios.isAxiosError(error)) {
        const errorCode = error.response?.data?.error || 'TOKEN_REFRESH_FAILED';
        const errorDescription =
          error.response?.data?.error_description || 'Failed to refresh token';

        logger.error(
          {
            context: 'GoogleAuthService/refreshAccessToken',
            metadata: {
              errorCode,
              errorDescription,
              status: error.response?.status,
            },
          },
          'Token refresh failed',
          error,
        );

        return {
          success: false,
          error: {
            code: errorCode,
            message: errorDescription,
            details: error.response?.data,
          },
        };
      }

      logger.error(
        {
          context: 'GoogleAuthService/refreshAccessToken',
        },
        'Unexpected error during token refresh',
        error instanceof Error ? error : new Error(String(error)),
      );

      return {
        success: false,
        error: {
          code: 'UNKNOWN_ERROR',
          message: 'An unexpected error occurred during token refresh',
          details: error,
        },
      };
    }
  }
}
