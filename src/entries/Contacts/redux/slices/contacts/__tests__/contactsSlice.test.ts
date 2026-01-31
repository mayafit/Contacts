/**
 * @fileoverview ContactsSlice Redux slice tests
 * @module Contacts/redux/slices/contacts/__tests__/contactsSlice.test
 * @jest-environment node
 */

import { jest } from '@jest/globals';

// Mock GoogleContactsService BEFORE importing anything else
const mockFetchAllContacts = jest.fn();
const mockUpdateContact = jest.fn();

jest.mock('../../../../services/GoogleContactsService', () => ({
  GoogleContactsService: {
    fetchAllContacts: mockFetchAllContacts,
    updateContact: mockUpdateContact,
  },
}));

// Now import after mocks
import { configureStore } from '@reduxjs/toolkit';
import contactsReducer, {
  fetchContacts,
  updateContact,
  contactUpdated,
  clearContactsError,
  resetContacts,
} from '../contactsSlice';
import type { Contact } from '../../../../types/Contact';
import { ErrorCode } from '../../../../types';

describe('contactsSlice', () => {
  const mockContacts: Contact[] = [
    {
      resourceName: 'people/123',
      names: [{ givenName: 'John', familyName: 'Doe', displayName: 'John Doe' }],
      emailAddresses: [{ value: 'john@example.com', type: 'work' }],
      phoneNumbers: [{ value: '+1234567890', type: 'mobile' }],
    },
    {
      resourceName: 'people/456',
      names: [{ givenName: 'Jane', familyName: 'Smith', displayName: 'Jane Smith' }],
      emailAddresses: [{ value: 'jane@example.com', type: 'home' }],
    },
  ];

  beforeEach(() => {
    jest.clearAllMocks();
  });

  describe('fetchContacts', () => {
    it('fetchContacts_Should_SetLoadingTrue_When_Pending', () => {
      const initialState = contactsReducer(undefined, { type: '@@INIT' });
      const pendingState = contactsReducer(
        initialState,
        fetchContacts.pending('', undefined),
      );

      expect(pendingState.isLoading).toBe(true);
      expect(pendingState.error).toBeNull();
    });

    it('fetchContacts_Should_NormalizeContacts_When_Fulfilled', () => {
      const initialState = contactsReducer(undefined, { type: '@@INIT' });
      const fulfilledState = contactsReducer(
        initialState,
        fetchContacts.fulfilled(mockContacts, '', undefined),
      );

      expect(fulfilledState.isLoading).toBe(false);
      expect(fulfilledState.ids).toHaveLength(2);
      expect(fulfilledState.ids).toContain('people/123');
      expect(fulfilledState.ids).toContain('people/456');
      expect(fulfilledState.entities['people/123']).toEqual(mockContacts[0]);
      expect(fulfilledState.entities['people/456']).toEqual(mockContacts[1]);
      expect(fulfilledState.lastFetched).not.toBeNull();
    });

    it('fetchContacts_Should_SetError_When_Rejected', () => {
      const initialState = contactsReducer(undefined, { type: '@@INIT' });
      const error = {
        code: ErrorCode.FETCH_FAILED,
        message: 'Failed to fetch',
        timestamp: new Date().toISOString(),
      };

      const rejectedState = contactsReducer(
        initialState,
        fetchContacts.rejected(null, '', undefined, error),
      );

      expect(rejectedState.isLoading).toBe(false);
      expect(rejectedState.error).toEqual(error);
    });
  });

  describe('updateContact', () => {
    it('updateContact_Should_UpdateEntity_When_Fulfilled', () => {
      // Start with contacts already loaded
      const stateWithContacts = contactsReducer(
        undefined,
        fetchContacts.fulfilled(mockContacts, '', undefined),
      );

      const updatedContact: Contact = {
        ...mockContacts[0],
        emailAddresses: [{ value: 'newemail@example.com', type: 'work' }],
      };

      const updatedState = contactsReducer(
        stateWithContacts,
        updateContact.fulfilled(
          updatedContact,
          '',
          { resourceName: 'people/123', updates: {} },
        ),
      );

      expect(updatedState.entities['people/123']?.emailAddresses?.[0]?.value).toBe(
        'newemail@example.com',
      );
    });

    it('updateContact_Should_SetError_When_Rejected', () => {
      const initialState = contactsReducer(undefined, { type: '@@INIT' });
      const error = {
        code: ErrorCode.UPDATE_FAILED,
        message: 'Failed to update',
        timestamp: new Date().toISOString(),
      };

      const rejectedState = contactsReducer(
        initialState,
        updateContact.rejected(null, '', { resourceName: '', updates: {} }, error),
      );

      expect(rejectedState.error).toEqual(error);
    });
  });

  describe('contactUpdated reducer', () => {
    it('contactUpdated_Should_UpdateExistingContact_When_ContactExists', () => {
      const stateWithContacts = contactsReducer(
        undefined,
        fetchContacts.fulfilled(mockContacts, '', undefined),
      );

      const updatedContact: Contact = {
        ...mockContacts[0],
        phoneNumbers: [{ value: '+9999999999', type: 'work' }],
      };

      const newState = contactsReducer(
        stateWithContacts,
        contactUpdated(updatedContact),
      );

      expect(newState.entities['people/123']?.phoneNumbers?.[0]?.value).toBe(
        '+9999999999',
      );
    });
  });

  describe('clearContactsError reducer', () => {
    it('clearContactsError_Should_ClearError_When_ErrorExists', () => {
      const stateWithError = contactsReducer(
        undefined,
        fetchContacts.rejected(
          null,
          '',
          undefined,
          {
            code: ErrorCode.FETCH_FAILED,
            message: 'Error',
            timestamp: new Date().toISOString(),
          },
        ),
      );

      const clearedState = contactsReducer(stateWithError, clearContactsError());
      expect(clearedState.error).toBeNull();
    });
  });

  describe('resetContacts reducer', () => {
    it('resetContacts_Should_ClearAllContacts_When_Called', () => {
      const stateWithContacts = contactsReducer(
        undefined,
        fetchContacts.fulfilled(mockContacts, '', undefined),
      );

      const resetState = contactsReducer(stateWithContacts, resetContacts());

      expect(resetState.ids).toHaveLength(0);
      expect(resetState.entities).toEqual({});
      expect(resetState.lastFetched).toBeNull();
      expect(resetState.error).toBeNull();
    });
  });

});
