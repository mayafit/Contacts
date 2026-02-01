/**
 * @fileoverview VirtualizedContactsTable component with TanStack Table and Virtual
 * @module Contacts/components/VirtualizedContactsTable
 *
 * Story 2.4: Implements virtualized table for 1000+ contacts with 60fps scrolling
 * Uses TanStack Table v8 for table features and TanStack Virtual for row virtualization
 */

import React, { useRef, useMemo } from 'react';
import { useSelector } from 'react-redux';
import { useReactTable, getCoreRowModel, ColumnDef, CellContext, flexRender } from '@tanstack/react-table';
import { useVirtualizer } from '@tanstack/react-virtual';
import { Box, CircularProgress, Typography, Paper } from '@mui/material';
import {
  selectAllContacts,
  selectContactsLoading,
  selectContactsError,
} from '../redux/slices/contacts/selectors';
import { selectColumnConfig } from '../redux/slices/ui/uiSlice';
import { AVAILABLE_COLUMNS } from '../features/columnConfig/columnDefinitions';
import { logger } from '../../../shared/logger';
import type { Contact } from '../types/Contact';

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
 * Memoized cell renderer for contact name
 */
// eslint-disable-next-line react/prop-types
const NameCell = React.memo<{ contact: Contact }>(({ contact }) => (
  <div style={{ padding: '12px' }}>{getDisplayName(contact)}</div>
));
NameCell.displayName = 'NameCell';

/**
 * Memoized cell renderer for phone number
 */
// eslint-disable-next-line react/prop-types
const PhoneCell = React.memo<{ contact: Contact }>(({ contact }) => (
  <div style={{ padding: '12px' }}>{getPrimaryPhone(contact)}</div>
));
PhoneCell.displayName = 'PhoneCell';

/**
 * Memoized cell renderer for email
 */
// eslint-disable-next-line react/prop-types
const EmailCell = React.memo<{ contact: Contact }>(({ contact }) => (
  <div style={{ padding: '12px' }}>{getPrimaryEmail(contact)}</div>
));
EmailCell.displayName = 'EmailCell';

/**
 * VirtualizedContactsTable component
 * Displays contacts using TanStack Table with virtual scrolling for performance
 * Story 2.4: Handles 1000+ contacts with 60fps scrolling
 */
const VirtualizedContactsTable: React.FC = () => {
  const parentRef = useRef<HTMLDivElement>(null);

  // Select contacts state from Redux store using memoized selectors
  const contacts = useSelector(selectAllContacts);
  const loading = useSelector(selectContactsLoading);
  const error = useSelector(selectContactsError);
  const columnConfig = useSelector(selectColumnConfig);

  // Log component mount
  React.useEffect(() => {
    logger.info(
      {
        context: 'VirtualizedContactsTable/mount',
        metadata: { contactCount: contacts.length },
      },
      'VirtualizedContactsTable mounted',
    );
  }, [contacts.length]);

  // Define columns for TanStack Table based on Redux column configuration
  // Filters and reorders columns based on user preferences
  // Memoized for performance (<100ms update requirement)
  const columns = useMemo<ColumnDef<Contact>[]>(() => {
    const { visibleColumns, columnOrder } = columnConfig;

    // Safety check: ensure we have valid column config
    if (!visibleColumns || visibleColumns.length === 0) {
      logger.warn(
        { context: 'VirtualizedContactsTable/columns' },
        'No visible columns configured, using defaults'
      );
      // Return default columns if config is invalid
      return AVAILABLE_COLUMNS.filter((col) => col.isDefault).map((colDef) => ({
        id: colDef.id,
        header: colDef.label,
        accessorFn: (row: Contact) => colDef.accessor(row),
        cell: (info: CellContext<Contact, unknown>) => (
          <div style={{ padding: '12px' }}>{info.getValue() as string}</div>
        ),
      }));
    }

    // Get visible column definitions
    const visibleColumnDefs = AVAILABLE_COLUMNS.filter((col) =>
      visibleColumns.includes(col.id)
    );

    // Sort columns according to columnOrder
    const orderedColumns = visibleColumnDefs.sort((a, b) => {
      const indexA = columnOrder.indexOf(a.id);
      const indexB = columnOrder.indexOf(b.id);
      return indexA - indexB;
    });

    // Convert to TanStack Table column definitions
    return orderedColumns.map((colDef) => ({
      id: colDef.id,
      header: colDef.label,
      accessorFn: (row: Contact) => colDef.accessor(row),
      cell: (info: CellContext<Contact, unknown>) => (
        <div style={{ padding: '12px' }}>{info.getValue() as string}</div>
      ),
    }));
  }, [columnConfig]);

  // Initialize TanStack Table
  const table = useReactTable({
    data: contacts,
    columns,
    getCoreRowModel: getCoreRowModel(),
  });

  const { rows } = table.getRowModel();

  // Initialize TanStack Virtual for row virtualization
  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => parentRef.current,
    estimateSize: () => 48, // Row height in pixels
    overscan: 10, // Number of items to render outside visible area
  });

  const virtualRows = virtualizer.getVirtualItems();
  const totalSize = virtualizer.getTotalSize();

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

  // Virtualized table
  return (
    <Paper
      sx={{ width: '100%', height: '600px', overflow: 'hidden' }}
      data-testid="virtualized-table-container"
    >
      {/* Table Header */}
      <Box
        sx={{
          display: 'flex',
          borderBottom: '2px solid #e0e0e0',
          bgcolor: '#f5f5f5',
          fontWeight: 'bold',
        }}
      >
        {table.getHeaderGroups().map((headerGroup) =>
          headerGroup.headers.map((header) => (
            <Box
              key={header.id}
              sx={{
                flex: 1,
                padding: '12px',
                textAlign: 'left',
              }}
            >
              {flexRender(header.column.columnDef.header, header.getContext())}
            </Box>
          )),
        )}
      </Box>

      {/* Virtualized Table Body */}
      <Box
        ref={parentRef}
        sx={{
          height: 'calc(100% - 48px)',
          overflow: 'auto',
          position: 'relative',
        }}
      >
        <Box
          sx={{
            height: `${totalSize}px`,
            width: '100%',
            position: 'relative',
          }}
        >
          {virtualRows.map((virtualRow) => {
            const row = rows[virtualRow.index];
            return (
              <Box
                key={row.id}
                sx={{
                  position: 'absolute',
                  top: 0,
                  left: 0,
                  width: '100%',
                  height: `${virtualRow.size}px`,
                  transform: `translateY(${virtualRow.start}px)`,
                  display: 'flex',
                  borderBottom: '1px solid #e0e0e0',
                  '&:hover': {
                    bgcolor: '#f5f5f5',
                  },
                }}
              >
                {row.getVisibleCells().map((cell) => (
                  <Box
                    key={cell.id}
                    sx={{
                      flex: 1,
                      display: 'flex',
                      alignItems: 'center',
                    }}
                  >
                    {flexRender(cell.column.columnDef.cell, cell.getContext())}
                  </Box>
                ))}
              </Box>
            );
          })}
        </Box>
      </Box>
    </Paper>
  );
};

export default VirtualizedContactsTable;
