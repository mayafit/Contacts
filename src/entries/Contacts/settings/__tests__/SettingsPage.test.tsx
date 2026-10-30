import React from 'react';
import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { axe } from 'jest-axe';
import { MemoryRouter } from 'react-router';
import SettingsProvider, { SAVE_DELAY_MS, STORAGE_KEY, useUi } from '../SettingsProvider';
import SettingsPage, { saveText } from '../SettingsPage';
import { DEFAULT_UI } from '../uiSettings';

jest.mock('../../../../services/api/BackendApiClient', () => ({
  backendApiClient: {
    settings: jest.fn(),
    saveSettings: jest.fn(),
    resetSettings: jest.fn(),
  },
}));

// eslint-disable-next-line @typescript-eslint/no-var-requires
const api = jest.requireMock('../../../../services/api/BackendApiClient');
const client = api.backendApiClient as Record<string, jest.Mock>;

/** Shows a value in effect, as a component that draws with the settings would */
const Probe: React.FC = () => {
  const ui = useUi();
  return <output aria-label="body size">{ui.text.body}</output>;
};

const renderPage = (signedIn = true) =>
  render(
    <MemoryRouter>
      <SettingsProvider signedIn={signedIn}>
        <Probe />
        <SettingsPage />
      </SettingsProvider>
    </MemoryRouter>,
  );

beforeEach(() => {
  jest.clearAllMocks();
  localStorage.clear();
  client.settings.mockResolvedValue({ json: {}, updatedAt: null });
  client.saveSettings.mockResolvedValue(undefined);
  client.resetSettings.mockResolvedValue(undefined);
});

afterEach(() => {
  jest.useRealTimers();
  document.documentElement.removeAttribute('style');
  document.getElementById('echo-pointer')?.remove();
});

describe('the settings in effect', () => {
  it('start from the browser’s copy, then take the Owner’s document', async () => {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ text: { body: 17 } }));
    client.settings.mockResolvedValue({
      json: { text: { body: 21 } },
      updatedAt: '2026-10-30T10:00:00Z',
    });
    renderPage();
    expect(screen.getByLabelText('body size')).toHaveTextContent('17');

    await act(async () => {});
    expect(screen.getByLabelText('body size')).toHaveTextContent('21');
    expect(document.documentElement.style.getPropertyValue('--ui-body')).toBe(String(21 / 15));
    expect(JSON.parse(localStorage.getItem(STORAGE_KEY)!).text.body).toBe(21);
  });

  it('change at once, and are saved once after a burst of changes', async () => {
    jest.useFakeTimers();
    renderPage();
    await act(async () => {});
    const pointer = screen.getByRole('group', { name: 'Pointer size' });

    fireEvent.click(within(pointer).getByRole('button', { name: '32 px' }));
    fireEvent.click(within(pointer).getByRole('button', { name: '48 px' }));
    expect(decodeURIComponent(document.getElementById('echo-pointer')!.textContent!)).toContain(
      'width="48"',
    );
    expect(client.saveSettings).not.toHaveBeenCalled();

    await act(async () => {
      jest.advanceTimersByTime(SAVE_DELAY_MS);
    });
    expect(client.saveSettings).toHaveBeenCalledTimes(1);
    expect(client.saveSettings.mock.calls[0][1].pointer.size).toBe(48);
    expect(screen.getByText(/^Saved \d\d:\d\d$/)).toBeInTheDocument();

    // Back to the system's pointer: its style sheet goes
    fireEvent.click(within(pointer).getByRole('button', { name: 'System' }));
    expect(document.getElementById('echo-pointer')).toBeNull();
  });

  it('are put back by a section’s reset and by reset everything', async () => {
    jest.useFakeTimers();
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ text: { body: 20 }, map: { visit: 12 } }));
    renderPage();
    await act(async () => {});
    const text = screen.getByRole('region', { name: 'Text' });

    fireEvent.click(within(text).getByRole('button', { name: 'Reset' }));
    expect(screen.getByLabelText('body size')).toHaveTextContent('15');
    expect(
      within(screen.getByRole('region', { name: 'Map symbols' })).getByText('12px (default 5px)'),
    ).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Reset everything' }));
    await act(async () => {
      jest.advanceTimersByTime(SAVE_DELAY_MS);
    });
    expect(client.resetSettings).toHaveBeenCalledWith('ui');
    expect(localStorage.getItem(STORAGE_KEY)).toBeNull();
    expect(
      within(screen.getByRole('region', { name: 'Map symbols' })).getAllByText('5px (default)')
        .length,
    ).toBeGreaterThan(0);
  });

  it('keep a change in the browser when saving fails, and say so', async () => {
    jest.useFakeTimers();
    client.saveSettings.mockRejectedValue(new Error('offline'));
    renderPage();
    await act(async () => {});

    fireEvent.click(
      within(screen.getByRole('group', { name: 'Pointer size' })).getByRole('button', {
        name: '24 px',
      }),
    );
    await act(async () => {
      jest.advanceTimersByTime(SAVE_DELAY_MS);
    });
    expect(screen.getByText(/^Not saved: offline/)).toBeInTheDocument();
    expect(JSON.parse(localStorage.getItem(STORAGE_KEY)!).pointer.size).toBe(24);
  });

  it('are not loaded or saved while signed out', async () => {
    jest.useFakeTimers();
    renderPage(false);
    fireEvent.click(
      within(screen.getByRole('group', { name: 'Pointer size' })).getByRole('button', {
        name: '24 px',
      }),
    );
    await act(async () => {
      jest.advanceTimersByTime(SAVE_DELAY_MS);
    });
    expect(client.settings).not.toHaveBeenCalled();
    expect(client.saveSettings).not.toHaveBeenCalled();
  });
});

describe('the Settings page', () => {
  it('has every section, each control with its default, and no a11y violations', async () => {
    const { container } = renderPage();
    await act(async () => {});

    ['Text', 'Pointer', 'Map symbols', 'Colours', 'Routes', 'Charts', 'Other settings'].forEach(
      (name) => expect(screen.getByRole('region', { name })).toBeInTheDocument(),
    );
    expect(screen.getByRole('slider', { name: 'Body text' })).toHaveValue(
      String(DEFAULT_UI.text.body),
    );
    expect(screen.getByLabelText('Ride colour')).toHaveValue('#3fb6ff');
    expect(screen.getByRole('img', { name: 'Map symbols preview' })).toBeInTheDocument();
    expect(await axe(container)).toHaveNoViolations();
  });

  it('changes a colour and offers its default back', async () => {
    renderPage();
    await act(async () => {});

    fireEvent.change(screen.getByLabelText('Visits colour'), { target: { value: '#00ff00' } });
    expect(screen.getByLabelText('Visits colour')).toHaveValue('#00ff00');
    fireEvent.click(screen.getByRole('button', { name: 'Reset the Visits colour' }));
    expect(screen.getByLabelText('Visits colour')).toHaveValue('#e879f9');
  });

  it('says where saving stands', () => {
    expect(saveText({ state: 'idle' })).toMatch(/apply at once/);
    expect(saveText({ state: 'saving' })).toBe('Saving…');
    expect(saveText({ state: 'failed', message: 'offline' })).toMatch(/^Not saved: offline\./);
  });
});
