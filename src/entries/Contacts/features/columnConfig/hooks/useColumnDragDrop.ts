/**
 * @fileoverview Custom hook for column drag-and-drop functionality
 * @module features/columnConfig/hooks
 */

import { useState, useCallback } from 'react';
import type { DragEndEvent } from '@dnd-kit/core';
import {
  KeyboardSensor,
  PointerSensor,
  useSensor,
  useSensors,
} from '@dnd-kit/core';
import { sortableKeyboardCoordinates } from '@dnd-kit/sortable';
import { useAppDispatch } from '../../../types/hooks';
import { reorderColumns } from '../../../redux/slices/ui/uiSlice';

/**
 * Custom hook for managing column drag-and-drop
 * Provides sensors configuration and drag end handler
 * Implements keyboard accessibility for drag operations
 */
export const useColumnDragDrop = () => {
  const dispatch = useAppDispatch();
  const [activeId, setActiveId] = useState<string | null>(null);

  /**
   * Configure sensors for drag interactions
   * Pointer sensor for mouse/touch
   * Keyboard sensor for accessibility (Space/Enter to activate)
   */
  const sensors = useSensors(
    useSensor(PointerSensor, {
      activationConstraint: {
        distance: 8, // Minimum drag distance to activate
      },
    }),
    useSensor(KeyboardSensor, {
      coordinateGetter: sortableKeyboardCoordinates,
    })
  );

  /**
   * Handle drag start
   */
  const handleDragStart = useCallback((event: DragEndEvent) => {
    setActiveId(event.active.id as string);
  }, []);

  /**
   * Handle drag end - calculate new order and dispatch Redux action
   */
  const handleDragEnd = useCallback(
    (event: DragEndEvent, columnOrder: string[]) => {
      const { active, over } = event;

      setActiveId(null);

      if (!over || active.id === over.id) {
        return;
      }

      const oldIndex = columnOrder.indexOf(active.id as string);
      const newIndex = columnOrder.indexOf(over.id as string);

      if (oldIndex !== -1 && newIndex !== -1) {
        // Dispatch reorderColumns action to Redux
        dispatch(reorderColumns({ oldIndex, newIndex }));
      }
    },
    [dispatch]
  );

  /**
   * Handle drag cancel
   */
  const handleDragCancel = useCallback(() => {
    setActiveId(null);
  }, []);

  return {
    sensors,
    activeId,
    handleDragStart,
    handleDragEnd,
    handleDragCancel,
  };
};
