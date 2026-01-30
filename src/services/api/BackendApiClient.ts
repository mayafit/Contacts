/**
 * @fileoverview Backend API Client
 * Handles all communication with the C# ASP.NET backend API
 * Replaces direct Google People API calls with backend endpoints
 * Story: 2.1B - Backend API Integration
 */

import type { Contact } from '../../entries/Contacts/types/Contact';
import { logger } from '../../shared/logger';

const API_BASE_URL = process.env.REACT_APP_API_BASE_URL || 'http://localhost:5000/api';

/**
 * API response wrapper from backend
 */
interface ApiResponse<T> {
  success: boolean;
  data?: T;
  error?: {
    code: string;
    message: string;
    details?: unknown;
    timestamp: string;
  };
}

/**
 * Contacts list response
 */
interface ContactsResponse {
  contacts: Contact[];
  nextPageToken?: string;
  totalCount: number;
}

/**
 * Authentication status response
 */
interface AuthStatusResponse {
  isAuthenticated: boolean;
  user?: {
    id: string;
    email: string;
    name: string;
    picture?: string;
  };
}

/**
 * Backend API client for communicating with C# ASP.NET API
 */
export class BackendApiClient {
  private readonly baseUrl: string;

  constructor(baseUrl: string = API_BASE_URL) {
    this.baseUrl = baseUrl;
  }

  /**
   * Make authenticated API request to backend
   */
  private async request<T>(
    endpoint: string,
    options: RequestInit = {}
  ): Promise<ApiResponse<T>> {
    const url = `${this.baseUrl}${endpoint}`;

    const config: RequestInit = {
      ...options,
      credentials: 'include', // Send cookies with request
      headers: {
        'Content-Type': 'application/json',
        ...options.headers,
      },
    };

    logger.debug(
      {
        context: 'BackendApiClient',
        metadata: { method: config.method || 'GET', url },
      },
      'Making API request'
    );

    try {
      const response = await fetch(url, config);
      const data = await response.json();

      if (!response.ok) {
        logger.error(
          {
            context: 'BackendApiClient',
            metadata: {
              status: response.status,
              statusText: response.statusText,
              error: data.error,
            },
          },
          'API request failed'
        );

        // Return error response
        return data as ApiResponse<T>;
      }

      logger.debug(
        {
          context: 'BackendApiClient',
          metadata: { url, success: data.success },
        },
        'API request successful'
      );

      return data as ApiResponse<T>;
    } catch (error) {
      logger.error(
        {
          context: 'BackendApiClient',
          metadata: { url, error },
        },
        'Network error during API request'
      );

      throw error;
    }
  }

  // ===================================
  // Authentication Methods
  // ===================================

  /**
   * Redirect to backend OAuth login
   */
  login(): void {
    logger.info(
      { context: 'BackendApiClient' },
      'Redirecting to backend OAuth login'
    );

    window.location.href = `${this.baseUrl}/auth/login`;
  }

  /**
   * Logout from backend
   */
  async logout(): Promise<void> {
    logger.info(
      { context: 'BackendApiClient' },
      'Logging out'
    );

    const response = await this.request('/auth/logout', {
      method: 'POST',
    });

    if (!response.success) {
      throw new Error(response.error?.message || 'Logout failed');
    }
  }

  /**
   * Check authentication status
   */
  async getAuthStatus(): Promise<AuthStatusResponse> {
    logger.info(
      { context: 'BackendApiClient/getAuthStatus' },
      'Checking authentication status'
    );

    const response = await this.request<AuthStatusResponse>('/auth/status');

    logger.info(
      {
        context: 'BackendApiClient/getAuthStatus',
        metadata: {
          success: response.success,
          isAuthenticated: response.data?.isAuthenticated,
          hasUser: !!response.data?.user,
        },
      },
      'Auth status response received'
    );

    if (!response.success || !response.data) {
      throw new Error(response.error?.message || 'Failed to get auth status');
    }

    return response.data;
  }

  /**
   * Refresh access token
   */
  async refreshToken(): Promise<void> {
    logger.debug(
      { context: 'BackendApiClient' },
      'Refreshing access token'
    );

    const response = await this.request('/auth/refresh', {
      method: 'POST',
    });

    if (!response.success) {
      throw new Error(response.error?.message || 'Token refresh failed');
    }
  }

  // ===================================
  // Contacts Methods
  // ===================================

  /**
   * Fetch all contacts with pagination
   */
  async fetchContacts(
    pageSize: number = 1000,
    pageToken?: string
  ): Promise<{ contacts: Contact[]; nextPageToken?: string }> {
    logger.info(
      {
        context: 'BackendApiClient',
        metadata: { pageSize, hasPageToken: !!pageToken },
      },
      'Fetching contacts'
    );

    const params = new URLSearchParams({
      pageSize: pageSize.toString(),
    });

    if (pageToken) {
      params.append('pageToken', pageToken);
    }

    const response = await this.request<ContactsResponse>(
      `/contacts?${params.toString()}`
    );

    if (!response.success || !response.data) {
      logger.error(
        {
          context: 'BackendApiClient',
          metadata: { error: response.error },
        },
        'Failed to fetch contacts'
      );

      throw new Error(response.error?.message || 'Failed to fetch contacts');
    }

    logger.info(
      {
        context: 'BackendApiClient',
        metadata: { count: response.data.contacts.length },
      },
      'Contacts fetched successfully'
    );

    return {
      contacts: response.data.contacts,
      nextPageToken: response.data.nextPageToken,
    };
  }

  /**
   * Get a single contact by resource name
   */
  async getContact(resourceName: string): Promise<Contact> {
    logger.info(
      {
        context: 'BackendApiClient',
        metadata: { resourceName },
      },
      'Fetching contact'
    );

    // URL encode the resource name
    const encodedResourceName = encodeURIComponent(resourceName);

    const response = await this.request<Contact>(
      `/contacts/${encodedResourceName}`
    );

    if (!response.success || !response.data) {
      throw new Error(response.error?.message || 'Failed to fetch contact');
    }

    return response.data;
  }

  /**
   * Update a contact
   */
  async updateContact(resourceName: string, updates: Contact): Promise<Contact> {
    logger.info(
      {
        context: 'BackendApiClient',
        metadata: { resourceName },
      },
      'Updating contact'
    );

    // URL encode the resource name
    const encodedResourceName = encodeURIComponent(resourceName);

    const response = await this.request<Contact>(
      `/contacts/${encodedResourceName}`,
      {
        method: 'PUT',
        body: JSON.stringify(updates),
      }
    );

    if (!response.success || !response.data) {
      throw new Error(response.error?.message || 'Failed to update contact');
    }

    logger.info(
      {
        context: 'BackendApiClient',
        metadata: { resourceName },
      },
      'Contact updated successfully'
    );

    return response.data;
  }
}

// Export singleton instance
export const backendApiClient = new BackendApiClient();
