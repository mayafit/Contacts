/**
 * @fileoverview Tests for contactView helpers
 */

import type { Contact } from '../../types/Contact';
import {
  classifyPhone,
  editableName,
  getDisplayName,
  getLabelNames,
  getPhoneticName,
  indexLetterOf,
  initialsOf,
  matchesQuery,
  phoneKey,
  scriptOf,
  splitFullName,
} from '../contactView';

const contact = (overrides: Partial<Contact>): Contact => ({
  resourceName: 'people/c1',
  ...overrides,
});

describe('getDisplayName', () => {
  it('prefers Google displayName', () => {
    expect(getDisplayName(contact({ names: [{ displayName: 'דנה לוי', givenName: 'x' }] }))).toBe(
      'דנה לוי',
    );
  });

  it('falls back to name parts, then nickname, organization, email and phone', () => {
    expect(getDisplayName(contact({ names: [{ givenName: 'Dana', familyName: 'Levi' }] }))).toBe(
      'Dana Levi',
    );
    expect(getDisplayName(contact({ nicknames: [{ value: 'Dudu' }] }))).toBe('Dudu');
    expect(getDisplayName(contact({ organizations: [{ name: 'Elta' }] }))).toBe('Elta');
    expect(getDisplayName(contact({ emailAddresses: [{ value: 'a@b.c' }] }))).toBe('a@b.c');
    expect(getDisplayName(contact({ phoneNumbers: [{ value: '050-1234567' }] }))).toBe(
      '050-1234567',
    );
    expect(getDisplayName(contact({}))).toBe('(no name)');
  });
});

describe('getPhoneticName', () => {
  it('joins phonetic parts', () => {
    expect(
      getPhoneticName(
        contact({ names: [{ phoneticGivenName: 'Shmulik', phoneticFamilyName: 'Mosek' }] }),
      ),
    ).toBe('Shmulik Mosek');
  });

  it('falls back to the full phonetic form', () => {
    expect(getPhoneticName(contact({ names: [{ phoneticFullName: 'Dana Levi' }] }))).toBe(
      'Dana Levi',
    );
  });

  it('is empty when there is none', () => {
    expect(getPhoneticName(contact({ names: [{ displayName: 'דנה' }] }))).toBe('');
  });
});

describe('splitFullName / editableName', () => {
  it('splits on the first word', () => {
    expect(splitFullName('  Shmulik   Mosek Baruch ')).toEqual({
      first: 'Shmulik',
      last: 'Mosek Baruch',
    });
    expect(splitFullName('Madonna')).toEqual({ first: 'Madonna', last: '' });
    expect(splitFullName('')).toEqual({ first: '', last: '' });
  });

  it('fills phonetic parts from the full form so an edit does not drop it', () => {
    expect(editableName({ phoneticFullName: 'Dana Levi' })).toMatchObject({
      phoneticGivenName: 'Dana',
      phoneticFamilyName: 'Levi',
    });
  });

  it('keeps existing phonetic parts untouched', () => {
    expect(
      editableName({ phoneticFullName: 'X Y', phoneticGivenName: 'Dana' }).phoneticGivenName,
    ).toBe('Dana');
  });
});

describe('phoneKey', () => {
  it('matches local and international forms of the same number', () => {
    expect(phoneKey('052-352-7960')).toBe(phoneKey('+972 52-352-7960'));
    expect(phoneKey('0523527960')).toBe('523527960');
  });

  it('prefers canonical form and ignores very short numbers', () => {
    expect(phoneKey('weird', '+972523527960')).toBe('523527960');
    expect(phoneKey('*9')).toBeNull();
  });
});

describe('classifyPhone', () => {
  it.each([
    ['052-488-8854', 'il-mobile'],
    ['+972 54-227-0763', 'il-mobile'],
    ['00972546730000', 'il-mobile'],
    ['03-6123456', 'il-landline'],
    ['077-1234567', 'il-landline'],
    ['+1 415 555 0100', 'international'],
    ['12345', 'unparseable'],
  ])('%s -> %s', (value, expected) => {
    expect(classifyPhone(value)).toBe(expected);
  });
});

describe('scriptOf / indexLetterOf / initialsOf', () => {
  it('detects scripts', () => {
    expect(scriptOf('שמוליק')).toBe('hebrew');
    expect(scriptOf('Dana')).toBe('latin');
    expect(scriptOf('שלי 6n')).toBe('mixed');
    expect(scriptOf('')).toBe('none');
  });

  it('files names under a letter, folding Hebrew finals', () => {
    expect(indexLetterOf('שמעון')).toBe('ש');
    expect(indexLetterOf('ךלום')).toBe('כ');
    expect(indexLetterOf('dana')).toBe('D');
    expect(indexLetterOf('  "Bob"')).toBe('B');
    expect(indexLetterOf('123')).toBe('#');
  });

  it('builds initials', () => {
    expect(initialsOf('Dana Levi')).toBe('DL');
    expect(initialsOf('שמוליק מוסק ברוך')).toBe('שב');
    expect(initialsOf('+972')).toBe('#');
  });
});

describe('matchesQuery', () => {
  const c = contact({
    names: [{ displayName: 'שמוליק מוסק', phoneticGivenName: 'Shmulik' }],
    phoneNumbers: [{ value: '+972 54-227-0763' }],
    emailAddresses: [{ value: 'Shmulik@Example.com' }],
  });

  it('matches every word across fields, case-insensitively', () => {
    expect(matchesQuery(c, 'shmulik example')).toBe(true);
    expect(matchesQuery(c, 'מוסק')).toBe(true);
    expect(matchesQuery(c, 'shmulik nope')).toBe(false);
  });

  it('matches phone digits regardless of formatting and leading 0', () => {
    expect(matchesQuery(c, '054-2270')).toBe(true);
    expect(matchesQuery(c, '0542270763')).toBe(true);
    expect(matchesQuery(c, '999')).toBe(false);
  });

  it('does not turn a local prefix like 050 into a match on every "50"', () => {
    const other = { resourceName: 'p2', phoneNumbers: [{ value: '052-150-1234' }] };
    const local = { resourceName: 'p3', phoneNumbers: [{ value: '+972 50-123-4567' }] };
    expect(matchesQuery(other, '050')).toBe(false);
    expect(matchesQuery(local, '050')).toBe(true);
  });
});

describe('getLabelNames', () => {
  it('hides system groups and resolves names', () => {
    const groups = {
      'contactGroups/abc': { resourceName: 'contactGroups/abc', name: 'Family' },
      'contactGroups/myContacts': { resourceName: 'contactGroups/myContacts', name: 'My Contacts' },
    };
    expect(
      getLabelNames(contact({ labels: ['contactGroups/myContacts', 'contactGroups/abc'] }), groups),
    ).toEqual(['Family']);
  });
});
