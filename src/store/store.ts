/**
 * @fileoverview Redux store type definitions
 * @module store/store
 */

import type { ThunkDispatch, UnknownAction } from '@reduxjs/toolkit'

/**
 * Root state type
 * Note: Store is configured dynamically via addDynamicReducer
 */
export type RootState = {
  contacts?: {
    auth?: import('../entries/Contacts/types').AuthState
  }
  [key: string]: any
}

/**
 * App dispatch type with thunk support
 */
export type AppDispatch = ThunkDispatch<RootState, undefined, UnknownAction>
