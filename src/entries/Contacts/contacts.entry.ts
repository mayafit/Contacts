/**
 * @fileoverview Contacts entry point for dynamic module loading
 * @module Contacts/contacts.entry
 */

import { combineReducers } from '@reduxjs/toolkit';
import { getGlobals } from '../../GLOBALS';
import authReducer from './redux/slices/auth/authSlice';
import contactsReducer from './redux/slices/contacts/contactsSlice';

/**
 * Initialize Contacts entry module
 * Registers auth and contacts reducers with dynamic reducer injection
 * Updated for Story 2.2: Added normalized contacts state management
 */
export const initContactsEntry = () => {
  getGlobals().addDynamicReducer({
    reducerName: 'contacts',
    reducer: combineReducers({
      auth: authReducer,
      contacts: contactsReducer,
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
