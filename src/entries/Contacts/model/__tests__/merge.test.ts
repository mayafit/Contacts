/**
 * @fileoverview Tests for merge planning
 */

import type { Contact } from '../../types/Contact';
import { isHebrewEnglishPair, planMerge, suggestPrimary } from '../merge';

const main: Contact = {
  resourceName: 'people/main',
  etag: 'e-main',
  names: [{ displayName: 'שמוליק מוסק', givenName: 'שמוליק', familyName: 'מוסק' }],
  phoneNumbers: [{ value: '054-227-0763', type: 'mobile' }],
  emailAddresses: [{ value: 'shmulik@x.com' }],
  labels: ['contactGroups/myContacts', 'contactGroups/fam'],
  biographies: [{ value: 'met at work' }],
};

const other: Contact = {
  resourceName: 'people/other',
  etag: 'e-other',
  names: [
    {
      displayName: 'Shmulik',
      givenName: 'Shmulik',
      phoneticGivenName: 'Shmulik',
      phoneticFamilyName: 'Mosek',
    },
  ],
  phoneNumbers: [
    { value: '+972 54-227-0763', type: 'mobile' },
    { value: '03-6123456', type: 'home' },
  ],
  emailAddresses: [{ value: 'SHMULIK@x.com' }, { value: 'mosek@work.com' }],
  organizations: [{ name: 'Elta' }],
  birthdays: [{ date: { day: 1, month: 2 } }],
  labels: ['contactGroups/myContacts', 'contactGroups/work'],
  biographies: [{ value: 'plays guitar' }],
};

describe('planMerge', () => {
  const plan = planMerge(main, [other]);

  it("keeps the main contact's name and fills only its empty parts", () => {
    expect(plan.merged.names?.[0]).toMatchObject({
      givenName: 'שמוליק',
      familyName: 'מוסק',
      phoneticGivenName: 'Shmulik',
      phoneticFamilyName: 'Mosek',
    });
  });

  it('unions phones and emails without duplicates in any format or case', () => {
    expect(plan.merged.phoneNumbers?.map((p) => p.value)).toEqual(['054-227-0763', '03-6123456']);
    expect(plan.merged.emailAddresses?.map((e) => e.value)).toEqual([
      'shmulik@x.com',
      'mosek@work.com',
    ]);
  });

  it('takes groups the main contact lacks and joins distinct notes', () => {
    expect(plan.merged.organizations).toEqual([{ name: 'Elta' }]);
    expect(plan.merged.birthdays).toEqual([{ date: { day: 1, month: 2 } }]);
    expect(plan.merged.biographies?.[0].value).toBe('met at work\n\nplays guitar');
  });

  it('carries over user labels only', () => {
    expect(plan.addLabels).toEqual(['contactGroups/work']);
  });

  it("patches only changed groups, with the main contact's etag", () => {
    expect(plan.patch?.etag).toBe('e-main');
    expect(Object.keys(plan.patch!).sort()).toEqual([
      'biographies',
      'birthdays',
      'emailAddresses',
      'etag',
      'names',
      'organizations',
      'phoneNumbers',
    ]);
  });

  it('needs no patch when the others add nothing', () => {
    const copy: Contact = { ...main, resourceName: 'people/copy', labels: ['contactGroups/fam'] };
    const p = planMerge(main, [copy]);
    expect(p.patch).toBeNull();
    expect(p.addLabels).toEqual([]);
  });
});

