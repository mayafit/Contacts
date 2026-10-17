/**
 * @fileoverview Main Contacts home page with contact grid
 * @module Contacts/components/ContactsHome
 *
 * Story 3.7: Added ConflictResolutionDialog wiring for 412 conflict handling
 */

import React, { useState, useCallback } from 'react';
import { useSelector } from 'react-redux';
import { useNavigate } from 'react-router';
import {
  Container,
  Typography,
  Button,
  Box,
  Card,
  CardContent,
  Avatar,
  IconButton,
  Tooltip,
} from '@mui/material';
import { Logout as LogoutIcon, Settings as SettingsIcon } from '@mui/icons-material';
import { logout } from '../redux/slices/auth/authSlice';
import { selectUser } from '../redux/slices/auth/selectors';
import { selectConflictedOperations } from '../redux/slices/syncQueue/selectors';
import { operationSuccess, operationFailed } from '../redux/slices/syncQueue/syncQueueSlice';
import { contactUpdated } from '../redux/slices/contacts/contactsSlice';
import { executeFieldUpdate } from '../redux/thunks/syncThunks';
import { GoogleContactsService } from '../services/GoogleContactsService';
import { logger } from '../../../shared/logger';
import VirtualizedContactsTable from './VirtualizedContactsTable';
import ColumnConfigDialog from '../features/columnConfig/components/ColumnConfigDialog';
import { ConflictResolutionDialog } from './ConflictResolutionDialog';
import { useAppSelector, useAppDispatch } from '../types/hooks';

/**
 * Contacts home page component
 * Displays authenticated user info, sign out button, and Google contacts table
 */
const ContactsHome: React.FC = () => {
  const dispatch = useAppDispatch();
  const navigate = useNavigate();
  const user = useSelector(selectUser);
  const [isColumnConfigOpen, setIsColumnConfigOpen] = useState(false);

  // Story 3.7: Conflict resolution state
  const conflictedOperations = useAppSelector(selectConflictedOperations);
  const firstConflict = conflictedOperations.length > 0 ? conflictedOperations[0] : null;

  const handleSignOut = () => {
    logger.info(
      {
        context: 'ContactsHome/handleSignOut',
        metadata: { userId: user?.id, userEmail: user?.email },
      },
      'User initiated sign out',
    );

    dispatch(logout());
    navigate('/login');

    logger.info(
      {
        context: 'ContactsHome/handleSignOut',
      },
      'User signed out successfully and redirected to login',
    );
  };

  /**
   * Story 3.7 AC4: Keep user's change — fetch fresh etag, retry the update
   */
  const handleKeepMine = useCallback(async () => {
    if (!firstConflict) {
      return;
    }

    const { id, resourceName, fieldPath, newValue, oldValue } = firstConflict;

    logger.info(
      {
        context: 'ContactsHome/handleKeepMine',
        metadata: { operationId: id, resourceName, fieldPath },
      },
      'Conflict resolved: user kept their change',
    );

    // Clear the conflict status, then re-dispatch through the sync queue
    dispatch(operationSuccess(id));
    dispatch(
      executeFieldUpdate({
        resourceName,
        fieldPath,
        newValue,
        oldValue,
      }),
    );
  }, [firstConflict, dispatch]);

  /**
   * Story 3.7 AC5: Use remote value — rollback optimistic update
   */
  const handleUseRemote = useCallback(async () => {
    if (!firstConflict) {
      return;
    }

    const { id, resourceName, fieldPath, remoteValue } = firstConflict;

    logger.info(
      {
        context: 'ContactsHome/handleUseRemote',
        metadata: { operationId: id, resourceName, fieldPath },
      },
      'Conflict resolved: user accepted remote value',
    );

    // Fetch the full remote contact to update the store with complete data
    const remoteResult = await GoogleContactsService.getContact(resourceName);
    if (remoteResult.success && remoteResult.data) {
      dispatch(contactUpdated(remoteResult.data));
      dispatch(operationSuccess(id));
    } else {
      // Cannot safely apply partial data — would corrupt the entity store
      logger.error(
        {
          context: 'ContactsHome/handleUseRemote',
          metadata: { operationId: id, resourceName },
        },
        'Failed to fetch remote contact for rollback — operation remains in conflict state',
      );
      dispatch(operationFailed({ id, error: 'Failed to fetch remote contact. Please try again.' }));
      return;
    }

    logger.info(
      {
        context: 'ContactsHome/handleUseRemote',
        metadata: { operationId: id, resourceName, fieldPath },
      },
      'Conflict resolved: remote value applied',
    );
  }, [firstConflict, dispatch]);

  return (
    <Container maxWidth="lg">
      <Box sx={{ mt: 4, mb: 4 }}>
        <Card>
          <CardContent>
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, mb: 3 }}>
              {user?.profilePicture && (
                <Avatar src={user.profilePicture} alt={user.name} sx={{ width: 56, height: 56 }} />
              )}
              <Box>
                <Typography variant="h5" component="h1">
                  Welcome, {user?.name}
                </Typography>
                <Typography variant="body2" color="text.secondary">
                  {user?.email}
                </Typography>
              </Box>
              <Box sx={{ ml: 'auto' }}>
                <Button variant="outlined" startIcon={<LogoutIcon />} onClick={handleSignOut}>
                  Sign Out
                </Button>
              </Box>
            </Box>
            <Box
              sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', mb: 2 }}
            >
              <Typography variant="h6" gutterBottom sx={{ mb: 0 }}>
                Your Contacts
              </Typography>
              <Tooltip title="Configure Columns">
                <IconButton
                  onClick={() => setIsColumnConfigOpen(true)}
                  aria-label="Configure columns"
                >
                  <SettingsIcon />
                </IconButton>
              </Tooltip>
            </Box>
          </CardContent>
        </Card>
        <Box sx={{ mt: 3 }}>
          <VirtualizedContactsTable />
        </Box>
      </Box>

      {/* Column Configuration Dialog */}
      <ColumnConfigDialog open={isColumnConfigOpen} onClose={() => setIsColumnConfigOpen(false)} />

      {/* Story 3.7: Conflict Resolution Dialog */}
      {firstConflict && (
        <ConflictResolutionDialog
          operation={firstConflict}
          remoteValue={firstConflict.remoteValue}
          onKeepMine={handleKeepMine}
          onUseRemote={handleUseRemote}
        />
      )}
    </Container>
  );
};

export default ContactsHome;
