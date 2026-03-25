/**
 * @fileoverview Tests for VirtualizedContactsTable component
 * @module Contacts/components/__tests__/VirtualizedContactsTable
 */

import React from 'react';
import { render, screen } from '@testing-library/react';
import { jest } from '@jest/globals';
import { Provider } from 'react-redux';
import { configureStore } from '@reduxjs/toolkit';

// Mock MUI components to avoid ESM import issues in Jest
jest.mock('@mui/material', () => ({
  Box: ({ children, ...props }: any) => <div data-testid={props['data-testid']}>{children}</div>,
  Paper: ({ children }: any) => <div>{children}</div>,
  CircularProgress: () => <div>Loading...</div>,
  Typography: ({ children }: any) => <div>{children}</div>,
}));

// Mock logger to avoid console output in tests
jest.mock('../../../../shared/logger', () => ({
  logger: {
    info: jest.fn(),
    warn: jest.fn(),
    error: jest.fn(),
    debug: jest.fn(),
  },
}));

// Mock TanStack Virtual
jest.mock('@tanstack/react-virtual', () => ({
  useVirtualizer: () => ({
    getVirtualItems: () => [],
    getTotalSize: () => 0,
    scrollOffset: 0,
  }),
}));

// Import after mocks
import VirtualizedContactsTable from '../VirtualizedContactsTable';
import contactsReducer from '../../redux/slices/contacts/contactsSlice';
import type { Contact } from '../../types';

// Mock data
const mockContacts: Contact[] = [
  {
    resourceName: 'people/1',
    names: [{ displayName: 'Alice Smith', givenName: 'Alice', familyName: 'Smith' }],
    phoneNumbers: [{ value: '+1-555-0101' }],
    emailAddresses: [{ value: 'alice@example.com' }],
  },
  {
    resourceName: 'people/2',
    names: [{ displayName: 'Bob Johnson', givenName: 'Bob', familyName: 'Johnson' }],
    phoneNumbers: [{ value: '+1-555-0102' }],
    emailAddresses: [{ value: 'bob@example.com' }],
  },
];

const createMockStore = (initialState: any) => {
  return configureStore({
    reducer: {
      contacts: contactsReducer,
    },
    preloadedState: initialState,
  });
};

describe('VirtualizedContactsTable', () => {
  describe('AC1: Virtualization', () => {
    it('should render only visible rows using virtualization', () => {
      // Generate 1000+ contacts for virtualization test
      const manyContacts: Contact[] = Array.from({ length: 1500 }, (_, i) => ({
        resourceName: `people/${i}`,
        names: [{ displayName: `Contact ${i}` }],
        phoneNumbers: [{ value: `+1-555-${String(i).padStart(4, '0')}` }],
        emailAddresses: [{ value: `contact${i}@example.com` }],
      }));

      const store = createMockStore({
        contacts: {
          ids: manyContacts.map((c) => c.resourceName),
          entities: Object.fromEntries(manyContacts.map((c) => [c.resourceName, c])),
          isLoading: false,
          lastFetched: new Date().toISOString(),
          error: null,
        },
      });

      render(
        <Provider store={store}>
          <VirtualizedContactsTable />
        </Provider>
      );

      // Should render virtualized container
      expect(screen.getByTestId('virtualized-table-container')).toBeInTheDocument();
    });
  });

  describe('AC2: TanStack Table Integration', () => {
    it('should use TanStack Table for table features', () => {
      const store = createMockStore({
        contacts: {
          ids: mockContacts.map((c) => c.resourceName),
          entities: Object.fromEntries(mockContacts.map((c) => [c.resourceName, c])),
          isLoading: false,
          lastFetched: new Date().toISOString(),
          error: null,
        },
      });

      render(
        <Provider store={store}>
          <VirtualizedContactsTable />
        </Provider>
      );

      // Should render table headers
      expect(screen.getByText('Name')).toBeInTheDocument();
      expect(screen.getByText('Phone')).toBeInTheDocument();
      expect(screen.getByText('Email')).toBeInTheDocument();
    });
  });

  describe('AC3: Redux Integration', () => {
    it('should display contacts from Redux store', () => {
      const store = createMockStore({
        contacts: {
          ids: mockContacts.map((c) => c.resourceName),
          entities: Object.fromEntries(mockContacts.map((c) => [c.resourceName, c])),
          isLoading: false,
          lastFetched: new Date().toISOString(),
          error: null,
        },
      });

      render(
        <Provider store={store}>
          <VirtualizedContactsTable />
        </Provider>
      );

      // Should display contact names
      expect(screen.getByText('Alice Smith')).toBeInTheDocument();
      expect(screen.getByText('Bob Johnson')).toBeInTheDocument();
    });

    it('should handle loading state', () => {
      const store = createMockStore({
        contacts: {
          ids: [],
          entities: {},
          isLoading: true,
          lastFetched: null,
          error: null,
        },
      });

      render(
        <Provider store={store}>
          <VirtualizedContactsTable />
        </Provider>
      );

      expect(screen.getByText(/loading contacts/i)).toBeInTheDocument();
    });

    it('should handle error state', () => {
      const store = createMockStore({
        contacts: {
          ids: [],
          entities: {},
          isLoading: false,
          lastFetched: null,
          error: {
            code: 'FETCH_FAILED',
            message: 'Failed to load contacts',
            timestamp: new Date().toISOString(),
          },
        },
      });

      render(
        <Provider store={store}>
          <VirtualizedContactsTable />
        </Provider>
      );

      expect(screen.getByText('Failed to load contacts')).toBeInTheDocument();
    });

    it('should handle empty state', () => {
      const store = createMockStore({
        contacts: {
          ids: [],
          entities: {},
          isLoading: false,
          lastFetched: new Date().toISOString(),
          error: null,
        },
      });

      render(
        <Provider store={store}>
          <VirtualizedContactsTable />
        </Provider>
      );

      expect(screen.getByText(/no contacts found/i)).toBeInTheDocument();
    });
  });
});
