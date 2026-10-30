/**
 * @fileoverview The contact card's two fills (AD-43): the phonetic name from the Hebrew
 * name, and the email from the mail. Both change the draft only.
 * @module Contacts/components/card/useCardAutofill
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { Contact } from '../../types/Contact';
import { useAppSelector } from '../../types/hooks';
import { selectAllContacts } from '../../redux/slices/contacts/selectors';
import { backendApiClient } from '../../../../services/api/BackendApiClient';
import {
  buildDictionary,
  findTwins,
  PHONETIC_PARTS,
  planFill,
  wordsToAsk,
  type PhoneticPart,
  type SpellingDictionary,
} from '../../model/phonetic';
import {
  autoPick,
  choicesFor,
  emailType,
  nameVariants,
  type EmailChoice,
} from '../../model/emailFind';
import { clean } from '../../model/contactView';
import { formatRelative } from '../../model/activity';

/** A line under a section, with an optional action */
export interface FillCaption {
  text: string;
  tone: 'info' | 'warn';
  action?: { label: string; onClick: () => void };
}

/** The candidates' menu */
export interface EmailMenu {
  anchor: HTMLElement;
  /** The empty row clicked, or null for a new row */
  row: number | null;
  choices: EmailChoice[];
}

// Gemini's spellings, kept for the session (AD-43 rule 4)
const spellingCache: Record<string, string> = {};

// The dictionary of a contact list, built at the first click on it (rule 3)
const dictionaries = new WeakMap<Contact[], SpellingDictionary>();
const dictionaryFor = (contacts: Contact[]): SpellingDictionary => {
  let dict = dictionaries.get(contacts);
  if (!dict) {
    dict = buildDictionary(contacts);
    dictionaries.set(contacts, dict);
  }
  return dict;
};

/**
 * Where a filled address was seen.
 * @param c - Candidate
 * @returns "Found in 12 mails as “Dana Cohen”"
 */
const foundText = (c: EmailChoice): string =>
  [
    'Found',
    c.mails ? `in ${c.mails} ${c.mails === 1 ? 'mail' : 'mails'}` : 'in your mail',
    c.names[0] ? `as “${c.names[0]}”` : '',
  ]
    .filter(Boolean)
    .join(' ');

/** For tests: forget the session's spellings */
export const clearSpellingCache = (): void => {
  Object.keys(spellingCache).forEach((k) => delete spellingCache[k]);
};

/**
 * How a found address was seen.
 * @param c - Candidate
 * @returns "as “Dana Cohen” · 12 mails · last 3 weeks ago"
 */
export const describeChoice = (c: EmailChoice): string =>
  [
    c.names[0] ? `as “${c.names[0]}”` : '',
    c.mails ? `${c.mails} ${c.mails === 1 ? 'mail' : 'mails'}` : '',
    c.lastSeen ? `last ${formatRelative(c.lastSeen)}` : '',
    c.onContact ? `already on ${c.onContact}` : '',
  ]
    .filter(Boolean)
    .join(' · ');

/**
 * The card's fills.
 * @param draft - The card's draft
 * @param onChange - Replaces the draft
 * @returns State and actions for the form
 */
