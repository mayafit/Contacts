/**
 * @fileoverview The Owner's appearance settings in effect (AD-41 rules 3, 4, 5 and 7): the
 * browser's copy first (applied before the first paint), then the Owner's document unless
 * the browser holds changes not saved yet; every change applied at once (the text
 * variables, the pointer, the store) and saved after a pause, or when the page closes
 * @module Contacts/settings/SettingsProvider
 */

import React, {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import { pointerCss } from './pointer';
import { DEFAULT_UI, mergeUi, setCurrentUi, textVariables } from './uiSettings';
import type { UiSettings } from './uiSettings';

/** The browser's copy of the Owner's settings: { ui, dirty } (dirty: not saved to the account yet) */
export const STORAGE_KEY = 'settings.ui';

/** A burst of changes is saved this long after the last one */
export const SAVE_DELAY_MS = 600;

/** Where saving stands: saved (when), saving, or failed (why) */
export type SaveState =
  | { state: 'idle' }
  | { state: 'saving' }
  | { state: 'saved'; at: Date }
  | { state: 'failed'; message: string };

export interface UiContext {
  ui: UiSettings;
  /** Changes the settings (applied at once, saved after a pause) */
  update: (change: (current: UiSettings) => UiSettings) => void;
  /** Puts every setting back to its default (and forgets the saved document) */
  resetAll: () => void;
  save: SaveState;
}

const Context = createContext<UiContext>({
  ui: DEFAULT_UI,
  update: () => undefined,
  resetAll: () => undefined,
  save: { state: 'idle' },
});

/** The appearance settings in effect, and how to change them */
export const useSettings = (): UiContext => useContext(Context);

/** The appearance settings in effect (a component that draws with them re-renders when they change) */
export const useUi = (): UiSettings => useContext(Context).ui;

/** The browser's copy, and whether it holds changes not saved to the account */
const readKept = (): { ui: UiSettings; dirty: boolean } => {
  try {
    const kept = localStorage.getItem(STORAGE_KEY);
    if (!kept) {
      return { ui: DEFAULT_UI, dirty: false };
    }
    const doc = JSON.parse(kept) as { ui?: unknown; dirty?: unknown };
    // An older copy is the settings themselves
    return doc && typeof doc === 'object' && 'ui' in doc
      ? { ui: mergeUi(doc.ui), dirty: doc.dirty === true }
      : { ui: mergeUi(doc), dirty: false };
  } catch {
    return { ui: DEFAULT_UI, dirty: false };
  }
};

const keep = (ui: UiSettings, dirty: boolean) => {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ ui, dirty }));
  } catch {
    // Not kept in this browser: the Owner's document still is
  }
};

const POINTER_STYLE_ID = 'echo-pointer';

/** Applies what CSS carries: the text variables on the root, and the pointer's style sheet */
export const applyCss = (ui: UiSettings): void => {
  const root = document.documentElement;
  Object.entries(textVariables(ui.text)).forEach(([k, v]) => root.style.setProperty(k, v));
  let sheet = document.getElementById(POINTER_STYLE_ID) as HTMLStyleElement | null;
  const css = pointerCss(ui.pointer.size);
  if (!css) {
    sheet?.remove();
    return;
  }
  if (!sheet) {
    sheet = document.createElement('style');
    sheet.id = POINTER_STYLE_ID;
    document.head.appendChild(sheet);
  }
  sheet.textContent = css;
};

export interface SettingsProviderProps {
  /** Signed in: the Owner's document is loaded and saved */
  signedIn: boolean;
  children: React.ReactNode;
}

/**
 * Keeps the appearance settings in effect for everything inside it.
 * @param props - Component props
 * @returns Provider
 */
