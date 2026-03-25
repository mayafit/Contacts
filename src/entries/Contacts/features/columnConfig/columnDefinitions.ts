/**
 * @fileoverview Available column definitions for contacts table
 * @module features/columnConfig/columnDefinitions
 */

import type { Contact } from '../../types/Contact';
import type { ColumnDefinition } from './types';

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
    accessor: (contact: Contact) => contact.names?.[0]?.displayName || '—',
  },
  {
    id: 'phoneNumbers',
    label: 'Phone',
    category: 'Basic',
    isDefault: true,
    accessor: (contact: Contact) =>
      contact.phoneNumbers?.[0]?.value || '—',
  },
  {
    id: 'emailAddresses',
    label: 'Email',
    category: 'Basic',
    isDefault: true,
    accessor: (contact: Contact) =>
      contact.emailAddresses?.[0]?.value || '—',
  },
  {
    id: 'organizations',
    label: 'Organization',
    category: 'Basic',
    isDefault: false,
    accessor: (contact: Contact) =>
      contact.organizations?.[0]?.name || '—',
  },
  {
    id: 'jobTitle',
    label: 'Job Title',
    category: 'Basic',
    isDefault: false,
    accessor: (contact: Contact) =>
      contact.organizations?.[0]?.title || '—',
  },

  // Additional Fields
  {
    id: 'birthdays',
    label: 'Birthday',
    category: 'Additional',
    isDefault: false,
    accessor: (contact: Contact) => {
      const birthday = contact.birthdays?.[0];
      if (!birthday) return '—';
      const { year, month, day } = birthday.date || {};
      if (!month || !day) return '—';
      return year ? `${month}/${day}/${year}` : `${month}/${day}`;
    },
  },
  {
    id: 'urls',
    label: 'Website',
    category: 'Additional',
    isDefault: false,
    accessor: (contact: Contact) => contact.urls?.[0]?.value || '—',
  },
  {
    id: 'addresses',
    label: 'Address',
    category: 'Additional',
    isDefault: false,
    accessor: (contact: Contact) =>
      contact.addresses?.[0]?.formattedValue || '—',
  },
  {
    id: 'biographies',
    label: 'Notes',
    category: 'Additional',
    isDefault: false,
    accessor: (contact: Contact) =>
      contact.biographies?.[0]?.value || '—',
  },

  // Advanced Fields
  {
    id: 'phoneticGivenName',
    label: 'Phonetic First Name',
    category: 'Advanced',
    isDefault: false,
    accessor: (contact: Contact) =>
      contact.names?.[0]?.phoneticGivenName || '—',
  },
  {
    id: 'phoneticFamilyName',
    label: 'Phonetic Last Name',
    category: 'Advanced',
    isDefault: false,
    accessor: (contact: Contact) =>
      contact.names?.[0]?.phoneticFamilyName || '—',
  },
  {
    id: 'userDefined',
    label: 'Custom Fields',
    category: 'Advanced',
    isDefault: false,
    accessor: (contact: Contact) => {
      const customFields = contact.userDefined;
      if (!customFields || customFields.length === 0) return '—';
      return customFields
        .map((field) => `${field.key}: ${field.value}`)
        .join(', ');
    },
  },
  {
    id: 'relations',
    label: 'Relations',
    category: 'Advanced',
    isDefault: false,
    accessor: (contact: Contact) => {
      const relations = contact.relations;
      if (!relations || relations.length === 0) return '—';
      return relations
        .map((rel) => `${rel.person} (${rel.type})`)
        .join(', ');
    },
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
 * Get default columns (displayName, phoneNumbers, emailAddresses)
 */
export const getDefaultColumns = (): ColumnDefinition[] => {
  return AVAILABLE_COLUMNS.filter((col) => col.isDefault);
};
