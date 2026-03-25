/**
 * @fileoverview Tests for GoogleContactsService
 * Story: 3.1 - Service Layer for Google Contacts API Operations
 * Updated for BackendApiClient architecture (2026-02-06)
 */

import { jest } from '@jest/globals';
import { GoogleContactsService } from '../GoogleContactsService';
import { backendApiClient } from '../../../../services/api/BackendApiClient';
import { logger } from '../../../../shared/logger';
import type { Contact } from '../../types/Contact';

describe('GoogleContactsService', () => {
  let updateContactFieldSpy: jest.SpiedFunction<typeof backendApiClient.updateContactField>;
  let loggerInfoSpy: jest.SpiedFunction<typeof logger.info>;
  let loggerWarnSpy: jest.SpiedFunction<typeof logger.warn>;
  let loggerErrorSpy: jest.SpiedFunction<typeof logger.error>;

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

  beforeEach(() => {
    // Mock backendApiClient.updateContactField
    updateContactFieldSpy = jest
      .spyOn(backendApiClient, 'updateContactField')
      .mockResolvedValue(mockContact);

    // Mock logger methods
    loggerInfoSpy = jest.spyOn(logger, 'info').mockImplementation(() => {});
    loggerWarnSpy = jest.spyOn(logger, 'warn').mockImplementation(() => {});
    loggerErrorSpy = jest.spyOn(logger, 'error').mockImplementation(() => {});
  });

  afterEach(() => {
    updateContactFieldSpy.mockRestore();
    loggerInfoSpy.mockRestore();
    loggerWarnSpy.mockRestore();
    loggerErrorSpy.mockRestore();
  });

  describe('updateContactField', () => {
    it('updateContactField_Should_UpdateField_When_Successful', async () => {
      const result = await GoogleContactsService.updateContactField(
        'people/c12345',
        'names',
        { givenName: 'Jane', familyName: 'Doe', displayName: 'Jane Doe' }
      );

      expect(result.success).toBe(true);
      expect(result.data).toEqual(mockContact);
      expect(updateContactFieldSpy).toHaveBeenCalledWith(
        'people/c12345',
        'names',
        { givenName: 'Jane', familyName: 'Doe', displayName: 'Jane Doe' }
      );
    });

    it('updateContactField_Should_ReturnValidationError_When_InvalidResourceName', async () => {
      const result = await GoogleContactsService.updateContactField(
        'invalid-format',
        'names',
        { givenName: 'Jane' }
      );

      expect(result.success).toBe(false);
      expect(result.error?.code).toBe('VALIDATION_ERROR');
      expect(result.error?.message).toBe('Invalid input parameters');
      expect(updateContactFieldSpy).not.toHaveBeenCalled();
    });

    it('updateContactField_Should_ReturnValidationError_When_InvalidFieldPath', async () => {
      const result = await GoogleContactsService.updateContactField(
        'people/c12345',
        'invalidField',
        { value: 'test' }
      );

      expect(result.success).toBe(false);
      expect(result.error?.code).toBe('VALIDATION_ERROR');
      expect(updateContactFieldSpy).not.toHaveBeenCalled();
    });

    it('updateContactField_Should_HandleBackendError_When_UpdateFails', async () => {
      updateContactFieldSpy.mockRejectedValueOnce(
        new Error('CONTACT_NOT_FOUND: Contact not found')
      );

      const result = await GoogleContactsService.updateContactField(
        'people/c12345',
        'names',
        { givenName: 'Jane' }
      );

      expect(result.success).toBe(false);
      expect(result.error?.code).toBe('UPDATE_FIELD_FAILED');
      expect(result.error?.message).toBe('Failed to update names');
    });

    it('updateContactField_Should_UpdatePhoneNumbers_When_Called', async () => {
      const phoneValue = [{ value: '555-1234', type: 'mobile' }];
      const updatedContact = {
        ...mockContact,
        phoneNumbers: phoneValue,
      };
      updateContactFieldSpy.mockResolvedValueOnce(updatedContact);

      const result = await GoogleContactsService.updateContactField(
        'people/c12345',
        'phoneNumbers',
        phoneValue
      );

      expect(result.success).toBe(true);
      expect(result.data?.phoneNumbers).toEqual(phoneValue);
    });

    it('updateContactField_Should_UpdateEmailAddresses_When_Called', async () => {
      const emailValue = [{ value: 'jane@example.com', type: 'work' }];
      const updatedContact = {
        ...mockContact,
        emailAddresses: emailValue,
      };
      updateContactFieldSpy.mockResolvedValueOnce(updatedContact);

      const result = await GoogleContactsService.updateContactField(
        'people/c12345',
        'emailAddresses',
        emailValue
      );

      expect(result.success).toBe(true);
      expect(result.data?.emailAddresses).toEqual(emailValue);
    });

    it('updateContactField_Should_LogValidationFailure_When_InvalidInput', async () => {
      await GoogleContactsService.updateContactField(
        'invalid-format',
        'names',
        {}
      );

      expect(loggerWarnSpy).toHaveBeenCalled();
      expect(loggerWarnSpy.mock.calls[0][0]).toMatchObject({
        context: 'GoogleContactsService/updateContactField',
      });
    });

    it('updateContactField_Should_LogOperationStartAndCompletion_When_Called', async () => {
      await GoogleContactsService.updateContactField(
        'people/c12345',
        'names',
        { givenName: 'Jane' }
      );

      expect(loggerInfoSpy).toHaveBeenCalledTimes(2);
      expect(loggerInfoSpy.mock.calls[0][0]).toMatchObject({
        context: 'GoogleContactsService/updateContactField',
        metadata: { resourceName: 'people/c12345', fieldPath: 'names' },
      });
      expect(loggerInfoSpy.mock.calls[0][1]).toBe('Updating contact field via backend');
      expect(loggerInfoSpy.mock.calls[1][1]).toBe('Successfully updated contact field');
    });

    it('updateContactField_Should_LogError_When_UpdateFails', async () => {
      const error = new Error('Backend error');
      updateContactFieldSpy.mockRejectedValueOnce(error);

      await GoogleContactsService.updateContactField(
        'people/c12345',
        'names',
        {}
      );

      expect(loggerErrorSpy).toHaveBeenCalled();
      expect(loggerErrorSpy.mock.calls[0][0]).toMatchObject({
        context: 'GoogleContactsService/updateContactField',
        metadata: {
          resourceName: 'people/c12345',
          fieldPath: 'names',
          errorMessage: 'Backend error',
        },
      });
      expect(loggerErrorSpy.mock.calls[0][1]).toBe('Failed to update contact field');
      expect(loggerErrorSpy.mock.calls[0][2]).toBe(error);
    });

    it('updateContactField_Should_ValidateAllAllowedFieldPaths_When_Called', async () => {
      const allowedFields = [
        'names',
        'phoneNumbers',
        'emailAddresses',
        'addresses',
        'organizations',
        'birthdays',
        'urls',
        'biographies',
        'userDefined',
        'relations',
      ];

      for (const fieldPath of allowedFields) {
        updateContactFieldSpy.mockClear();
        updateContactFieldSpy.mockResolvedValueOnce(mockContact);

        const result = await GoogleContactsService.updateContactField(
          'people/c12345',
          fieldPath,
          {}
        );

        expect(result.success).toBe(true);
        expect(updateContactFieldSpy).toHaveBeenCalledWith(
          'people/c12345',
          fieldPath,
          {}
        );
      }
    });

    it('updateContactField_Should_HandleNonErrorException_When_StringThrown', async () => {
      updateContactFieldSpy.mockRejectedValueOnce('String error');

      const result = await GoogleContactsService.updateContactField(
        'people/c12345',
        'names',
        {}
      );

      expect(result.success).toBe(false);
      expect(result.error?.code).toBe('UPDATE_FIELD_FAILED');
      expect(loggerErrorSpy).toHaveBeenCalled();
      expect(loggerErrorSpy.mock.calls[0][2]).toBeInstanceOf(Error);
    });
  });
});
