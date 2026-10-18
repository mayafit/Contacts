/**
 * @fileoverview Tests for contact card draft/patch logic
 */

import type { Contact } from '../../../types/Contact';
import {
  buildPatch,
  changedGroups,
  createDraft,
  normalizeGroup,
  validateDraft,
} from '../cardModel';

const original: Contact = {
  resourceName: 'people/c1',
  etag: 'e1',
  names: [
    { displayName: 'דנה לוי', givenName: 'דנה', familyName: 'לוי', phoneticFullName: 'Dana Levi' },
  ],
  phoneNumbers: [{ value: '052-111-1111', type: 'mobile', canonicalForm: '+972521111111' }],
  emailAddresses: [{ value: 'dana@x.com' }],
};

describe('createDraft', () => {
  it('deep-copies and splits the phonetic full name into parts', () => {
    const draft = createDraft(original);
    expect(draft.names?.[0]).toMatchObject({
      phoneticGivenName: 'Dana',
      phoneticFamilyName: 'Levi',
    });
    draft.phoneNumbers![0].value = 'changed';
    expect(original.phoneNumbers![0].value).toBe('052-111-1111');
  });
});

describe('normalizeGroup', () => {
  it('drops empties, read-only keys and orders keys', () => {
    expect(
      normalizeGroup([{ value: ' a ', type: '', canonicalForm: '+1' }, { value: '' }]),
    ).toEqual([{ value: 'a' }]);
    expect(normalizeGroup([])).toBeUndefined();
    expect(normalizeGroup(null)).toBeUndefined();
  });
});

describe('changedGroups / buildPatch', () => {
  it('reports nothing for an untouched draft', () => {
    expect(changedGroups(original, createDraft(original))).toEqual([]);
    expect(buildPatch(original, createDraft(original))).toBeNull();
  });

  it('ignores whitespace-only and read-only differences', () => {
    const draft = createDraft(original);
    draft.phoneNumbers![0].canonicalForm = null;
    draft.emailAddresses![0].value = ' dana@x.com ';
    expect(changedGroups(original, draft)).toEqual([]);
  });

  it('sends only changed groups with the etag', () => {
    const draft = createDraft(original);
    draft.names![0].phoneticGivenName = 'Danna';
    draft.phoneNumbers!.push({ value: '03-1234567', type: 'home' });
    const patch = buildPatch(original, draft);
    expect(Object.keys(patch!).sort()).toEqual(['etag', 'names', 'phoneNumbers']);
    expect(patch!.etag).toBe('e1');
  });

  it('sends an emptied group as [] so Google clears it', () => {
    const draft = createDraft(original);
    draft.emailAddresses = [];
    expect(buildPatch(original, draft)).toEqual({ etag: 'e1', emailAddresses: [] });
  });

  it('uses a fresh etag when overwriting after a conflict', () => {
    const draft = createDraft(original);
    draft.nicknames = [{ value: 'Dani' }];
    expect(buildPatch(original, draft, 'e2')?.etag).toBe('e2');
  });
});

describe('validateDraft', () => {
  it('blocks a half-entered birthday instead of letting it clear the field', () => {
    expect(validateDraft({ resourceName: 'p', birthdays: [{ date: { day: 4 } }] })).toMatch(
      /day and a month/,
    );
    expect(
      validateDraft({ resourceName: 'p', birthdays: [{ date: { month: 13, day: 1 } }] }),
    ).toMatch(/range/);
  });

  it('accepts complete, absent or year-less birthdays', () => {
    expect(
      validateDraft({ resourceName: 'p', birthdays: [{ date: { day: 4, month: 7 } }] }),
    ).toBeNull();
    expect(validateDraft({ resourceName: 'p' })).toBeNull();
    expect(validateDraft({ resourceName: 'p', birthdays: [{ text: 'spring' }] })).toBeNull();
  });
});
