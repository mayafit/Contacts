/**
 * @fileoverview ContactsTable component tests
 * @module Contacts/components/__tests__/ContactsTable.test
 */

import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { jest } from '@jest/globals';
import { Provider } from 'react-redux';
import { configureStore } from '@reduxjs/toolkit';
import userEvent from '@testing-library/user-event';

// Mock MUI components to avoid ESM import issues in Jest
jest.mock('@mui/material', () => ({
  Table: ({ children }: { children: React.ReactNode }) => <table>{children}</table>,
  TableBody: ({ children }: { children: React.ReactNode }) => <tbody>{children}</tbody>,
  TableCell: ({ children }: { children: React.ReactNode }) => <td>{children}</td>,
  TableContainer: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  TableHead: ({ children }: { children: React.ReactNode }) => <thead>{children}</thead>,
  TableRow: ({ children }: { children: React.ReactNode }) => <tr>{children}</tr>,
  Paper: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  CircularProgress: () => <div>Loading...</div>,
  Box: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  Typography: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  Button: ({
    children,
    onClick,
  }: {
    children: React.ReactNode;
    onClick?: () => void;
  }) => <button onClick={onClick}>{children}</button>,
}));

jest.mock('@mui/icons-material', () => ({
  Refresh: () => <span>Refresh Icon</span>,
}));

// Mock logger
jest.mock('../../../../shared/logger', () => ({
  logger: {
    info: jest.fn(),
    error: jest.fn(),
    warn: jest.fn(),
    debug: jest.fn(),
  },
}));

// Mock GoogleContactsService
const mockFetchAllContacts = jest.fn();
jest.mock('../../services/GoogleContactsService', () => ({
  GoogleContactsService: {
    fetchAllContacts: mockFetchAllContacts,
  },
}));

// Import after mocks
import ContactsTable from '../ContactsTable';
import { logger } from '../../../../shared/logger';
import type { Contact } from '../../types/Contact';
import authReducer from '../../redux/slices/auth/authSlice';
import contactsReducer from '../../redux/slices/contacts/contactsSlice';
import { combineReducers } from '@reduxjs/toolkit';

// Mock Redux store with actual reducers
const createMockStore = (accessToken: string | null = 'test-token-123') =>
  configureStore({
    reducer: {
      contacts: combineReducers({
        auth: authReducer,
        contacts: contactsReducer,
      }),
    },
    preloadedState: {
      contacts: {
        auth: {
          accessToken,
          isAuthenticated: !!accessToken,
          user: accessToken
            ? { id: '1', email: 'test@example.com', name: 'Test User' }
            : null,
          isLoading: false,
          error: null,
          tokenExpiry: null,
        },
        contacts: {
          entities: {},
          ids: [],
          isLoading: false,
          lastFetched: null,
          error: null,
        },
      },
    },
  });

