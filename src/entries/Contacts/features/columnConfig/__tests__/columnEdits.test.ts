/**
 * @fileoverview Inline edits must change only what the user typed
 */

import type { Contact } from '../../../types/Contact';
import { getColumnById } from '../columnDefinitions';

const contact: Contact = {
  resourceName: 'people/c1',
  names: [
    {
      displayName: 'Dr. John Ronald Tolkien',
      honorificPrefix: 'Dr.',
      givenName: 'John',
      middleName: 'Ronald',
      familyName: 'Tolkien',
      phoneticGivenName: 'Jon',
    },
  ],
  phoneNumbers: [
    { value: '050-1111111', type: 'mobile' },
    { value: '03-2222222', type: 'home' },
  ],
  organizations: [{ name: 'Acme', title: 'Eng', location: 'Bldg 4', current: true }],
};

const apply = (columnId: string, text: string) =>
  getColumnById(columnId)!.edit!.apply(contact, text);

describe('inline column edits', () => {
  it('keeps the middle name when the edited name still has three words', () => {
    const [name] = apply('displayName', 'John Ronald Tolkein') as Contact['names'] & object[];
    expect(name).toMatchObject({
      givenName: 'John',
      middleName: 'Ronald',
      familyName: 'Tolkein',
      honorificPrefix: 'Dr.',
      phoneticGivenName: 'Jon',
    });
  });

  it('phonetic edit leaves the real name untouched', () => {
    const [name] = apply('phoneticName', 'Jon Tolkin') as object[];
    expect(name).toMatchObject({
      givenName: 'John',
      familyName: 'Tolkien',
      phoneticGivenName: 'Jon',
      phoneticFamilyName: 'Tolkin',
    });
  });

  it('phone edit changes only the first number and keeps its type and the others', () => {
    expect(apply('phoneNumbers', '050-9999999')).toEqual([
      { value: '050-9999999', type: 'mobile', canonicalForm: null },
      { value: '03-2222222', type: 'home' },
    ]);
  });

  it('organization edit keeps fields the UI does not show', () => {
    expect(apply('organizations', 'Acme Ltd')).toEqual([
      { name: 'Acme Ltd', title: 'Eng', location: 'Bldg 4', current: true },
    ]);
  });
});
