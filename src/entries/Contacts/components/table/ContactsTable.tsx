/**
 * @fileoverview Virtualized, sortable contacts table with inline editing and an
 * alphabet rail.
 * @module Contacts/components/table/ContactsTable
 *
 * Story 2.4 (virtualization) and 3.4 (inline edit), redesigned in Story 4.3/4.4.
 */

import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useSelector } from 'react-redux';
import { useVirtualizer } from '@tanstack/react-virtual';
import { Box, ButtonBase, Checkbox, Chip } from '@mui/material';
import { ArrowDownward, ArrowUpward } from '@mui/icons-material';
import type { Contact, ContactGroup } from '../../types/Contact';
import { selectColumnConfig } from '../../redux/slices/ui/uiSlice';
import { executeFieldUpdate } from '../../redux/thunks/syncThunks';
import { useAppDispatch } from '../../types/hooks';
import type { ActivityIndex } from '../../model/activity';
import { AVAILABLE_COLUMNS } from '../../features/columnConfig/columnDefinitions';
import type { ColumnDefinition } from '../../features/columnConfig/types';
import { EditableCell } from '../EditableCell';
import ContactAvatar from '../common/ContactAvatar';
import AlphabetRail, { type RailEntry } from '../common/AlphabetRail';
import {
  compareByName,
  getDisplayName,
  getLabelNames,
  indexLetterOf,
  nameCollator,
} from '../../model/contactView';
import { tokens } from '../../theme/contactsTheme';

/** Row height in px */
export const ROW_HEIGHT = 52;
const HEADER_HEIGHT = 40;

/** Sort state */
interface SortState {
  /** Column being sorted */
  columnId: string;
  /** Descending order */
  desc: boolean;
}

/** Props for ContactsTable */
export interface ContactsTableProps {
  /** Contacts to show (already filtered) */
  contacts: Contact[];
  /** Labels by resource name */
  groups: Record<string, ContactGroup>;
  /** Contact whose card is open, highlighted */
  selectedResourceName?: string | null;
  /** Open the card for a contact */
  onOpenCard: (resourceName: string) => void;
  /**
   * Changes when the search or filter changes; the list then scrolls to the
   * top. Deliberately not the contacts array, which changes on every save.
   */
  scrollResetKey?: string;
  /** Checked rows (resource names) */
  checked?: Set<string>;
  /** Replace the checked set */
  onCheckedChange?: (next: Set<string>) => void;
  /** Email / meeting activity per contact */
  activity?: ActivityIndex;
}

/** Width of the checkbox column */
const CHECK_WIDTH = 44;

const PLACEHOLDERS: Record<string, string> = {
  phoneticName: 'Add phonetic',
  phoneNumbers: 'Add phone',
  emailAddresses: 'Add email',
  organizations: 'Add organization',
  jobTitle: 'Add title',
  nickname: 'Add nickname',
};

/**
 * Contacts table.
 * @param props - Component props
 * @returns Table element
 */
