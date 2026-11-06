/**
 * @fileoverview Public profiles by email hash (AD-46 rule 3): the look-up's state, the
 * contacts' hashes sent while it is on (new addresses as they appear), and the profiles
 * found mapped back to contacts
 * @module Contacts/hooks/useProfiles
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { Contact } from '../types/Contact';
import {
  backendApiClient,
  type GravatarProfile,
  type ProfilesStatus,
} from '../../../services/api/BackendApiClient';
import { emailHash } from '../model/profiles';
import { clean } from '../model/contactView';
import { logger } from '../../../shared/logger';

/** Between reads while look-ups are pending */
export const PROFILES_POLL_MS = 2 * 60_000;

/** Hashes per request (the server's limit) */
const HASHES_PER_REQUEST = 20000;

/** What useProfiles returns */
export interface ProfilesState {
  status: ProfilesStatus | null;
  /** Gravatar profiles per contact resource name */
  byContact: Map<string, GravatarProfile[]>;
  enable: () => Promise<void>;
  forget: () => Promise<void>;
}

/**
 * The public-profile look-up.
 * @param contacts - The Owner's contacts
 * @returns State and actions
 */
export const useProfiles = (contacts: Contact[]): ProfilesState => {
  const [status, setStatus] = useState<ProfilesStatus | null>(null);
  const [hashOf, setHashOf] = useState<Map<string, string>>(new Map());
  const sent = useRef(new Set<string>());
  // Bumped by enable and forget: a reply from before one is dropped
  const epoch = useRef(0);

  const read = useCallback(async () => {
    try {
      setStatus(await backendApiClient.profiles());
    } catch (e) {
      logger.warn(
        { context: 'useProfiles', metadata: { message: (e as Error).message } },
        'Profiles unavailable',
      );
    }
  }, []);

  useEffect(() => {
    void read();
  }, [read]);

  // Each address's hash, computed once
  const emails = useMemo(
    () =>
      [
        ...new Set(
          contacts.flatMap((c) =>
            (c.emailAddresses ?? []).map((e) => clean(e.value).toLowerCase()).filter(Boolean),
          ),
        ),
      ].sort(),
    [contacts],
  );
  useEffect(() => {
    let alive = true;
    const missing = emails.filter((e) => !hashOf.has(e));
    if (!missing.length) {
      return undefined;
    }
    void Promise.all(missing.map(async (e) => [e, await emailHash(e)] as const)).then((pairs) => {
      if (alive) {
        setHashOf((prev) => new Map([...prev, ...pairs]));
      }
    });
    return () => {
      alive = false;
    };
  }, [emails, hashOf]);

  // While on: the hashes not sent yet (all at first, then new addresses)
  const enabled = !!status?.enabled;
  useEffect(() => {
    if (!enabled) {
      return;
    }
    const fresh = [...new Set(hashOf.values())].filter((h) => !sent.current.has(h));
    if (!fresh.length) {
      return;
    }
    // The first send of a page, with every address hashed, holds them all: the server drops
    // what no contact has any more
    const complete =
      sent.current.size === 0 && hashOf.size >= emails.length && fresh.length <= HASHES_PER_REQUEST;
    fresh.forEach((h) => sent.current.add(h));
    const at = epoch.current;
    void (async () => {
      try {
        for (let i = 0; i < fresh.length; i += HASHES_PER_REQUEST) {
          const s = await backendApiClient.sendProfileHashes(
            fresh.slice(i, i + HASHES_PER_REQUEST),
            complete,
          );
          // A reply from before Turn off (or on) is stale
          if (epoch.current !== at) {
            return;
          }
          setStatus((prev) => ({ ...s, profiles: prev?.profiles }));
        }
      } catch (e) {
        fresh.forEach((h) => sent.current.delete(h));
        logger.warn(
          { context: 'useProfiles', metadata: { message: (e as Error).message } },
          'Sending the addresses failed',
        );
      }
    })();
  }, [enabled, hashOf, emails.length]);

  // Pending: read again now and then
  const pending = (status?.pending ?? 0) > 0;
  useEffect(() => {
    if (!enabled || !pending) {
      return undefined;
    }
    const t = window.setInterval(() => {
      if (document.visibilityState === 'visible') {
        void read();
      }
    }, PROFILES_POLL_MS);
    return () => window.clearInterval(t);
  }, [enabled, pending, read]);

  const byContact = useMemo(() => {
    const map = new Map<string, GravatarProfile[]>();
    const profiles = status?.profiles ?? {};
    contacts.forEach((c) => {
      const found = (c.emailAddresses ?? [])
        .map((e) => hashOf.get(clean(e.value).toLowerCase()))
        .map((h) => (h ? profiles[h] : undefined))
        .filter((p): p is GravatarProfile => !!p);
      if (found.length) {
        map.set(c.resourceName, found);
      }
    });
    return map;
  }, [contacts, hashOf, status?.profiles]);

  const enable = useCallback(async () => {
    epoch.current += 1;
    sent.current.clear();
    setStatus(await backendApiClient.enableProfiles());
  }, []);

  const forget = useCallback(async () => {
    epoch.current += 1;
    sent.current.clear();
    setStatus(await backendApiClient.forgetProfiles());
  }, []);

  return { status, byContact, enable, forget };
};
