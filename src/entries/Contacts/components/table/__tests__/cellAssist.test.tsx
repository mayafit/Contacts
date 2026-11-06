import type { Contact } from '../../../types/Contact';
import { cellAssistFor } from '../cellAssist';
import { clearSpellingCache } from '../../card/useCardAutofill';
import { backendApiClient } from '../../../../../services/api/BackendApiClient';

jest.mock('../../../../../services/api/BackendApiClient', () => ({
  backendApiClient: {
    transliterateNames: jest.fn(),
    findEmail: jest.fn(),
    connectSignatures: jest.fn(),
  },
}));
const client = backendApiClient as jest.Mocked<typeof backendApiClient>;

const known = {
  resourceName: 'people/k',
  names: [
    {
      givenName: 'יוסי',
      familyName: 'לוי',
      phoneticGivenName: 'Yossi',
      phoneticFamilyName: 'Levi',
    },
  ],
} as Contact;

describe('table cell fills (AD-43 rule 10)', () => {
  beforeEach(() => {
    jest.resetAllMocks();
    clearSpellingCache();
  });

  it('spells an empty phonetic name, whole or not at all', async () => {
    const row = {
      resourceName: 'people/1',
      names: [{ givenName: 'יוסי', familyName: 'אברהמי' }],
    } as Contact;
    client.transliterateNames.mockResolvedValue({
      spellings: { אברהמי: 'Avrahami' },
      sources: { אברהמי: 'translate' },
    });
    expect(await cellAssistFor('phoneticName', row, [known, row])!.run()).toEqual({
      value: 'Yossi Avrahami',
    });

    clearSpellingCache();
    client.transliterateNames.mockRejectedValue(new Error('The daily limit is reached.'));
    expect(await cellAssistFor('phoneticName', row, [known, row])!.run()).toEqual({
      note: 'Spelled as in your contacts · Could not spell “אברהמי”: The daily limit is reached.',
    });
  });

  it('does not put a last name alone into the first-name slot', async () => {
    const row = { resourceName: 'people/2', names: [{ familyName: 'לוי' }] } as Contact;
    const result = await cellAssistFor('phoneticName', row, [known, row])!.run();
    expect(result.value).toBeUndefined();
    expect(result.note).toMatch(/^Levi: this name does not fit/);
    // A Latin first name keeps its place before the spelled last name
    const mixed = {
      resourceName: 'people/3',
      names: [{ givenName: 'Dana', familyName: 'לוי' }],
    } as Contact;
    expect(await cellAssistFor('phoneticName', mixed, [known, mixed])!.run()).toEqual({
      value: 'Dana Levi',
    });
  });

  it('has no phonetic assist for a phonetic name on file or an English name', () => {
    expect(cellAssistFor('phoneticName', known, [known])).toBeUndefined();
    expect(
      cellAssistFor(
        'phoneticName',
        { resourceName: 'p', names: [{ givenName: 'Dana' }] } as Contact,
        [],
      ),
    ).toBeUndefined();
  });

  it('finds an email, or offers the choices', async () => {
    const row = {
      resourceName: 'people/1',
      names: [{ givenName: 'Dana', familyName: 'Cohen' }],
    } as Contact;
    client.findEmail.mockResolvedValue({
      candidates: [
        { email: 'dana@x.com', names: ['Dana Cohen'], score: 3, mails: 2, sources: ['mail'] },
      ],
      directory: true,
      mail: 'searched',
    });
    expect(await cellAssistFor('emailAddresses', row, [row])!.run()).toEqual({
      value: 'dana@x.com',
    });

    client.findEmail.mockResolvedValue({
      candidates: [
        { email: 'dana@x.com', names: ['Dana C'], score: 1, mails: 2, sources: ['mail'] },
      ],
      directory: true,
      mail: 'searched',
    });
    expect(await cellAssistFor('emailAddresses', row, [row])!.run()).toEqual({
      choices: [{ value: 'dana@x.com', detail: 'as “Dana C” · 2 mails' }],
    });
    // A contact with an email, or without a two-word name, has none
    expect(
      cellAssistFor('emailAddresses', { ...row, emailAddresses: [{ value: 'a@b.com' }] }, []),
    ).toBeUndefined();
    expect(
      cellAssistFor(
        'emailAddresses',
        { resourceName: 'p', names: [{ givenName: 'Dana' }] } as Contact,
        [],
      ),
    ).toBeUndefined();
  });
});
