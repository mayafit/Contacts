import { awaitingReply, buildReachOut, contactsByEmail, daysToBirthday } from '../reachOut';
import { buildActivityIndex, describeGap, overdueOf, typicalGapOf } from '../activity';
import type { Contact } from '../../types/Contact';
import type { PersonActivity, ThreadState } from '../../../../services/api/BackendApiClient';

const NOW = new Date('2026-10-21T12:00:00Z');

const contact = (rn: string, email?: string, extra: Partial<Contact> = {}): Contact =>
  ({
    resourceName: rn,
    etag: 'e',
    names: [{ givenName: rn.slice(-1).toUpperCase(), familyName: 'X' }],
    emailAddresses: email ? [{ value: email }] : [],
    ...extra,
  }) as Contact;

const thread = (p: Partial<ThreadState>): ThreadState => ({
  threadId: 't',
  lastAt: '2026-10-18T12:00:00Z',
  lastFromMe: false,
  counterparts: ['a@x.com'],
  subject: 'Proposal',
  inInbox: true,
  messages: 2,
  ...p,
});

describe('cadence', () => {
  it('takes the median gap between active months', () => {
    expect(typicalGapOf(['2026-01', '2026-02'])).toBeNull();
    expect(typicalGapOf(['2026-01', '2026-02', '2026-03', '2026-04'])).toBe(30);
    expect(typicalGapOf(['2025-10', '2026-01', '2026-04', '2026-07'])).toBe(90);
    expect(describeGap(30)).toBe('about monthly');
    expect(describeGap(90)).toBe('every ~3 months');
  });

  it('flags contacts overdue against their rhythm', () => {
    const monthly = {
      'a@x.com': {
        sentCount: 6,
        receivedCount: 6,
        lastEmailAt: '2026-08-01T00:00:00Z',
        emailMonths: {
          '2026-03': 2,
          '2026-04': 2,
          '2026-05': 2,
          '2026-06': 2,
          '2026-07': 2,
          '2026-08': 2,
        },
        meetingMonths: {},
      } as PersonActivity,
    };
    const index = buildActivityIndex([contact('people/a', 'a@x.com')], monthly, NOW);
    const o = overdueOf(index.get('people/a'), NOW);
    expect(o?.typicalGapDays).toBe(30);
    expect(o?.daysSince).toBeGreaterThan(70);
    // Recent contact is not overdue
    expect(overdueOf(index.get('people/a'), new Date('2026-08-20T00:00:00Z'))).toBeNull();
  });
});

describe('reach out', () => {
  it('splits threads into replies owed and answers awaited', () => {
    const a = contact('people/a', 'a@x.com');
    const { owe, waiting } = awaitingReply(
      [
        thread({ threadId: 'owe' }),
        thread({ threadId: 'archived', inInbox: false }),
        thread({ threadId: 'wait', lastFromMe: true, lastAt: '2026-10-10T12:00:00Z' }),
        thread({ threadId: 'fresh', lastFromMe: true, lastAt: '2026-10-20T12:00:00Z' }),
      ],
      contactsByEmail([a]),
      NOW,
    );
    expect(owe.map((o) => o.thread.threadId)).toEqual(['owe']);
    expect(owe[0].contact).toBe(a);
    expect(waiting.map((w) => w.thread.threadId)).toEqual(['wait']);
  });

  it('counts days to the next birthday', () => {
    expect(
      daysToBirthday(
        contact('people/b', undefined, { birthdays: [{ date: { month: 10, day: 21 } }] }),
        NOW,
      ),
    ).toBe(0);
    expect(
      daysToBirthday(
        contact('people/b', undefined, { birthdays: [{ date: { month: 10, day: 24 } }] }),
        NOW,
      ),
    ).toBe(3);
    expect(
      daysToBirthday(
        contact('people/b', undefined, { birthdays: [{ date: { month: 10, day: 20 } }] }),
        NOW,
      ),
    ).toBe(364);
  });

  it('ranks reasons and leaves out dismissed ones', () => {
    const a = contact('people/a', 'a@x.com');
    const b = contact('people/b', undefined, { birthdays: [{ date: { month: 10, day: 21 } }] });
    const items = buildReachOut([a, b], undefined, [thread({})], NOW);
    expect(items.map((i) => i.contact.resourceName)).toEqual(['people/b', 'people/a']);
    expect(items[1].reasons[0].kind).toBe('oweReply');
    expect(items[1].reasons[0].text).toContain('Proposal');
    const left = buildReachOut(
      [a, b],
      undefined,
      [thread({})],
      NOW,
      new Set(['people/b:birthday', 'people/a:oweReply:t']),
    );
    expect(left.map((i) => i.contact.resourceName)).toEqual([]);
  });
});
