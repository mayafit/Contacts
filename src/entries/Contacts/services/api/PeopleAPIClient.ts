/**
 * @fileoverview Google People API client for contact operations
 * @module Contacts/services/api/PeopleAPIClient
 */

import { google, people_v1 } from 'googleapis';
import { logger } from '../../../../shared/logger';
import { GoogleAPIError } from '../../errors/GoogleAPIError';
import type { Contact } from '../../types/Contact';

/**
 * Client for Google People API v1 operations
 * Wraps googleapis library and provides typed methods for contact management
 */
export class PeopleAPIClient {
  private readonly peopleService: people_v1.People;
  private readonly accessToken: string;

  /**
   * Creates a new PeopleAPIClient instance
   * @param accessToken - OAuth 2.0 access token for authentication
   * @throws {GoogleAPIError} If access token is missing or invalid
   */
  constructor(accessToken: string) {
    if (!accessToken || typeof accessToken !== 'string' || accessToken.trim() === '') {
      throw new GoogleAPIError(
        'Access token is required and must be a non-empty string',
        'INVALID_ACCESS_TOKEN',
      );
    }

    this.accessToken = accessToken;

    // Configure googleapis client with OAuth token
    const auth = new google.auth.OAuth2();
    auth.setCredentials({ access_token: accessToken });

    this.peopleService = google.people({
      version: 'v1',
      auth,
    });

    logger.info(
      {
        context: 'PeopleAPIClient/constructor',
      },
      'People API client initialized',
    );
  }

  /**
   * Fetches all contacts from Google People API with pagination
   * @returns Promise resolving to array of Contact objects
   * @throws {GoogleAPIError} If API request fails
   */
  async fetchAllContacts(): Promise<Contact[]> {
    try {
      logger.info(
        {
          context: 'PeopleAPIClient/fetchAllContacts',
        },
        'Starting to fetch all contacts',
      );

      const allContacts: Contact[] = [];
      let pageToken: string | undefined;
      let pageCount = 0;

      do {
        pageCount++;
        const response = await this.peopleService.people.connections.list({
          resourceName: 'people/me',
          personFields: 'names,emailAddresses,phoneNumbers,addresses',
          pageSize: 1000,
          pageToken,
        });

        const contacts = response.data.connections || [];
        allContacts.push(...(contacts as Contact[]));

        pageToken = response.data.nextPageToken || undefined;

        logger.info(
          {
            context: 'PeopleAPIClient/fetchAllContacts',
            metadata: {
              pageNumber: pageCount,
              contactsInPage: contacts.length,
              totalSoFar: allContacts.length,
              hasMorePages: !!pageToken,
            },
          },
          'Fetched contacts page',
        );
      } while (pageToken);

      logger.info(
        {
          context: 'PeopleAPIClient/fetchAllContacts',
          metadata: {
            totalContacts: allContacts.length,
            totalPages: pageCount,
          },
        },
        'Successfully fetched all contacts',
      );

      return allContacts;
    } catch (error) {
      logger.error(
        {
          context: 'PeopleAPIClient/fetchAllContacts',
          metadata: {
            errorMessage: error instanceof Error ? error.message : String(error),
          },
        },
        'Failed to fetch contacts',
        error instanceof Error ? error : new Error(String(error)),
      );

      throw new GoogleAPIError(
        'Failed to fetch contacts from Google People API',
        'FETCH_CONTACTS_FAILED',
        error instanceof Error ? error : undefined,
        {
          operation: 'fetchAllContacts',
        },
      );
    }
  }

  /**
   * Retrieves a single contact by resource name
   * @param resourceName - Contact resource name (e.g., "people/123")
   * @returns Promise resolving to Contact object
   * @throws {GoogleAPIError} If contact not found or API request fails
   */
  async getContact(resourceName: string): Promise<Contact> {
    try {
      logger.info(
        {
          context: 'PeopleAPIClient/getContact',
          metadata: { resourceName },
        },
        'Fetching contact',
      );

      const response = await this.peopleService.people.get({
        resourceName,
        personFields: 'names,emailAddresses,phoneNumbers,addresses',
      });

      logger.info(
        {
          context: 'PeopleAPIClient/getContact',
          metadata: { resourceName },
        },
        'Successfully fetched contact',
      );

      return response.data as Contact;
    } catch (error) {
      logger.error(
        {
          context: 'PeopleAPIClient/getContact',
          metadata: {
            resourceName,
            errorMessage: error instanceof Error ? error.message : String(error),
          },
        },
        'Failed to fetch contact',
        error instanceof Error ? error : new Error(String(error)),
      );

      throw new GoogleAPIError(
        `Failed to fetch contact: ${resourceName}`,
        'GET_CONTACT_FAILED',
        error instanceof Error ? error : undefined,
        {
          operation: 'getContact',
          resourceName,
        },
      );
    }
  }

  /**
   * Updates a contact with new data
   * @param resourceName - Contact resource name (e.g., "people/123")
   * @param updates - Partial contact data to update
   * @returns Promise resolving to updated Contact object
   * @throws {GoogleAPIError} If update fails
   */
  async updateContact(resourceName: string, updates: Partial<Contact>): Promise<Contact> {
    try {
      logger.info(
        {
          context: 'PeopleAPIClient/updateContact',
          metadata: {
            resourceName,
            updateFields: Object.keys(updates),
          },
        },
        'Updating contact',
      );

      const response = await this.peopleService.people.updateContact({
        resourceName,
        updatePersonFields: 'names,emailAddresses,phoneNumbers,addresses',
        requestBody: updates as people_v1.Schema$Person,
      });

      logger.info(
        {
          context: 'PeopleAPIClient/updateContact',
          metadata: { resourceName },
        },
        'Successfully updated contact',
      );

      return response.data as Contact;
    } catch (error) {
      logger.error(
        {
          context: 'PeopleAPIClient/updateContact',
          metadata: {
            resourceName,
            errorMessage: error instanceof Error ? error.message : String(error),
          },
        },
        'Failed to update contact',
        error instanceof Error ? error : new Error(String(error)),
      );

      throw new GoogleAPIError(
        `Failed to update contact: ${resourceName}`,
        'UPDATE_CONTACT_FAILED',
        error instanceof Error ? error : undefined,
        {
          operation: 'updateContact',
          resourceName,
        },
      );
    }
  }
}
