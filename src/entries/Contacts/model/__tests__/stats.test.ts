/**
 * @fileoverview Tests for address-book statistics
 */

import type { Contact } from '../../types/Contact';
import { findDuplicateGroups } from '../duplicates';
import { computeContactStats } from '../stats';

const contacts: Contact[] = [
  {
    resourceName: 'people/1',
    names: [{ displayName: 'שמוליק מוסק', phoneticGivenName: 'Shmulik' }],
    phoneNumbers: [{ value: '054-227-0763' }, { value: '+1 415 555 0100' }],
    labels: ['contactGroups/myContacts', 'contactGroups/fam'],
    birthdays: [{ date: { month: 3, day: 14 } }],
  },
  {
    resourceName: 'people/2',
    names: [{ displayName: 'דנה' }],
    phoneNumbers: [{ value: '0542270763' }],
    emailAddresses: [{ value: 'dana@x.com' }],
    labels: ['contactGroups/fam'],
  },
  {
    resourceName: 'people/3',
    names: [{ displayName: 'Bob' }],
    photos: [{ url: 'u', isDefault: true }],
  },
  { resourceName: 'people/4', emailAddresses: [{ value: 'nameless@x.com' }] },
];

const groups = { 'contactGroups/fam': { resourceName: 'contactGroups/fam', name: 'Family' } };

describe('computeContactStats', () => {
  const stats = computeContactStats(contacts, groups, findDuplicateGroups(contacts));

  it('counts totals and phonetic coverage of Hebrew names', () => {
    expect(stats.total).toBe(4);
    expect(stats.withPhonetic).toBe(1);
    expect(stats.hebrewNamed).toBe(2);
    expect(stats.hebrewNamedWithPhonetic).toBe(1);
  });

  it('reports completeness with links to the matching filters', () => {
    const row = (key: string) => stats.completeness.find((r) => r.key === key);
    expect(row('name')).toMatchObject({ count: 3, missingFilter: 'noName' });
    expect(row('phone')?.count).toBe(2);
    expect(row('email')?.count).toBe(2);
    expect(row('photo')?.count).toBe(0);
  });

  it('classifies every phone number', () => {
    expect(stats.phoneCount).toBe(3);
    expect(stats.phoneOrigins.find((r) => r.label === 'Israeli mobile')?.count).toBe(2);
    expect(stats.phoneOrigins.find((r) => r.label === 'International')?.count).toBe(1);
  });

  it('counts scripts, labels, birthdays and duplicates', () => {
    expect(stats.nameScripts.find((r) => r.label === 'Hebrew')?.count).toBe(2);
    expect(stats.nameScripts.find((r) => r.label === 'No name')?.count).toBe(1);
    expect(stats.topLabels).toEqual([{ label: 'Family', count: 2 }]);
    expect(stats.birthdaysByMonth[2]).toEqual({ label: 'Mar', count: 1 });
    expect(stats.duplicateGroups).toBe(1);
    expect(stats.contactsInDuplicates).toBe(2);
  });
});
