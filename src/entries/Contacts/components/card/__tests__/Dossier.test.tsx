import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { Contact } from '../../../types/Contact';
import Dossier, { type DossierData } from '../Dossier';
import { backendApiClient } from '../../../../../services/api/BackendApiClient';

jest.mock('../../../../../services/api/BackendApiClient', () => ({
  backendApiClient: { signatureLinks: jest.fn() },
}));
const client = backendApiClient as jest.Mocked<typeof backendApiClient>;

const dana = {
  resourceName: 'people/1',
  names: [{ displayName: 'Dana Cohen' }],
  organizations: [{ name: 'Acme Ltd' }],
  emailAddresses: [{ value: 'dana@acme.com' }],
  urls: [],
} as unknown as Contact;
const colleague = {
  resourceName: 'people/2',
  names: [{ displayName: 'Ron Levi' }],
  organizations: [{ name: 'ACME' }],
} as unknown as Contact;

const data = (over: Partial<DossierData> = {}): DossierData => ({
  contacts: [dana, colleague],
  activity: null,
  threads: [],
  segments: [
    {
      id: 's1',
      name: 'Acme people',
      match: 'all',
      rules: [{ kind: 'organization', op: 'contains', value: 'acme' }],
    },
  ],
  segmentCtx: { activity: null, mainCountry: '972', now: new Date() },
  gravatar: new Map([
    [
      'people/1',
      [
        {
          jobTitle: 'CTO',
          location: 'Haifa',
          verifiedAccounts: [{ label: 'GitHub', url: 'https://github.com/dana' }],
        },
      ],
    ],
  ]),
  signaturesAllowed: true,
  onOpenCard: jest.fn(),
  ...over,
});

describe('the dossier (AD-46 rule 6)', () => {
  it('shows profiles, about, segments and colleagues; Add puts the link in the draft', () => {
    const onDraftChange = jest.fn();
    const d = data();
    render(
      <Dossier contact={dana} draft={dana} onDraftChange={onDraftChange} groups={{}} data={d} />,
    );
    expect(screen.getByText('dana')).toBeTruthy(); // GitHub handle
    expect(screen.getByText(/CTO · Haifa/)).toBeTruthy();
    expect(screen.getByText('Segment: Acme people')).toBeTruthy();
    fireEvent.click(screen.getByText('Ron Levi'));
    expect(d.onOpenCard).toHaveBeenCalledWith('people/2');
    fireEvent.click(screen.getAllByRole('button', { name: 'Add to contact' })[0]);
    expect(onDraftChange).toHaveBeenCalledWith(
      expect.objectContaining({ urls: [{ value: 'https://github.com/dana', type: 'GitHub' }] }),
    );
  });

  it("lists only this contact's awaiting threads, not the whole mailbox's", () => {
    const daysAgo = (n: number) => new Date(Date.now() - n * 86_400_000).toISOString();
    const thread = (threadId: string, subject: string, ...counterparts: string[]) =>
      ({
        threadId,
        subject,
        lastAt: daysAgo(5),
        lastFromMe: false,
        inInbox: true,
        counterparts,
      }) as unknown as DossierData['threads'][number];
    // A second address, in upper case on the contact (counterparts come lower-cased)
    const both = {
      ...dana,
      emailAddresses: [{ value: 'dana@acme.com' }, { value: 'Dana.Cohen@Gmail.com' }],
    } as Contact;
    render(
      <Dossier
        contact={both}
        draft={both}
        onDraftChange={jest.fn()}
        groups={{}}
        data={data({
          activity: new Map() as unknown as DossierData['activity'],
          threads: [
            thread('t1', 'Budget review', 'dana@acme.com'),
            thread('t2', 'Your car has been selected', 'noreply@rental.example'),
            // She is not the first counterpart
            thread('t3', 'Trip plan', 'ron@acme.com', 'dana.cohen@gmail.com'),
          ],
        })}
      />,
    );
    expect(screen.getByText(/Budget review/)).toBeTruthy();
    expect(screen.getByText(/Trip plan/)).toBeTruthy();
    expect(screen.queryByText(/Your car has been selected/)).toBeNull();
  });

  it('reads the signature links on request', async () => {
    client.signatureLinks.mockResolvedValue({
      emails: 3,
      urls: ['https://x.com/dana', 'https://promo.example.com'],
    });
    render(
      <Dossier
        contact={dana}
        draft={dana}
        onDraftChange={jest.fn()}
        groups={{}}
        data={data({ gravatar: new Map() })}
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Find links in their signatures' }));
    await waitFor(() => expect(screen.getByText(/1 links in their signatures/)).toBeTruthy());
    expect(client.signatureLinks).toHaveBeenCalledWith(['dana@acme.com']);
    expect(screen.queryByText('promo.example.com')).toBeNull();
  });

  it('does not say "no email" when their emails were only invitations', async () => {
    client.signatureLinks.mockResolvedValue({ matched: 12, emails: 0, urls: [] });
    render(
      <Dossier
        contact={dana}
        draft={dana}
        onDraftChange={jest.fn()}
        groups={{}}
        data={data({ gravatar: new Map() })}
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Find links in their signatures' }));
    expect(
      await screen.findByText(
        'Their last 12 emails were meeting invitations or had no text of their own.',
      ),
    ).toBeTruthy();
    expect(screen.queryByText(/No email from them/)).toBeNull();
  });
});
