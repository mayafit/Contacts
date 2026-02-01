/**
 * @fileoverview ColumnList component organizing columns by category with drag-drop
 * @module features/columnConfig/components
 */

import React from 'react';
import {
  DndContext,
  closestCenter,
  DragOverlay,
} from '@dnd-kit/core';
import {
  SortableContext,
  verticalListSortingStrategy,
} from '@dnd-kit/sortable';
import {
  List,
  ListSubheader,
  Box,
  Typography,
} from '@mui/material';
import ColumnItem from './ColumnItem';
import { useColumnDragDrop } from '../hooks/useColumnDragDrop';
import type { ColumnDefinition } from '../types';

/**
 * ColumnList component props
 */
export interface ColumnListProps {
  /** All available columns */
  columns: ColumnDefinition[];
  /** Array of visible column IDs */
  visibleColumns: string[];
  /** Column display order */
  columnOrder: string[];
  /** Handler for column toggle */
  onToggle: (columnId: string) => void;
}

/**
 * Column list with category organization and drag-drop reordering
 * Uses @dnd-kit for accessible drag-and-drop functionality
 */
const ColumnList: React.FC<ColumnListProps> = ({
  columns,
  visibleColumns,
  columnOrder,
  onToggle,
}) => {
  const { sensors, activeId, handleDragStart, handleDragEnd, handleDragCancel } =
    useColumnDragDrop();

  // Group columns by category
  const columnsByCategory = {
    Basic: columns.filter((col) => col.category === 'Basic'),
    Additional: columns.filter((col) => col.category === 'Additional'),
    Advanced: columns.filter((col) => col.category === 'Advanced'),
  };

  const categories: Array<'Basic' | 'Additional' | 'Advanced'> = [
    'Basic',
    'Additional',
    'Advanced',
  ];

  // Get the active column for drag overlay
  const activeColumn = activeId
    ? columns.find((col) => col.id === activeId)
    : null;

  return (
    <DndContext
      sensors={sensors}
      collisionDetection={closestCenter}
      onDragStart={handleDragStart}
      onDragEnd={(event) => handleDragEnd(event, columnOrder)}
      onDragCancel={handleDragCancel}
    >
      <Box>
        {categories.map((category) => {
          const categoryColumns = columnsByCategory[category];
          if (categoryColumns.length === 0) return null;

          return (
            <List
              key={category}
              subheader={
                <ListSubheader
                  component="div"
                  sx={{
                    backgroundColor: 'background.paper',
                    lineHeight: '32px',
                    fontWeight: 600,
                  }}
                >
                  {category}
                </ListSubheader>
              }
              sx={{ py: 0 }}
            >
              <SortableContext
                items={categoryColumns.map((col) => col.id)}
                strategy={verticalListSortingStrategy}
              >
                {categoryColumns.map((column) => (
                  <ColumnItem
                    key={column.id}
                    column={column}
                    isVisible={visibleColumns.includes(column.id)}
                    onToggle={onToggle}
                  />
                ))}
              </SortableContext>
            </List>
          );
        })}
      </Box>

      {/* Drag overlay for better visual feedback */}
      <DragOverlay>
        {activeColumn ? (
          <Box
            sx={{
              p: 1,
              backgroundColor: 'background.paper',
              boxShadow: 3,
              borderRadius: 1,
              opacity: 0.9,
            }}
          >
            <Typography>{activeColumn.label}</Typography>
          </Box>
        ) : null}
      </DragOverlay>
    </DndContext>
  );
};

export default ColumnList;
