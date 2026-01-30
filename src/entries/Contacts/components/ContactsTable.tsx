/**
 * @fileoverview ContactsTable component - displays Google contacts in a basic table view
 * @module Contacts/components/ContactsTable
 *
 * Updated for Story 2.1B: Now fetches from backend API instead of direct Google API calls
 * Authentication handled via session cookies, no access token needed
 */

import React, { useState, useEffect } from 'react';
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
import { GoogleContactsService } from '../services/GoogleContactsService';
import { logger } from '../../../shared/logger';
import type { Contact } from '../types/Contact';

/**
 * ContactsTable component
 * Fetches and displays Google contacts in a basic table view with Name, Phone, and Email columns
 */
const ContactsTable: React.FC = () => {
  const [contacts, setContacts] = useState<Contact[]>([]);
  const [loading, setLoading] = useState<boolean>(true);
  const [error, setError] = useState<string | null>(null);

  /**
   * Fetches contacts from backend API
   */
  const fetchContacts = async () => {
    try {
      logger.info(
        {
          context: 'ContactsTable/fetchContacts',
        },
        'Starting to fetch contacts from backend',
      );

      setLoading(true);
      setError(null);

      const response = await GoogleContactsService.fetchAllContacts();

      if (response.success && response.data) {
        setContacts(response.data);
        logger.info(
          {
            context: 'ContactsTable/fetchContacts',
            metadata: {
              contactCount: response.data.length,
            },
          },
          'Successfully fetched contacts',
        );
      } else {
        const errorMessage = response.error?.message || 'Failed to fetch contacts';
        setError(errorMessage);
        logger.error(
          {
            context: 'ContactsTable/fetchContacts',
            metadata: {
              errorCode: response.error?.code,
              errorMessage,
            },
          },
          'Failed to fetch contacts',
        );
      }
    } catch (err) {
      const errorMessage = err instanceof Error ? err.message : 'An unexpected error occurred';
      setError(errorMessage);
      logger.error(
        {
          context: 'ContactsTable/fetchContacts',
          metadata: {
            errorMessage,
          },
        },
        'Exception while fetching contacts',
      );
    } finally {
      setLoading(false);
    }
  };

  /**
   * Fetch contacts on component mount
   */
  useEffect(() => {
    fetchContacts();
  }, []);

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
      return contact.phoneNumbers[0].value || '-';
    }
    return '-';
  };

  /**
   * Extract primary email from contact
   */
  const getPrimaryEmail = (contact: Contact): string => {
    if (contact.emailAddresses && contact.emailAddresses.length > 0) {
      return contact.emailAddresses[0].value || '-';
    }
    return '-';
  };

  /**
   * Handle retry button click
   */
  const handleRetry = () => {
    logger.info(
      {
        context: 'ContactsTable/handleRetry',
      },
      'User initiated retry',
    );
    fetchContacts();
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
          <Typography variant="body1">{error}</Typography>
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
