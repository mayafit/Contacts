import type { Contact, ContactName } from '../../types/Contact';
import {
  buildDictionary,
  findTwins,
  normalizeWord,
  planFill,
  twinValue,
  wordsToAsk,
} from '../phonetic';

const c = (resourceName: string, name: ContactName, extra: Partial<Contact> = {}): Contact =>
  ({ resourceName, names: [name], ...extra }) as Contact;

describe('phonetic fill (AD-43)', () => {
  it('normalizes points, geresh and gershayim', () => {
    expect(normalizeWord('יוֹסִי')).toBe('יוסי');
    expect(normalizeWord('צ׳רלי')).toBe("צ'רלי");
    expect(normalizeWord('בן־דוד')).toBe('בן-דוד'); // the maqaf is a hyphen
    expect(normalizeWord('(עבודה)')).toBe('עבודה');
    expect(normalizeWord('ד.')).toBe('ד');
  });

  it('learns spellings word by word, most common first, ties alphabetical', () => {
    const dict = buildDictionary([
      c('a', {
        givenName: 'יוסי',
        familyName: 'כהן',
        phoneticGivenName: 'Yossi',
        phoneticFamilyName: 'Cohen',
      }),
      c('b', {
        givenName: 'יוסי',
        familyName: 'כהן',
        phoneticGivenName: 'yossi',
        phoneticFamilyName: 'Kohen',
      }),
      c('c', {
        givenName: 'דני',
        familyName: 'כהן',
        phoneticGivenName: 'Dani',
        phoneticFamilyName: 'Cohen',
      }),
      c('d', { givenName: 'דני', phoneticGivenName: 'Danny' }),
      // Different word counts: nothing is learned
      c('e', { givenName: 'אבי', familyName: 'בר לב', phoneticFamilyName: 'Barlev' }),
      // Hyphenated pieces pair too
      c('f', { familyName: 'בן-דוד', phoneticFamilyName: 'Ben-David' }),
      // A Latin name part is not Hebrew
      c('g', { givenName: 'Dana', phoneticGivenName: 'Dana' }),
    ]);
    expect(dict.get('יוסי')).toEqual({ spelling: 'Yossi', count: 2 });
    expect(dict.get('כהן')).toEqual({ spelling: 'Cohen', count: 2 });
    expect(dict.get('דני')).toEqual({ spelling: 'Dani', count: 1 });
    expect(dict.has('בר')).toBe(false);
    expect(dict.get('בן-דוד')?.spelling).toBe('Ben-David');
    expect(dict.get('דוד')?.spelling).toBe('David');
    expect(dict.has('Dana')).toBe(false);
  });

  it('takes an English twin by phone or email, only when the twins agree', () => {
    const draft = c(
      'me',
      { givenName: 'דנה', familyName: 'כהן' },
      { phoneNumbers: [{ value: '054-123-4567' }], emailAddresses: [{ value: 'Dana@X.com' }] },
    );
    const contacts = [
      draft,
      c(
        'en',
        { givenName: 'Dana', familyName: 'Cohen' },
        { phoneNumbers: [{ value: '+972541234567' }] },
      ),
      c('he', { givenName: 'דנה' }, { phoneNumbers: [{ value: '0541234567' }] }),
      c('other', { displayName: 'Dana Cohen-Levi' }, { emailAddresses: [{ value: 'dana@x.com' }] }),
    ];
    const twins = findTwins(draft, contacts);
    expect(twins.map((t) => [t.name, t.via])).toEqual([
      ['Dana Cohen', 'phone'],
      ['Dana Cohen-Levi', 'email'],
    ]);
    expect(twinValue(twins, 'given')?.value).toBe('Dana');
    // "Cohen" and "Cohen-Levi" disagree
    expect(twinValue(twins, 'family')).toBeNull();
  });

  it('fills every empty Hebrew part, whole or not at all', () => {
    const dict = buildDictionary([
      c('a', {
        givenName: 'יוסי',
        familyName: 'כהן',
        phoneticGivenName: 'Yossi',
        phoneticFamilyName: 'Cohen',
      }),
    ]);
    const name = {
      givenName: 'יוסי',
      middleName: 'Ted',
      familyName: 'בר לב',
      phoneticFamilyName: '',
    };
    let plan = planFill(name, [], dict);
    // The middle name is Latin: nothing to fill
    expect(plan.map((p) => p.part)).toEqual(['given', 'family']);
    expect(plan[0]).toMatchObject({ value: 'Yossi', source: 'contacts' });
    expect(plan[1]).toMatchObject({ value: null, unknown: ['בר', 'לב'] });
    expect(wordsToAsk(plan)).toEqual(['בר', 'לב']);

    plan = planFill(name, [], dict, {
      בר: { spelling: 'Bar', source: 'translate' },
      לב: { spelling: 'Lev', source: 'gemini' },
    });
    // The weakest source names the part
    expect(plan[1]).toMatchObject({ value: 'Bar Lev', source: 'gemini' });

    // A filled phonetic part is left alone; a mixed part keeps its Latin word
    plan = planFill(
      { givenName: 'יוסי', phoneticGivenName: 'Yosef', familyName: 'כהן Smith' },
      [],
      dict,
    );
    expect(plan).toEqual([
      { part: 'family', value: 'Cohen Smith', source: 'contacts', unknown: [] },
    ]);
  });

  it('spells a hyphenated word piece by piece', () => {
    const dict = buildDictionary([
      c('a', {
        givenName: 'בן',
        familyName: 'דוד',
        phoneticGivenName: 'Ben',
        phoneticFamilyName: 'David',
      }),
    ]);
    expect(planFill({ familyName: 'בן-דוד' }, [], dict)[0].value).toBe('Ben-David');
  });
});
