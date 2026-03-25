/**
 * @fileoverview Tests for column configuration localStorage utility
 * @module utils/storage/__tests__/columnConfigStorage
 */

import { jest } from '@jest/globals';
import { saveColumnConfig, loadColumnConfig, STORAGE_KEY } from '../columnConfigStorage';
import { logger } from '../../../../../shared/logger';
import type { ColumnConfig } from '../../../features/columnConfig/types';

describe('columnConfigStorage', () => {
  const validConfig: ColumnConfig = {
    visibleColumns: ['displayName', 'phoneNumbers', 'emailAddresses'],
    columnOrder: ['displayName', 'phoneNumbers', 'emailAddresses'],
  };

  // Spy on logger methods
  let warnSpy: jest.SpiedFunction<typeof logger.warn>;
  let infoSpy: jest.SpiedFunction<typeof logger.info>;
  let errorSpy: jest.SpiedFunction<typeof logger.error>;

  beforeEach(() => {
    // Clear localStorage before each test
    localStorage.clear();

    // Set up spies on logger methods
    warnSpy = jest.spyOn(logger, 'warn').mockImplementation(() => {});
    infoSpy = jest.spyOn(logger, 'info').mockImplementation(() => {});
    errorSpy = jest.spyOn(logger, 'error').mockImplementation(() => {});
  });

  afterEach(() => {
    // Restore original logger methods
    warnSpy.mockRestore();
    infoSpy.mockRestore();
    errorSpy.mockRestore();
  });

  describe('saveColumnConfig', () => {
    it('should save column config to localStorage correctly', () => {
      saveColumnConfig(validConfig);

      const stored = localStorage.getItem(STORAGE_KEY);
      expect(stored).not.toBeNull();
      expect(stored).toBe(JSON.stringify(validConfig));
    });

    it('should overwrite existing config when called multiple times', () => {
      const config1: ColumnConfig = {
        visibleColumns: ['displayName'],
        columnOrder: ['displayName'],
      };
      const config2: ColumnConfig = {
        visibleColumns: ['displayName', 'phoneNumbers'],
        columnOrder: ['displayName', 'phoneNumbers'],
      };

      saveColumnConfig(config1);
      saveColumnConfig(config2);

      const stored = localStorage.getItem(STORAGE_KEY);
      expect(stored).toBe(JSON.stringify(config2));
    });

    it('should log warning on localStorage quota exceeded error', () => {
      // Mock setItem to throw QuotaExceededError
      const setItemSpy = jest.spyOn(Storage.prototype, 'setItem');
      setItemSpy.mockImplementation(() => {
        const error = new Error('QuotaExceededError');
        error.name = 'QuotaExceededError';
        throw error;
      });

      saveColumnConfig(validConfig);

      expect(warnSpy).toHaveBeenCalledWith(
        expect.objectContaining({
          context: 'columnConfigStorage/save',
          error: expect.any(Error),
        }),
        'Failed to save column config to localStorage'
      );

      setItemSpy.mockRestore();
    });

    it('should handle localStorage disabled (private browsing mode)', () => {
      // Mock setItem to throw SecurityError
      const setItemSpy = jest.spyOn(Storage.prototype, 'setItem');
      setItemSpy.mockImplementation(() => {
        const error = new Error('SecurityError');
        error.name = 'SecurityError';
        throw error;
      });

      saveColumnConfig(validConfig);

      expect(warnSpy).toHaveBeenCalledWith(
        expect.objectContaining({
          context: 'columnConfigStorage/save',
        }),
        'Failed to save column config to localStorage'
      );

      setItemSpy.mockRestore();
    });
  });

  describe('loadColumnConfig', () => {
    it('should load column config from localStorage correctly', () => {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(validConfig));

      const result = loadColumnConfig();

      expect(result).toEqual(validConfig);
      expect(warnSpy).not.toHaveBeenCalled();
    });

    it('should return null when localStorage key does not exist', () => {
      const result = loadColumnConfig();

      expect(result).toBeNull();
      expect(warnSpy).not.toHaveBeenCalled();
    });

    it('should return null and log warning for invalid JSON', () => {
      localStorage.setItem(STORAGE_KEY, '{invalid json');

      const result = loadColumnConfig();

      expect(result).toBeNull();
      expect(warnSpy).toHaveBeenCalledWith(
        expect.objectContaining({
          context: 'columnConfigStorage/load',
          error: expect.any(SyntaxError),
        }),
        'Failed to load column config from localStorage, using defaults'
      );
    });

    it('should return null and log warning for invalid column IDs in visibleColumns', () => {
      const invalidConfig = {
        visibleColumns: ['displayName', 'invalidColumnId'],
        columnOrder: ['displayName'],
      };
      localStorage.setItem(STORAGE_KEY, JSON.stringify(invalidConfig));

      const result = loadColumnConfig();

      expect(result).toBeNull();
      expect(warnSpy).toHaveBeenCalledWith(
        expect.objectContaining({
          context: 'columnConfigStorage/load',
        }),
        'Failed to load column config from localStorage, using defaults'
      );
    });

    it('should return null and log warning for invalid column IDs in columnOrder', () => {
      const invalidConfig = {
        visibleColumns: ['displayName'],
        columnOrder: ['displayName', 'nonexistentColumn'],
      };
      localStorage.setItem(STORAGE_KEY, JSON.stringify(invalidConfig));

      const result = loadColumnConfig();

      expect(result).toBeNull();
      expect(warnSpy).toHaveBeenCalledWith(
        expect.objectContaining({
          context: 'columnConfigStorage/load',
        }),
        'Failed to load column config from localStorage, using defaults'
      );
    });

    it('should return null and log warning for missing visibleColumns property', () => {
      const invalidConfig = {
        columnOrder: ['displayName'],
      };
      localStorage.setItem(STORAGE_KEY, JSON.stringify(invalidConfig));

      const result = loadColumnConfig();

      expect(result).toBeNull();
      expect(warnSpy).toHaveBeenCalled();
    });

    it('should return null and log warning for missing columnOrder property', () => {
      const invalidConfig = {
        visibleColumns: ['displayName'],
      };
      localStorage.setItem(STORAGE_KEY, JSON.stringify(invalidConfig));

      const result = loadColumnConfig();

      expect(result).toBeNull();
      expect(warnSpy).toHaveBeenCalled();
    });

    it('should validate that column IDs match AVAILABLE_COLUMNS', () => {
      // Valid column IDs from columnDefinitions
      const validWithAllColumns: ColumnConfig = {
        visibleColumns: ['displayName', 'phoneNumbers', 'emailAddresses', 'organizations', 'jobTitle'],
        columnOrder: ['displayName', 'phoneNumbers', 'emailAddresses', 'organizations', 'jobTitle'],
      };
      localStorage.setItem(STORAGE_KEY, JSON.stringify(validWithAllColumns));

      const result = loadColumnConfig();

      expect(result).toEqual(validWithAllColumns);
      expect(warnSpy).not.toHaveBeenCalled();
    });
  });

  describe('integration scenarios', () => {
    it('should save and load config successfully (round trip)', () => {
      saveColumnConfig(validConfig);
      const loaded = loadColumnConfig();

      expect(loaded).toEqual(validConfig);
    });

    it('should handle empty localStorage', () => {
      const result = loadColumnConfig();

      expect(result).toBeNull();
    });

    it('should handle corrupted data gracefully', () => {
      localStorage.setItem(STORAGE_KEY, 'not valid json {');

      const result = loadColumnConfig();

      expect(result).toBeNull();
      expect(warnSpy).toHaveBeenCalled();
    });

    it('should handle manually edited localStorage with extra properties', () => {
      const configWithExtra = {
        visibleColumns: ['displayName'],
        columnOrder: ['displayName'],
        extraProperty: 'should be ignored',
      };
      localStorage.setItem(STORAGE_KEY, JSON.stringify(configWithExtra));

      const result = loadColumnConfig();

      // Zod will strip extra properties and return valid config
      expect(result).toEqual({
        visibleColumns: ['displayName'],
        columnOrder: ['displayName'],
      });
    });
  });
});
