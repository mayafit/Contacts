/**
 * @fileoverview EditableCell component for inline table cell editing
 * @module Contacts/components/EditableCell/EditableCell
 *
 * Story 3.4: inline cell editing. Story 4.4: edits in place without changing the
 * row's font, size or height; double-click opens the contact card instead.
 */

import React, { useState, useCallback, useEffect, useRef } from 'react';
import {
  Box,
  CircularProgress,
  IconButton,
  InputBase,
  Menu,
  MenuItem,
  Tooltip,
  Typography,
} from '@mui/material';
import { SyncStatusIcon } from '../SyncStatusIcon';
import { tokens } from '../../theme/contactsTheme';

/** How long a click waits for a second click before it starts editing */
export const DOUBLE_CLICK_WINDOW_MS = 220;

/** What an assist found for an empty cell */
export interface CellAssistResult {
  /** Put into the text box at once */
  value?: string;
  /** Offered for the Owner to pick */
  choices?: { value: string; detail?: string }[];
  /** Said in the menu (where it came from, or why nothing was found) */
  note?: string;
  /** A link in the menu (a permission to give) */
  action?: { label: string; onClick: () => void };
}

/** A button inside an empty text box that finds its value (AD-43) */
export interface CellAssist {
  /** Tooltip and accessible name */
  label: string;
  icon: React.ReactNode;
  run: () => Promise<CellAssistResult>;
}

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
  /** Shown inside the text box while it is empty */
  assist?: CellAssist;
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
  assist,
}) => {
  const initialText = editValue ?? value;
  const [isEditing, setIsEditing] = useState(false);
  const [text, setText] = useState(initialText);
  const isCancelledRef = useRef(false);
  const isSavedRef = useRef(false);
  const clickTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const inputRef = useRef<HTMLInputElement | null>(null);
  const [assisting, setAssisting] = useState(false);
  const [assistMenu, setAssistMenu] = useState<{
    anchor: HTMLElement;
    result: CellAssistResult;
  } | null>(null);
  // While the assist's menu is open the text box loses focus without being done
  const holdRef = useRef(false);
  // One per edit: an assist's answer is applied only within the edit that asked
  const sessionRef = useRef(0);
  // The assist's run, for the keyboard shortcut (declared before the key handler)
  const runAssistRef = useRef<(anchor: HTMLElement) => Promise<void>>(async () => undefined);
  const textRef = useRef(text);
  textRef.current = text;

  const startEditing = useCallback(() => {
    setText(initialText);
    isCancelledRef.current = false;
    isSavedRef.current = false;
    sessionRef.current += 1;
    holdRef.current = false;
    setAssistMenu(null);
    setAssisting(false);
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
      sessionRef.current += 1;
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
        sessionRef.current += 1;
        setText(initialText);
        setIsEditing(false);
      } else if (e.key === 'ArrowDown' && e.altKey && assist && !text.trim() && !assisting) {
        // The assist from the keyboard (Tab moves to the next cell)
        e.preventDefault();
        void runAssistRef.current(e.currentTarget as HTMLElement);
      }
    },
    [initialText, onNavigate, save, text, assist, assisting],
  );

  const handleBlur = useCallback(() => {
    if (!isCancelledRef.current && !holdRef.current) {
      save(text);
    }
  }, [save, text]);

  const runAssist = useCallback(
    async (anchor: HTMLElement) => {
      if (!assist) {
        return;
      }
      const session = sessionRef.current;
      setAssisting(true);
      let result: CellAssistResult;
      try {
        result = await assist.run();
      } catch (e) {
        result = { note: (e as Error).message };
      }
      // Editing ended (saved, cancelled) or began again meanwhile: the answer is dropped
      if (session !== sessionRef.current) {
        return;
      }
      setAssisting(false);
      // What the Owner typed meanwhile wins
      if (textRef.current.trim()) {
        return;
      }
      if (result.value) {
        setText(result.value);
      }
      // A value filled needs no menu; one opens for choices, or to say why nothing was
      if (result.choices?.length || (!result.value && (result.note || result.action))) {
        holdRef.current = true;
        setAssistMenu({ anchor, result });
      } else {
        inputRef.current?.focus();
      }
    },
    [assist],
  );

  runAssistRef.current = runAssist;

  const closeAssist = useCallback((value?: string) => {
    if (value) {
      setText(value);
    }
    setAssistMenu(null);
    // Back to the text box: Enter saves, Escape cancels, as when typing
    setTimeout(() => {
      holdRef.current = false;
      inputRef.current?.focus();
    }, 0);
  }, []);

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
          inputRef={inputRef}
          onFocus={(e) => e.target.select()}
          endAdornment={
            assist && (!text.trim() || assisting) ? (
              <Tooltip title={`${assist.label} (Alt+↓)`}>
                <span>
                  <IconButton
                    aria-label={assist.label}
                    size="small"
                    disabled={assisting}
                    // Keeps the text box focused, so the click is not a blur that saves
                    onMouseDown={(e) => e.preventDefault()}
                    onClick={(e) => void runAssist(e.currentTarget)}
                    sx={{ mr: 0.25, p: 0.25 }}
                  >
                    {assisting ? (
                      <CircularProgress size={14} aria-label="Looking it up" />
                    ) : (
                      assist.icon
                    )}
                  </IconButton>
                </span>
              </Tooltip>
            ) : undefined
          }
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
        <Menu
          open={!!assistMenu}
          anchorEl={assistMenu?.anchor}
          onClose={() => closeAssist()}
          disableRestoreFocus
        >
          {assistMenu?.result.choices?.map((c) => (
            <MenuItem
              key={c.value}
              onClick={() => closeAssist(c.value)}
              sx={{ display: 'block', maxWidth: 420 }}
            >
              <Typography variant="body2" sx={{ fontWeight: 600 }}>
                {c.value}
              </Typography>
              {c.detail && (
                <Typography
                  variant="caption"
                  component="div"
                  dir="auto"
                  sx={{ color: tokens.textMuted, whiteSpace: 'normal' }}
                >
                  {c.detail}
                </Typography>
              )}
            </MenuItem>
          ))}
          {assistMenu?.result.note && (
            <Typography
              role="status"
              variant="caption"
              component="div"
              dir="auto"
              sx={{ px: 2, py: 1, maxWidth: 420, color: tokens.textMuted, whiteSpace: 'normal' }}
            >
              {assistMenu.result.note}
            </Typography>
          )}
          {assistMenu?.result.action && (
            <MenuItem onClick={assistMenu.result.action.onClick}>
              <Typography variant="body2" sx={{ color: tokens.accent }}>
                {assistMenu.result.action.label}
              </Typography>
            </MenuItem>
          )}
          {!!assistMenu?.result.choices?.length && (
            <MenuItem onClick={() => closeAssist()}>
              <Typography variant="body2">None of these</Typography>
            </MenuItem>
          )}
        </Menu>
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
