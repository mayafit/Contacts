/**
 * @fileoverview Typed Redux hooks for Contacts entry
 * @module Contacts/types/hooks
 */

import { useDispatch, useSelector } from 'react-redux';
import type { AppDispatch, RootState } from './store';

/**
 * Typed useDispatch hook
 */
export const useAppDispatch = () => useDispatch<AppDispatch>();

/**
 * Typed useSelector hook
 */
export const useAppSelector = useSelector.withTypes<RootState>();
