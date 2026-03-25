/**
 * @fileoverview Tests for BackendApiClient
 * Story: 3.1 - Service Layer for Google Contacts API Operations
 */

import { jest } from '@jest/globals';
import { BackendApiClient, backendApiClient } from '../BackendApiClient';
import type { Contact } from '../../../entries/Contacts/types/Contact';

// Mock global fetch
global.fetch = jest.fn() as jest.MockedFunction<typeof fetch>;

describe('BackendApiClient', () => {
  let client: BackendApiClient;

  beforeEach(() => {
    client = new BackendApiClient('http://localhost:5000/api');
    jest.clearAllMocks();
  });

  describe('updateContactField', () => {
    const mockContact: Contact = {
      resourceName: 'people/c12345',
      names: [
        {
          givenName: 'Jane',
          familyName: 'Doe',
          displayName: 'Jane Doe',
        },
      ],
    };

    it('should update contact field successfully', async () => {
      const mockResponse = {
        success: true,
        data: mockContact,
      };

      (global.fetch as jest.Mock).mockResolvedValueOnce({
        ok: true,
        json: async () => mockResponse,
      });

      const result = await client.updateContactField(
        'people/c12345',
        'names',
        { givenName: 'Jane', familyName: 'Doe', displayName: 'Jane Doe' }
      );

      expect(result).toEqual(mockContact);
      expect(global.fetch).toHaveBeenCalledWith(
        'http://localhost:5000/api/contacts/people%2Fc12345/fields',
        expect.objectContaining({
          method: 'PATCH',
          credentials: 'include',
          headers: expect.objectContaining({
            'Content-Type': 'application/json',
          }),
          body: JSON.stringify({
            fieldPath: 'names',
            newValue: { givenName: 'Jane', familyName: 'Doe', displayName: 'Jane Doe' },
          }),
        })
      );
    });

    it('should URL encode resource name', async () => {
      const mockResponse = {
        success: true,
        data: mockContact,
      };

      (global.fetch as jest.Mock).mockResolvedValueOnce({
        ok: true,
        json: async () => mockResponse,
      });

      await client.updateContactField(
        'people/c12345',
        'phoneNumbers',
        [{ value: '555-1234', type: 'mobile' }]
      );

      expect(global.fetch).toHaveBeenCalledWith(
        expect.stringContaining('people%2Fc12345'),
        expect.any(Object)
      );
    });

    it('should throw error when backend returns failure', async () => {
      const mockErrorResponse = {
        success: false,
        error: {
          code: 'CONTACT_NOT_FOUND',
          message: 'Contact not found',
          timestamp: '2026-02-06T10:00:00Z',
        },
      };

      (global.fetch as jest.Mock).mockResolvedValueOnce({
        ok: true,
        json: async () => mockErrorResponse,
      });

      await expect(
        client.updateContactField('people/c12345', 'names', {})
      ).rejects.toThrow('Contact not found');
    });

    it('should throw error when response data is missing', async () => {
      const mockResponse = {
        success: true,
        data: undefined,
      };

      (global.fetch as jest.Mock).mockResolvedValueOnce({
        ok: true,
        json: async () => mockResponse,
      });

      await expect(
        client.updateContactField('people/c12345', 'names', {})
      ).rejects.toThrow('Failed to update field');
    });
  });
});