describe('ContactsTable', () => {
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
      phoneNumbers: [{ value: '+0987654321', type: 'work' }],
    },
  ];

  beforeEach(() => {
    jest.clearAllMocks();
  });

  describe('fetchAllContacts', () => {
    it('fetchAllContacts_Should_DisplayContacts_When_FetchSuccessful', async () => {
      mockFetchAllContacts.mockResolvedValue({
        success: true,
        data: mockContacts,
      });

      const store = createMockStore();

      render(
        <Provider store={store}>
          <ContactsTable />
        </Provider>,
      );

      // Should show loading state initially
      expect(screen.getByText(/loading/i)).toBeInTheDocument();

      // Wait for contacts to load
      await waitFor(() => {
        expect(screen.getByText('John Doe')).toBeInTheDocument();
      });

      // Verify all contacts are displayed
      expect(screen.getByText('John Doe')).toBeInTheDocument();
      expect(screen.getByText('jane@example.com')).toBeInTheDocument();
      expect(screen.getByText('+1234567890')).toBeInTheDocument();

      // Verify structured logging
      expect(logger.info).toHaveBeenCalledWith(
        expect.objectContaining({
          context: expect.stringContaining('ContactsTable'),
        }),
        expect.any(String),
      );
    });

    it('fetchAllContacts_Should_DisplayError_When_FetchFails', async () => {
      mockFetchAllContacts.mockResolvedValue({
        success: false,
        error: {
          code: 'FETCH_FAILED',
          message: 'Failed to fetch contacts',
        },
      });

      const store = createMockStore();

      render(
        <Provider store={store}>
          <ContactsTable />
        </Provider>,
      );

      // Wait for error message
      await waitFor(() => {
        expect(screen.getByText(/failed to fetch contacts/i)).toBeInTheDocument();
      });
    });

    it('fetchAllContacts_Should_ShowRetryButton_When_ErrorOccurs', async () => {
      mockFetchAllContacts.mockResolvedValue({
        success: false,
        error: {
          code: 'FETCH_FAILED',
          message: 'Failed to fetch contacts',
        },
      });

      const store = createMockStore();

      render(
        <Provider store={store}>
          <ContactsTable />
        </Provider>,
      );

      // Wait for error and retry button
      await waitFor(() => {
        expect(screen.getByRole('button', { name: /retry/i })).toBeInTheDocument();
      });
    });

    it('fetchAllContacts_Should_RetryFetch_When_RetryButtonClicked', async () => {
      mockFetchAllContacts
        .mockResolvedValueOnce({
          success: false,
          error: { code: 'FETCH_FAILED', message: 'Failed' },
        })
        .mockResolvedValueOnce({
          success: true,
          data: mockContacts,
        });

      const store = createMockStore();
      const user = userEvent.setup();

      render(
        <Provider store={store}>
          <ContactsTable />
        </Provider>,
      );

      // Wait for error
      await waitFor(() => {
        expect(screen.getByRole('button', { name: /retry/i })).toBeInTheDocument();
      });

      // Click retry
      const retryButton = screen.getByRole('button', { name: /retry/i });
      await user.click(retryButton);

      // Verify contacts are loaded after retry
      await waitFor(() => {
        expect(screen.getByText('John Doe')).toBeInTheDocument();
      });

      expect(mockFetchAllContacts).toHaveBeenCalledTimes(2);
    });
  });

  describe('table structure', () => {
    it('table_Should_DisplayColumns_When_ContactsLoaded', async () => {
      mockFetchAllContacts.mockResolvedValue({
        success: true,
        data: mockContacts,
      });

      const store = createMockStore();

      render(
        <Provider store={store}>
          <ContactsTable />
        </Provider>,
      );

      await waitFor(() => {
        expect(screen.getByText('John Doe')).toBeInTheDocument();
      });

      // Verify table headers
      expect(screen.getByText('Name')).toBeInTheDocument();
      expect(screen.getByText('Phone')).toBeInTheDocument();
      expect(screen.getByText('Email')).toBeInTheDocument();
    });

    it('table_Should_BeScrollable_When_ManyContacts', async () => {
      const manyContacts = Array.from({ length: 100 }, (_, i) => ({
        resourceName: `people/${i}`,
        names: [{ displayName: `Contact ${i}` }],
        emailAddresses: [{ value: `contact${i}@example.com` }],
        phoneNumbers: [{ value: `+123456789${i}` }],
      }));

      mockFetchAllContacts.mockResolvedValue({
        success: true,
        data: manyContacts,
      });

      const store = createMockStore();

      const { container } = render(
        <Provider store={store}>
          <ContactsTable />
        </Provider>,
      );

      await waitFor(() => {
        expect(screen.getByText('Contact 0')).toBeInTheDocument();
      });

      // Verify scrollable container exists
      const scrollContainer = container.querySelector('[style*="overflow"]');
      expect(scrollContainer).toBeInTheDocument();
    });
  });

  describe('UTF-8 character support', () => {
    it('table_Should_DisplayNonLatinCharacters_When_Present', async () => {
      const nonLatinContacts: Contact[] = [
        {
          resourceName: 'people/789',
          names: [{ displayName: '李明' }], // Chinese
          emailAddresses: [{ value: 'li@example.com' }],
        },
        {
          resourceName: 'people/101',
          names: [{ displayName: 'محمد' }], // Arabic
          emailAddresses: [{ value: 'mohammed@example.com' }],
        },
      ];

      mockFetchAllContacts.mockResolvedValue({
        success: true,
        data: nonLatinContacts,
      });

      const store = createMockStore();

      render(
        <Provider store={store}>
          <ContactsTable />
        </Provider>,
      );

      await waitFor(() => {
        expect(screen.getByText('李明')).toBeInTheDocument();
      });

      expect(screen.getByText('محمد')).toBeInTheDocument();
    });
  });

  describe('loading state', () => {
    it('loading_Should_ShowSpinner_When_Fetching', async () => {
      mockFetchAllContacts.mockImplementation(
        () =>
          new Promise((resolve) => {
            setTimeout(
              () =>
                resolve({
                  success: true,
                  data: mockContacts,
                }),
              100,
            );
          }),
      );

      const store = createMockStore();

      render(
        <Provider store={store}>
          <ContactsTable />
        </Provider>,
      );

      // Verify loading indicator is shown
      expect(screen.getByText(/loading/i)).toBeInTheDocument();
    });
  });

  describe('authentication', () => {
    it('error_Should_Display_When_FetchFailsWithAuthError', async () => {
      mockFetchAllContacts.mockResolvedValue({
        success: false,
        error: {
          code: 'AUTH_FAILED',
          message: 'Not authenticated',
        },
      });

      const store = createMockStore(null);

      render(
        <Provider store={store}>
          <ContactsTable />
        </Provider>,
      );

      await waitFor(() => {
        expect(screen.getByText(/not authenticated/i)).toBeInTheDocument();
      });
    });
  });

  describe('structured logging', () => {
    it('logging_Should_LogFetchStart_When_ComponentMounts', async () => {
      mockFetchAllContacts.mockResolvedValue({
        success: true,
        data: [],
      });

      const store = createMockStore();

      render(
        <Provider store={store}>
          <ContactsTable />
        </Provider>,
      );

      await waitFor(() => {
        expect(logger.info).toHaveBeenCalledWith(
          expect.objectContaining({
            context: expect.stringContaining('ContactsTable'),
          }),
          expect.stringMatching(/dispatching fetchContacts/i),
        );
      });
    });
  });
});
