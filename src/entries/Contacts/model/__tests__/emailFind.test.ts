import type { Contact } from '../../types/Contact';
import type { FoundEmail } from '../../../../services/api/BackendApiClient';
import { autoPick, choicesFor, emailType, nameVariants } from '../emailFind';

const found = (email: string, score = 3): FoundEmail => ({
  email,
  names: ['Dana Cohen'],
  score,
  mails: 4,
  sources: ['mail'],
});

describe('find the email (AD-43)', () => {
  it('looks for names of two words or more', () => {
    const c = {
      resourceName: 'p/1',
      names: [
        {
          givenName: 'דנה',
          familyName: 'כהן',
          phoneticGivenName: 'Dana',
          phoneticFamilyName: 'Cohen',
        },
      ],
      nicknames: [{ value: 'Dani' }],
    } as Contact;
    expect(nameVariants(c)).toEqual(expect.arrayContaining(['דנה כהן', 'Dana Cohen', 'Dani כהן']));
    expect(
      nameVariants({ resourceName: 'p/2', names: [{ givenName: 'Dana' }] } as Contact),
    ).toEqual([]);
  });

  it('types an address on a personal mail service as home', () => {
    expect(emailType('dana@Gmail.com')).toBe('home');
    expect(emailType('dana@walla.co.il')).toBe('home');
    expect(emailType('dana@elta.co.il')).toBe('work');
  });

  it('drops the draft’s own addresses and moves another contact’s last', () => {
    const draft = { resourceName: 'p/1', emailAddresses: [{ value: 'Old@x.com' }] } as Contact;
    const contacts = [
      draft,
      {
        resourceName: 'p/2',
        names: [{ displayName: 'Dana Levi' }],
        emailAddresses: [{ value: 'taken@x.com' }],
      },
    ] as Contact[];
    const choices = choicesFor(
      [found('taken@x.com'), found('old@x.com'), found('dana@x.com', 1)],
      draft,
      contacts,
    );
    expect(choices.map((c) => [c.email, c.onContact])).toEqual([
      ['dana@x.com', undefined],
      ['taken@x.com', 'Dana Levi'],
    ]);
  });

  it('fills at once only the one exact match on no other contact', () => {
    expect(autoPick([found('a@x.com')])?.email).toBe('a@x.com');
    expect(autoPick([found('a@x.com', 2)])).toBeNull();
    expect(autoPick([found('a@x.com'), found('b@x.com')])).toBeNull();
    expect(autoPick([{ ...found('a@x.com'), onContact: 'Dana Levi' }])).toBeNull();
  });
});
