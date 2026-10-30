/**
 * @fileoverview Available column definitions for contacts table
 * @module features/columnConfig/columnDefinitions
 */

import type { Contact } from '../../types/Contact';
import type { ColumnDefinition, ColumnEdit } from './types';
import {
  clean,
  editableName,
  formatBirthday,
  getDisplayName,
  getJobTitle,
  getLabelNames,
  getName,
  getOrganization,
  getPhoneticName,
  getPrimaryEmail,
  getPrimaryPhone,
  splitFullName,
} from '../../model/contactView';
import { formatRelativeShort } from '../../model/activity';
import { emailType } from '../../model/emailFind';

/**
 * Replaces the first entry of a list group, adds one when the list is empty,
 * or removes the first entry when the new text is empty. Other entries and the
 * entry's other properties are kept.
 * @param list - Current list
 * @param text - New text for the first entry
 * @param build - Builds an entry from text, given the previous first entry
 * @returns New list
 */
function replaceFirst<T>(
  list: T[] | null | undefined,
  text: string,
  build: (prev: T | undefined) => T,
): T[] {
  const items = [...(list ?? [])];
  if (!clean(text)) {
    return items.slice(1);
  }
  if (items.length === 0) {
    return [build(undefined)];
  }
  items[0] = build(items[0]);
  return items;
}

/** Name edited as one string: first word -> given name, rest -> family name */
const nameEdit: ColumnEdit = {
  fieldPath: 'names',
  value: (c) => {
    const n = getName(c);
    return (
      clean([n?.givenName, n?.middleName, n?.familyName].filter(Boolean).join(' ')) ||
      clean(n?.displayName)
    );
  },
  apply: (c, text) => {
    const name = editableName(getName(c));
    const words = clean(text).split(' ').filter(Boolean);
    // "John Ronald Tolkien" with a middle name on file keeps it as the middle
    // name; otherwise first word -> given, rest -> family.
    if (name.middleName && words.length >= 3) {
      return [
        {
          ...name,
          givenName: words[0],
          middleName: words.slice(1, -1).join(' '),
          familyName: words[words.length - 1],
          displayName: null,
        },
      ];
    }
    const { first, last } = splitFullName(text);
    return [{ ...name, givenName: first, middleName: null, familyName: last, displayName: null }];
  },
};

/** Phonetic name edited as one string: first word -> phonetic given, rest -> phonetic family */
const phoneticEdit: ColumnEdit = {
  fieldPath: 'names',
  value: getPhoneticName,
  apply: (c, text) => {
    const { first, last } = splitFullName(text);
    return [
      {
        ...editableName(getName(c)),
        phoneticGivenName: first,
        phoneticMiddleName: null,
        phoneticFamilyName: last,
        phoneticFullName: null,
      },
    ];
  },
};

/**
 * All available column definitions organized by category
 * Includes accessor functions to extract data from Contact objects
 */
