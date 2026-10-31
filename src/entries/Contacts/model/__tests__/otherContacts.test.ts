import type { Contact } from '../../types/Contact';
import type { OtherContactEntry, PersonActivity } from '../../../../services/api/BackendApiClient';
import { buildOtherSuggestions, localPartKey, wordKey } from '../otherContacts';

const contact = (id: string, given: string, family: string, extra: Partial<Contact> = {}) =>
  ({
    resourceName: id,
    etag: 'e',
    names: [{ givenName: given, familyName: family, displayName: `${given} ${family}` }],
    ...extra,
  }) as Contact;

const entry = (
  name: string | null,
  emails: string[],
  phones: string[] = [],
): OtherContactEntry => ({
  resourceName: `otherContacts/${emails[0]}`,
  name,
  emails,
  phones,
});

const activity = (sent: number, received = 0, meetings = 0): PersonActivity => ({
  sentCount: sent,
  receivedCount: received,
  emailMonths: {},
  meetingMonths: meetings ? { '2026-09': meetings } : {},
  lastEmailAt: '2026-09-01T00:00:00Z',
});

const build = (
  entries: OtherContactEntry[],
  contacts: Contact[],
  opts: {
    all?: Contact[];
    people?: Record<string, PersonActivity> | null;
    dismissed?: string[];
  } = {},
) =>
  buildOtherSuggestions(
    entries,
    contacts,
    opts.all ?? contacts,
    opts.people === undefined ? {} : opts.people,
    new Set(opts.dismissed ?? []),
  );

describe('suggestions from Other contacts (AD-48)', () => {
  it('compares names as sets of words, folded; one word never matches', () => {
    expect(wordKey('Abergel Edna')).toBe(wordKey('Edna Abergel'));
    expect(wordKey('Abergel  Edna')).toBe('abergel edna');
    // Vowel points are folded away, and the order does not matter
    expect(wordKey('אַיֶּלֶת שרמן')).toBe(wordKey('שרמן אילת'));
    expect(wordKey('Edna')).toBeNull();
    expect(localPartKey('edna.abergel2@bankleumi.co.il')).toBe('abergel edna');
    expect(localPartKey('eabergel@bankleumi.co.il')).toBeNull();
  });

  it('a reversed name gives a contact without an email its address', () => {
    const edna = contact('people/1', 'Edna', 'Abergel');
    const { updates } = build([entry('Abergel Edna', ['edna.abergel@bankleumi.co.il'])], [edna]);
    expect(updates).toHaveLength(1);
    expect(updates[0]).toMatchObject({
      kind: 'missing',
      emails: ['edna.abergel@bankleumi.co.il'],
      reasons: ['name'],
      names: ['Abergel Edna'],
    });
    expect(updates[0].key).toBe('u:people/1:edna.abergel@bankleumi.co.il');
  });

  it('a known address brings the other address and the phone to its contact', () => {
    const dana = contact('people/1', 'Dana', 'Cohen', {
      emailAddresses: [{ value: 'Dana@Work.com' }],
      phoneNumbers: [{ value: '050-111-2222' }],
    });
    const { updates } = build(
      [entry(null, ['dana@work.com', 'dana.home@gmail.com'], ['+972 50 111 2222', '052-999-8888'])],
      [dana],
    );
    expect(updates[0]).toMatchObject({
      kind: 'extra',
      emails: ['dana.home@gmail.com'],
      phones: ['052-999-8888'],
      reasons: ['email'],
    });
  });

  it('matches by phone before the name', () => {
    const a = contact('people/1', 'Dana', 'Cohen', { phoneNumbers: [{ value: '0501234567' }] });
    const b = contact('people/2', 'Dana', 'Levi');
    const { updates } = build([entry('Dana Levi', ['dl@x.com'], ['+972-50-123-4567'])], [a, b]);
    expect(updates.map((u) => [u.contact.resourceName, u.reasons])).toEqual([
      ['people/1', ['phone']],
    ]);
  });

  it('the address spells the name when there is no name', () => {
    const { updates } = build(
      [entry(null, ['dana_cohen@x.com'])],
      [contact('people/1', 'Dana', 'Cohen')],
    );
    expect(updates[0].reasons).toEqual(['address']);
  });

  it('a name shared by two contacts suggests nothing, and never falls to a weaker step', () => {
    const r = build(
      [entry('Dana Cohen', ['dana.cohen@x.com'])],
      [contact('people/1', 'Dana', 'Cohen'), contact('people/2', 'Dana', 'Cohen')],
    );
    expect(r.updates).toEqual([]);
    expect(r.saves).toEqual([]);
    expect(r.ambiguous).toBe(1);
  });

  it('an address on a contact in Trash is never suggested, nor its owner saved', () => {
    const trashed = contact('people/9', 'Ayelet', 'Sherman', {
      emailAddresses: [{ value: 'ayelet@x.com' }],
    });
    const active = contact('people/1', 'Ayelet', 'Sherman');
    const r = build([entry('Ayelet Sherman', ['ayelet@x.com'])], [active], {
      all: [active, trashed],
      people: { 'ayelet@x.com': activity(5) },
    });
    expect(r.updates).toEqual([]);
    expect(r.saves).toEqual([]);
  });

  it('saves people the Owner wrote to or met, most in touch first, and only with activity', () => {
    const people = {
      'a@x.com': activity(1),
      'b@x.com': activity(4, 2),
      'c@x.com': activity(0, 30),
      'd@x.com': activity(0, 0, 2),
    };
    const entries = [
      entry('Avi A', ['a@x.com']),
      entry('Beni B', ['b@x.com'], ['050-1']),
      entry('Carmel C', ['c@x.com']),
      entry(null, ['d@x.com']),
    ];
    const r = build(entries, [], { people });
    expect(r.saves.map((s) => s.emails[0])).toEqual(['b@x.com', 'd@x.com', 'a@x.com']);
    expect(r.saves[0]).toMatchObject({ key: 'n:b@x.com', name: 'Beni B', sent: 4, received: 2 });
    expect(r.saves[1]).toMatchObject({ name: null, meetings: 2 });
    expect(build(entries, [], { people: null }).saves).toEqual([]);
  });

  it('leaves out dismissed suggestions, and shows one again when it changes', () => {
    const edna = contact('people/1', 'Edna', 'Abergel');
    const one = [entry('Abergel Edna', ['e@x.com'])];
    const key = build(one, [edna]).updates[0].key;
    expect(build(one, [edna], { dismissed: [key] }).updates).toEqual([]);
    const two = [entry('Abergel Edna', ['e@x.com']), entry('Edna Abergel', ['e2@x.com'])];
    const merged = build(two, [edna], { dismissed: [key] }).updates;
    expect(merged).toHaveLength(1);
    expect(merged[0].emails).toEqual(['e@x.com', 'e2@x.com']);
  });

  it('lists contacts without an email first', () => {
    const r = build(
      [entry('Avi Aharon', ['avi@x.com']), entry('Zvi Zur', ['zvi@x.com'])],
      [
        contact('people/1', 'Avi', 'Aharon', { emailAddresses: [{ value: 'old@x.com' }] }),
        contact('people/2', 'Zvi', 'Zur'),
      ],
    );
    expect(r.updates.map((u) => u.kind)).toEqual(['missing', 'extra']);
  });
});
