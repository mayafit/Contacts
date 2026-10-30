/**
 * @fileoverview The Owner's appearance settings in effect (AD-41 rules 3, 4, 5 and 7): the
 * browser's copy first, then the Owner's document; every change applied at once (the
 * text variables, the pointer, the store) and saved after a pause
 * @module Contacts/settings/SettingsProvider
 */

import React, {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import { pointerCss } from './pointer';
import { DEFAULT_UI, currentUi, mergeUi, setCurrentUi, textVariables } from './uiSettings';
import type { UiSettings } from './uiSettings';

/** The browser's copy of the Owner's settings */
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

const readKept = (): UiSettings => {
  try {
    const kept = localStorage.getItem(STORAGE_KEY);
    return kept ? mergeUi(JSON.parse(kept)) : DEFAULT_UI;
  } catch {
    return DEFAULT_UI;
  }
};

const keep = (ui: UiSettings | null) => {
  try {
    if (ui) {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(ui));
    } else {
      localStorage.removeItem(STORAGE_KEY);
    }
  } catch {
    // Not kept in this browser: the Owner's document still is
  }
};

const POINTER_STYLE_ID = 'echo-pointer';

/** Applies what CSS carries: the text variables on the root, and the pointer's style sheet */
const applyCss = (ui: UiSettings) => {
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
  const [ui, setUi] = useState<UiSettings>(() => {
    const kept = readKept();
    setCurrentUi(kept);
    return kept;
  });
  const [save, setSave] = useState<SaveState>({ state: 'idle' });
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  // Changes made before the Owner's document arrived win over it
  const changed = useRef(false);

  const apply = useCallback((next: UiSettings) => {
    setCurrentUi(next);
    setUi(next);
    keep(next);
  }, []);

  useEffect(() => applyCss(ui), [ui]);

  const schedule = useCallback(
    (next: UiSettings | null) => {
      if (!signedIn) {
        return;
      }
      if (timer.current) {
        clearTimeout(timer.current);
      }
      timer.current = setTimeout(() => {
        setSave({ state: 'saving' });
        (next ? backendApiClient.saveSettings('ui', next) : backendApiClient.resetSettings('ui'))
          .then(() => setSave({ state: 'saved', at: new Date() }))
          .catch((e: Error) => setSave({ state: 'failed', message: e.message || 'Saving failed' }));
      }, SAVE_DELAY_MS);
    },
    [signedIn],
  );

  useEffect(() => {
    if (!signedIn) {
      return undefined;
    }
    let alive = true;
    backendApiClient
      .settings('ui')
      .then((doc) => {
        if (!alive || changed.current) {
          return;
        }
        if (!doc.updatedAt) {
          // Nothing saved for the Owner yet: the browser's copy stays, and becomes theirs
          if (JSON.stringify(currentUi()) !== JSON.stringify(DEFAULT_UI)) {
            schedule(currentUi());
          }
          return;
        }
        apply(mergeUi(doc.json));
        setSave({ state: 'saved', at: new Date(doc.updatedAt) });
      })
      .catch(() => {
        // Not loaded: the browser's copy stays in effect
      });
    return () => {
      alive = false;
    };
  }, [signedIn, apply, schedule]);

  useEffect(
    () => () => {
      if (timer.current) {
        clearTimeout(timer.current);
      }
    },
    [],
  );

  const update = useCallback(
    (change: (current: UiSettings) => UiSettings) => {
      changed.current = true;
      setUi((prev) => {
        const next = mergeUi(change(prev));
        setCurrentUi(next);
        keep(next);
        schedule(next);
        return next;
      });
    },
    [schedule],
  );

  const resetAll = useCallback(() => {
    changed.current = true;
    setCurrentUi(DEFAULT_UI);
    setUi(DEFAULT_UI);
    keep(null);
    schedule(null);
  }, [schedule]);

  const value = useMemo(() => ({ ui, update, resetAll, save }), [ui, update, resetAll, save]);
  return <Context.Provider value={value}>{children}</Context.Provider>;
};

export default SettingsProvider;
