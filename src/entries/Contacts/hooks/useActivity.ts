/**
 * @fileoverview Loads the Gmail / Calendar activity snapshot and follows its scans
 * @module Contacts/hooks/useActivity
 */

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  backendApiClient,
  type ActivityStatusResponse,
  type PersonActivity,
  type ThreadState,
} from '../../../services/api/BackendApiClient';
import { logger } from '../../../shared/logger';

/** Snapshots older than this are brought up to date when the app opens */
const STALE_AFTER_MS = 6 * 60 * 60 * 1000;

/** Poll interval while a scan runs */
const POLL_MS = 3000;

/** Longest wait between polls after errors */
const MAX_POLL_MS = 30000;

/** What useActivity returns */
export interface ActivityState {
  /** Snapshot state, null until loaded (or when activity is unavailable) */
  status: ActivityStatusResponse | null;
  /** Activity per address, null until there is data */
  people: Record<string, PersonActivity> | null;
  /** Recent email threads (replies owed / awaited) */
  threads: ThreadState[];
  /** Gmail or Calendar granted */
  connected: boolean;
  /** Last request error */
  error: string | null;
  /** Start Google's consent for Gmail & Calendar (leaves the page) */
  connect: () => void;
  /** Bring the data up to date */
  refresh: (full?: boolean) => Promise<void>;
  /** Read the stored data again (no new scan) */
  reload: () => Promise<void>;
  /** Delete the stored activity */
  forget: () => Promise<void>;
}

/**
 * Activity snapshot for the signed-in user: loads it, starts a scan when it is
 * missing or stale, and polls while a scan runs.
 * @returns Activity state and actions
 */
export const useActivity = (): ActivityState => {
  const [status, setStatus] = useState<ActivityStatusResponse | null>(null);
  const [people, setPeople] = useState<Record<string, PersonActivity> | null>(null);
  const [threads, setThreads] = useState<ThreadState[]>([]);
  const [error, setError] = useState<string | null>(null);
  const pollTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const alive = useRef(true);

  const loadData = useCallback(async () => {
    const full = await backendApiClient.getActivity();
    if (!alive.current) {
      return;
    }
    setStatus(full);
    setPeople(full.peopleCount > 0 || full.status === 'ready' ? full.people : null);
    setThreads(full.threads ?? []);
  }, []);

  const poll = useCallback(
    (delay = POLL_MS) => {
      if (pollTimer.current) {
        clearTimeout(pollTimer.current);
      }
      pollTimer.current = setTimeout(async () => {
        try {
          const next = await backendApiClient.getActivityStatus();
          if (!alive.current) {
            return;
          }
          setError(null);
          setStatus(next);
          if (next.status === 'running') {
            poll();
          } else {
            await loadData();
          }
        } catch (e) {
          // A failed poll (network blip, deploy) must not leave the scan looking
          // stuck: keep asking, more slowly
          if (alive.current) {
            setError((e as Error).message);
            poll(Math.min(delay * 2, MAX_POLL_MS));
          }
        }
      }, delay);
    },
    [loadData],
  );

  const refresh = useCallback(
    async (full = false) => {
      try {
        setError(null);
        const next = await backendApiClient.refreshActivity(full);
        setStatus(next);
        if (next.status === 'running') {
          poll();
        }
      } catch (e) {
        setError((e as Error).message);
      }
    },
    [poll],
  );

  const forget = useCallback(async () => {
    try {
      setStatus(await backendApiClient.forgetActivity());
      setPeople(null);
    } catch (e) {
      setError((e as Error).message);
    }
  }, []);

  useEffect(() => {
    alive.current = true;
    (async () => {
      try {
        const current = await backendApiClient.getActivityStatus();
        if (!alive.current) {
          return;
        }
        setStatus(current);
        const connected = current.connected.gmail || current.connected.calendar;
        if (current.peopleCount > 0) {
          await loadData();
        }
        if (current.status === 'running') {
          poll();
        } else if (
          connected &&
          (current.status === 'none' ||
            !current.updatedAt ||
            Date.now() - Date.parse(current.updatedAt) > STALE_AFTER_MS)
        ) {
          await refresh();
        } else if (connected && current.status === 'ready' && current.hasDirectory === false) {
          // Snapshot made before names were collected: one full scan builds them
          await refresh(true);
        }
      } catch (e) {
        // Activity is optional; the rest of the app works without it
        logger.warn(
          { context: 'useActivity', metadata: { message: (e as Error).message } },
          'Activity unavailable',
        );
      }
    })();
    return () => {
      alive.current = false;
      if (pollTimer.current) {
        clearTimeout(pollTimer.current);
      }
    };
    // Load once per mount
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return {
    status,
    people,
    threads,
    connected: !!status && (status.connected.gmail || status.connected.calendar),
    error,
    connect: () => backendApiClient.connectActivity(),
    refresh,
    reload: loadData,
    forget,
  };
};