export const useCardAutofill = (draft: Contact, onChange: (draft: Contact) => void) => {
  const contacts = useAppSelector(selectAllContacts);
  // A click's answer is applied to the draft as it is then, not as it was clicked
  const draftRef = useRef(draft);
  draftRef.current = draft;
  const onChangeRef = useRef(onChange);
  onChangeRef.current = onChange;
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const [phoneticBusy, setPhoneticBusy] = useState(false);
  const [phoneticCaption, setPhoneticCaption] = useState<FillCaption | null>(null);
  const [emailBusy, setEmailBusy] = useState(false);
  const [emailCaption, setEmailCaption] = useState<FillCaption | null>(null);
  const [emailMenu, setEmailMenu] = useState<EmailMenu | null>(null);

  const name = draft.names?.[0] ?? {};
  const fillable = useMemo(
    () => new Set<PhoneticPart>(planFill(name, [], new Map()).map((p) => p.part)),
    [name],
  );
  const emailNames = useMemo(() => nameVariants(draft), [draft]);

  const fillPhonetic = useCallback(async () => {
    const dictionary = dictionaryFor(contacts);
    const id = draftRef.current.resourceName;
    let twins = findTwins(draftRef.current, contacts);
    let plan = planFill(draftRef.current.names?.[0] ?? {}, twins, dictionary, spellingCache);
    const ask = wordsToAsk(plan);
    let why: string | null = null;
    if (ask.length) {
      setPhoneticBusy(true);
      try {
        Object.assign(spellingCache, await backendApiClient.transliterateNames(ask));
      } catch (e) {
        why = (e as Error).message;
      }
      if (!mounted.current) {
        return;
      }
      setPhoneticBusy(false);
      // Another contact opened meanwhile: the answer was not for it
      if (draftRef.current.resourceName !== id) {
        return;
      }
      twins = findTwins(draftRef.current, contacts);
      plan = planFill(draftRef.current.names?.[0] ?? {}, twins, dictionary, spellingCache);
    }
    const filled = plan.filter((p) => p.value);
    if (filled.length) {
      const cur = draftRef.current;
      const n = cur.names?.[0] ?? {};
      const patch: Record<string, string> = {};
      filled.forEach((p) => {
        const field = PHONETIC_PARTS.find((x) => x.part === p.part)!.phonetic;
        patch[field] = p.value!;
      });
      onChangeRef.current({
        ...cur,
        names: [{ ...n, ...patch, phoneticFullName: null }, ...(cur.names ?? []).slice(1)],
      });
    }
    const said: string[] = [];
    const twin = filled.find((p) => p.twin)?.twin;
    if (twin) {
      said.push(`Filled from ${twin.name} (same ${twin.via})`);
    }
    if (filled.some((p) => p.source === 'contacts')) {
      said.push('Spelled as in your contacts');
    }
    if (filled.some((p) => p.source === 'gemini')) {
      said.push('Suggested by Gemini; check the spelling');
    }
    const unknown = [...new Set(plan.flatMap((p) => p.unknown))];
    if (unknown.length) {
      said.push(`Could not spell “${unknown.join('”, “')}”${why ? `: ${why}` : ''}`);
    }
    setPhoneticCaption(
      said.length ? { text: said.join(' · '), tone: unknown.length ? 'warn' : 'info' } : null,
    );
  }, [contacts]);

  const putEmail = useCallback((choice: EmailChoice, row: number | null) => {
    const cur = draftRef.current;
    const list = cur.emailAddresses ?? [];
    const entry = { value: choice.email, type: emailType(choice.email) };
    const next =
      row !== null && row < list.length && !clean(list[row].value)
        ? list.map((e, i) => (i === row ? { ...e, ...entry } : e))
        : [...list, entry];
    onChangeRef.current({ ...cur, emailAddresses: next });
    setEmailMenu(null);
    setEmailCaption({ text: foundText(choice), tone: 'info' });
  }, []);

  const findEmail = useCallback(
    async (anchor: HTMLElement, row: number | null) => {
      const names = nameVariants(draftRef.current);
      if (!names.length) {
        return;
      }
      const id = draftRef.current.resourceName;
      setEmailBusy(true);
      setEmailCaption(null);
      try {
        const r = await backendApiClient.findEmail(names);
        // Closed, or another contact opened meanwhile: the answer was not for it
        if (!mounted.current || draftRef.current.resourceName !== id) {
          return;
        }
        const choices = choicesFor(r.candidates, draftRef.current, contacts);
        const pick = autoPick(choices);
        const notes: string[] = [];
        if (pick) {
          putEmail(pick, row);
          notes.push(foundText(pick));
        } else if (choices.length) {
          setEmailMenu({ anchor, row, choices });
        }
        if (!choices.length) {
          notes.push(`No mail from “${names[0]}”`);
        }
        if (r.mail === 'sign_in') {
          notes.push('Sign in to Google again to search your mail');
        } else if (r.mail === 'failed') {
          notes.push('Gmail did not answer; searched the activity scan only');
        } else if (r.mail === 'not_allowed' && !choices.length) {
          notes.push(
            r.directory
              ? 'Searched the activity scan only: allow reading email content to search the mailbox'
              : 'Allow reading email content to search the mailbox',
          );
        }
        if (notes.length > (pick ? 1 : 0)) {
          setEmailCaption({
            text: notes.join(' · '),
            tone: 'warn',
            action:
              r.mail === 'not_allowed' && !choices.length
                ? {
                    label: 'Allow reading email content (leaves this page)',
                    onClick: () => backendApiClient.connectSignatures(),
                  }
                : undefined,
          });
        }
      } catch (e) {
        if (mounted.current) {
          setEmailCaption({ text: (e as Error).message, tone: 'warn' });
        }
      } finally {
        if (mounted.current) {
          setEmailBusy(false);
        }
      }
    },
    [contacts, putEmail],
  );

  return {
    phonetic: { fillable, busy: phoneticBusy, caption: phoneticCaption, fill: fillPhonetic },
    email: {
      canSearch: emailNames.length > 0,
      busy: emailBusy,
      caption: emailCaption,
      menu: emailMenu,
      find: findEmail,
      pick: putEmail,
      closeMenu: () => setEmailMenu(null),
    },
  };
};