describe('planMerge safety', () => {
  it("never drops the kept contact's own entries, even without a name or title", () => {
    const kept: Contact = {
      resourceName: 'k',
      etag: 'e',
      organizations: [{ jobDescription: 'volunteer', current: true }],
      addresses: [{ countryCode: 'IL', type: 'home' }],
    };
    const plan = planMerge(kept, [{ resourceName: 'o', organizations: [{ name: 'Acme' }] }]);
    expect(plan.merged.organizations).toEqual([
      { jobDescription: 'volunteer', current: true },
      { name: 'Acme' },
    ]);
    expect(plan.merged.addresses).toEqual([{ countryCode: 'IL', type: 'home' }]);
  });

  it('fills missing details of a matching entry instead of dropping them', () => {
    const plan = planMerge(
      { resourceName: 'k', etag: 'e', phoneNumbers: [{ value: '052-1111111' }] },
      [{ resourceName: 'o', phoneNumbers: [{ value: '+972 52 111 1111', type: 'work' }] }],
    );
    expect(plan.merged.phoneNumbers).toEqual([{ value: '052-1111111', type: 'work' }]);
  });

  it('keeps line breaks in notes', () => {
    const plan = planMerge(
      { resourceName: 'k', etag: 'e', biographies: [{ value: 'line 1\nline 2' }] },
      [{ resourceName: 'o', biographies: [{ value: 'other\nnote' }] }],
    );
    expect(plan.merged.biographies?.[0].value).toBe('line 1\nline 2\n\nother\nnote');
  });

  it('keeps different international numbers that share their last 9 digits', () => {
    const plan = planMerge(
      { resourceName: 'k', etag: 'e', phoneNumbers: [{ value: '+1 212-555-1234' }] },
      [{ resourceName: 'o', phoneNumbers: [{ value: '+1 312-555-1234' }] }],
    );
    expect(plan.merged.phoneNumbers).toHaveLength(2);
  });

  it('reports a conflicting birthday or name part instead of dropping it silently', () => {
    const plan = planMerge(
      {
        resourceName: 'k',
        etag: 'e',
        names: [{ givenName: 'Bob' }],
        birthdays: [{ date: { day: 1, month: 2 } }],
      },
      [
        {
          resourceName: 'o',
          names: [{ displayName: 'Robert', givenName: 'Robert' }],
          birthdays: [{ date: { day: 3, month: 4 } }],
        },
      ],
    );
    expect(plan.dropped).toEqual(['Robert: first name "Robert"', 'Robert: birthday 3/4']);
  });

  it('does not try to add system labels Google refuses', () => {
    const plan = planMerge({ resourceName: 'k', etag: 'e' }, [
      {
        resourceName: 'o',
        labels: ['contactGroups/friends', 'contactGroups/starred', 'contactGroups/abc123'],
      },
    ]);
    expect(plan.addLabels).toEqual(['contactGroups/starred', 'contactGroups/abc123']);
  });
});

describe('Hebrew / English pairs', () => {
  const hebrew: Contact = {
    resourceName: 'people/he',
    etag: 'e-he',
    names: [{ displayName: 'אייל רייצס', givenName: 'אייל', familyName: 'רייצס' }],
    phoneNumbers: [{ value: '054-326-7307' }],
    emailAddresses: [{ value: 'eyalrit@gmail.com' }],
  };
  const english: Contact = {
    resourceName: 'people/en',
    etag: 'e-en',
    names: [{ displayName: 'Eyal Ritzes', givenName: 'Eyal', familyName: 'Ritzes' }],
    phoneNumbers: [{ value: '054-326-7307' }],
    emailAddresses: [{ value: 'eyalrit@gmail.com' }],
    updateTime: '2026-10-01T00:00:00Z',
  };

  it('recognizes the pair and suggests keeping the Hebrew contact', () => {
    expect(isHebrewEnglishPair([hebrew, english])).toBe(true);
    expect(suggestPrimary([english, hebrew])).toBe('people/he');
  });

  it('stores the English name as the phonetic name instead of reporting a conflict', () => {
    const plan = planMerge(hebrew, [english]);
    expect(plan.merged.names?.[0]).toMatchObject({
      givenName: 'אייל',
      familyName: 'רייצס',
      phoneticGivenName: 'Eyal',
      phoneticFamilyName: 'Ritzes',
    });
    expect(plan.transliterated).toEqual(['Eyal Ritzes']);
    expect(plan.dropped).toEqual([]);
    expect(Object.keys(plan.patch!).sort()).toEqual(['etag', 'names']);
  });

  it('reports the English name when a different phonetic name is already set', () => {
    const withPhonetic: Contact = {
      ...hebrew,
      names: [{ ...hebrew.names![0], phoneticGivenName: 'Eial' }],
    };
    expect(planMerge(withPhonetic, [english]).dropped[0]).toMatch(/English name "Eyal Ritzes"/);
  });
});

describe('suggestPrimary', () => {
  it('prefers the contact with more information, then the most recent', () => {
    // main (Hebrew) and other (English) are a pair: the Hebrew one is kept
    // even though other holds more data
    expect(suggestPrimary([main, other])).toBe('people/main');
    const a: Contact = {
      resourceName: 'a',
      names: [{ givenName: 'A' }],
      updateTime: '2025-01-01T00:00:00Z',
    };
    const b: Contact = {
      resourceName: 'b',
      names: [{ givenName: 'B' }],
      updateTime: '2026-01-01T00:00:00Z',
    };
    expect(suggestPrimary([a, b])).toBe('b');
  });
});
