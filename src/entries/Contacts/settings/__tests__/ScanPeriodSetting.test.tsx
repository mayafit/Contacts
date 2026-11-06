import React from 'react';
import { fireEvent, render, screen, within } from '@testing-library/react';
import ScanPeriodSetting from '../ScanPeriodSetting';

jest.mock('../../../../services/api/BackendApiClient', () => ({
  backendApiClient: { getActivityStatus: jest.fn(), setActivityWindow: jest.fn() },
}));
// eslint-disable-next-line @typescript-eslint/no-var-requires
const api = jest.requireMock('../../../../services/api/BackendApiClient');
const client = api.backendApiClient as Record<string, jest.Mock>;

const status = (windowYears: number, connected = true) => ({
  connected: { gmail: connected, calendar: connected },
  status: 'ready',
  peopleCount: 0,
  windowYears,
});

describe('the scan period (FR-90)', () => {
  it('shows the saved period and saves a new one, starting a full scan', async () => {
    client.getActivityStatus.mockResolvedValue(status(1));
    client.setActivityWindow.mockResolvedValue(status(3));
    render(<ScanPeriodSetting />);
    const select = await screen.findByRole('combobox', { name: 'Scan period' });
    expect(select.textContent).toContain('1 year');
    fireEvent.mouseDown(select);
    fireEvent.click(within(screen.getByRole('listbox')).getByText('3 years'));
    expect(client.setActivityWindow).toHaveBeenCalledWith(3);
    expect(await screen.findByText(/A full scan with this period has started/)).toBeTruthy();
  });

  it('says the period waits for a connection', async () => {
    client.getActivityStatus.mockResolvedValue(status(2, false));
    render(<ScanPeriodSetting />);
    expect(await screen.findByText(/not connected/)).toBeTruthy();
    expect(screen.getByRole('combobox', { name: 'Scan period' }).textContent).toContain('2 years');
  });
});
