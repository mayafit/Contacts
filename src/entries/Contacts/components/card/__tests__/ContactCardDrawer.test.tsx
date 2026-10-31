import React from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import type { Contact } from '../../../types/Contact';
import type { DossierData } from '../Dossier';

const dana = {
  resourceName: 'people/1',
  etag: 'e1',
  names: [{ givenName: 'Dana', familyName: 'Cohen', displayName: 'Dana Cohen' }],
  organizations: [{ name: 'Acme Ltd' }],
} as unknown as Contact;
const ron = {
  resourceName: 'people/2',
  etag: 'e2',
  names: [{ givenName: 'Ron', familyName: 'Levi', displayName: 'Ron Levi' }],
  organizations: [{ name: 'ACME' }],
} as unknown as Contact;
const mockEntities: Record<string, Contact> = { 'people/1': dana, 'people/2': ron };

jest.mock('../../../types/hooks', () => ({
  useAppDispatch: () => jest.fn(),
  useAppSelector: (selector: unknown) =>
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    selector === require('../../../redux/slices/contacts/selectors').selectContactEntities
      ? mockEntities
      : Object.values(mockEntities),
}));
jest.mock('../../../../../services/api/BackendApiClient', () => ({
  backendApiClient: {},
}));

// eslint-disable-next-line @typescript-eslint/no-require-imports
const ContactCardDrawer = require('../ContactCardDrawer').default;

const Harness: React.FC = () => {
  const [open, setOpen] = React.useState('people/1');
  const dossier: DossierData = {
    contacts: [dana, ron],
    activity: null,
    threads: [],
    segments: [],
    segmentCtx: { activity: null, mainCountry: '972', now: new Date() },
    gravatar: new Map(),
    signaturesAllowed: false,
    onOpenCard: setOpen,
  };
  return (
    <ContactCardDrawer resourceName={open} groups={{}} onClose={jest.fn()} dossier={dossier} />
  );
};

describe('the contact card', () => {
  it('shows the colleague opened from the dossier: title, details and one dossier', () => {
    render(<Harness />);
    expect(screen.getByDisplayValue('Dana')).toBeTruthy();
    fireEvent.click(screen.getByText('Ron Levi'));
    expect(screen.getByRole('heading', { name: 'Ron Levi' })).toBeTruthy();
    expect(screen.getByDisplayValue('Ron')).toBeTruthy();
    expect(screen.queryByDisplayValue('Dana')).toBeNull();
    expect(screen.getAllByRole('region', { name: 'Dossier' })).toHaveLength(1);
  });
});
