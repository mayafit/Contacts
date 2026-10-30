import React from 'react';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { axe } from 'jest-axe';
import GoogleDriveTab from '../GoogleDriveTab';
import { takeoutLastText, takeoutStatusText } from '../reviewText';
import type { SourceConnection, TakeoutDriveStatus } from '../types';

jest.mock('../../../../services/api/BackendApiClient', () => ({
  backendApiClient: {
    atlasTakeoutDrive: jest.fn(),
    atlasTakeoutDriveConnect: jest.fn(),
    atlasTakeoutDriveSync: jest.fn(),
    atlasTakeoutDriveDisconnect: jest.fn(),
    connectTakeoutDrive: jest.fn(),
  },
}));

// eslint-disable-next-line @typescript-eslint/no-var-requires
const api = jest.requireMock('../../../../services/api/BackendApiClient');
const client = api.backendApiClient as Record<string, jest.Mock>;

const conn = (c: Partial<SourceConnection> = {}): SourceConnection => ({
  provider: 'google-drive-takeout',
  account: 'me@example.com',
  status: 'idle',
  lastSyncAt: '2026-10-30T09:00:00Z',
  lastError: null,
  itemsTotal: 0,
  itemsDone: 0,
  itemsImported: 42,
  ...c,
});

const status = (s: Partial<TakeoutDriveStatus> = {}): TakeoutDriveStatus => ({
  granted: true,
  connection: conn(),
  lastExport: {
    id: 'z1',
    name: 'takeout-20260901T000000Z-001.zip',
    modified: '2026-09-01T00:00:00Z',
    reviews: 12,
    note: null,
  },
  stale: false,
  ...s,
});

beforeEach(() => jest.clearAllMocks());

describe('Takeout on Drive texts', () => {
  it('says when Drive was checked and what the last export brought', () => {
    const now = new Date('2026-10-30T12:00:00Z').getTime();
    expect(takeoutStatusText(conn(), now)).toBe('Checked 3 hours ago · 42 reviews imported');
    expect(takeoutStatusText(conn({ lastSyncAt: null, itemsImported: 1 }), now)).toBe(
      'Not checked yet · 1 review imported',
    );
    expect(takeoutLastText(status().lastExport)).toMatch(
      /^Last export: takeout-20260901T000000Z-001\.zip, 1 Sept? 2026 — 12 reviews$/,
    );
    expect(
      takeoutLastText({ ...status().lastExport!, reviews: null, note: 'larger than 500 MB' }),
    ).toMatch(/— larger than 500 MB$/);
    expect(takeoutLastText(null)).toBe('No Takeout export found in Drive yet');
  });
});

describe('the Google Drive tab', () => {
  it('asks the sign-in to allow Drive when it does not yet', async () => {
    client.atlasTakeoutDrive.mockResolvedValue(
      status({ granted: false, connection: null, lastExport: null }),
    );
    const { container } = render(<GoogleDriveTab open onChanged={jest.fn()} />);
    await act(async () => {});

    fireEvent.click(screen.getByRole('button', { name: 'Connect Google Drive' }));
    expect(client.connectTakeoutDrive).toHaveBeenCalled();
    expect(client.atlasTakeoutDriveConnect).not.toHaveBeenCalled();
    expect(screen.getByRole('link', { name: 'takeout.google.com' })).toBeInTheDocument();
    expect(await axe(container)).toHaveNoViolations();
  });

  it('connects right after the sign-in allowed Drive, then checks on request', async () => {
    client.atlasTakeoutDriveConnect.mockResolvedValue(status());
    client.atlasTakeoutDriveSync.mockResolvedValue(
      status({ connection: conn({ status: 'syncing' }) }),
    );
    const onChanged = jest.fn();
    render(<GoogleDriveTab open onChanged={onChanged} justAllowed />);
    await act(async () => {});

    expect(client.atlasTakeoutDriveConnect).toHaveBeenCalledTimes(1);
    expect(screen.getByText('me@example.com')).toBeInTheDocument();
    expect(screen.getByText(/12 reviews$/)).toBeInTheDocument();
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Check now' })));
    expect(client.atlasTakeoutDriveSync).toHaveBeenCalled();
    expect(screen.getByText('checking…')).toBeInTheDocument();
    expect(onChanged).toHaveBeenCalled();
  });

  it('offers to allow Drive again when access was lost, and notes an ended schedule', async () => {
    client.atlasTakeoutDrive.mockResolvedValue(
      status({
        stale: true,
        connection: conn({
          status: 'expired',
          lastError: 'Sign in to Google again and allow Drive access.',
        }),
      }),
    );
    render(<GoogleDriveTab open onChanged={jest.fn()} />);
    await act(async () => {});

    expect(screen.getByText('Sign in to Google again and allow Drive access.')).toBeInTheDocument();
    expect(screen.getByText(/No new export for over 70 days/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Allow Drive again' }));
    expect(client.connectTakeoutDrive).toHaveBeenCalled();
  });
});
