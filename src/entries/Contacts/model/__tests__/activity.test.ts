import {
  buildActivityIndex,
  correspondentsNotInContacts,
  formatRelative,
  formatRelativeShort,
  goneQuiet,
  lastMonths,
  mostInTouch,
  neverInTouch,
} from '../activity';
import type { Contact } from '../../types/Contact';
import type { PersonActivity } from '../../../../services/api/BackendApiClient';

const NOW = new Date('2026-10-18T12:00:00Z');

const contact = (rn: string, ...emails: string[]): Contact =>
  ({
    resourceName: rn,
    etag: 'e',
    emailAddresses: emails.map((value) => ({ value })),
  }) as Contact;

const person = (p: Partial<PersonActivity>): PersonActivity => ({
  sentCount: 0,
  receivedCount: 0,
  emailMonths: {},
  meetingMonths: {},
  ...p,
});

describe('activity model', () => {
  it('lists the last 12 month keys', () => {
    const keys = lastMonths(NOW, 12);
    expect(keys.size).toBe(12);
    expect(keys.has('2026-10')).toBe(true);
    expect(keys.has('2025-11')).toBe(true);
    expect(keys.has('2025-10')).toBe(false);
  });

  it('combines a contact’s addresses case-insensitively', () => {
    const people = {
      'a@x.com': person({
        lastEmailAt: '2026-09-01T00:00:00Z',
        sentCount: 2,
        emailMonths: { '2026-09': 2, '2024-01': 5 },
      }),
      'a@home.com': person({
        lastMeetingAt: '2026-10-01T00:00:00Z',
        lastMeetingTitle: 'Sync',
        meetingMonths: { '2026-10': 1 },
        receivedCount: 1,
      }),
    };
    const index = buildActivityIndex([contact('people/a', 'A@x.com', 'a@home.com')], people, NOW);
    const a = index.get('people/a')!;
    expect(a.lastInTouch).toBe('2026-10-01T00:00:00Z');
    expect(a.lastMeetingTitle).toBe('Sync');
    expect(a.emails12m).toBe(2);
    expect(a.meetings12m).toBe(1);
    expect(a.score).toBe(5);
    expect(a.sent + a.received).toBe(3);
  });

  it('ranks most in touch, gone quiet and never in touch', () => {
    const contacts = [
      contact('people/busy', 'busy@x.com'),
      contact('people/quiet', 'quiet@x.com'),
      contact('people/never', 'never@x.com'),
      contact('people/nophone'),
    ];
    const people = {
      'busy@x.com': person({
        lastEmailAt: '2026-10-10T00:00:00Z',
        sentCount: 5,
        emailMonths: { '2026-10': 5 },
      }),
      'quiet@x.com': person({
        lastEmailAt: '2026-01-10T00:00:00Z',
        sentCount: 3,
        receivedCount: 2,
        emailMonths: { '2026-01': 5 },
      }),
    };
    const index = buildActivityIndex(contacts, people, NOW);
    expect(mostInTouch(contacts, index).map((r) => r.contact.resourceName)).toEqual([
      'people/busy',
      'people/quiet',
    ]);
    expect(goneQuiet(contacts, index, NOW).map((r) => r.contact.resourceName)).toEqual([
      'people/quiet',
    ]);
    expect(neverInTouch(contacts, index).map((c) => c.resourceName)).toEqual(['people/never']);
  });

  it('suggests people you write to who are not contacts', () => {
    const people = {
      'known@x.com': person({ sentCount: 9 }),
      'new@x.com': person({ sentCount: 4, receivedCount: 2 }),
      'once@x.com': person({ sentCount: 1 }),
    };
    expect(
      correspondentsNotInContacts(people, [contact('people/k', 'Known@x.com')]).map((c) => c.email),
    ).toEqual(['new@x.com']);
  });

  it('formats relative dates', () => {
    expect(formatRelative('2026-10-18T08:00:00Z', NOW)).toBe('today');
    expect(formatRelative('2026-10-15T08:00:00Z', NOW)).toBe('3 days ago');
    expect(formatRelative('2026-07-01T00:00:00Z', NOW)).toBe('4 months ago');
    expect(formatRelative('2024-01-01T00:00:00Z', NOW)).toBe('2 years ago');
    expect(formatRelative('2026-10-25T12:00:00Z', NOW)).toBe('in 7 days');
    expect(formatRelative(null, NOW)).toBe('');
    expect(formatRelativeShort('2026-07-01T00:00:00Z', NOW)).toBe('4mo ago');
    expect(formatRelativeShort('2026-10-15T08:00:00Z', NOW)).toBe('3d ago');
  });
});
