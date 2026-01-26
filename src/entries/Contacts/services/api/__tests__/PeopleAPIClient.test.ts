/**
 * @fileoverview Google People API Client tests
 * @module Contacts/services/api/__tests__/PeopleAPIClient
 */

import { jest } from '@jest/globals';

// Create mock functions
const mockList = jest.fn();
const mockGet = jest.fn();
const mockUpdateContact = jest.fn();
const mockSetCredentials = jest.fn();

// Mock googleapis before importing anything
jest.mock('googleapis', () => ({
  google: {
    auth: {
      OAuth2: jest.fn().mockImplementation(() => ({
        setCredentials: mockSetCredentials,
      })),
    },
    people: jest.fn(() => ({
      people: {
        connections: {
          list: mockList,
        },
        get: mockGet,
        updateContact: mockUpdateContact,
      },
    })),
  },
}));

// Mock logger
jest.mock('../../../../../shared/logger', () => ({
  logger: {
    info: jest.fn(),
    error: jest.fn(),
    warn: jest.fn(),
    debug: jest.fn(),
  },
}));

// Now import after mocks are set up
import { PeopleAPIClient } from '../PeopleAPIClient';
import { GoogleAPIError } from '../../../errors/GoogleAPIError';
import { logger } from '../../../../../shared/logger';

