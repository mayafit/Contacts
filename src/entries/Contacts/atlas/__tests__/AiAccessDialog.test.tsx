import React from 'react';
import { act, fireEvent, render, screen } from '@testing-library/react';
import AiAccessDialog from '../AiAccessDialog';
import type { AccessTokenInfo } from '../types';

jest.mock('../../../../services/api/BackendApiClient', () => ({
  backendApiClient: {
    atlasTokens: jest.fn(),
    atlasCreateToken: jest.fn(),
    atlasRevokeToken: jest.fn(),
  },
}));

// eslint-disable-next-line @typescript-eslint/no-var-requires
const api = jest.requireMock('../../../../services/api/BackendApiClient');
const client = api.backendApiClient as Record<string, jest.Mock>;

const token = (t: Partial<AccessTokenInfo> = {}): AccessTokenInfo => ({
  id: 't1',
  name: 'Claude',
  prefix: 'atl_abcd',
  createdAt: '2026-11-01T10:00:00Z',
  lastUsedAt: '2026-11-05T10:00:00Z',
  expiresAt: '2099-01-30T10:00:00Z',
  canWrite: false,
  legacy: false,
  ...t,
});

describe('AiAccessDialog (AD-50 rule 9)', () => {
  beforeEach(() => jest.clearAllMocks());

  it('lists each token with its rights, last use and expiry, and notes legacy tokens', async () => {
    client.atlasTokens.mockResolvedValue([
      token(),
      token({ id: 't2', name: 'Old', canWrite: true, legacy: true }),
    ]);

    await act(async () => {
      render(<AiAccessDialog open onClose={() => undefined} />);
    });

    expect(screen.getByText(/read only/)).toBeInTheDocument();
    expect(screen.getByText(/can change data/)).toBeInTheDocument();
    expect(screen.getAllByText(/last used/)).toHaveLength(2);
    expect(screen.getAllByText(/expires 30\/01\/2099/)).toHaveLength(2);
    expect(screen.getByText(/Made before tokens expired/)).toBeInTheDocument();
  });

  it('creates a read-only token for 90 days unless changes are allowed', async () => {
    client.atlasTokens.mockResolvedValue([]);
    client.atlasCreateToken.mockResolvedValue({ token: 'atl_secret', info: token() });

    await act(async () => {
      render(<AiAccessDialog open onClose={() => undefined} />);
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Create token' }));
    });
    expect(client.atlasCreateToken).toHaveBeenLastCalledWith('Claude', false, 90);

    await act(async () => {
      fireEvent.click(screen.getByLabelText('Allow changes'));
    });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Create token' }));
    });
    expect(client.atlasCreateToken).toHaveBeenLastCalledWith('Claude', true, 90);
  });

  it('shows an expired token as expired', async () => {
    client.atlasTokens.mockResolvedValue([token({ expiresAt: '2020-01-01T00:00:00Z' })]);

    await act(async () => {
      render(<AiAccessDialog open onClose={() => undefined} />);
    });

    expect(screen.getByText(/· expired/)).toBeInTheDocument();
  });
});
