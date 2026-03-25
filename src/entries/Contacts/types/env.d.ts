/**
 * @fileoverview Environment variable type definitions
 * @module Contacts/types/env
 */

/// <reference types="react-scripts" />

declare namespace NodeJS {
  interface ProcessEnv {
    /** Google OAuth Client ID from Google Cloud Console */
    REACT_APP_GOOGLE_CLIENT_ID: string;
    /** Google People API base URL */
    REACT_APP_API_BASE_URL: string;
    /** Node environment */
    NODE_ENV: 'development' | 'production' | 'test';
  }
}
