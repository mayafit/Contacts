/**
 * @fileoverview Keeps favourites and labels in step with Google (AD-44 rule 3): a check a
 * minute after the contacts load, every 5 minutes in view, and on coming back
 * @module Contacts/hooks/useLabelSync
 */

import { useEffect, useRef } from 'react';
import type { Contact } from '../types/Contact';
import { useAppDispatch } from '../types/hooks';
import { checkLabels } from '../redux/thunks/labelThunks';
import { logger } from '../../../shared/logger';

/** First check after the contacts load */
export const FIRST_CHECK_MS = 60_000;

/** Between checks while the page is in view */
export const CHECK_EVERY_MS = 5 * 60_000;

/** Coming back checks when the last check is at least this old */
export const RECHECK_AFTER_MS = 60_000;

/**
 * Checks Google's favourites and labels while the contacts are loaded.
 * @param contacts - The store's contacts ([] while loading: no check)
 */
export const useLabelSync = (contacts: Contact[]): void => {
  const dispatch = useAppDispatch();
  const contactsRef = useRef(contacts);
  contactsRef.current = contacts;
  const loaded = contacts.length > 0;

  useEffect(() => {
    if (!loaded) {
      return undefined;
    }
    let last = Date.now();
    let running = false;
    const check = async () => {
      if (running || document.visibilityState !== 'visible') {
        return;
      }
      running = true;
      last = Date.now();
      try {
        await checkLabels(dispatch, contactsRef.current);
      } catch (e) {
        // The next check tries again
        logger.warn(
          { context: 'useLabelSync', metadata: { message: (e as Error).message } },
          'Label check failed',
        );
      } finally {
        running = false;
      }
    };
    const first = window.setTimeout(() => void check(), FIRST_CHECK_MS);
    const every = window.setInterval(() => void check(), CHECK_EVERY_MS);
    const onVisible = () => {
      if (document.visibilityState === 'visible' && Date.now() - last >= RECHECK_AFTER_MS) {
        void check();
      }
    };
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      window.clearTimeout(first);
      window.clearInterval(every);
      document.removeEventListener('visibilitychange', onVisible);
    };
  }, [loaded, dispatch]);
};
