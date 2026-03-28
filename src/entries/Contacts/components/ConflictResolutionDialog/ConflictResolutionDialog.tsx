/**
 * @fileoverview Conflict resolution dialog for concurrent edit conflicts
 * @module Contacts/components/ConflictResolutionDialog/ConflictResolutionDialog
 *
 * Story 3.7: Displays when a 412 Precondition Failed is detected,
 * allowing the user to choose between their local change and the remote value.
 */

import React from 'react';
import {
  Dialog,
  DialogTitle,
  DialogContent,
  DialogActions,
  Button,
  Typography,
  Box,
} from '@mui/material';
import type { SyncOperation } from '../../types/SyncOperation';

/**
 * Props for ConflictResolutionDialog
 */
export interface ConflictResolutionDialogProps {
  /** The conflicted sync operation containing old/new values and metadata */
  operation: SyncOperation;
  /** The remote (server-side) value fetched after 412 detection */
  remoteValue: unknown;
  /** Handler invoked when user chooses to keep their local change */
  onKeepMine: () => void;
  /** Handler invoked when user chooses to accept the remote value */
  onUseRemote: () => void;
}

/**
 * Format a value for display in the conflict dialog.
 * Handles null, undefined, objects, and primitives.
 * @param value - The value to format
 * @returns A human-readable string representation
 */
function formatValue(value: unknown): string {
  if (value === null || value === undefined) {
    return '(empty)';
  }
  if (typeof value === 'string') {
    return value || '(empty)';
  }
  return JSON.stringify(value);
}

/**
 * ConflictResolutionDialog - MUI Dialog for resolving 412 etag conflicts.
 *
 * Shows three values side-by-side:
 * - Original value (before user's edit)
 * - User's change (the edit they attempted)
 * - Current value from Google (remote state)
 *
 * Two resolution actions:
 * - "Keep Your Change": re-fetches etag and retries the update
 * - "Use Current Value": rolls back to the remote value
 */
const ConflictResolutionDialog: React.FC<ConflictResolutionDialogProps> = ({
  operation,
  remoteValue,
  onKeepMine,
  onUseRemote,
}) => (
  <Dialog open maxWidth="sm" fullWidth data-testid="conflict-resolution-dialog">
    <DialogTitle>Contact Modified Elsewhere</DialogTitle>
    <DialogContent>
      <Typography>This contact was modified elsewhere. Choose which value to keep:</Typography>
      <Box sx={{ mt: 2 }}>
        <Typography variant="body2" color="text.secondary">
          Original value:
        </Typography>
        <Typography data-testid="conflict-old-value">{formatValue(operation.oldValue)}</Typography>

        <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
          Your change:
        </Typography>
        <Typography fontWeight="bold" data-testid="conflict-new-value">
          {formatValue(operation.newValue)}
        </Typography>

        <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
          Current value (from Google):
        </Typography>
        <Typography color="warning.main" data-testid="conflict-remote-value">
          {formatValue(remoteValue)}
        </Typography>
      </Box>
      <Box sx={{ mt: 2 }}>
        <Typography variant="body2" color="text.secondary">
          Field: {operation.fieldPath}
        </Typography>
      </Box>
    </DialogContent>
    <DialogActions>
      <Button onClick={onUseRemote} color="inherit" data-testid="conflict-use-remote-btn">
        Use Current Value
      </Button>
      <Button onClick={onKeepMine} variant="contained" data-testid="conflict-keep-mine-btn">
        Keep Your Change
      </Button>
    </DialogActions>
  </Dialog>
);

ConflictResolutionDialog.displayName = 'ConflictResolutionDialog';

export { ConflictResolutionDialog };
export default ConflictResolutionDialog;
