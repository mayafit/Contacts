import React, { useState } from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { Contact } from '../../../types/Contact';
import ContactForm from '../ContactForm';
import { clearSpellingCache } from '../useCardAutofill';
import { backendApiClient } from '../../../../../services/api/BackendApiClient';

let mockContacts: Contact[] = [];
jest.mock('../../../types/hooks', () => ({
  useAppSelector: () => mockContacts,
}));
jest.mock('../../../../../services/api/BackendApiClient', () => ({
  backendApiClient: {
    transliterateNames: jest.fn(),
    findEmail: jest.fn(),
    connectSignatures: jest.fn(),
  },
}));
const client = backendApiClient as jest.Mocked<typeof backendApiClient>;

let latest: Contact;
const Harness: React.FC<{ initial: Contact }> = ({ initial }) => {
  const [draft, setDraft] = useState(initial);
  latest = draft;
  return <ContactForm draft={draft} onChange={setDraft} />;
};

describe('contact card auto-fill (FR-77, FR-78)', () => {
  beforeEach(() => {
    jest.resetAllMocks();
    clearSpellingCache();
    mockContacts = [
      {
        resourceName: 'people/known',
        names: [
          {
            givenName: 'יוסי',
            familyName: 'לוי',
            phoneticGivenName: 'Yossi',
            phoneticFamilyName: 'Levi',
          },
        ],
      } as Contact,
    ];
  });

  it('fills the phonetic name from the contacts and Gemini, only the empty Hebrew parts', async () => {
    client.transliterateNames.mockResolvedValue({
      spellings: { אברהמי: 'Avrahami' },
      sources: { אברהמי: 'gemini' },
    });
    render(
      <Harness
        initial={{ resourceName: 'people/1', names: [{ givenName: 'יוסי', familyName: 'אברהמי' }] }}
      />,
    );
    // First and last name are Hebrew and their phonetic fields empty; the middle is not
    expect(screen.getAllByRole('button', { name: 'Fill from the Hebrew name' })).toHaveLength(2);
    fireEvent.click(screen.getAllByRole('button', { name: 'Fill from the Hebrew name' })[0]);
    await waitFor(() => expect(latest.names?.[0].phoneticFamilyName).toBe('Avrahami'));
    // Only the word the contacts do not spell was sent
    expect(client.transliterateNames).toHaveBeenCalledWith(['אברהמי'], ['יוסי', 'אברהמי']);
    expect(latest.names?.[0]).toMatchObject({
      phoneticGivenName: 'Yossi',
      phoneticFullName: null,
    });
    expect(screen.getByText(/Spelled as in your contacts · Suggested by Gemini/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Fill from the Hebrew name' })).toBeNull();
  });

  it('leaves a part empty when Gemini cannot spell a word', async () => {
    client.transliterateNames.mockRejectedValue(new Error('The daily limit is reached.'));
    render(<Harness initial={{ resourceName: 'people/1', names: [{ familyName: 'אברהמי' }] }} />);
    fireEvent.click(screen.getByRole('button', { name: 'Fill from the Hebrew name' }));
    expect(
      await screen.findByText('Could not spell “אברהמי”: The daily limit is reached.'),
    ).toBeTruthy();
    expect(latest.names?.[0].phoneticFamilyName).toBeFalsy();
  });

  it('fills an exact match from the mail at once, into a new row typed by its domain', async () => {
    client.findEmail.mockResolvedValue({
      candidates: [
        { email: 'dana@elta.co.il', names: ['Dana Cohen'], score: 3, mails: 12, sources: ['mail'] },
      ],
      directory: true,
      mail: 'searched',
    });
    render(
      <Harness
        initial={{ resourceName: 'people/1', names: [{ givenName: 'Dana', familyName: 'Cohen' }] }}
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Find in mail' }));
    await waitFor(() =>
      expect(latest.emailAddresses).toEqual([{ value: 'dana@elta.co.il', type: 'work' }]),
    );
    expect(client.findEmail).toHaveBeenCalledWith(['Dana Cohen']);
    expect(screen.getByText('Found in 12 mails as “Dana Cohen”')).toBeTruthy();
  });

  it('offers a menu when the match is not certain, and fills the empty row picked', async () => {
    client.findEmail.mockResolvedValue({
      candidates: [
        { email: 'dana@gmail.com', names: ['Dana C'], score: 1, mails: 3, sources: ['directory'] },
      ],
      directory: true,
      mail: 'searched',
    });
    render(
      <Harness
        initial={{
          resourceName: 'people/1',
          names: [{ givenName: 'Dana', familyName: 'Cohen' }],
          emailAddresses: [{ value: '', type: 'other' }],
        }}
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Find in mail' }));
    fireEvent.click(await screen.findByText('dana@gmail.com'));
    expect(latest.emailAddresses).toEqual([{ value: 'dana@gmail.com', type: 'home' }]);
  });

  it('asks for the permission when nothing is found without the mailbox', async () => {
    client.findEmail.mockResolvedValue({ candidates: [], directory: true, mail: 'not_allowed' });
    render(
      <Harness
        initial={{ resourceName: 'people/1', names: [{ givenName: 'Dana', familyName: 'Cohen' }] }}
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Find in mail' }));
    fireEvent.click(
      await screen.findByRole('button', { name: 'Allow reading email content (leaves this page)' }),
    );
    expect(client.connectSignatures).toHaveBeenCalled();
    expect(screen.getByText(/No mail from “Dana Cohen”/)).toBeTruthy();
  });

  it('needs a first and last name to search', () => {
    render(<Harness initial={{ resourceName: 'people/1', names: [{ givenName: 'Dana' }] }} />);
    expect(
      (screen.getByRole('button', { name: 'Find in mail' }) as HTMLButtonElement).disabled,
    ).toBe(true);
  });
});
