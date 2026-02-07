/**
 * @fileoverview Google Contacts service layer for contact operations
 * @module Contacts/services/GoogleContactsService
 *
 * Updated for Story 2.1B: Now communicates with C# ASP.NET backend API
 * instead of calling Google People API directly from the browser.
 */

import { logger } from '../../../shared/logger';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import type { Contact, APIResponse } from '../types/Contact';
import { UpdateContactFieldParamsSchema } from './schemas/updateContactFieldSchema';

/**
 * Google Contacts Service
 * Service layer that wraps BackendApiClient and provides APIResponse<T> format
 * This is the layer that Redux thunks should call
 *
 * Note: No longer requires access token parameters - backend handles auth via cookies
 */
export class GoogleContactsService {
  /**
   * Fetches all contacts from backend API
   * @returns Promise resolving to APIResponse with Contact array
   */
  static async fetchAllContacts(): Promise<APIResponse<Contact[]>> {
    try {
      logger.info(
        {
          context: 'GoogleContactsService/fetchAllContacts',
        },
        'Fetching all contacts from backend (paginated)',
      );

      const allContacts: Contact[] = [];
      let nextPageToken: string | undefined;
      let pageCount = 0;

      // Loop through all pages until no more nextPageToken
      do {
        pageCount++;
        logger.debug(
          {
            context: 'GoogleContactsService/fetchAllContacts',
            metadata: {
              pageNumber: pageCount,
              hasPageToken: !!nextPageToken,
              totalSoFar: allContacts.length,
            },
          },
          `Fetching page ${pageCount}`,
        );

        const { contacts, nextPageToken: nextToken } =
          await backendApiClient.fetchContacts(1000, nextPageToken);

        allContacts.push(...contacts);
        nextPageToken = nextToken;

        logger.debug(
          {
            context: 'GoogleContactsService/fetchAllContacts',
            metadata: {
              pageNumber: pageCount,
              pageSize: contacts.length,
              totalSoFar: allContacts.length,
              hasMorePages: !!nextToken,
            },
          },
          `Page ${pageCount} fetched`,
        );
      } while (nextPageToken);

      logger.info(
        {
          context: 'GoogleContactsService/fetchAllContacts',
          metadata: {
            totalContacts: allContacts.length,
            totalPages: pageCount,
          },
        },
        'Successfully fetched all contacts',
      );

      return {
        success: true,
        data: allContacts,
      };
    } catch (error) {
      logger.error(
        {
          context: 'GoogleContactsService/fetchAllContacts',
          metadata: {
            errorMessage: error instanceof Error ? error.message : String(error),
            errorName: error instanceof Error ? error.name : 'Unknown',
          },
        },
        'Failed to fetch contacts',
        error instanceof Error ? error : new Error(String(error)),
      );

      return {
        success: false,
        error: {
          code: 'FETCH_CONTACTS_FAILED',
          message: 'Failed to fetch contacts from backend API',
          details: error,
        },
      };
    }
  }

  /**
   * Retrieves a single contact by resource name
   * @param resourceName - Contact resource name (e.g., "people/123")
   * @returns Promise resolving to APIResponse with Contact
   */
  static async getContact(
    resourceName: string,
  ): Promise<APIResponse<Contact>> {
    try {
      logger.info(
        {
          context: 'GoogleContactsService/getContact',
          metadata: { resourceName },
        },
        'Fetching contact from backend',
      );

      const contact = await backendApiClient.getContact(resourceName);

      logger.info(
        {
          context: 'GoogleContactsService/getContact',
          metadata: { resourceName },
        },
        'Successfully fetched contact',
      );

      return {
        success: true,
        data: contact,
      };
    } catch (error) {
      logger.error(
        {
          context: 'GoogleContactsService/getContact',
          metadata: {
            resourceName,
            errorMessage: error instanceof Error ? error.message : String(error),
          },
        },
        'Failed to fetch contact',
        error instanceof Error ? error : new Error(String(error)),
      );

      return {
        success: false,
        error: {
          code: 'GET_CONTACT_FAILED',
          message: `Failed to fetch contact: ${resourceName}`,
          details: error,
        },
      };
    }
  }

  /**
   * Updates a contact with new data
   * @param resourceName - Contact resource name (e.g., "people/123")
   * @param updates - Partial contact data to update
   * @returns Promise resolving to APIResponse with updated Contact
   */
  static async updateContact(
    resourceName: string,
    updates: Partial<Contact>,
  ): Promise<APIResponse<Contact>> {
    try {
      logger.info(
        {
          context: 'GoogleContactsService/updateContact',
          metadata: {
            resourceName,
            updateFields: Object.keys(updates),
          },
        },
        'Updating contact via backend',
      );

      const updatedContact = await backendApiClient.updateContact(
        resourceName,
        updates as Contact, // Backend expects full Contact object
      );

      logger.info(
        {
          context: 'GoogleContactsService/updateContact',
          metadata: { resourceName },
        },
        'Successfully updated contact',
      );

      return {
        success: true,
        data: updatedContact,
      };
    } catch (error) {
      logger.error(
        {
          context: 'GoogleContactsService/updateContact',
          metadata: {
            resourceName,
            errorMessage: error instanceof Error ? error.message : String(error),
          },
        },
        'Failed to update contact',
        error instanceof Error ? error : new Error(String(error)),
      );

      return {
        success: false,
        error: {
          code: 'UPDATE_CONTACT_FAILED',
          message: `Failed to update contact: ${resourceName}`,
          details: error,
        },
      };
    }
  }

