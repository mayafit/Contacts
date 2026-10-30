import type { Contact } from '../../../types/Contact';
import { changeMembership, reconcileMembers, STARRED } from '../labelThunks';
import { backendApiClient } from '../../../../../services/api/BackendApiClient';
import { GoogleContactsService } from '../../../services/GoogleContactsService';

jest.mock('../../../../../services/api/BackendApiClient', () => ({
  backendApiClient: { modifyLabel: jest.fn(), fetchContactGroups: jest.fn() },
}));
jest.mock('../../../services/GoogleContactsService', () => ({
  GoogleContactsService: { getContact: jest.fn() },
}));
const client = backendApiClient as jest.Mocked<typeof backendApiClient>;
const google = GoogleContactsService as jest.Mocked<typeof GoogleContactsService>;

const c = (id: string, labels: string[]): Contact => ({ resourceName: id, labels }) as Contact;

describe('label check against Google (AD-44 rule 3)', () => {
  it('finds the contacts whose checked labels differ, keeping the other labels', () => {
    const contacts = [
      c('p/1', ['contactGroups/myContacts', STARRED]),
      c('p/2', ['contactGroups/myContacts']),
      c('p/3', ['contactGroups/a']),
    ];
    const members = { [STARRED]: ['p/2'], 'contactGroups/a': ['p/3'] };
    const { changed, unknown } = reconcileMembers(contacts, members);
    expect(changed).toEqual([
      { resourceName: 'p/1', labels: ['contactGroups/myContacts'] },
      { resourceName: 'p/2', labels: ['contactGroups/myContacts', STARRED] },
    ]);
    expect(unknown).toBe(false);
    expect(reconcileMembers(contacts, { [STARRED]: ['p/9'] }).unknown).toBe(true);
  });
});

describe('changing a membership (AD-44 rule 2)', () => {
  beforeEach(() => jest.resetAllMocks());

  it('changes the store first, and undoes it when Google refuses', async () => {
    const actions: { type: string; payload?: unknown }[] = [];
    const dispatch = jest.fn((a: unknown) => {
      if (typeof a === 'object' && a && 'type' in a) {
        actions.push(a as { type: string; payload?: unknown });
      }
      return Promise.resolve(a);
    }) as never;
    client.modifyLabel.mockRejectedValue(new Error('PERMISSION_DENIED'));
    const refused = await changeMembership(dispatch, ['p/1'], STARRED, true);
    expect(refused).toBe('PERMISSION_DENIED');
    const changes = actions.filter((a) => a.type.endsWith('membershipChanged'));
    expect(changes.map((a) => (a.payload as { on: boolean }).on)).toEqual([true, false]);
  });

  it('writes Google and reads the contact again for its etag', async () => {
    const dispatch = jest.fn((a: unknown) => Promise.resolve(a)) as never;
    client.modifyLabel.mockResolvedValue('contactGroups/starred');
    google.getContact.mockResolvedValue({ success: true, data: c('p/1', [STARRED]) } as never);
    expect(await changeMembership(dispatch, ['p/1'], STARRED, true)).toBeNull();
    expect(client.modifyLabel).toHaveBeenCalledWith({ labelResourceName: STARRED, add: ['p/1'] });
    expect(google.getContact).toHaveBeenCalledWith('p/1');
  });
});
