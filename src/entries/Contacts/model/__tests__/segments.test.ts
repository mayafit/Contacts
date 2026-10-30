import type { Contact } from '../../types/Contact';
import type { ActivityIndex, ContactActivity } from '../activity';
import {
  describeRule,
  labelDiff,
  mainCountryOf,
  matchesSegment,
  membersOf,
  normalizeOrganization,
  phoneCountriesOf,
  suggestSegments,
  tierOf,
  type SegmentContext,
} from '../segments';
import { goneQuiet, suggestedFavourites } from '../favourites';
import { callingCodeOf } from '../callingCodes';

const NOW = new Date('2026-10-31T12:00:00Z');
const daysAgo = (d: number) => new Date(NOW.getTime() - d * 86_400_000).toISOString();
const act = (score: number, lastDaysAgo: number | null, extra: Partial<ContactActivity> = {}) =>
  ({
    score,
    lastInTouch: lastDaysAgo === null ? null : daysAgo(lastDaysAgo),
    typicalGapDays: null,
    ...extra,
  }) as ContactActivity;
const c = (id: string, extra: Partial<Contact> = {}): Contact =>
  ({ resourceName: `people/${id}`, names: [{ givenName: id }], ...extra }) as Contact;
const ctx = (activity: ActivityIndex | null = null): SegmentContext => ({
  activity,
  mainCountry: '972',
  now: NOW,
});

