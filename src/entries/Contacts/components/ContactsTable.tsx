/**
 * @fileoverview ContactsTable component - displays Google contacts in a basic table view
 * @module Contacts/components/ContactsTable
 *
 * Updated for Story 2.2: Now uses Redux normalized state for centralized state management
 * Provides efficient O(1) lookups and maintains referential equality
 */

import React, { useEffect } from 'react';
import { useDispatch, useSelector } from 'react-redux';
import {
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Paper,
  CircularProgress,
  Box,
  Typography,
  Button,
} from '@mui/material';
import { Refresh as RefreshIcon } from '@mui/icons-material';
import { fetchContacts } from '../redux/slices/contacts/contactsSlice';
import {
  selectAllContacts,
  selectContactsLoading,
  selectContactsError,
} from '../redux/slices/contacts/selectors';
import { logger } from '../../../shared/logger';
import type { Contact } from '../types/Contact';
import type { AppDispatch } from '../types/store';

/**
 * ContactsTable component
 * Fetches and displays Google contacts using Redux normalized state
 * Story 2.2: Implements centralized state management with efficient selectors
 */
const ContactsTable: React.FC = () => {
  const dispatch = useDispatch<AppDispatch>();

  // Select contacts state from Redux store using memoized selectors
  const contacts = useSelector(selectAllContacts);
  const loading = useSelector(selectContactsLoading);
  const error = useSelector(selectContactsError);

  /**
   * Fetch contacts on component mount
   * Dispatches Redux thunk action
   */
  useEffect(() => {
    logger.info(
      {
        context: 'ContactsTable/mount',
      },
      'Component mounted, dispatching fetchContacts action',
    );

    dispatch(fetchContacts());
  }, [dispatch]);

  /**
   * Extract display name from contact
   */
  const getDisplayName = (contact: Contact): string => {
    if (contact.names && contact.names.length > 0) {
      return contact.names[0].displayName || contact.names[0].givenName || 'Unknown';
    }
    return 'Unknown';
  };

  /**
   * Extract primary phone number from contact
   */
  const getPrimaryPhone = (contact: Contact): string => {
    if (contact.phoneNumbers && contact.phoneNumbers.length > 0) {
      return contact.phoneNumbers[0].value || '—';
    }
    return '—';
  };

  /**
   * Extract primary email from contact
   */
  const getPrimaryEmail = (contact: Contact): string => {
    if (contact.emailAddresses && contact.emailAddresses.length > 0) {
      return contact.emailAddresses[0].value || '—';
    }
    return '—';
  };

  /**
   * Handle retry button click
   * Dispatches fetchContacts action again
   */
  const handleRetry = () => {
    logger.info(
      {
        context: 'ContactsTable/handleRetry',
      },
      'User initiated retry, dispatching fetchContacts',
    );
    dispatch(fetchContacts());
  };

  // Loading state
  if (loading) {
    return (
      <Box
        sx={{
          display: 'flex',
          justifyContent: 'center',
          alignItems: 'center',
          minHeight: '400px',
        }}
      >
        <CircularProgress />
        <Typography variant="body1" sx={{ ml: 2 }}>
          Loading contacts...
        </Typography>
      </Box>
    );
  }

  // Error state
  if (error) {
    return (
      <Box sx={{ p: 3 }}>
        <Box
          sx={{
            mb: 2,
            p: 2,
            borderRadius: 1,
            bgcolor: '#f44336',
            color: 'white',
          }}
        >
          <Typography variant="body1">{error.message}</Typography>
        </Box>
        <Button variant="contained" startIcon={<RefreshIcon />} onClick={handleRetry}>
          Retry
        </Button>
      </Box>
    );
  }

  // Empty state
  if (contacts.length === 0) {
    return (
      <Box sx={{ p: 3 }}>
        <Typography variant="body1" color="text.secondary">
          No contacts found.
        </Typography>
      </Box>
    );
  }

  // Contacts table
  return (
    <TableContainer component={Paper} sx={{ maxHeight: '600px', overflow: 'auto' }}>
      <Table stickyHeader>
        <TableHead>
          <TableRow>
            <TableCell>
              <Typography variant="subtitle2" fontWeight="bold">
                Name
              </Typography>
            </TableCell>
            <TableCell>
              <Typography variant="subtitle2" fontWeight="bold">
                Phone
              </Typography>
            </TableCell>
            <TableCell>
              <Typography variant="subtitle2" fontWeight="bold">
                Email
              </Typography>
            </TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {contacts.map((contact) => (
            <TableRow key={contact.resourceName} hover>
              <TableCell>{getDisplayName(contact)}</TableCell>
              <TableCell>{getPrimaryPhone(contact)}</TableCell>
              <TableCell>{getPrimaryEmail(contact)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </TableContainer>
  );
};

export default ContactsTable;
