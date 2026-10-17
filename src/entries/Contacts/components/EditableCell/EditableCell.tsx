/**
 * @fileoverview EditableCell component for inline table cell editing
 * @module Contacts/components/EditableCell/EditableCell
 *
 * Story 3.4: Enable inline cell editing for contact fields (AC: #1, #2, #3, #4, #5, #7)
 */

import React, { useState, useCallback, useEffect, useRef } from 'react';
import { TextField } from '@mui/material';
import { useAppDispatch } from '../../types/hooks';
import { executeFieldUpdate } from '../../redux/thunks/syncThunks';
import { logger } from '../../../../shared/logger';
import { SyncStatusIcon } from '../SyncStatusIcon';

/**
 * Props for EditableCell component
 */
export interface EditableCellProps {
  /** Contact resource name (e.g. "people/c12345") */
  resourceName: string;
  /** Field path for the sync operation (e.g. "names", "phoneNumbers", "emailAddresses") */
  fieldPath: string;
  /** Current display value */
  value: string;
  /** Human-readable field label for aria-label (e.g. "Name", "Phone") */
  fieldLabel: string;
  /** Contact name for aria-label context */
  contactName: string;
  /** Callback for keyboard navigation between cells */
  onNavigate?: (direction: 'right' | 'down') => void;
  /** When true, auto-enter edit mode (used for keyboard navigation) */
  autoEdit?: boolean;
  /** Callback when autoEdit has been consumed */
  onAutoEditConsumed?: () => void;
}

/**
 * EditableCell — click-to-edit inline cell for the contacts table.
 *
 * - Click to activate edit mode with pre-populated value
 * - Enter: save + navigate down
 * - Tab: save + navigate right
 * - Escape: cancel without saving
 * - Blur: save if value changed
 * - No dispatch when value is unchanged
 */
const EditableCell: React.FC<EditableCellProps> = ({
  resourceName,
  fieldPath,
  value,
  fieldLabel,
  contactName,
  onNavigate,
  autoEdit = false,
  onAutoEditConsumed,
}) => {
  const dispatch = useAppDispatch();
  const [isEditing, setIsEditing] = useState(false);
  const [editValue, setEditValue] = useState(value);
  const isCancelledRef = useRef(false);

  useEffect(() => {
    if (autoEdit && !isEditing) {
      setIsEditing(true);
      setEditValue(value);
      isCancelledRef.current = false;
      onAutoEditConsumed?.();
    }
  }, [autoEdit, isEditing, value, onAutoEditConsumed]);

  const handleSave = useCallback(
    (newValue: string) => {
      if (newValue === value) {
        setIsEditing(false);
        return;
      }

      logger.info(
        {
          context: 'EditableCell/handleSave',
          metadata: { resourceName, fieldPath, newValue, oldValue: value },
        },
        'Saving field update',
      );

      dispatch(
        executeFieldUpdate({
          resourceName,
          fieldPath,
          newValue,
          oldValue: value,
        }),
      );
      setIsEditing(false);
    },
    [dispatch, resourceName, fieldPath, value],
  );

  const handleClick = useCallback(() => {
    setIsEditing(true);
    setEditValue(value);
    isCancelledRef.current = false;
  }, [value]);

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        handleSave(editValue);
        onNavigate?.('down');
      } else if (e.key === 'Tab') {
        e.preventDefault();
        handleSave(editValue);
        onNavigate?.('right');
      } else if (e.key === 'Escape') {
        e.preventDefault();
        isCancelledRef.current = true;
        setEditValue(value);
        setIsEditing(false);
      }
    },
    [editValue, handleSave, onNavigate, value],
  );

  const handleBlur = useCallback(() => {
    if (!isCancelledRef.current) {
      handleSave(editValue);
    }
  }, [editValue, handleSave]);

  if (isEditing) {
    return (
      <TextField
        size="small"
        value={editValue}
        onChange={(e) => setEditValue(e.target.value)}
        onKeyDown={handleKeyDown}
        onBlur={handleBlur}
        autoFocus
        inputProps={{
          'aria-label': `Edit ${fieldLabel} for ${contactName}`,
          role: 'textbox',
        }}
        sx={{ width: '100%' }}
      />
    );
  }

  return (
    <div
      style={{
        padding: '12px',
        cursor: 'pointer',
        width: '100%',
        display: 'flex',
        alignItems: 'center',
      }}
      onClick={handleClick}
    >
      <span>{value}</span>
      <SyncStatusIcon
        resourceName={resourceName}
        fieldPath={fieldPath}
        contactName={contactName}
        fieldLabel={fieldLabel}
      />
    </div>
  );
};

EditableCell.displayName = 'EditableCell';

export { EditableCell };
export default EditableCell;
