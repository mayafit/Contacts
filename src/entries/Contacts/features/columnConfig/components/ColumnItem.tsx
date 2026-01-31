/**
 * @fileoverview ColumnItem component for individual column in configuration list
 * @module features/columnConfig/components
 */

import React from 'react';
import { useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import {
  Checkbox,
  FormControlLabel,
  ListItem,
  Tooltip,
  IconButton,
  Box,
} from '@mui/material';
import DragIndicatorIcon from '@mui/icons-material/DragIndicator';
import type { ColumnDefinition } from '../types';

/**
 * ColumnItem component props
 */
export interface ColumnItemProps {
  /** Column definition */
  column: ColumnDefinition;
  /** Whether the column is currently visible */
  isVisible: boolean;
  /** Handler for checkbox toggle */
  onToggle: (columnId: string) => void;
  /** Whether dragging is disabled */
  isDragDisabled?: boolean;
}

/**
 * Individual column item with checkbox and drag handle
 * Implements @dnd-kit/sortable for drag-and-drop reordering
 * Keyboard accessible with Space/Enter activation
 */
const ColumnItem: React.FC<ColumnItemProps> = React.memo(
  ({ column, isVisible, onToggle, isDragDisabled = false }) => {
    const {
      attributes,
      listeners,
      setNodeRef,
      transform,
      transition,
      isDragging,
    } = useSortable({
      id: column.id,
      disabled: isDragDisabled,
    });

    const style = {
      transform: CSS.Transform.toString(transform),
      transition,
      opacity: isDragging ? 0.5 : 1,
      backgroundColor: isDragging ? 'action.hover' : 'transparent',
    };

    const handleToggle = () => {
      if (!column.isDefault) {
        onToggle(column.id);
      }
    };

    const handleKeyPress = (event: React.KeyboardEvent) => {
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        handleToggle();
      }
    };

    const checkboxLabel = column.isDefault
      ? `${column.label} (Default column, cannot be removed)`
      : column.label;

    return (
      <ListItem
        ref={setNodeRef}
        style={style}
        sx={{
          py: 0.5,
          px: 1,
          borderRadius: 1,
          '&:hover': {
            backgroundColor: 'action.hover',
          },
        }}
      >
        <Box sx={{ display: 'flex', alignItems: 'center', width: '100%' }}>
          {/* Drag Handle */}
          <Tooltip
            title={
              isDragDisabled
                ? 'Dragging disabled'
                : 'Drag to reorder (Space/Enter to activate)'
            }
          >
            <span>
              <IconButton
                size="small"
                {...attributes}
                {...listeners}
                disabled={isDragDisabled}
                aria-label="Drag to reorder"
                sx={{
                  cursor: isDragDisabled ? 'default' : 'grab',
                  '&:active': {
                    cursor: isDragDisabled ? 'default' : 'grabbing',
                  },
                  opacity: isDragDisabled ? 0.3 : 1,
                }}
              >
                <DragIndicatorIcon />
              </IconButton>
            </span>
          </Tooltip>

          {/* Checkbox with Label */}
          <FormControlLabel
            control={
              <Checkbox
                checked={isVisible}
                onChange={handleToggle}
                onKeyPress={handleKeyPress}
                disabled={column.isDefault}
                aria-disabled={column.isDefault}
                inputProps={{
                  'aria-label': checkboxLabel,
                }}
              />
            }
            label={
              column.isDefault ? (
                <Tooltip title="Default columns cannot be removed">
                  <span>{column.label}</span>
                </Tooltip>
              ) : (
                column.label
              )
            }
            sx={{ flex: 1, marginLeft: 0 }}
          />
        </Box>
      </ListItem>
    );
  }
);

ColumnItem.displayName = 'ColumnItem';

export default ColumnItem;