const SettingsProvider: React.FC<SettingsProviderProps> = ({ signedIn, children }) => {
  const initial = useRef<{ ui: UiSettings; dirty: boolean } | null>(null);
  if (!initial.current) {
    // Before the first paint: the store, the text variables and the pointer
    initial.current = readKept();
    setCurrentUi(initial.current.ui);
    applyCss(initial.current.ui);
  }
  const [ui, setUi] = useState<UiSettings>(initial.current.ui);
  const uiRef = useRef(ui);
  const [save, setSave] = useState<SaveState>({ state: 'idle' });
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  // What waits to be saved: the settings, or null for a reset
  const pending = useRef<{ next: UiSettings | null } | null>(null);
  const signedInRef = useRef(signedIn);
  signedInRef.current = signedIn;
  // Changes made before the Owner's document arrived win over it
  const changed = useRef(initial.current.dirty);

  useLayoutEffect(() => applyCss(ui), [ui]);

  /** Sends what waits now (keepalive: it may outlive the page) */
  const flush = useCallback((keepalive = false) => {
    const item = pending.current;
    if (timer.current) {
      clearTimeout(timer.current);
      timer.current = null;
    }
    if (!item || !signedInRef.current) {
      return;
    }
    pending.current = null;
    setSave({ state: 'saving' });
    (item.next
      ? backendApiClient.saveSettings('ui', item.next, keepalive)
      : backendApiClient.resetSettings('ui', keepalive)
    )
      .then(() => {
        // Saved: the browser's copy is no longer ahead (unless it changed meanwhile)
        if (!pending.current) {
          keep(uiRef.current, false);
        }
        setSave({ state: 'saved', at: new Date() });
      })
      .catch((e: Error) => setSave({ state: 'failed', message: e.message || 'Saving failed' }));
  }, []);

  const schedule = useCallback(
    (next: UiSettings | null) => {
      pending.current = { next };
      if (!signedInRef.current) {
        return;
      }
      if (timer.current) {
        clearTimeout(timer.current);
      }
      timer.current = setTimeout(() => flush(), SAVE_DELAY_MS);
    },
    [flush],
  );

  const apply = useCallback((next: UiSettings, dirty: boolean) => {
    uiRef.current = next;
    setCurrentUi(next);
    keep(next, dirty);
    setUi(next);
  }, []);

  useEffect(() => {
    if (!signedIn) {
      return undefined;
    }
    let alive = true;
    // Changes the browser holds and never saved go first (a failed save, a closed tab)
    if (changed.current) {
      schedule(uiRef.current);
    }
    backendApiClient
      .settings('ui')
      .then((doc) => {
        if (!alive || changed.current) {
          return;
        }
        if (!doc.updatedAt) {
          // Nothing saved for the Owner yet: the browser's copy stays, and becomes theirs
          if (JSON.stringify(uiRef.current) !== JSON.stringify(DEFAULT_UI)) {
            schedule(uiRef.current);
          }
          return;
        }
        apply(mergeUi(doc.json), false);
        setSave({ state: 'saved', at: new Date(doc.updatedAt) });
      })
      .catch(() => {
        // Not loaded: the browser's copy stays in effect
      });
    return () => {
      alive = false;
    };
  }, [signedIn, apply, schedule]);

  // Leaving the page: what waits is sent at once
  useEffect(() => {
    const leave = () => flush(true);
    window.addEventListener('pagehide', leave);
    return () => {
      window.removeEventListener('pagehide', leave);
      flush(true);
    };
  }, [flush]);

  const update = useCallback(
    (change: (current: UiSettings) => UiSettings) => {
      changed.current = true;
      const next = mergeUi(change(uiRef.current));
      apply(next, true);
      schedule(next);
    },
    [apply, schedule],
  );

  const resetAll = useCallback(() => {
    changed.current = true;
    apply(DEFAULT_UI, true);
    schedule(null);
  }, [apply, schedule]);

  const value = useMemo(() => ({ ui, update, resetAll, save }), [ui, update, resetAll, save]);
  return <Context.Provider value={value}>{children}</Context.Provider>;
};

export default SettingsProvider;
