/**
 * @fileoverview The Owner's segments (AD-45 rule 3), kept in Echo's settings store
 * (kind "segments") and saved on each change
 * @module Contacts/hooks/useSegments
 */

import { useCallback, useEffect, useRef, useState } from 'react';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import { MAX_RULES, MAX_SEGMENTS, type Segment, type SegmentsDoc } from '../model/segments';
import { logger } from '../../../shared/logger';

/** What useSegments returns */
export interface SegmentsState {
  segments: Segment[];
  /** Loaded from the server (or found unavailable) */
  ready: boolean;
  /** Adds or replaces one */
  save: (segment: Segment) => Promise<void>;
  remove: (id: string) => Promise<void>;
}

/**
 * A stored document's segments, the malformed ones dropped.
 * @param json - The document
 * @returns Segments
 */
export const readSegments = (json: unknown): Segment[] => {
  const doc = json as Partial<SegmentsDoc> | null;
  if (!doc || !Array.isArray(doc.segments)) {
    return [];
  }
  return doc.segments
    .filter(
      (s): s is Segment =>
        !!s &&
        typeof s.id === 'string' &&
        typeof s.name === 'string' &&
        (s.match === 'all' || s.match === 'any') &&
        Array.isArray(s.rules),
    )
    .map((s) => ({ ...s, rules: s.rules.slice(0, MAX_RULES) }))
    .slice(0, MAX_SEGMENTS);
};

/**
 * The Owner's segments.
 * @returns State and changes
 */
export const useSegments = (): SegmentsState => {
  const [segments, setSegments] = useState<Segment[]>([]);
  const [ready, setReady] = useState(false);
  const current = useRef<Segment[]>([]);

  useEffect(() => {
    let alive = true;
    backendApiClient
      .settings('segments')
      .then((doc) => {
        if (alive) {
          current.current = readSegments(doc.json);
          setSegments(current.current);
        }
      })
      .catch((e) =>
        logger.warn(
          { context: 'useSegments', metadata: { message: (e as Error).message } },
          'Segments unavailable',
        ),
      )
      .finally(() => alive && setReady(true));
    return () => {
      alive = false;
    };
  }, []);

  const store = useCallback(async (next: Segment[]) => {
    const before = current.current;
    current.current = next;
    setSegments(next);
    try {
      const doc: SegmentsDoc = { version: 1, segments: next };
      await backendApiClient.saveSettings('segments', doc);
    } catch (e) {
      current.current = before;
      setSegments(before);
      throw e;
    }
  }, []);

  const save = useCallback(
    async (segment: Segment) => {
      const list = current.current;
      const at = list.findIndex((s) => s.id === segment.id);
      if (at < 0 && list.length >= MAX_SEGMENTS) {
        throw new Error(`At most ${MAX_SEGMENTS} segments`);
      }
      await store(at < 0 ? [...list, segment] : list.map((s, i) => (i === at ? segment : s)));
    },
    [store],
  );

  const remove = useCallback(
    (id: string) => store(current.current.filter((s) => s.id !== id)),
    [store],
  );

  return { segments, ready, save, remove };
};