  /**
   * Updates a specific field of a contact (field-level update for inline editing)
   * Story 3.1 - Service Layer for Google Contacts API Operations
   * @param resourceName - Contact resource name (e.g., "people/c123")
   * @param fieldPath - Field to update (e.g., "names", "phoneNumbers")
   * @param newValue - New value for the field
   * @returns Promise resolving to APIResponse with updated Contact
   */
  static async updateContactField(
    resourceName: string,
    fieldPath: string,
    newValue: unknown,
  ): Promise<APIResponse<Contact>> {
    // Validate inputs using Zod schema
    const validationResult = UpdateContactFieldParamsSchema.safeParse({
      resourceName,
      fieldPath,
      newValue,
    });

    if (!validationResult.success) {
      logger.warn(
        {
          context: 'GoogleContactsService/updateContactField',
          metadata: {
            resourceName,
            fieldPath,
            validationErrors: validationResult.error.errors,
          },
        },
        'Input validation failed',
      );

      return {
        success: false,
        error: {
          code: 'VALIDATION_ERROR',
          message: 'Invalid input parameters',
          details: validationResult.error,
        },
      };
    }

    try {
      logger.info(
        {
          context: 'GoogleContactsService/updateContactField',
          metadata: { resourceName, fieldPath },
        },
        'Updating contact field via backend',
      );

      const contact = await backendApiClient.updateContactField(
        resourceName,
        fieldPath,
        newValue,
      );

      logger.info(
        {
          context: 'GoogleContactsService/updateContactField',
          metadata: { resourceName, fieldPath },
        },
        'Successfully updated contact field',
      );

      return {
        success: true,
        data: contact,
      };
    } catch (error) {
      logger.error(
        {
          context: 'GoogleContactsService/updateContactField',
          metadata: {
            resourceName,
            fieldPath,
            errorMessage: error instanceof Error ? error.message : String(error),
          },
        },
        'Failed to update contact field',
        error instanceof Error ? error : new Error(String(error)),
      );

      return {
        success: false,
        error: {
          code: 'UPDATE_FIELD_FAILED',
          message: `Failed to update ${fieldPath}`,
          details: error,
        },
      };
    }
  }

  /**
   * Check authentication status with backend
   * @returns Promise resolving to APIResponse with auth status
   */
  static async checkAuthStatus(): Promise<APIResponse<{ isAuthenticated: boolean }>> {
    try {
      logger.info(
        {
          context: 'GoogleContactsService/checkAuthStatus',
        },
        'Checking authentication status',
      );

      const status = await backendApiClient.getAuthStatus();

      logger.info(
        {
          context: 'GoogleContactsService/checkAuthStatus',
          metadata: { isAuthenticated: status.isAuthenticated },
        },
        'Auth status retrieved',
      );

      return {
        success: true,
        data: { isAuthenticated: status.isAuthenticated },
      };
    } catch (error) {
      logger.error(
        {
          context: 'GoogleContactsService/checkAuthStatus',
          metadata: {
            errorMessage: error instanceof Error ? error.message : String(error),
          },
        },
        'Failed to check auth status',
        error instanceof Error ? error : new Error(String(error)),
      );

      return {
        success: false,
        error: {
          code: 'AUTH_STATUS_CHECK_FAILED',
          message: 'Failed to check authentication status',
          details: error,
        },
      };
    }
  }

  /**
   * Initiate OAuth login flow (redirects to backend)
   */
  static login(): void {
    logger.info(
      {
        context: 'GoogleContactsService/login',
      },
      'Initiating backend OAuth login',
    );

    backendApiClient.login();
  }

  /**
   * Logout from backend
   * @returns Promise resolving to APIResponse with logout result
   */
  static async logout(): Promise<APIResponse<void>> {
    try {
      logger.info(
        {
          context: 'GoogleContactsService/logout',
        },
        'Logging out',
      );

      await backendApiClient.logout();

      logger.info(
        {
          context: 'GoogleContactsService/logout',
        },
        'Logout successful',
      );

      return {
        success: true,
        data: undefined,
      };
    } catch (error) {
      logger.error(
        {
          context: 'GoogleContactsService/logout',
          metadata: {
            errorMessage: error instanceof Error ? error.message : String(error),
          },
        },
        'Failed to logout',
        error instanceof Error ? error : new Error(String(error)),
      );

      return {
        success: false,
        error: {
          code: 'LOGOUT_FAILED',
          message: 'Failed to logout',
          details: error,
        },
      };
    }
  }
}
