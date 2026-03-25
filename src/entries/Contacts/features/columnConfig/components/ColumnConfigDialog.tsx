/**
 * @fileoverview ColumnConfigDialog for customizing visible table columns
 * @module features/columnConfig/components
 */

import React, { useState, useEffect } from 'react';
import {
  Dialog,
  DialogTitle,
  DialogContent,
  DialogActions,
  Button,
} from '@mui/material';
import ColumnList from './ColumnList';
import { AVAILABLE_COLUMNS } from '../columnDefinitions';
import { useAppDispatch, useAppSelector } from '../../../types/hooks';
import {
  selectColumnConfig,
  setColumnConfig,
  toggleColumn,
} from '../../../redux/slices/ui/uiSlice';
import type { ColumnConfig } from '../types';

/**
 * ColumnConfigDialog component props
 */
export interface ColumnConfigDialogProps {
  /** Whether the dialog is open */
  open: boolean;
  /** Handler for dialog close */
  onClose: () => void;
}

/**
 * Dialog for configuring visible columns and their order
 * Implements Material-UI Dialog with proper ARIA attributes
 * Supports add/remove columns and drag-drop reordering
 * ESC key and overlay click close the dialog
 */
const ColumnConfigDialog: React.FC<ColumnConfigDialogProps> = ({
  open,
  onClose,
}) => {
  const dispatch = useAppDispatch();
  const currentConfig = useAppSelector(selectColumnConfig);

  // Local state for temporary changes (only applied on "Apply")
  const [tempConfig, setTempConfig] = useState<ColumnConfig>(currentConfig);

  // Sync temp config with Redux when dialog opens
  useEffect(() => {
    if (open) {
      setTempConfig(currentConfig);
    }
  }, [open, currentConfig]);

  const handleToggle = (columnId: string) => {
    const isCurrentlyVisible = tempConfig.visibleColumns.includes(columnId);
    const column = AVAILABLE_COLUMNS.find((col) => col.id === columnId);

    if (!column) return;

    // Prevent removal of default columns
    if (isCurrentlyVisible && column.isDefault) {
      return;
    }

    if (isCurrentlyVisible) {
      // Remove column
      setTempConfig({
        visibleColumns: tempConfig.visibleColumns.filter(
          (id) => id !== columnId
        ),
        columnOrder: tempConfig.columnOrder.filter((id) => id !== columnId),
      });
    } else {
      // Add column
      setTempConfig({
        visibleColumns: [...tempConfig.visibleColumns, columnId],
        columnOrder: [...tempConfig.columnOrder, columnId],
      });
    }
  };

  const handleApply = () => {
    // Dispatch Redux action to apply changes
    dispatch(setColumnConfig(tempConfig));
    onClose();
  };

  const handleCancel = () => {
    // Discard changes and close
    setTempConfig(currentConfig);
    onClose();
  };

  return (
    <Dialog
      open={open}
      onClose={handleCancel}
      aria-labelledby="column-config-dialog-title"
      aria-describedby="column-config-dialog-description"
      maxWidth="sm"
      fullWidth
    >
      <DialogTitle id="column-config-dialog-title">
        Configure Columns
      </DialogTitle>
      <DialogContent
        id="column-config-dialog-description"
        sx={{ p: 0, minHeight: 400 }}
      >
        <ColumnList
          columns={AVAILABLE_COLUMNS}
          visibleColumns={tempConfig.visibleColumns}
          columnOrder={tempConfig.columnOrder}
          onToggle={handleToggle}
        />
      </DialogContent>
      <DialogActions>
        <Button onClick={handleCancel}>Cancel</Button>
        <Button onClick={handleApply} variant="contained">
          Apply
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default ColumnConfigDialog;