describe('PeopleAPIClient', () => {
  const mockAccessToken = 'test-access-token-123';
  let client: PeopleAPIClient;

  beforeEach(() => {
    jest.clearAllMocks();
  });

  describe('constructor', () => {
    it('Constructor_Should_InitializeClient_When_ValidAccessToken', () => {
      client = new PeopleAPIClient(mockAccessToken);

      expect(client).toBeInstanceOf(PeopleAPIClient);
      expect(logger.info).toHaveBeenCalled();
    });

    it('Constructor_Should_ThrowError_When_EmptyAccessToken', () => {
      expect(() => new PeopleAPIClient('')).toThrow(GoogleAPIError);
    });

    it('Constructor_Should_ThrowError_When_NullAccessToken', () => {
      expect(() => new PeopleAPIClient(null as unknown as string)).toThrow(GoogleAPIError);
    });
  });

  describe('fetchAllContacts', () => {
    beforeEach(() => {
      client = new PeopleAPIClient(mockAccessToken);
    });

    it('fetchAllContacts_Should_ReturnContacts_When_SinglePage', async () => {
      const mockContacts = [
        {
          resourceName: 'people/123',
          names: [{ givenName: 'John', familyName: 'Doe', displayName: 'John Doe' }],
          emailAddresses: [{ value: 'john@example.com', type: 'work' }],
        },
      ];

      mockList.mockResolvedValue({
        data: {
          connections: mockContacts,
          nextPageToken: undefined,
        },
      });

      const result = await client.fetchAllContacts();

      expect(result).toEqual(mockContacts);
      expect(mockList).toHaveBeenCalledWith({
        resourceName: 'people/me',
        personFields: 'names,emailAddresses,phoneNumbers,addresses',
        pageSize: 1000,
      });
      expect(logger.info).toHaveBeenCalled();
    });

    it('fetchAllContacts_Should_HandlePagination_When_MultiplePages', async () => {
      const mockPage1 = [{ resourceName: 'people/1', names: [{ displayName: 'Contact 1' }] }];
      const mockPage2 = [{ resourceName: 'people/2', names: [{ displayName: 'Contact 2' }] }];

      mockList
        .mockResolvedValueOnce({
          data: {
            connections: mockPage1,
            nextPageToken: 'token-page-2',
          },
        })
        .mockResolvedValueOnce({
          data: {
            connections: mockPage2,
            nextPageToken: undefined,
          },
        });

      const result = await client.fetchAllContacts();

      expect(result).toHaveLength(2);
      expect(result).toEqual([...mockPage1, ...mockPage2]);
      expect(mockList).toHaveBeenCalledTimes(2);
      expect(mockList).toHaveBeenNthCalledWith(2, {
        resourceName: 'people/me',
        personFields: 'names,emailAddresses,phoneNumbers,addresses',
        pageSize: 1000,
        pageToken: 'token-page-2',
      });
    });

    it('fetchAllContacts_Should_ReturnEmptyArray_When_NoContacts', async () => {
      mockList.mockResolvedValue({
        data: {
          connections: undefined,
          nextPageToken: undefined,
        },
      });

      const result = await client.fetchAllContacts();

      expect(result).toEqual([]);
    });

    it('fetchAllContacts_Should_ThrowGoogleAPIError_When_APIFails', async () => {
      mockList.mockRejectedValue(new Error('API Error'));

      await expect(client.fetchAllContacts()).rejects.toThrow(GoogleAPIError);
      expect(logger.error).toHaveBeenCalled();
    });

    it('fetchAllContacts_Should_LogStructuredJSON_When_OperationStarts', async () => {
      mockList.mockResolvedValue({
        data: {
          connections: [],
          nextPageToken: undefined,
        },
      });

      await client.fetchAllContacts();

      expect(logger.info).toHaveBeenCalledWith(
        expect.objectContaining({
          context: expect.stringContaining('PeopleAPIClient'),
        }),
        expect.any(String),
      );
    });
  });

  describe('getContact', () => {
    beforeEach(() => {
      client = new PeopleAPIClient(mockAccessToken);
    });

    it('getContact_Should_ReturnContact_When_ValidResourceName', async () => {
      const mockContact = {
        resourceName: 'people/123',
        names: [{ displayName: 'John Doe' }],
      };

      mockGet.mockResolvedValue({
        data: mockContact,
      });

      const result = await client.getContact('people/123');

      expect(result).toEqual(mockContact);
      expect(mockGet).toHaveBeenCalledWith({
        resourceName: 'people/123',
        personFields: 'names,emailAddresses,phoneNumbers,addresses',
      });
    });

    it('getContact_Should_ThrowGoogleAPIError_When_ContactNotFound', async () => {
      mockGet.mockRejectedValue({ code: 404, message: 'Not found' });

      await expect(client.getContact('people/invalid')).rejects.toThrow(GoogleAPIError);
      expect(logger.error).toHaveBeenCalled();
    });
  });

  describe('updateContact', () => {
    beforeEach(() => {
      client = new PeopleAPIClient(mockAccessToken);
    });

    it('updateContact_Should_UpdateContact_When_ValidData', async () => {
      const resourceName = 'people/123';
      const updates = {
        names: [{ givenName: 'Jane', familyName: 'Doe' }],
      };
      const mockUpdatedContact = {
        resourceName,
        ...updates,
      };

      mockUpdateContact.mockResolvedValue({
        data: mockUpdatedContact,
      });

      const result = await client.updateContact(resourceName, updates);

      expect(result).toEqual(mockUpdatedContact);
      expect(mockUpdateContact).toHaveBeenCalledWith({
        resourceName,
        updatePersonFields: 'names,emailAddresses,phoneNumbers,addresses',
        requestBody: updates,
      });
      expect(logger.info).toHaveBeenCalled();
    });

    it('updateContact_Should_ThrowGoogleAPIError_When_UpdateFails', async () => {
      mockUpdateContact.mockRejectedValue(new Error('Update failed'));

      await expect(client.updateContact('people/123', { names: [] })).rejects.toThrow(
        GoogleAPIError,
      );
      expect(logger.error).toHaveBeenCalled();
    });
  });

  describe('error enrichment', () => {
    beforeEach(() => {
      client = new PeopleAPIClient(mockAccessToken);
    });

    it('Error_Should_IncludeContext_When_GoogleAPIErrorThrown', async () => {
      mockList.mockRejectedValue(new Error('API Error'));

      try {
        await client.fetchAllContacts();
        fail('Should have thrown GoogleAPIError');
      } catch (error) {
        expect(error).toBeInstanceOf(GoogleAPIError);
        expect((error as GoogleAPIError).message).toContain('fetch contacts');
      }
    });
  });
});
