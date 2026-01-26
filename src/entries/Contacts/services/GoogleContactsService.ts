/**
 * @fileoverview Google Contacts service layer for contact operations
 * @module Contacts/services/GoogleContactsService
 */

import { logger } from '../../../shared/logger';
import { PeopleAPIClient } from './api/PeopleAPIClient';
import { GoogleAPIError } from '../errors/GoogleAPIError';
import type { Contact, APIResponse } from '../types/Contact';

/**
 * Google Contacts Service
 * Service layer that wraps PeopleAPIClient and provides APIResponse<T> format
 * This is the layer that Redux thunks should call
 */
export class GoogleContactsService {
  /**
   * Fetches all contacts from Google People API
   * @param accessToken - OAuth 2.0 access token
   * @returns Promise resolving to APIResponse with Contact array
   */
  static async fetchAllContacts(accessToken: string): Promise<APIResponse<Contact[]>> {
    try {
      logger.info(
        {
          context: 'GoogleContactsService/fetchAllContacts',
        },
        'Fetching all contacts',
      );

      const client = new PeopleAPIClient(accessToken);
      const contacts = await client.fetchAllContacts();

      logger.info(
        {
          context: 'GoogleContactsService/fetchAllContacts',
          metadata: {
            contactCount: contacts.length,
          },
        },
        'Successfully fetched contacts',
      );

      return {
        success: true,
        data: contacts,
      };
    } catch (error) {
      logger.error(
        {
          context: 'GoogleContactsService/fetchAllContacts',
          metadata: {
            errorMessage: error instanceof Error ? error.message : String(error),
            errorCode: error instanceof GoogleAPIError ? error.code : 'UNKNOWN_ERROR',
          },
        },
        'Failed to fetch contacts',
        error instanceof Error ? error : new Error(String(error)),
      );

      if (error instanceof GoogleAPIError) {
        return {
          success: false,
          error: {
            code: error.code,
            message: 'Failed to fetch contacts from Google People API',
            details: error,
          },
        };
      }

      return {
        success: false,
        error: {
          code: 'UNKNOWN_ERROR',
          message: 'An unexpected error occurred while fetching contacts',
          details: error,
        },
      };
    }
  }

  /**
   * Retrieves a single contact by resource name
   * @param accessToken - OAuth 2.0 access token
   * @param resourceName - Contact resource name (e.g., "people/123")
   * @returns Promise resolving to APIResponse with Contact
   */
  static async getContact(
    accessToken: string,
    resourceName: string,
  ): Promise<APIResponse<Contact>> {
    try {
      logger.info(
        {
          context: 'GoogleContactsService/getContact',
          metadata: { resourceName },
        },
        'Fetching contact',
      );

      const client = new PeopleAPIClient(accessToken);
      const contact = await client.getContact(resourceName);

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
            errorCode: error instanceof GoogleAPIError ? error.code : 'UNKNOWN_ERROR',
          },
        },
        'Failed to fetch contact',
        error instanceof Error ? error : new Error(String(error)),
      );

      if (error instanceof GoogleAPIError) {
        return {
          success: false,
          error: {
            code: error.code,
            message: `Failed to fetch contact: ${resourceName}`,
            details: error,
          },
        };
      }

      return {
        success: false,
        error: {
          code: 'UNKNOWN_ERROR',
          message: `An unexpected error occurred while fetching contact: ${resourceName}`,
          details: error,
        },
      };
    }
  }

  /**
   * Updates a contact with new data
   * @param accessToken - OAuth 2.0 access token
   * @param resourceName - Contact resource name (e.g., "people/123")
   * @param updates - Partial contact data to update
   * @returns Promise resolving to APIResponse with updated Contact
   */
  static async updateContact(
    accessToken: string,
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
        'Updating contact',
      );

      const client = new PeopleAPIClient(accessToken);
      const updatedContact = await client.updateContact(resourceName, updates);

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
            errorCode: error instanceof GoogleAPIError ? error.code : 'UNKNOWN_ERROR',
          },
        },
        'Failed to update contact',
        error instanceof Error ? error : new Error(String(error)),
      );

      if (error instanceof GoogleAPIError) {
        return {
          success: false,
          error: {
            code: error.code,
            message: `Failed to update contact: ${resourceName}`,
            details: error,
          },
        };
      }

      return {
        success: false,
        error: {
          code: 'UNKNOWN_ERROR',
          message: `An unexpected error occurred while updating contact: ${resourceName}`,
          details: error,
        },
      };
    }
  }
}
