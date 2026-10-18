/**
 * @fileoverview Backend API Client
 * Handles all communication with the C# ASP.NET backend API
 * Replaces direct Google People API calls with backend endpoints
 * Story: 2.1B - Backend API Integration
 */

import type { Contact, ContactGroup, ContactPatch } from '../../entries/Contacts/types/Contact';
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
  /** HTTP status of the response (added by the client, not sent by the server) */
  status?: number;
}

/**
 * Error thrown for a failed backend call. The message ends with "[HTTP <status>]"
 * so status-based classification (retryable, 412 conflict) works on the text alone.
 */
export class ApiRequestError extends Error {
  /** HTTP status, 0 when the server could not be reached */
  readonly status: number;

  /** Backend error code, e.g. CONTACT_CONFLICT */
  readonly code: string;

  constructor(message: string, status: number, code: string) {
    super(status ? `${message} [HTTP ${status}]` : message);
    this.name = 'ApiRequestError';
    this.status = status;
    this.code = code;
  }
}

/**
 * Throws ApiRequestError when a response is not a success with data.
 * @param response - Backend response
 * @param fallback - Message when the server sent none
 * @returns The response data
 */
function unwrap<T>(response: ApiResponse<T>, fallback: string): T {
  if (!response.success || response.data === undefined || response.data === null) {
    throw new ApiRequestError(
      response.error?.message || fallback,
      response.status ?? 0,
      response.error?.code || 'UNKNOWN_ERROR',
    );
  }
  return response.data;
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
  private async request<T>(endpoint: string, options: RequestInit = {}): Promise<ApiResponse<T>> {
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
      'Making API request',
    );

    try {
      const response = await fetch(url, config);
      // A proxy error page (502/504) is HTML, not JSON; keep the status either way.
      const data = (await response.json().catch(() => ({
        success: false,
        error: {
          code: 'BAD_RESPONSE',
          message: `Unexpected response from server (${response.status})`,
          timestamp: new Date().toISOString(),
        },
      }))) as ApiResponse<T>;
      data.status = response.status;

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
          'API request failed',
        );

        // Return error response
        return data as ApiResponse<T>;
      }

      logger.debug(
        {
          context: 'BackendApiClient',
          metadata: { url, success: data.success },
        },
        'API request successful',
      );

      return data as ApiResponse<T>;
    } catch (error) {
      logger.error(
        {
          context: 'BackendApiClient',
          metadata: { url, error },
        },
        'Network error during API request',
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
    logger.info({ context: 'BackendApiClient' }, 'Redirecting to backend OAuth login');

    window.location.href = `${this.baseUrl}/auth/login`;
  }

  /**
   * Logout from backend
   */
  async logout(): Promise<void> {
    logger.info({ context: 'BackendApiClient' }, 'Logging out');

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
    logger.info({ context: 'BackendApiClient/getAuthStatus' }, 'Checking authentication status');

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
      'Auth status response received',
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
    logger.debug({ context: 'BackendApiClient' }, 'Refreshing access token');

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
    pageToken?: string,
  ): Promise<{ contacts: Contact[]; nextPageToken?: string }> {
    logger.info(
      {
        context: 'BackendApiClient',
        metadata: { pageSize, hasPageToken: !!pageToken },
      },
      'Fetching contacts',
    );

    const params = new URLSearchParams({
      pageSize: pageSize.toString(),
    });

    if (pageToken) {
      params.append('pageToken', pageToken);
    }

    const response = await this.request<ContactsResponse>(`/contacts?${params.toString()}`);
    const data = unwrap(response, 'Failed to fetch contacts');

    logger.info(
      {
        context: 'BackendApiClient',
        metadata: { count: data.contacts.length },
      },
      'Contacts fetched successfully',
    );

    return {
      contacts: data.contacts,
      nextPageToken: data.nextPageToken,
    };
  }

  /**
   * Fetch the user's contact groups (labels)
   */
  async fetchContactGroups(): Promise<ContactGroup[]> {
    const response = await this.request<ContactGroup[]>('/contacts/groups');
    return unwrap(response, 'Failed to fetch labels');
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
      'Fetching contact',
    );

    const response = await this.request<Contact>(`/contacts/${encodeURIComponent(resourceName)}`);
    return unwrap(response, 'Failed to fetch contact');
  }

  /**
   * Update a contact. Only the groups present in the patch are written; the
   * etag must be the one the contact was last read with.
   * @param resourceName - Contact resource name (e.g., "people/c123")
   * @param patch - Etag plus the changed field groups
   * @returns The full updated contact with its new etag
   */
  async updateContact(resourceName: string, patch: ContactPatch): Promise<Contact> {
    logger.info(
      {
        context: 'BackendApiClient',
        metadata: { resourceName, fields: Object.keys(patch).filter((k) => k !== 'etag') },
      },
      'Updating contact',
    );

    const response = await this.request<Contact>(`/contacts/${encodeURIComponent(resourceName)}`, {
      method: 'PUT',
      body: JSON.stringify(patch),
    });
    const contact = unwrap(response, 'Failed to update contact');

    logger.info(
      {
        context: 'BackendApiClient',
        metadata: { resourceName },
      },
      'Contact updated successfully',
    );

    return contact;
  }

  /**
   * Update one field group of a contact (inline editing)
   * Story 3.1 - Service Layer for Google Contacts API Operations
   * @param resourceName - Contact resource name (e.g., "people/c123")
   * @param fieldPath - Field group to replace (e.g., "names", "phoneNumbers")
   * @param newValue - New value for the whole group
   * @param etag - Etag the contact was last read with
   * @returns Promise resolving to updated Contact
   */
  async updateContactField(
    resourceName: string,
    fieldPath: string,
    newValue: unknown,
    etag?: string | null,
  ): Promise<Contact> {
    logger.info(
      {
        context: 'BackendApiClient/updateContactField',
        metadata: { resourceName, fieldPath },
      },
      'Updating contact field',
    );

    return this.updateContact(resourceName, { etag, [fieldPath]: newValue } as ContactPatch);
  }
}

// Export singleton instance
export const backendApiClient = new BackendApiClient();
