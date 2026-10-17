/**
 * @fileoverview SyncStatusIcon component for real-time sync status indicators
 * @module Contacts/components/SyncStatusIcon/SyncStatusIcon
 *
 * Story 3.5: Displays per-cell sync status (pending/in-progress/success/failed)
 * with ARIA live region announcements and manual retry support.
 */

import React, { useState, useEffect, useRef, useCallback } from 'react';
import { Box, CircularProgress, Tooltip } from '@mui/material';
import CheckCircleIcon from '@mui/icons-material/CheckCircle';
import WarningAmberIcon from '@mui/icons-material/WarningAmber';
import { useAppSelector, useAppDispatch } from '../../types/hooks';
import {
  selectOperationByContact,
  selectRetryCount,
  selectMaxRetries,
} from '../../redux/slices/syncQueue/selectors';
import { retryOperation } from '../../redux/slices/syncQueue/syncQueueSlice';
import { executeFieldUpdate } from '../../redux/thunks/syncThunks';
import { logger } from '../../../../shared/logger';
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined';
import type { SyncOperation, SyncOperationStatus } from '../../types/SyncOperation';

/**
 * Props for SyncStatusIcon component
 */
export interface SyncStatusIconProps {
  /** Contact resource name (e.g. "people/c12345") */
  resourceName: string;
  /** Field path for the sync operation (e.g. "names") */
  fieldPath: string;
  /** Contact name for ARIA announcements */
  contactName: string;
  /** Human-readable field label for ARIA announcements (e.g. "Name") */
  fieldLabel: string;
}

/**
 * Determines the tooltip message based on operation status and retry count.
 *
 * @param status - Current sync operation status
 * @param error - Error message from the operation, if any
 * @param retryCount - Number of retry attempts so far
 * @returns Tooltip string to display
 */
const getTooltipMessage = (
  status: SyncOperationStatus,
  error: string | null,
  retryCount: number,
  maxRetries: number,
): string => {
  if (status === 'pending') {
    return 'Syncing...';
  }
  if (status === 'in-progress') {
    if (retryCount > 0) {
      return `Retrying... (Attempt ${retryCount} of ${maxRetries})`;
    }
    return 'Syncing...';
  }
  if (status === 'success') {
    return 'Synced';
  }
  if (status === 'failed') {
    if (retryCount >= maxRetries) {
      return 'Sync failed. Click to retry manually.';
    }
    return `Sync failed: ${error ?? 'Unknown error'}`;
  }
  if (status === 'conflict') {
    return 'Conflict detected — resolve in dialog';
  }
  return '';
};

/**
 * Determines the ARIA announcement message on status change.
 *
 * @param status - Current sync operation status
 * @param contactName - Contact display name
 * @param fieldLabel - Human-readable field label
 * @returns ARIA announcement string, or empty string for non-announcing statuses
 */
const getAriaMessage = (
  status: SyncOperationStatus | 'success-inferred',
  contactName: string,
  fieldLabel: string,
): string => {
  if (status === 'success' || status === 'success-inferred') {
    return `${contactName} ${fieldLabel} synced successfully`;
  }
  if (status === 'failed') {
    return `${contactName} ${fieldLabel} sync failed`;
  }
  return '';
};

/**
 * SyncStatusIcon -- displays a per-cell sync status indicator.
 *
 * - Pending / in-progress: yellow spinner
 * - Success: green checkmark, auto-hides after 2 seconds
 * - Failed: red warning triangle, clickable for manual retry
 * - ARIA live region announces status changes for screen readers
 *
 * The component tracks a "previous operation" ref to detect when an operation
 * is removed from Redux (which signals success in the current thunk implementation).
 */
