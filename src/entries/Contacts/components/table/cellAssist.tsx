/**
 * @fileoverview The table's fills (AD-43 rule 10): the Phonetic name and Email cells,
 * while their text box is empty, find the value the contact card would
 * @module Contacts/components/table/cellAssist
 */

import React from 'react';
import { AutoFixHigh, ManageSearch } from '@mui/icons-material';
import type { Contact } from '../../types/Contact';
import type { CellAssist } from '../EditableCell/EditableCell';
import {
  describeChoice,
  emailCaption,
  fillableParts,
  lookUpEmail,
  phoneticCaption,
  spellPhonetic,
} from '../card/useCardAutofill';
import { PHONETIC_PARTS } from '../../model/phonetic';
import { nameVariants } from '../../model/emailFind';
import { clean, getPhoneticName, getPrimaryEmail, scriptOf } from '../../model/contactView';

/**
 * The assist of a cell, when its column has one and the contact can use it.
 * @param columnId - The column
 * @param contact - The row's contact
 * @param contacts - All the Owner's contacts
 * @returns The assist, or undefined
 */
export const cellAssistFor = (
  columnId: string,
  contact: Contact,
  contacts: Contact[],
): CellAssist | undefined => {
  if (columnId === 'phoneticName') {
    // The cell edits the whole phonetic name: only an empty one of a Hebrew name
    if (getPhoneticName(contact) || !fillableParts(contact).size) {
      return undefined;
    }
    return {
      label: 'Fill from the Hebrew name',
      icon: <AutoFixHigh fontSize="small" />,
      run: async () => {
        const result = await spellPhonetic(contact, contacts);
        const name = contact.names?.[0] ?? {};
        // A part not spelled (written in Latin letters) is its own phonetic part
        const [given, middle, family] = PHONETIC_PARTS.map(({ part, name: field }) => {
          const planned = result.plan.find((p) => p.part === part);
          return planned
            ? (planned.value ?? '')
            : scriptOf(name[field]) === 'latin'
              ? clean(name[field])
              : '';
        });
        // Whole or not at all: the cell is one text
        if (!result.plan.every((p) => p.value)) {
          return { note: phoneticCaption(result)?.text };
        }
        // The cell saves its first word as the first name and the rest as the last name:
        // only a one-word first name and no middle name come back as they were spelled
        if (middle || !given || given.includes(' ')) {
          return {
            note: `${[given, middle, family].filter(Boolean).join(' ')}: this name does not fit the one-box Phonetic name column. Fill it in the contact card (double-click the row).`,
          };
        }
        return { value: [given, family].filter(Boolean).join(' ') };
      },
    };
  }
  if (columnId === 'emailAddresses') {
    if (getPrimaryEmail(contact) || !nameVariants(contact).length) {
      return undefined;
    }
    return {
      label: 'Find in mail',
      icon: <ManageSearch fontSize="small" />,
      run: async () => {
        const result = await lookUpEmail(contact, contacts);
        if (!result) {
          return { note: 'Needs a first and last name' };
        }
        if (result.pick) {
          return { value: result.pick.email };
        }
        if (result.choices.length) {
          return {
            choices: result.choices.map((c) => ({ value: c.email, detail: describeChoice(c) })),
          };
        }
        const caption = emailCaption(result);
        return { note: caption?.text, action: caption?.action };
      },
    };
  }
  return undefined;
};
