/**
 * @fileoverview EditableCell component for inline table cell editing
 * @module Contacts/components/EditableCell/EditableCell
 *
 * Story 3.4: inline cell editing. Story 4.4: edits in place without changing the
 * row's font, size or height; double-click opens the contact card instead.
 */

import React, { useState, useCallback, useEffect, useRef } from 'react';
import { Box, InputBase } from '@mui/material';
import { SyncStatusIcon } from '../SyncStatusIcon';
import { tokens } from '../../theme/contactsTheme';

/** How long a click waits for a second click before it starts editing */
export const DOUBLE_CLICK_WINDOW_MS = 220;

/**
 * Props for EditableCell component
 */
export interface EditableCellProps {
  /** Contact resource name (e.g. "people/c12345") */
  resourceName: string;
  /** Field group the edit writes (for the sync status icon) */
  fieldPath: string;
  /** Text shown when not editing ('' shows the placeholder) */
  value: string;
  /** Text the editor starts with (defaults to value) */
  editValue?: string;
  /** Human-readable field label for aria-label (e.g. "Name", "Phone") */
  fieldLabel: string;
  /** Contact name for aria-label context */
  contactName: string;
  /** Muted text shown for an empty value */
  placeholder?: string;
  /** Element shown before the text, e.g. an avatar */
  leading?: React.ReactNode;
  /** Emphasize the text (used for the name column) */
  strong?: boolean;
  /** Called with the new text when the edit is committed and changed */
  onCommit: (text: string) => void;
  /** Called on double-click: open the full contact card */
  onOpenCard?: () => void;
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
 * - Click: edit in place, pre-populated
 * - Double-click: open the contact card (no edit)
 * - Enter: save + navigate down; Tab: save + navigate right; Escape: cancel
 * - Blur: save if value changed; unchanged values never dispatch
 */
const EditableCell: React.FC<EditableCellProps> = ({
  resourceName,
  fieldPath,
  value,
  editValue,
  fieldLabel,
  contactName,
  placeholder = '',
  leading,
  strong = false,
  onCommit,
  onOpenCard,
  onNavigate,
  autoEdit = false,
  onAutoEditConsumed,
}) => {
  const initialText = editValue ?? value;
  const [isEditing, setIsEditing] = useState(false);
  const [text, setText] = useState(initialText);
  const isCancelledRef = useRef(false);
  const isSavedRef = useRef(false);
  const clickTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const startEditing = useCallback(() => {
    setText(initialText);
    isCancelledRef.current = false;
    isSavedRef.current = false;
    setIsEditing(true);
  }, [initialText]);

  useEffect(() => {
    if (autoEdit && !isEditing) {
      startEditing();
      onAutoEditConsumed?.();
    }
  }, [autoEdit, isEditing, startEditing, onAutoEditConsumed]);

  useEffect(
    () => () => {
      if (clickTimerRef.current) {
        clearTimeout(clickTimerRef.current);
      }
    },
    [],
  );

  const save = useCallback(
    (newText: string) => {
      if (isSavedRef.current) {
        return;
      }
      isSavedRef.current = true;
      setIsEditing(false);
      if (newText.trim() !== initialText.trim()) {
        onCommit(newText);
      }
    },
    [initialText, onCommit],
  );

  const handleClick = useCallback(
    (e: React.MouseEvent) => {
      if (e.detail > 1) {
        return;
      }
      clickTimerRef.current = setTimeout(() => {
        clickTimerRef.current = null;
        startEditing();
      }, DOUBLE_CLICK_WINDOW_MS);
    },
    [startEditing],
  );

  const handleDoubleClick = useCallback(() => {
    if (clickTimerRef.current) {
      clearTimeout(clickTimerRef.current);
      clickTimerRef.current = null;
    }
    onOpenCard?.();
  }, [onOpenCard]);

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        save(text);
        onNavigate?.('down');
      } else if (e.key === 'Tab') {
        e.preventDefault();
        save(text);
        onNavigate?.('right');
      } else if (e.key === 'Escape') {
        e.preventDefault();
        isCancelledRef.current = true;
        setText(initialText);
        setIsEditing(false);
      }
    },
    [initialText, onNavigate, save, text],
  );

  const handleBlur = useCallback(() => {
    if (!isCancelledRef.current) {
      save(text);
    }
  }, [save, text]);

  const shell = {
    width: '100%',
    height: '100%',
    display: 'flex',
    alignItems: 'center',
    gap: 1.25,
    px: 1.5,
    minWidth: 0,
  } as const;

  if (isEditing) {
    return (
      <Box sx={shell}>
        {leading}
        <InputBase
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={handleKeyDown}
          onBlur={handleBlur}
          autoFocus
          onFocus={(e) => e.target.select()}
          inputProps={{
            'aria-label': `Edit ${fieldLabel} for ${contactName}`,
            role: 'textbox',
            dir: 'auto',
          }}
          sx={{
            flex: 1,
            minWidth: 0,
            font: 'inherit',
            fontWeight: strong ? 600 : 'inherit',
            color: tokens.text,
            borderRadius: '6px',
            boxShadow: `0 0 0 1.5px ${tokens.accent}`,
            bgcolor: tokens.page,
            '& input': { p: '4px 8px', height: 'auto' },
          }}
        />
      </Box>
    );
  }

  return (
    <Box
      sx={{
        ...shell,
        cursor: 'text',
      }}
      onClick={handleClick}
      onDoubleClick={handleDoubleClick}
      title={value || undefined}
    >
      {leading}
      <Box
        component="span"
        dir="auto"
        sx={{
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          whiteSpace: 'nowrap',
          minWidth: 0,
          fontWeight: strong ? 600 : 400,
          color: value ? tokens.text : tokens.textMuted,
          fontStyle: value ? 'normal' : 'italic',
        }}
      >
        {value || placeholder}
      </Box>
      <SyncStatusIcon
        resourceName={resourceName}
        fieldPath={fieldPath}
        contactName={contactName}
        fieldLabel={fieldLabel}
      />
    </Box>
  );
};

EditableCell.displayName = 'EditableCell';

export { EditableCell };
export default EditableCell;