describe('segments (AD-45)', () => {
  it('ranks relationships in tiers', () => {
    expect(tierOf(act(30, 10), NOW)).toBe('inner');
    expect(tierOf(act(30, 45), NOW)).toBe('regular');
    expect(tierOf(act(6, 90), NOW)).toBe('regular');
    expect(tierOf(act(5, 20), NOW)).toBe('occasional');
    expect(tierOf(act(0, 400), NOW)).toBe('dormant');
    expect(tierOf(act(0, null), NOW)).toBe('never');
    expect(tierOf(undefined, NOW)).toBe('never');
  });

  it('normalizes organizations', () => {
    expect(normalizeOrganization('Elta Systems Ltd.')).toBe('elta systems');
    expect(normalizeOrganization('ELTA SYSTEMS')).toBe('elta systems');
    expect(normalizeOrganization('אלתא מערכות בע"מ')).toBe('אלתא מערכות');
    expect(normalizeOrganization('אלתא מערכות בע״מ')).toBe('אלתא מערכות');
    expect(normalizeOrganization('Acme Inc')).toBe('acme');
    // A suffix alone is the name
    expect(normalizeOrganization('Company')).toBe('company');
  });

  it('finds the phone countries', () => {
    expect(callingCodeOf('972541234567')).toBe('972');
    expect(callingCodeOf('14155550100')).toBe('1');
    expect(callingCodeOf('447700900123')).toBe('44');
    const contact = c('a', {
      phoneNumbers: [
        { value: '054-123-4567' },
        { value: '+44 7700 900123' },
        { value: '0049 30 1234567' },
        { value: '1-800', canonicalForm: '+14155550100' },
      ],
    });
    expect([...phoneCountriesOf(contact, '972')].sort()).toEqual(['1', '44', '49', '972']);
    expect(mainCountryOf([c('x', { phoneNumbers: [{ value: '+972 54 1' }] })])).toBe('972');
    expect(mainCountryOf([])).toBe('972');
  });

  it('evaluates rules, all or any', () => {
    const dana = c('dana', {
      labels: ['contactGroups/starred', 'contactGroups/fam'],
      organizations: [{ name: 'Elta Systems Ltd' }],
      emailAddresses: [{ value: 'dana@elta.co.il' }],
      birthdays: [{ date: { month: 3, day: 2 } }],
    });
    const ron = c('רון', { phoneNumbers: [{ value: '+1 415 555 0100' }] });
    const activity: ActivityIndex = new Map([[dana.resourceName, act(30, 5)]]);
    const all = [dana, ron];
    const seg = (match: 'all' | 'any', rules: Parameters<typeof matchesSegment>[1]['rules']) =>
      membersOf(all, { match, rules }, ctx(activity)).map((x) => x.names?.[0]?.givenName);

    expect(
      seg('all', [
        { kind: 'favourite', value: true },
        { kind: 'organization', op: 'is', value: 'ELTA SYSTEMS' },
      ]),
    ).toEqual(['dana']);
    expect(seg('all', [{ kind: 'organization', op: 'contains', value: 'elta' }])).toEqual(['dana']);
    expect(seg('all', [{ kind: 'emailDomain', value: 'Elta.co.il' }])).toEqual(['dana']);
    expect(seg('all', [{ kind: 'phoneCountry', value: '1' }])).toEqual(['רון']);
    expect(seg('all', [{ kind: 'script', value: 'hebrew' }])).toEqual(['רון']);
    expect(seg('all', [{ kind: 'field', op: 'missing', value: 'email' }])).toEqual(['רון']);
    expect(seg('all', [{ kind: 'birthdayMonth', value: 3 }])).toEqual(['dana']);
    expect(seg('all', [{ kind: 'label', op: 'notIn', value: 'contactGroups/fam' }])).toEqual([
      'רון',
    ]);
    expect(seg('all', [{ kind: 'tier', value: 'inner' }])).toEqual(['dana']);
    expect(seg('all', [{ kind: 'lastTouch', op: 'never' }])).toEqual(['רון']);
    expect(seg('all', [{ kind: 'lastTouch', op: 'within', days: 7 }])).toEqual(['dana']);
    expect(
      seg('any', [
        { kind: 'birthdayMonth', value: 3 },
        { kind: 'phoneCountry', value: '1' },
      ]),
    ).toEqual(['dana', 'רון']);
    // No rule: all is everyone, any is no one
    expect(seg('all', [])).toHaveLength(2);
    expect(seg('any', [])).toHaveLength(0);
    // Without the activity, its rules match no one
    expect(
      membersOf(all, { match: 'all', rules: [{ kind: 'lastTouch', op: 'never' }] }, ctx(null)),
    ).toEqual([]);
  });

  it('finds what a linked label lacks and holds beyond the segment', () => {
    const a = c('a', { labels: ['contactGroups/L'] });
    const b = c('b');
    const z = c('z', { labels: ['contactGroups/L'] });
    const diff = labelDiff([a, b], [a, b, z], 'contactGroups/L');
    expect(diff.add.map((x) => x.resourceName)).toEqual(['people/b']);
    expect(diff.remove.map((x) => x.resourceName)).toEqual(['people/z']);
  });

  it('suggests groups of 3 or more, not the main country, tiers always', () => {
    const at = (id: string, org: string, email: string, phone: string) =>
      c(id, {
        organizations: [{ name: org }],
        emailAddresses: [{ value: email }],
        phoneNumbers: [{ value: phone }],
      });
    const list = [
      at('a', 'Elta Ltd', 'a@elta.co.il', '+44 1'),
      at('b', 'ELTA', 'b@elta.co.il', '+44 2'),
      at('c', 'elta', 'c@gmail.com', '+44 3'),
      at('d', 'Other', 'd@gmail.com', '054 1'),
    ];
    const activity: ActivityIndex = new Map([['people/a', act(30, 1)]]);
    const found = suggestSegments(list, ctx(activity));
    const keys = found.map((s) => `${s.key}=${s.members.length}`);
    expect(keys).toContain('organization:elta=3');
    expect(keys).toContain('phoneCountry:44=3');
    expect(keys.some((k) => k.startsWith('emailDomain:'))).toBe(false); // 2 at elta, gmail is personal
    expect(keys.some((k) => k.startsWith('phoneCountry:972'))).toBe(false);
    expect(keys).toContain('tier:inner=1');
    expect(keys).toContain('tier:never=3');
    expect(found.find((s) => s.key === 'organization:elta')?.rule).toEqual({
      kind: 'organization',
      op: 'is',
      value: 'Elta Ltd',
    });
  });

  it('describes rules in words', () => {
    expect(describeRule({ kind: 'phoneCountry', value: '44' }, {})).toBe(
      'Phone in United Kingdom (+44)',
    );
    expect(describeRule({ kind: 'field', op: 'missing', value: 'phonetic' }, {})).toBe(
      'Has no phonetic name',
    );
    expect(describeRule({ kind: 'lastTouch', op: 'olderThan', days: 365 }, {})).toBe(
      'In touch more than 365 days ago',
    );
  });
});

describe('favourites (AD-44 rule 6)', () => {
  const fav = (id: string) => c(id, { labels: ['contactGroups/starred'] });

  it('lists favourites gone quiet, longest silence first', () => {
    const list = [fav('recent'), fav('old'), fav('never'), fav('overdue'), c('notfav')];
    const activity: ActivityIndex = new Map([
      ['people/recent', act(10, 5)],
      ['people/old', act(0, 400)],
      ['people/overdue', act(4, 120, { typicalGapDays: 30 })],
      ['people/notfav', act(0, 999)],
    ]);
    expect(
      goneQuiet(list, activity, NOW).map((q) => [q.contact.resourceName, q.daysSince]),
    ).toEqual([
      ['people/old', 400],
      ['people/overdue', 120],
      ['people/never', null],
    ]);
  });

  it('suggests inner-circle and regular people who are not favourites', () => {
    const list = [c('a'), c('b'), c('c'), fav('d')];
    const activity: ActivityIndex = new Map([
      ['people/a', act(8, 20)],
      ['people/b', act(40, 3)],
      ['people/c', act(3, 10)],
      ['people/d', act(50, 1)],
    ]);
    expect(suggestedFavourites(list, activity, NOW).map((x) => x.resourceName)).toEqual([
      'people/b',
      'people/a',
    ]);
  });
});
