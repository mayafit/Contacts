import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { Contact } from '../../../types/Contact';

const mockDispatch = jest.fn((action: unknown) => ({ unwrap: () => Promise.resolve(action) }));
jest.mock('../../../types/hooks', () => ({ useAppDispatch: () => mockDispatch }));
jest.mock('../../../redux/slices/contacts/contactsSlice', () => ({
  updateContact: (arg: unknown) => ({ type: 'update', arg }),
  contactAdded: (c: unknown) => ({ type: 'added', c }),
}));

const mockApi = {
  getOtherContacts: jest.fn(),
  getDismissedOther: jest.fn(),
  dismissOther: jest.fn(),
  clearDismissedOther: jest.fn(),
  connectOtherContacts: jest.fn(),
  createContact: jest.fn(),
};
jest.mock('../../../../../services/api/BackendApiClient', () => {
  class ApiRequestError extends Error {
    code: string;
    constructor(message: string, code: string) {
      super(message);
      this.code = code;
    }
  }
  return { backendApiClient: mockApi, ApiRequestError };
});

// eslint-disable-next-line @typescript-eslint/no-require-imports
const OtherContactsDialog = require('../OtherContactsDialog').default;
// eslint-disable-next-line @typescript-eslint/no-require-imports
const { ApiRequestError } = require('../../../../../services/api/BackendApiClient');

const edna = {
  resourceName: 'people/1',
  etag: 'e1',
  names: [{ givenName: 'Edna', familyName: 'Abergel', displayName: 'Edna Abergel' }],
} as unknown as Contact;

const open = (onApplied = jest.fn(), people: unknown = null) =>
  render(
    <OtherContactsDialog
      open
      onClose={jest.fn()}
      contacts={[edna]}
      allContacts={[edna]}
      groups={{}}
      people={people}
      onApplied={onApplied}
    />,
  );

beforeEach(() => {
  jest.clearAllMocks();
  mockApi.getDismissedOther.mockResolvedValue([]);
  mockApi.getOtherContacts.mockResolvedValue({
    entries: [
      {
        resourceName: 'otherContacts/1',
        name: 'Abergel Edna',
        emails: ['edna.abergel@bankleumi.co.il'],
        phones: [],
      },
      {
        resourceName: 'otherContacts/2',
        name: 'Avi Bar',
        emails: ['avi.bar@gmail.com'],
        phones: [],
      },
    ],
    read: 3,
    automated: 1,
    capped: false,
  });
});

describe('suggestions from Other contacts', () => {
  it('without the permission, offers to allow it', async () => {
    mockApi.getOtherContacts.mockRejectedValue(new ApiRequestError('no', 'NOT_CONNECTED'));
    open();
    fireEvent.click(await screen.findByRole('button', { name: 'Allow' }));
    expect(mockApi.connectOtherContacts).toHaveBeenCalled();
  });

  it('nothing is ticked; applying a ticked row adds the address to the contact', async () => {
    const onApplied = jest.fn();
    open(onApplied);
    const box = await screen.findByRole('checkbox', { name: 'Add to Edna Abergel' });
    expect((box as HTMLInputElement).checked).toBe(false);
    expect(screen.getByText(/1 automated senders left out/)).toBeTruthy();
    expect(screen.getByText(/People worth saving need Gmail/)).toBeTruthy();
    fireEvent.click(box);
    fireEvent.click(screen.getByRole('button', { name: 'Apply 1' }));
    await waitFor(() => expect(onApplied).toHaveBeenCalledWith(1, 0));
    const action = mockDispatch.mock.calls[0][0] as {
      arg: { resourceName: string; updates: { emailAddresses: { value: string; type: string }[] } };
    };
    expect(action.arg.resourceName).toBe('people/1');
    expect(action.arg.updates.emailAddresses).toEqual([
      expect.objectContaining({ value: 'edna.abergel@bankleumi.co.il', type: 'work' }),
    ]);
  });

  it('dismissing hides the row and is remembered; Show dismissed brings it back', async () => {
    mockApi.dismissOther.mockImplementation((keys: string[]) => Promise.resolve(keys));
    mockApi.clearDismissedOther.mockResolvedValue([]);
    open();
    fireEvent.click(await screen.findByRole('button', { name: 'Dismiss Edna Abergel' }));
    await waitFor(() =>
      expect(screen.queryByRole('checkbox', { name: 'Add to Edna Abergel' })).toBeNull(),
    );
    expect(mockApi.dismissOther).toHaveBeenCalledWith(['u:people/1:edna.abergel@bankleumi.co.il']);
    fireEvent.click(screen.getByRole('button', { name: 'Show dismissed (1)' }));
    expect(await screen.findByRole('checkbox', { name: 'Add to Edna Abergel' })).toBeTruthy();
  });

  it('saves a person the Owner writes to as a new contact', async () => {
    mockApi.createContact.mockResolvedValue({ resourceName: 'people/9' });
    const onApplied = jest.fn();
    open(onApplied, {
      'avi.bar@gmail.com': { sentCount: 3, receivedCount: 1, emailMonths: {}, meetingMonths: {} },
    });
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Save Avi Bar' }));
    fireEvent.click(screen.getByRole('button', { name: 'Apply 1' }));
    await waitFor(() => expect(onApplied).toHaveBeenCalledWith(0, 1));
    expect(mockApi.createContact).toHaveBeenCalledWith(
      expect.objectContaining({
        names: [expect.objectContaining({ givenName: 'Avi', familyName: 'Bar' })],
        emailAddresses: [expect.objectContaining({ value: 'avi.bar@gmail.com', type: 'home' })],
      }),
    );
  });
});
