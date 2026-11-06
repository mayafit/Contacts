/**
 * @fileoverview The contact card's two fills (AD-43): the phonetic name from the Hebrew
 * name, and the email from the mail. Both change the draft only. The look-ups are
 * shared with the table's cells (spellPhonetic, lookUpEmail).
 * @module Contacts/components/card/useCardAutofill
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { Contact } from '../../types/Contact';
import { useAppSelector } from '../../types/hooks';
import { selectAllContacts } from '../../redux/slices/contacts/selectors';
import {
  backendApiClient,
  type FindEmailResponse,
} from '../../../../services/api/BackendApiClient';
import {
  buildDictionary,
  findTwins,
  PHONETIC_PARTS,
  planFill,
  wordsToAsk,
  type PartFill,
  nameContext,
  type PhoneticPart,
  type ServiceSpelling,
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

// The services' spellings, kept for the session (AD-43 rule 4)
const spellingCache: Record<string, ServiceSpelling> = {};

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

/** For tests: forget the session's spellings */
export const clearSpellingCache = (): void => {
  Object.keys(spellingCache).forEach((k) => delete spellingCache[k]);
};

/**
 * Where a filled address was seen.
 * @param c - Candidate
 * @returns "Found in 12 mails as “Dana Cohen”"
 */
export const foundText = (c: EmailChoice): string =>
  [
    'Found',
    c.mails ? `in ${c.mails} ${c.mails === 1 ? 'mail' : 'mails'}` : 'in your mail',
    c.names[0] ? `as “${c.names[0]}”` : '',
  ]
    .filter(Boolean)
    .join(' ');

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

/** The empty phonetic parts of a contact that a click would fill */
export const fillableParts = (contact: Contact): Set<PhoneticPart> =>
  new Set(planFill(contact.names?.[0] ?? {}, [], new Map()).map((p) => p.part));

/**
 * The fill plan of a contact with the spellings known so far.
 * @param contact - The contact (or the card's draft)
 * @param contacts - All the Owner's contacts
 * @returns The plan
 */
const planFor = (contact: Contact, contacts: Contact[]): PartFill[] =>
  planFill(
    contact.names?.[0] ?? {},
    findTwins(contact, contacts),
    dictionaryFor(contacts),
    spellingCache,
  );

/** A phonetic look-up's outcome */
export interface PhoneticResult {
  /** One entry per empty Hebrew part */
  plan: PartFill[];
  /** Why Gemini did not answer, when it was asked */
  why: string | null;
}

/**
 * Spells a contact's empty Hebrew name parts (AD-43 rules 2 to 5): twin, dictionary,
 * then Gemini for the words left.
 * @param contact - The contact (or the card's draft)
 * @param contacts - All the Owner's contacts
 * @returns The plan, with Gemini's answer applied
 */
export const spellPhonetic = async (
  contact: Contact,
  contacts: Contact[],
): Promise<PhoneticResult> => {
  const ask = wordsToAsk(planFor(contact, contacts));
  let why: string | null = null;
  if (ask.length) {
    try {
      const answer = await backendApiClient.transliterateNames(
        ask,
        nameContext(contact.names?.[0] ?? {}),
      );
      Object.entries(answer.spellings).forEach(([word, spelling]) => {
        spellingCache[word] = { spelling, source: answer.sources?.[word] ?? 'gemini' };
      });
      why = answer.note ?? null;
    } catch (e) {
      why = (e as Error).message;
    }
  }
  return { plan: planFor(contact, contacts), why };
};

/**
 * What a phonetic fill says: where each spelling came from, and the words left.
 * @param result - The look-up's outcome
 * @returns The caption, or null
 */
export const phoneticCaption = ({ plan, why }: PhoneticResult): FillCaption | null => {
  const filled = plan.filter((p) => p.value);
  const said: string[] = [];
  const twin = filled.find((p) => p.twin)?.twin;
  if (twin) {
    said.push(`Filled from ${twin.name} (same ${twin.via})`);
  }
  if (filled.some((p) => p.source === 'contacts')) {
    said.push('Spelled as in your contacts');
  }
  if (filled.some((p) => p.source === 'translate')) {
    said.push('Spelled by Google Translate; check the spelling');
  }
  if (filled.some((p) => p.source === 'gemini')) {
    said.push('Suggested by Gemini; check the spelling');
  }
  const unknown = [...new Set(plan.flatMap((p) => p.unknown))];
  if (unknown.length) {
    said.push(`Could not spell “${unknown.join('”, “')}”${why ? `: ${why}` : ''}`);
  }
  return said.length ? { text: said.join(' · '), tone: unknown.length ? 'warn' : 'info' } : null;
};