const ContactsTable: React.FC<ContactsTableProps> = ({
  contacts,
  groups,
  selectedResourceName,
  onOpenCard,
  scrollResetKey,
  checked,
  onCheckedChange,
  activity,
}) => {
  const dispatch = useAppDispatch();
  const lastChecked = useRef<string | null>(null);
  const columnConfig = useSelector(selectColumnConfig);
  const scrollRef = useRef<HTMLDivElement>(null);
  const [sort, setSort] = useState<SortState>({ columnId: 'displayName', desc: false });
  const [focusedCell, setFocusedCell] = useState<{ rowIndex: number; colIndex: number } | null>(
    null,
  );
  const [firstVisible, setFirstVisible] = useState(0);
  const context = useMemo(() => ({ groups, activity }), [groups, activity]);

  const columns = useMemo<ColumnDefinition[]>(() => {
    const visible = AVAILABLE_COLUMNS.filter((c) => columnConfig.visibleColumns.includes(c.id));
    const ordered = visible.sort(
      (a, b) => columnConfig.columnOrder.indexOf(a.id) - columnConfig.columnOrder.indexOf(b.id),
    );
    return ordered.length > 0 ? ordered : AVAILABLE_COLUMNS.filter((c) => c.defaultVisible);
  }, [columnConfig]);

  const sorted = useMemo(() => {
    const column = AVAILABLE_COLUMNS.find((c) => c.id === sort.columnId);
    const rows = [...contacts];
    if (!column || column.id === 'displayName') {
      rows.sort(compareByName);
    } else {
      const key = column.sortKey ?? column.accessor;
      const keyed = rows.map((c) => ({ c, k: key(c, context) ?? '' }));
      keyed.sort((a, b) => {
        // Empty values always last, whatever the direction
        if (!a.k !== !b.k) {
          return a.k ? -1 : 1;
        }
        return nameCollator.compare(a.k, b.k) || compareByName(a.c, b.c);
      });
      rows.splice(0, rows.length, ...keyed.map((x) => x.c));
    }
    if (sort.desc) {
      rows.reverse();
    }
    return rows;
  }, [contacts, sort, context]);

  const railEntries = useMemo<RailEntry[]>(() => {
    if (sort.columnId !== 'displayName') {
      return [];
    }
    const seen = new Map<string, number>();
    sorted.forEach((c, i) => {
      const letter = indexLetterOf(getDisplayName(c));
      if (!seen.has(letter)) {
        seen.set(letter, i);
      }
    });
    return Array.from(seen.entries()).map(([letter, index]) => ({ letter, index }));
  }, [sorted, sort.columnId]);

  const virtualizer = useVirtualizer({
    count: sorted.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 12,
  });

  // Keep the rail's active letter in step with scrolling
  useEffect(() => {
    const el = scrollRef.current;
    if (!el) {
      return undefined;
    }
    const onScroll = () => setFirstVisible(Math.floor(el.scrollTop / ROW_HEIGHT));
    el.addEventListener('scroll', onScroll, { passive: true });
    return () => el.removeEventListener('scroll', onScroll);
  }, []);

  // New filter/search: start from the top
  useEffect(() => {
    scrollRef.current?.scrollTo?.({ top: 0 });
    setFirstVisible(0);
  }, [scrollResetKey]);

  const activeLetter = useMemo(() => {
    if (railEntries.length === 0 || sorted.length === 0) {
      return null;
    }
    const row = sorted[Math.min(firstVisible, sorted.length - 1)];
    return indexLetterOf(getDisplayName(row));
  }, [firstVisible, railEntries, sorted]);

  const jumpTo = useCallback(
    (index: number) => virtualizer.scrollToIndex(index, { align: 'start' }),
    [virtualizer],
  );

  const editableColumnIndexes = useMemo(
    () => columns.map((c, i) => (c.edit ? i : -1)).filter((i) => i >= 0),
    [columns],
  );

  const navigate = useCallback(
    (rowIndex: number, colIndex: number, direction: 'right' | 'down') => {
      if (direction === 'down') {
        if (rowIndex + 1 < sorted.length) {
          virtualizer.scrollToIndex(rowIndex + 1);
          setFocusedCell({ rowIndex: rowIndex + 1, colIndex });
        }
        return;
      }
      const next = editableColumnIndexes.find((i) => i > colIndex);
      if (next !== undefined) {
        setFocusedCell({ rowIndex, colIndex: next });
      }
    },
    [editableColumnIndexes, sorted.length, virtualizer],
  );

  const consumeAutoEdit = useCallback(() => setFocusedCell(null), []);

  const commit = useCallback(
    (contact: Contact, column: ColumnDefinition, text: string) => {
      if (!column.edit) {
        return;
      }
      const { fieldPath } = column.edit;
      dispatch(
        executeFieldUpdate({
          resourceName: contact.resourceName,
          fieldPath,
          newValue: column.edit.apply(contact, text),
          oldValue: contact[fieldPath] ?? null,
        }),
      );
    },
    [dispatch],
  );

  const toggleSort = (columnId: string) =>
    setSort((s) =>
      s.columnId === columnId ? { columnId, desc: !s.desc } : { columnId, desc: false },
    );

  const selectable = !!checked && !!onCheckedChange;
  const gridTemplate =
    (selectable ? `${CHECK_WIDTH}px ` : '') +
    columns
      .map((c) => `minmax(${Math.round((c.width ?? 160) * 0.7)}px, ${c.width ?? 160}fr)`)
      .join(' ');
  const minWidth =
    columns.reduce((sum, c) => sum + Math.round((c.width ?? 160) * 0.7), 0) +
    (selectable ? CHECK_WIDTH : 0);

  const visibleChecked = selectable ? sorted.filter((c) => checked!.has(c.resourceName)).length : 0;
  const allChecked = sorted.length > 0 && visibleChecked === sorted.length;

  const toggleAll = () => {
    if (!selectable) {
      return;
    }
    const next = new Set(checked);
    if (allChecked) {
      sorted.forEach((c) => next.delete(c.resourceName));
    } else {
      sorted.forEach((c) => next.add(c.resourceName));
    }
    onCheckedChange!(next);
  };

  // Shift-click checks or unchecks the whole range from the last clicked row
  const toggleRow = (index: number, shift: boolean) => {
    if (!selectable) {
      return;
    }
    const next = new Set(checked);
    const target = !next.has(sorted[index].resourceName);
    // The anchor is remembered by identity: an index would point at a different
    // row after the list is searched, filtered or re-sorted.
    const anchor =
      shift && lastChecked.current
        ? sorted.findIndex((c) => c.resourceName === lastChecked.current)
        : -1;
    const from = anchor >= 0 ? Math.min(anchor, index) : index;
    const to = anchor >= 0 ? Math.max(anchor, index) : index;
    for (let i = from; i <= to; i += 1) {
      if (target) {
        next.add(sorted[i].resourceName);
      } else {
        next.delete(sorted[i].resourceName);
      }
    }
    lastChecked.current = sorted[index].resourceName;
    onCheckedChange!(next);
  };

  const renderCell = (
    contact: Contact,
    column: ColumnDefinition,
    rowIndex: number,
    colIndex: number,
  ) => {
    const name = getDisplayName(contact);
    if (column.id === 'labels') {
      const labels = getLabelNames(contact, groups);
      return (
        <Box
          sx={{
            display: 'flex',
            gap: 0.5,
            px: 1.5,
            overflow: 'hidden',
            alignItems: 'center',
            height: '100%',
          }}
        >
          {labels.slice(0, 2).map((l) => (
            <Chip
              key={l}
              label={l}
              size="small"
              sx={{ height: 22, fontSize: 12, bgcolor: tokens.raised }}
            />
          ))}
          {labels.length > 2 && (
            <Box component="span" sx={{ color: tokens.textMuted, fontSize: 12 }}>
              +{labels.length - 2}
            </Box>
          )}
        </Box>
      );
    }
    const value = column.accessor(contact, context) ?? '';
    if (!column.edit) {
      return (
        <Box
          dir="auto"
          title={value}
          sx={{
            px: 1.5,
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
            color: value ? tokens.textSecondary : tokens.textMuted,
            lineHeight: `${ROW_HEIGHT}px`,
          }}
        >
          {value || '—'}
        </Box>
      );
    }
    return (
      <EditableCell
        resourceName={contact.resourceName}
        fieldPath={column.edit.fieldPath}
        value={value}
        editValue={column.edit.value(contact)}
        fieldLabel={column.label}
        contactName={name}
        placeholder={PLACEHOLDERS[column.id] ?? ''}
        strong={column.id === 'displayName'}
        leading={column.id === 'displayName' ? <ContactAvatar contact={contact} /> : undefined}
        onCommit={(text) => commit(contact, column, text)}
        onOpenCard={() => onOpenCard(contact.resourceName)}
        onNavigate={(dir) => navigate(rowIndex, colIndex, dir)}
        autoEdit={focusedCell?.rowIndex === rowIndex && focusedCell?.colIndex === colIndex}
        onAutoEditConsumed={consumeAutoEdit}
      />
    );
  };

  return (
    <Box sx={{ display: 'flex', flex: 1, minHeight: 0 }} data-testid="contacts-table">
      <Box
        ref={scrollRef}
        sx={{ flex: 1, minWidth: 0, overflow: 'auto', position: 'relative' }}
        role="grid"
        aria-rowcount={sorted.length}
      >
        <Box sx={{ minWidth }}>
          {/* Header */}
          <Box
            role="row"
            sx={{
              display: 'grid',
              gridTemplateColumns: gridTemplate,
              position: 'sticky',
              top: 0,
              zIndex: 2,
              height: HEADER_HEIGHT,
              bgcolor: tokens.raised,
              borderBottom: `1px solid ${tokens.border}`,
            }}
          >
            {selectable && (
              <Box sx={{ display: 'grid', placeItems: 'center' }}>
                <Checkbox
                  size="small"
                  checked={allChecked}
                  indeterminate={visibleChecked > 0 && !allChecked}
                  onChange={toggleAll}
                  inputProps={{
                    'aria-label': allChecked ? 'Uncheck all shown' : 'Check all shown',
                  }}
                />
              </Box>
            )}
            {columns.map((column) => {
              const active = sort.columnId === column.id;
              return (
                <ButtonBase
                  key={column.id}
                  role="columnheader"
                  aria-sort={active ? (sort.desc ? 'descending' : 'ascending') : 'none'}
                  onClick={() => toggleSort(column.id)}
                  sx={{
                    justifyContent: 'flex-start',
                    gap: 0.5,
                    px: 1.5,
                    fontSize: 12,
                    fontWeight: 600,
                    letterSpacing: '0.03em',
                    textTransform: 'uppercase',
                    color: active ? tokens.text : tokens.textSecondary,
                    '&:hover': { color: tokens.text },
                  }}
                >
                  {column.label}
                  {active &&
                    (sort.desc ? (
                      <ArrowDownward sx={{ fontSize: 14 }} />
                    ) : (
                      <ArrowUpward sx={{ fontSize: 14 }} />
                    ))}
                </ButtonBase>
              );
            })}
          </Box>

          {/* Body */}
          <Box sx={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
            {virtualizer.getVirtualItems().map((item) => {
              const contact = sorted[item.index];
              const selected = contact.resourceName === selectedResourceName;
              return (
                <Box
                  key={contact.resourceName}
                  role="row"
                  aria-rowindex={item.index + 1}
                  aria-selected={selected}
                  onDoubleClick={() => onOpenCard(contact.resourceName)}
                  // A double-click opens the card; without this it also selects a word
                  onMouseDown={(e) => e.detail > 1 && e.preventDefault()}
                  sx={{
                    position: 'absolute',
                    top: 0,
                    left: 0,
                    width: '100%',
                    height: ROW_HEIGHT,
                    transform: `translateY(${item.start}px)`,
                    display: 'grid',
                    gridTemplateColumns: gridTemplate,
                    alignItems: 'stretch',
                    borderBottom: `1px solid ${tokens.border}`,
                    bgcolor: selected
                      ? 'rgba(57,135,229,0.12)'
                      : checked?.has(contact.resourceName)
                        ? 'rgba(57,135,229,0.07)'
                        : 'transparent',
                    boxShadow: selected ? `inset 3px 0 0 ${tokens.accent}` : 'none',
                    '&:hover': { bgcolor: selected ? 'rgba(57,135,229,0.16)' : tokens.hover },
                  }}
                >
                  {selectable && (
                    <Box
                      role="gridcell"
                      sx={{ display: 'grid', placeItems: 'center', height: ROW_HEIGHT }}
                      onDoubleClick={(e) => e.stopPropagation()}
                    >
                      <Checkbox
                        size="small"
                        checked={checked!.has(contact.resourceName)}
                        onClick={(e) => toggleRow(item.index, e.shiftKey)}
                        inputProps={{ 'aria-label': `Check ${getDisplayName(contact)}` }}
                      />
                    </Box>
                  )}
                  {columns.map((column, colIndex) => (
                    <Box key={column.id} role="gridcell" sx={{ minWidth: 0, height: ROW_HEIGHT }}>
                      {renderCell(contact, column, item.index, colIndex)}
                    </Box>
                  ))}
                </Box>
              );
            })}
          </Box>
        </Box>
      </Box>
      <AlphabetRail entries={railEntries} activeLetter={activeLetter} onJump={jumpTo} />
    </Box>
  );
};

export default ContactsTable;
