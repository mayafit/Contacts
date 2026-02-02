/**
 * @fileoverview Contacts entry point for dynamic module loading
 * @module Contacts/contacts.entry
 */

import { combineReducers } from '@reduxjs/toolkit';
import { getGlobals } from '../../GLOBALS';
import authReducer from './redux/slices/auth/authSlice';
import contactsReducer from './redux/slices/contacts/contactsSlice';
import uiReducer from './redux/slices/ui/uiSlice';

/**
 * Initialize Contacts entry module
 * Registers auth, contacts, and ui reducers with dynamic reducer injection
 * Updated for Story 2.2: Added normalized contacts state management
 * Updated for Story 2.6: Added UI slice for column configuration
 * Updated for Story 2.7: Added localStorage persistence for column configuration
 * Note: Column config persistence middleware is registered in PluginWrapper.tsx
 *       before store initialization to ensure it's active when the store is created
 */
export const initContactsEntry = () => {
  // Register reducers
  getGlobals().addDynamicReducer({
    reducerName: 'contacts',
    reducer: combineReducers({
      auth: authReducer,
      contacts: contactsReducer,
      ui: uiReducer,
    }),
  });
};

/**
 * Destroy Contacts entry module
 * Cleanup function for module teardown
 */
export const destroyContactsEntry = () => {
  // Cleanup logic if needed
};