/** An email look-up's outcome */
export interface EmailResult {
  /** The names looked for */
  names: string[];
  response: FindEmailResponse;
  /** The candidates as offered */
  choices: EmailChoice[];
  /** The one filled without asking */
  pick: EmailChoice | null;
}

/**
 * Looks a contact's address up in the mail (AD-43 rules 6 to 8).
 * @param contact - The contact (or the card's draft)
 * @param contacts - All the Owner's contacts
 * @returns The outcome, or null without a name of two words
 */
export const lookUpEmail = async (
  contact: Contact,
  contacts: Contact[],
): Promise<EmailResult | null> => {
  const names = nameVariants(contact);
  if (!names.length) {
    return null;
  }
  const response = await backendApiClient.findEmail(names);
  const choices = choicesFor(response.candidates, contact, contacts);
  return { names, response, choices, pick: autoPick(choices) };
};

/**
 * What an email look-up says beyond the address filled: nothing found, and what of
 * the mailbox could not be searched.
 * @param result - The look-up's outcome
 * @returns The caption, or null
 */
export const emailCaption = ({
  names,
  response,
  choices,
  pick,
}: EmailResult): FillCaption | null => {
  const notes: string[] = pick ? [foundText(pick)] : [];
  if (!choices.length) {
    notes.push(`No mail from “${names[0]}”`);
  }
  const askAccess = response.mail === 'not_allowed' && !choices.length;
  if (response.mail === 'sign_in') {
    notes.push('Sign in to Google again to search your mail');
  } else if (response.mail === 'failed') {
    notes.push('Gmail did not answer; searched the activity scan only');
  } else if (askAccess) {
    notes.push(
      response.directory
        ? 'Searched the activity scan only: allow reading email content to search the mailbox'
        : 'Allow reading email content to search the mailbox',
    );
  }
  if (notes.length <= (pick ? 1 : 0)) {
    return pick ? { text: notes[0], tone: 'info' } : null;
  }
  return {
    text: notes.join(' · '),
    tone: 'warn',
    action: askAccess
      ? {
          label: 'Allow reading email content (leaves this page)',
          onClick: () => backendApiClient.connectSignatures(),
        }
      : undefined,
  };
};

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
  const [phoneticCaptionState, setPhoneticCaption] = useState<FillCaption | null>(null);
  const [emailBusy, setEmailBusy] = useState(false);
  const [emailCaptionState, setEmailCaption] = useState<FillCaption | null>(null);
  const [emailMenu, setEmailMenu] = useState<EmailMenu | null>(null);

  const fillable = useMemo(() => fillableParts(draft), [draft]);
  const emailNames = useMemo(() => nameVariants(draft), [draft]);

  const fillPhonetic = useCallback(async () => {
    const id = draftRef.current.resourceName;
    setPhoneticBusy(true);
    const result = await spellPhonetic(draftRef.current, contacts);
    if (!mounted.current) {
      return;
    }
    setPhoneticBusy(false);
    // Another contact opened meanwhile: the answer was not for it
    if (draftRef.current.resourceName !== id) {
      return;
    }
    const cur = draftRef.current;
    const n = cur.names?.[0] ?? {};
    const patch: Record<string, string> = {};
    // Planned again from the draft as it is now: a name changed while waiting is spelled as changed
    result.plan = planFor(cur, contacts);
    result.plan
      .filter((p) => p.value)
      .forEach((p) => {
        const field = PHONETIC_PARTS.find((x) => x.part === p.part)!.phonetic;
        // A part typed while Gemini answered stays as typed
        if (!clean(n[field])) {
          patch[field] = p.value!;
        }
      });
    if (Object.keys(patch).length) {
      onChangeRef.current({
        ...cur,
        names: [{ ...n, ...patch, phoneticFullName: null }, ...(cur.names ?? []).slice(1)],
      });
    }
    setPhoneticCaption(phoneticCaption(result));
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
      const id = draftRef.current.resourceName;
      setEmailBusy(true);
      setEmailCaption(null);
      try {
        const result = await lookUpEmail(draftRef.current, contacts);
        // Closed, or another contact opened meanwhile: the answer was not for it
        if (!result || !mounted.current || draftRef.current.resourceName !== id) {
          return;
        }
        if (result.pick) {
          putEmail(result.pick, row);
        } else if (result.choices.length) {
          setEmailMenu({ anchor, row, choices: result.choices });
        }
        setEmailCaption(emailCaption(result));
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
    phonetic: {
      fillable,
      busy: phoneticBusy,
      caption: phoneticCaptionState,
      fill: fillPhonetic,
    },
    email: {
      canSearch: emailNames.length > 0,
      busy: emailBusy,
      caption: emailCaptionState,
      menu: emailMenu,
      find: findEmail,
      pick: putEmail,
      closeMenu: () => setEmailMenu(null),
    },
  };
};