export const AVAILABLE_COLUMNS: ColumnDefinition[] = [
  // Basic Fields
  {
    id: 'displayName',
    label: 'Name',
    category: 'Basic',
    isDefault: true,
    defaultVisible: true,
    width: 260,
    accessor: getDisplayName,
    edit: nameEdit,
  },
  {
    id: 'phoneticName',
    label: 'Phonetic name',
    category: 'Basic',
    isDefault: false,
    defaultVisible: true,
    width: 200,
    accessor: getPhoneticName,
    edit: phoneticEdit,
  },
  {
    id: 'phoneNumbers',
    label: 'Phone',
    category: 'Basic',
    isDefault: false,
    defaultVisible: true,
    width: 170,
    accessor: getPrimaryPhone,
    edit: {
      fieldPath: 'phoneNumbers',
      value: getPrimaryPhone,
      apply: (c, text) =>
        replaceFirst(c.phoneNumbers, text, (prev) => ({
          ...prev,
          value: clean(text),
          canonicalForm: null,
        })),
    },
  },
  {
    id: 'emailAddresses',
    label: 'Email',
    category: 'Basic',
    isDefault: false,
    defaultVisible: true,
    width: 230,
    accessor: getPrimaryEmail,
    edit: {
      fieldPath: 'emailAddresses',
      value: getPrimaryEmail,
      apply: (c, text) =>
        // A new address is typed by its domain, as the card's find in mail does (AD-43)
        replaceFirst(c.emailAddresses, text, (prev) => ({
          ...prev,
          value: clean(text),
          type: prev?.type ?? emailType(clean(text)),
        })),
    },
  },
  {
    id: 'organizations',
    label: 'Organization',
    category: 'Basic',
    isDefault: false,
    defaultVisible: true,
    width: 180,
    accessor: getOrganization,
    edit: {
      fieldPath: 'organizations',
      value: getOrganization,
      apply: (c, text) => {
        const orgs = [...(c.organizations ?? [])];
        orgs[0] = { ...(orgs[0] ?? {}), name: clean(text) || null };
        return orgs;
      },
    },
  },
  {
    id: 'jobTitle',
    label: 'Job title',
    category: 'Basic',
    isDefault: false,
    width: 160,
    accessor: getJobTitle,
    edit: {
      fieldPath: 'organizations',
      value: getJobTitle,
      apply: (c, text) => {
        const orgs = [...(c.organizations ?? [])];
        orgs[0] = { ...(orgs[0] ?? {}), title: clean(text) || null };
        return orgs;
      },
    },
  },
  {
    id: 'labels',
    label: 'Labels',
    category: 'Basic',
    isDefault: false,
    defaultVisible: true,
    width: 170,
    accessor: (c, ctx) => getLabelNames(c, ctx?.groups ?? {}).join(', '),
  },

  // Additional Fields
  {
    id: 'nickname',
    label: 'Nickname',
    category: 'Additional',
    isDefault: false,
    width: 140,
    accessor: (c) => clean(c.nicknames?.[0]?.value),
    edit: {
      fieldPath: 'nicknames',
      value: (c) => clean(c.nicknames?.[0]?.value),
      apply: (c, text) => replaceFirst(c.nicknames, text, () => ({ value: clean(text) })),
    },
  },
  {
    id: 'birthdays',
    label: 'Birthday',
    category: 'Additional',
    isDefault: false,
    width: 120,
    accessor: formatBirthday,
  },
  {
    id: 'urls',
    label: 'Website',
    category: 'Additional',
    isDefault: false,
    width: 200,
    accessor: (c) => clean(c.urls?.[0]?.value),
  },
  {
    id: 'addresses',
    label: 'Address',
    category: 'Additional',
    isDefault: false,
    width: 240,
    accessor: (c) => clean(c.addresses?.[0]?.formattedValue),
  },
  {
    id: 'biographies',
    label: 'Notes',
    category: 'Additional',
    isDefault: false,
    width: 240,
    accessor: (c) => clean(c.biographies?.[0]?.value),
  },
  {
    id: 'updateTime',
    label: 'Last updated',
    category: 'Additional',
    isDefault: false,
    width: 130,
    accessor: (c: Contact) =>
      c.updateTime ? new Date(c.updateTime).toLocaleDateString('en-GB') : '',
    sortKey: (c: Contact) => c.updateTime ?? '',
  },
  {
    id: 'lastInTouch',
    label: 'Last in touch',
    category: 'Additional',
    isDefault: false,
    width: 110,
    accessor: (c, ctx) => formatRelativeShort(ctx?.activity?.get(c.resourceName)?.lastInTouch),
    sortKey: (c, ctx) => ctx?.activity?.get(c.resourceName)?.lastInTouch ?? '',
  },

  // Advanced Fields
  {
    id: 'phoneticGivenName',
    label: 'Phonetic first name',
    category: 'Advanced',
    isDefault: false,
    width: 160,
    accessor: (c) => clean(editableName(getName(c)).phoneticGivenName),
  },
  {
    id: 'phoneticFamilyName',
    label: 'Phonetic last name',
    category: 'Advanced',
    isDefault: false,
    width: 160,
    accessor: (c) => clean(editableName(getName(c)).phoneticFamilyName),
  },
  {
    id: 'userDefined',
    label: 'Custom fields',
    category: 'Advanced',
    isDefault: false,
    width: 220,
    accessor: (c) => (c.userDefined ?? []).map((f) => `${f.key}: ${f.value}`).join(', '),
  },
  {
    id: 'relations',
    label: 'Relations',
    category: 'Advanced',
    isDefault: false,
    width: 220,
    accessor: (c) =>
      (c.relations ?? []).map((r) => (r.type ? `${r.person} (${r.type})` : r.person)).join(', '),
  },
];

/**
 * Get column definition by ID
 */
export const getColumnById = (id: string): ColumnDefinition | undefined => {
  return AVAILABLE_COLUMNS.find((col) => col.id === id);
};

/**
 * Get columns by category
 */
export const getColumnsByCategory = (category: string): ColumnDefinition[] => {
  return AVAILABLE_COLUMNS.filter((col) => col.category === category);
};

/**
 * Get the columns shown to a new user
 */
export const getDefaultColumns = (): ColumnDefinition[] => {
  return AVAILABLE_COLUMNS.filter((col) => col.defaultVisible);
};