const SyncStatusIcon: React.FC<SyncStatusIconProps> = ({
  resourceName,
  fieldPath,
  contactName,
  fieldLabel,
}) => {
  const dispatch = useAppDispatch();

  const operations = useAppSelector((state) => selectOperationByContact(state, resourceName));
  const operation: SyncOperation | null =
    operations.find((op) => op.fieldPath === fieldPath) ?? null;

  const retryCount = useAppSelector((state) =>
    operation ? selectRetryCount(state, operation.id) : 0,
  );

  const maxRetries = useAppSelector(selectMaxRetries);

  const [visible, setVisible] = useState(true);
  const [ariaMessage, setAriaMessage] = useState('');
  const [successInferred, setSuccessInferred] = useState(false);
  const previousOperationRef = useRef<SyncOperation | null>(null);

  /**
   * Detect when an in-progress operation disappears from Redux.
   * The current thunk removes the operation on success, so we infer success
   * and show the checkmark for 2 seconds before hiding.
   * Also reset successInferred when a new operation appears (M2 fix).
   */
  useEffect(() => {
    const prevOp = previousOperationRef.current;

    if (prevOp && (prevOp.status === 'in-progress' || prevOp.status === 'pending') && !operation) {
      setSuccessInferred(true);
      setVisible(true);
      setAriaMessage(getAriaMessage('success-inferred', contactName, fieldLabel));
    } else if (operation && successInferred) {
      // New operation started while success was still showing — reset
      setSuccessInferred(false);
    }

    previousOperationRef.current = operation;
  }, [operation, contactName, fieldLabel]);

  /**
   * Auto-hide timer: when success is inferred (operation removed from Redux)
   * or when operation status is explicitly 'success', hide after 2 seconds.
   */
  useEffect(() => {
    if (successInferred || operation?.status === 'success') {
      const timer = setTimeout(() => {
        setVisible(false);
        setSuccessInferred(false);
      }, 2000);
      return () => clearTimeout(timer);
    }

    setVisible(true);
    return undefined;
  }, [successInferred, operation?.status]);

  /**
   * Update ARIA message when operation status changes (for statuses with Redux presence).
   */
  useEffect(() => {
    if (operation?.status === 'success') {
      setAriaMessage(getAriaMessage('success', contactName, fieldLabel));
    } else if (operation?.status === 'failed') {
      setAriaMessage(getAriaMessage('failed', contactName, fieldLabel));
    }
  }, [operation?.status, contactName, fieldLabel]);

  /**
   * Handle manual retry click for failed operations.
   * Dispatches retryOperation to reset status, then re-triggers executeFieldUpdate.
   */
  const handleRetryClick = useCallback((): void => {
    if (!operation || operation.status !== 'failed') {
      return;
    }

    dispatch(retryOperation(operation.id));
    dispatch(
      executeFieldUpdate({
        resourceName: operation.resourceName,
        fieldPath: operation.fieldPath,
        newValue: operation.newValue,
        oldValue: operation.oldValue,
      }),
    );

    logger.info(
      {
        context: 'SyncStatusIcon/handleRetryClick',
        metadata: { operationId: operation.id, resourceName },
      },
      'Manual retry triggered',
    );
  }, [dispatch, operation, resourceName]);

  // Render success checkmark when success is inferred (operation removed from Redux)
  if (successInferred && visible) {
    return (
      <Box component="span" sx={{ display: 'inline-flex', alignItems: 'center', ml: 0.5 }}>
        <Tooltip title="Synced">
          <CheckCircleIcon fontSize="small" color="success" data-testid="sync-status-success" />
        </Tooltip>
        <Box
          aria-live="polite"
          aria-atomic="true"
          sx={{
            position: 'absolute',
            width: 1,
            height: 1,
            overflow: 'hidden',
            clip: 'rect(0,0,0,0)',
          }}
        >
          {ariaMessage}
        </Box>
      </Box>
    );
  }

  // No operation and no inferred success — render nothing
  if (!operation) {
    return null;
  }

  // Hidden after success auto-hide timer
  if (!visible) {
    return null;
  }

  const status = operation.status;
  const tooltip = getTooltipMessage(status, operation.error, retryCount, maxRetries);

  const renderIcon = (): React.ReactElement => {
    if (status === 'pending' || status === 'in-progress') {
      return <CircularProgress size={14} color="warning" data-testid="sync-status-spinner" />;
    }

    if (status === 'success') {
      return <CheckCircleIcon fontSize="small" color="success" data-testid="sync-status-success" />;
    }

    if (status === 'failed') {
      return (
        <WarningAmberIcon
          fontSize="small"
          color="error"
          data-testid="sync-status-failed"
          onClick={handleRetryClick}
          sx={{ cursor: 'pointer' }}
          role="button"
          aria-label={`Retry sync for ${fieldLabel}`}
        />
      );
    }

    if (status === 'conflict') {
      return (
        <InfoOutlinedIcon
          fontSize="small"
          color="warning"
          data-testid="sync-status-conflict"
          aria-label="Conflict detected"
        />
      );
    }

    return <></>;
  };

  return (
    <Box component="span" sx={{ display: 'inline-flex', alignItems: 'center', ml: 0.5 }}>
      <Tooltip title={tooltip}>{renderIcon()}</Tooltip>
      <Box
        aria-live="polite"
        aria-atomic="true"
        sx={{
          position: 'absolute',
          width: 1,
          height: 1,
          overflow: 'hidden',
          clip: 'rect(0,0,0,0)',
        }}
      >
        {ariaMessage}
      </Box>
    </Box>
  );
};

SyncStatusIcon.displayName = 'SyncStatusIcon';

export { SyncStatusIcon };
export default SyncStatusIcon;
