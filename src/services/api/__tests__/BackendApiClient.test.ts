/**
 * @fileoverview Tests for BackendApiClient
 * Story: 3.1 - Service Layer for Google Contacts API Operations
 */

import { jest } from '@jest/globals';
import { ApiRequestError, BackendApiClient, backendApiClient } from '../BackendApiClient';
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
        [{ givenName: 'Jane', familyName: 'Doe' }],
        'etag-1',
      );

      expect(result).toEqual(mockContact);
      // One group plus the etag, through the real PUT endpoint
      expect(global.fetch).toHaveBeenCalledWith(
        'http://localhost:5000/api/contacts/people%2Fc12345',
        expect.objectContaining({
          method: 'PUT',
          credentials: 'include',
          headers: expect.objectContaining({
            'Content-Type': 'application/json',
          }),
          body: JSON.stringify({
            etag: 'etag-1',
            names: [{ givenName: 'Jane', familyName: 'Doe' }],
          }),
        }),
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

      await client.updateContactField('people/c12345', 'phoneNumbers', [
        { value: '555-1234', type: 'mobile' },
      ]);

      expect(global.fetch).toHaveBeenCalledWith(
        expect.stringContaining('people%2Fc12345'),
        expect.any(Object),
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

      await expect(client.updateContactField('people/c12345', 'names', {})).rejects.toThrow(
        'Contact not found',
      );
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

      await expect(client.updateContactField('people/c12345', 'names', {})).rejects.toThrow(
        'Failed to update contact',
      );
    });

    it('should put the HTTP status in the error so conflicts can be detected', async () => {
      (global.fetch as jest.Mock).mockResolvedValueOnce({
        ok: false,
        status: 412,
        json: async () => ({
          success: false,
          error: { code: 'CONTACT_CONFLICT', message: 'Changed elsewhere', timestamp: '' },
        }),
      });

      const error = await client
        .updateContactField('people/c12345', 'names', [], 'stale')
        .catch((e: unknown) => e);

      expect(error).toBeInstanceOf(ApiRequestError);
      expect((error as ApiRequestError).status).toBe(412);
      expect((error as ApiRequestError).code).toBe('CONTACT_CONFLICT');
      expect((error as Error).message).toBe('Changed elsewhere [HTTP 412]');
    });

    it('should keep the status when the server answers with a non-JSON error page', async () => {
      (global.fetch as jest.Mock).mockResolvedValueOnce({
        ok: false,
        status: 502,
        json: async () => {
          throw new SyntaxError('Unexpected token <');
        },
      });

      await expect(client.updateContactField('people/c12345', 'names', [], 'e')).rejects.toThrow(
        'Unexpected response from server (502) [HTTP 502]',
      );
    });
  });

  describe('deleteContacts', () => {
    it('should send at most 500 per request and sum the results', async () => {
      const names = Array.from({ length: 501 }, (_, i) => `people/c${i}`);
      (global.fetch as jest.Mock)
        .mockResolvedValueOnce({
          ok: true,
          status: 200,
          json: async () => ({ success: true, data: { deleted: 500 } }),
        })
        .mockResolvedValueOnce({
          ok: true,
          status: 200,
          json: async () => ({ success: true, data: { deleted: 1 } }),
        });

      await expect(client.deleteContacts(names)).resolves.toBe(501);
      const calls = (global.fetch as jest.Mock).mock.calls;
      expect(calls[0][0]).toBe('http://localhost:5000/api/contacts/batch-delete');
      expect(JSON.parse(calls[0][1].body).resourceNames).toHaveLength(500);
      expect(JSON.parse(calls[1][1].body).resourceNames).toEqual(['people/c500']);
    });
  });

  describe('mergeContacts', () => {
    it('should surface what a merge would lose (409 with details)', async () => {
      (global.fetch as jest.Mock).mockResolvedValueOnce({
        ok: false,
        status: 409,
        json: async () => ({
          success: false,
          error: {
            code: 'MERGE_WOULD_LOSE_DATA',
            message: 'Some contacts to delete hold data this app cannot merge',
            details: { 'people/c2': ['photo', 'events'] },
            timestamp: '',
          },
        }),
      });

      const error = await client
        .mergeContacts({
          primaryResourceName: 'people/c1',
          patch: null,
          deleteResourceNames: ['people/c2'],
          addLabels: [],
          acknowledgeDataLoss: false,
        })
        .catch((e: unknown) => e);

      expect(error).toBeInstanceOf(ApiRequestError);
      expect((error as ApiRequestError).status).toBe(409);
      expect((error as ApiRequestError).details).toEqual({ 'people/c2': ['photo', 'events'] });
    });
  });

  describe('fetchContactGroups', () => {
    it('should return the labels', async () => {
      const groups = [{ resourceName: 'contactGroups/a', name: 'Family' }];
      (global.fetch as jest.Mock).mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({ success: true, data: groups }),
      });

      await expect(client.fetchContactGroups()).resolves.toEqual(groups);
      expect(global.fetch).toHaveBeenCalledWith(
        'http://localhost:5000/api/contacts/groups',
        expect.any(Object),
      );
    });
  });
});
