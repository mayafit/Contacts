/**
 * @fileoverview A message for the page's snackbar from anywhere in the workspace (a star in
 * a table row, a label chip in the card)
 * @module Contacts/components/common/notify
 */

type Listener = (message: string) => void;

const listeners = new Set<Listener>();

/**
 * Shows a message in the workspace's snackbar.
 * @param message - The message
 */
export const notify = (message: string): void => listeners.forEach((l) => l(message));

/**
 * Receives the messages (the workspace page shows them).
 * @param listener - Called with each message
 * @returns Unsubscribes
 */
export const onNotify = (listener: Listener): (() => void) => {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
};
