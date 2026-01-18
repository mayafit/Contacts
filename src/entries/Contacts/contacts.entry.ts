/**
 * @fileoverview Contacts entry point for dynamic module loading
 * @module Contacts/contacts.entry
 */

import { getGlobals } from '../../GLOBALS';
import authReducer from './redux/slices/auth/authSlice';

/**
 * Initialize Contacts entry module
 * Registers auth reducer with dynamic reducer injection
 */
export const initContactsEntry = () => {
  getGlobals().addDynamicReducer({
    reducerName: 'contacts',
    reducer: {
      auth: authReducer,
    },
  });
};

/**
 * Destroy Contacts entry module
 * Cleanup function for module teardown
 */
export const destroyContactsEntry = () => {
  // Cleanup logic if needed
};
