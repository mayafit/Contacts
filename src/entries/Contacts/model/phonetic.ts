/**
 * @fileoverview Spells a Hebrew name in Latin letters for the phonetic name (AD-43):
 * from an English twin, then from how the Owner's contacts spell each word; the
 * words left are for Gemini
 * @module Contacts/model/phonetic
 */

import type { Contact, ContactName } from '../types/Contact';
import { clean, getDisplayName, phoneKey, scriptOf, splitFullName } from './contactView';

/** A phonetic name position */
export type PhoneticPart = 'given' | 'middle' | 'family';

/** Each position's name field and phonetic field */
export const PHONETIC_PARTS: {
  part: PhoneticPart;
  name: 'givenName' | 'middleName' | 'familyName';
  phonetic: 'phoneticGivenName' | 'phoneticMiddleName' | 'phoneticFamilyName';
}[] = [
  { part: 'given', name: 'givenName', phonetic: 'phoneticGivenName' },
  { part: 'middle', name: 'middleName', phonetic: 'phoneticMiddleName' },
  { part: 'family', name: 'familyName', phonetic: 'phoneticFamilyName' },
];

const HEBREW = /[\u05D0-\u05EA]/;
const LATIN = /[A-Za-z]/;
// Points and cantillation, but not the maqaf (U+05BE)
const POINTS = /[\u0591-\u05BD\u05BF-\u05C7]/g;
// Punctuation around a word: "(עבודה)", "ד."
const EDGES = /^[^\p{L}\p{N}'"]+|[^\p{L}\p{N}'"]+$/gu;
// What the server sends to Gemini (rule 4)
const SENDABLE = /^[\u05D0-\u05EA'"-]{1,40}$/;

/**
 * One word as looked up: without points or the punctuation around it, the maqaf as a
 * hyphen, with plain geresh and gershayim.
 * @param word - Word
 * @returns Normalized word
 */
export const normalizeWord = (word: string): string =>
  word
    .replace(POINTS, '')
    .replace(/\u05BE/g, '-')
    .replace(/[\u05F3\u2019]/g, "'")
    .replace(/\u05F4/g, '"')
    .replace(EDGES, '');

/**
 * The words of a name part.
 * @param text - Name part
 * @returns Normalized words
 */
export const wordsOf = (text: string | null | undefined): string[] =>
  clean(text).split(' ').map(normalizeWord).filter(Boolean);

/** A Hebrew letter and no Latin one */
export const isHebrewWord = (w: string): boolean => HEBREW.test(w) && !LATIN.test(w);

/** A Latin letter and no Hebrew one */
export const isLatinWord = (w: string): boolean => LATIN.test(w) && !HEBREW.test(w);

/** The same person written in Latin letters */
export interface Twin {
  /** Its display name */
  name: string;
  /** What it shares with the draft */
  via: 'phone' | 'email';
  /** Its value for each position ('' when none) */
  parts: Record<PhoneticPart, string>;
}

/**
 * A Latin contact's values per position: its phonetic parts when it has any,
 * else its name parts, else its display name split into first word and the rest.
 * @param n - Its name
 * @returns Values per position
 */
const latinParts = (n: ContactName): Record<PhoneticPart, string> => {
  if (n.phoneticGivenName || n.phoneticMiddleName || n.phoneticFamilyName) {
    return {
      given: clean(n.phoneticGivenName),
      middle: clean(n.phoneticMiddleName),
      family: clean(n.phoneticFamilyName),
    };
  }
  if (n.givenName || n.middleName || n.familyName) {
    return { given: clean(n.givenName), middle: clean(n.middleName), family: clean(n.familyName) };
  }
  const split = splitFullName(clean(n.displayName));
  return { given: split.first, middle: '', family: split.last };
};

/** First, middle and last name, else the display name */
const fullNameOf = (n: ContactName): string =>
  clean([n.givenName, n.middleName, n.familyName].filter(Boolean).join(' ')) ||
  clean(n.displayName);

/**
 * The other contacts that share a phone or an email with the draft and are
 * written in Latin letters (rule 2).
 * @param draft - The card's draft
 * @param contacts - The Owner's contacts
 * @returns Twins
 */
export const findTwins = (draft: Contact, contacts: Contact[]): Twin[] => {
  const phones = new Set(
    (draft.phoneNumbers ?? [])
      .map((p) => phoneKey(p.value, p.canonicalForm))
      .filter((k): k is string => !!k),
  );
  const emails = new Set(
    (draft.emailAddresses ?? []).map((e) => clean(e.value).toLowerCase()).filter(Boolean),
  );
  if (!phones.size && !emails.size) {
    return [];
  }
  return contacts.flatMap((c) => {
    if (c.resourceName === draft.resourceName) {
      return [];
    }
    const n = c.names?.[0] ?? {};
    if (scriptOf(fullNameOf(n)) !== 'latin') {
      return [];
    }
    const viaPhone = (c.phoneNumbers ?? []).some((p) => {
      const k = phoneKey(p.value, p.canonicalForm);
      return !!k && phones.has(k);
    });
    const viaEmail = (c.emailAddresses ?? []).some((e) => emails.has(clean(e.value).toLowerCase()));
    if (!viaPhone && !viaEmail) {
      return [];
    }
    return [{ name: getDisplayName(c), via: viaPhone ? 'phone' : 'email', parts: latinParts(n) }];
  });
};

/**
 * A position's value from the twins: used only when all twins with a value agree.
 * @param twins - Twins
 * @param part - Position
 * @returns The value and the first twin giving it, or null
 */
export const twinValue = (
  twins: Twin[],
  part: PhoneticPart,
): { value: string; twin: Twin } | null => {
  const giving = twins.filter((t) => t.parts[part]);
  if (!giving.length) {
    return null;
  }
  const first = giving[0].parts[part];
  return giving.every((t) => t.parts[part].toLowerCase() === first.toLowerCase())
    ? { value: first, twin: giving[0] }
    : null;
};

/** A Hebrew word's best spelling in the Owner's contacts */
export interface Spelling {
  spelling: string;
  /** How many name parts spell it so */
  count: number;
}

/** Hebrew word → its best spelling */
export type SpellingDictionary = Map<string, Spelling>;

/**
 * How the Owner's contacts spell each Hebrew word (rule 3): word i of a Hebrew name
 * part pairs with word i of its phonetic part when both have the same number of
 * words, and hyphenated pieces pair the same way.
 * @param contacts - The Owner's contacts
 * @returns The dictionary
 */
export const buildDictionary = (contacts: Contact[]): SpellingDictionary => {
  // word → lower-case spelling → (count, written forms)
  const seen = new Map<string, Map<string, { count: number; forms: Map<string, number> }>>();
  const add = (he: string, la: string) => {
    const bySpelling = seen.get(he) ?? new Map();
    seen.set(he, bySpelling);
    const key = la.toLowerCase();
    const entry = bySpelling.get(key) ?? { count: 0, forms: new Map<string, number>() };
    bySpelling.set(key, entry);
    entry.count += 1;
    entry.forms.set(la, (entry.forms.get(la) ?? 0) + 1);
  };
  contacts.forEach((c) => {
    const n = c.names?.[0];
    if (!n) {
      return;
    }
    PHONETIC_PARTS.forEach(({ name, phonetic }) => {
      const he = wordsOf(n[name]);
      const la = wordsOf(n[phonetic]);
      if (
        !he.length ||
        he.length !== la.length ||
        !he.every(isHebrewWord) ||
        !la.every(isLatinWord)
      ) {
        return;
      }
      he.forEach((w, i) => {
        add(w, la[i]);
        const hp = w.split('-');
        const lp = la[i].split('-');
        if (hp.length > 1 && hp.length === lp.length) {
          hp.forEach((piece, j) => piece && lp[j] && add(piece, lp[j]));
        }
      });
    });
  });
  const pickForm = (forms: Map<string, number>) =>
    [...forms.entries()].sort((a, b) => b[1] - a[1] || (a[0] < b[0] ? -1 : 1))[0][0];
  const dict: SpellingDictionary = new Map();
  seen.forEach((bySpelling, word) => {
    const [, best] = [...bySpelling.entries()].sort(
      (a, b) => b[1].count - a[1].count || (a[0] < b[0] ? -1 : 1),
    )[0];
    dict.set(word, { spelling: pickForm(best.forms), count: best.count });
  });
  return dict;
};

/** Where a part's spelling came from, strongest first */
export type SpellingSource = 'twin' | 'contacts' | 'translate' | 'gemini';

const WEAKNESS: Record<SpellingSource, number> = { twin: 0, contacts: 1, translate: 2, gemini: 3 };

/** A spelling a service gave */
export interface ServiceSpelling {
  spelling: string;
  source: 'translate' | 'gemini';
}

/**
 * One word's spelling: a Latin word as it is; a Hebrew word from the dictionary,
 * else piece by piece over its hyphens, else from Gemini's answers.
 * @param word - Normalized word
 * @param dict - The dictionary
 * @param extra - The services' spellings
 * @returns The spelling and its source, or null
 */
const spellWord = (
  word: string,
  dict: SpellingDictionary,
  extra: Record<string, ServiceSpelling>,
): { spelling: string; source: SpellingSource | null } | null => {
  if (!isHebrewWord(word)) {
    return { spelling: word, source: null };
  }
  const known = dict.get(word);
  if (known) {
    return { spelling: known.spelling, source: 'contacts' };
  }
  const pieces = word.split('-');
  if (pieces.length > 1 && pieces.every((p) => p && dict.has(p))) {
    return { spelling: pieces.map((p) => dict.get(p)!.spelling).join('-'), source: 'contacts' };
  }
  return extra[word] ?? null;
};

/** What a click does for one empty phonetic part */
export interface PartFill {
  part: PhoneticPart;
  /** The value, or null while a word is unspelled */
  value: string | null;
  source: SpellingSource | null;
  /** The twin used */
  twin?: Twin;
  /** Hebrew words not spelled yet */
  unknown: string[];
}

/**
 * The parts a click fills (rule 5): every empty phonetic part whose name part has
 * a Hebrew word.
 * @param name - The draft's name
 * @param twins - Its twins
 * @param dict - The dictionary
 * @param extra - The services' spellings so far
 * @returns One entry per part to fill
 */
export const planFill = (
  name: ContactName,
  twins: Twin[],
  dict: SpellingDictionary,
  extra: Record<string, ServiceSpelling> = {},
): PartFill[] =>
  PHONETIC_PARTS.flatMap(({ part, name: field, phonetic }): PartFill[] => {
    const words = wordsOf(name[field]);
    if (clean(name[phonetic]) || !words.some(isHebrewWord)) {
      return [];
    }
    const twin = twinValue(twins, part);
    if (twin) {
      return [{ part, value: twin.value, source: 'twin' as const, twin: twin.twin, unknown: [] }];
    }
    const spelled = words.map((w) => spellWord(w, dict, extra));
    const unknown = words.filter((_, i) => !spelled[i]);
    if (unknown.length) {
      return [{ part, value: null, source: null, unknown }];
    }
    const source = spelled
      .map((s) => s!.source)
      .filter((s): s is SpellingSource => !!s)
      .sort((a, b) => WEAKNESS[b] - WEAKNESS[a])[0];
    return [{ part, value: spelled.map((s) => s!.spelling).join(' '), source, unknown: [] }];
  });

/**
 * The name's words in order (first, middle, last) that may be sent for context: Hebrew
 * words of the sendable form and Latin words; at most 10.
 * @param name - The name
 * @returns Words
 */
export const nameContext = (name: ContactName): string[] =>
  PHONETIC_PARTS.flatMap(({ name: field }) => wordsOf(name[field]))
    .filter((w) => (isHebrewWord(w) && SENDABLE.test(w)) || /^[A-Za-z'-]{1,40}$/.test(w))
    .slice(0, 10);

/**
 * The distinct Hebrew words a plan still needs from a service (at most 20).
 * @param plan - The plan
 * @returns Words
 */
export const wordsToAsk = (plan: PartFill[]): string[] =>
  [...new Set(plan.flatMap((p) => p.unknown))].filter((w) => SENDABLE.test(w)).slice(0, 20);
