/**
 * @fileoverview Tests for duplicate detection
 */

import type { Contact } from '../../types/Contact';
import { duplicateMemberSet, findDuplicateGroups, MAX_SHARED, nameKey } from '../duplicates';

const c = (id: string, overrides: Partial<Contact>): Contact => ({
  resourceName: `people/${id}`,
  ...overrides,
});

describe('findDuplicateGroups', () => {
  it('links the same phone written differently', () => {
    const groups = findDuplicateGroups([
      c('a', { names: [{ displayName: 'Dana' }], phoneNumbers: [{ value: '052-352-7960' }] }),
      c('b', { names: [{ displayName: 'Dana L' }], phoneNumbers: [{ value: '+972 52 352 7960' }] }),
      c('x', { names: [{ displayName: 'Other' }], phoneNumbers: [{ value: '050-0000000' }] }),
    ]);
    expect(groups).toHaveLength(1);
    expect(groups[0].members.map((m) => m.resourceName)).toEqual(['people/a', 'people/b']);
    expect(groups[0].reasons).toEqual(['phone']);
  });

  it('links emails case-insensitively and names by normalized text', () => {
    const groups = findDuplicateGroups([
      c('a', { emailAddresses: [{ value: 'Dana@Example.com' }] }),
      c('b', { emailAddresses: [{ value: 'dana@example.com ' }] }),
      c('c', { names: [{ displayName: 'Dana-Levi' }] }),
      c('d', { names: [{ displayName: 'dana levi' }] }),
    ]);
    expect(groups).toHaveLength(2);
    expect(groups.map((g) => g.reasons[0]).sort()).toEqual(['email', 'name']);
  });

  it('merges chains transitively into one group with every reason', () => {
    const groups = findDuplicateGroups([
      c('a', { names: [{ displayName: 'Dana Levi' }], phoneNumbers: [{ value: '0521111111' }] }),
      c('b', { phoneNumbers: [{ value: '052-111-1111' }], emailAddresses: [{ value: 'd@x.com' }] }),
      c('c', { emailAddresses: [{ value: 'D@x.com' }] }),
    ]);
    expect(groups).toHaveLength(1);
    expect(groups[0].members).toHaveLength(3);
    expect(groups[0].reasons).toEqual(['phone', 'email']);
  });

  it('does not link a contact to itself through two copies of its own number', () => {
    const groups = findDuplicateGroups([
      c('a', { phoneNumbers: [{ value: '0521111111' }, { value: '+972521111111' }] }),
    ]);
    expect(groups).toHaveLength(0);
  });

  it('ignores contacts without a real name for name matching', () => {
    expect(nameKey(c('a', { emailAddresses: [{ value: 'x@y.z' }] }))).toBeNull();
    expect(nameKey(c('a', { names: [{ displayName: 'Al' }] }))).toBeNull();
  });

  it('ignores a value shared by too many contacts, so a switchboard does not chain people', () => {
    const sharers = Array.from({ length: MAX_SHARED + 1 }, (_, i) =>
      c(`s${i}`, {
        names: [{ displayName: `Person ${i}` }],
        phoneNumbers: [{ value: '03-6000000' }],
      }),
    );
    expect(findDuplicateGroups(sharers)).toHaveLength(0);

    const few = sharers.slice(0, MAX_SHARED);
    expect(findDuplicateGroups(few)).toHaveLength(1);
  });

  it('matches names regardless of an honorific prefix', () => {
    const groups = findDuplicateGroups([
      c('a', {
        names: [
          {
            displayName: 'Dr. Dana Levi',
            honorificPrefix: 'Dr.',
            givenName: 'Dana',
            familyName: 'Levi',
          },
        ],
      }),
      c('b', { names: [{ displayName: 'Dana Levi', givenName: 'Dana', familyName: 'Levi' }] }),
    ]);
    expect(groups).toHaveLength(1);
  });

  it('exposes member resource names', () => {
    const groups = findDuplicateGroups([
      c('a', { phoneNumbers: [{ value: '0521111111' }] }),
      c('b', { phoneNumbers: [{ value: '0521111111' }] }),
    ]);
    expect(duplicateMemberSet(groups)).toEqual(new Set(['people/a', 'people/b']));
  });
});
