/**
 * @fileoverview The import dialog's Google Drive tab (AD-40): how to schedule Takeout to
 * Drive, connecting Drive on the Google sign-in, and what the daily pickup found
 * @module Contacts/atlas/GoogleDriveTab
 */

import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, Box, Button, Chip, Link, Typography } from '@mui/material';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import type { TakeoutDriveStatus } from './types';
import { takeoutLastText, takeoutStatusText } from './reviewText';
import { tokens } from '../theme/contactsTheme';
import { fs } from '../settings/uiSettings';

export interface GoogleDriveTabProps {
  open: boolean;
  onChanged: () => void;
  /** Back from the Google sign-in that allowed Drive: connect right away */
  justAllowed?: boolean;
  /** Called once the connection is made after the sign-in (so it is not made again) */
  onConnected?: () => void;
}

/** The steps that schedule Takeout to Drive */
const STEPS = [
  <>
    Open{' '}
    <Link href="https://takeout.google.com" target="_blank" rel="noopener noreferrer">
      takeout.google.com
    </Link>{' '}
    and choose <strong>Deselect all</strong>.
  </>,
  <>
    Tick only <strong>Maps (your places)</strong>, then <strong>Next step</strong>.
  </>,
  <>
    Under <em>Destination</em>, choose <strong>Add to Drive</strong>.
  </>,
  <>
    Under <em>Frequency</em>, choose <strong>Export every 2 months for 1 year</strong>; keep{' '}
    <strong>.zip</strong>.
  </>,
  <>
    <strong>Create export</strong>. Echo checks your Drive once a day and imports the reviews.
  </>,
];

/**
 * The Google Drive tab.
 * @param props - Component props
 * @returns Tab content
 */
const GoogleDriveTab: React.FC<GoogleDriveTabProps> = ({
  open,
  onChanged,
  justAllowed,
  onConnected,
}) => {
  const [status, setStatus] = useState<TakeoutDriveStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const connecting = useRef(false);

  const load = useCallback(
    () =>
      backendApiClient
        .atlasTakeoutDrive()
        .then(setStatus)
        .catch((e: Error) => setError(e.message)),
    [],
  );

  // A check was asked for: the last check's time before it, polled until it changes (2 minutes at most)
  const [waiting, setWaiting] = useState<{ since: string | null; tries: number } | null>(null);

  const act = async (fn: () => Promise<TakeoutDriveStatus | void>, check = false) => {
    setBusy(true);
    setError(null);
    const before = status?.connection?.lastSyncAt ?? null;
    try {
      const s = await fn();
      if (s) {
        setStatus(s);
      } else {
        await load();
      }
      if (check) {
        setWaiting({ since: before, tries: 0 });
      }
      onChanged();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  useEffect(() => {
    if (!open) {
      return;
    }
    if (justAllowed && !connecting.current) {
      // The sign-in now allows Drive: connect once
      connecting.current = true;
      void act(() => backendApiClient.atlasTakeoutDriveConnect(), true).then(() => onConnected?.());
      return;
    }
    void load();
  }, [open, justAllowed]);

  // The asked-for check has run: its time moved on and it is no longer running
  const conn = status?.connection ?? null;
  useEffect(() => {
    if (waiting && conn && conn.status !== 'syncing' && conn.lastSyncAt !== waiting.since) {
      setWaiting(null);
    }
  }, [waiting, conn]);

  // While a check runs (or one was asked for), look again every few seconds
  const checking = conn?.status === 'syncing' || waiting !== null;
  useEffect(() => {
    if (!open || !checking) {
      return undefined;
    }
    const t = setTimeout(() => {
      void load();
      setWaiting((w) => (w && w.tries < 30 ? { ...w, tries: w.tries + 1 } : null));
    }, 4000);
    return () => clearTimeout(t);
  }, [open, checking, status, load]);

  return (
    <Box>
      <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 1 }}>
        Google has no way for apps to read the reviews you write, but Takeout can export them to
        your Google Drive every two months. Schedule that once, connect Drive here, and Echo picks
        up each export by itself. Echo only reads Takeout zips, and only their reviews.
      </Typography>
      <Box
        component="ol"
        sx={{ m: 0, pl: 2.5, color: tokens.textSecondary, fontSize: fs(14), mb: 1.5 }}
      >
        {STEPS.map((s, i) => (
          <li key={i}>{s}</li>
        ))}
      </Box>

      {conn ? (
        <Box sx={{ border: `1px solid ${tokens.border}`, borderRadius: 1, p: 1.5 }}>
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, flexWrap: 'wrap' }}>
            <Typography variant="body2" sx={{ fontWeight: 600 }}>
              Google Drive
            </Typography>
            {conn.account && (
              <Typography variant="body2" sx={{ color: tokens.textMuted }}>
                {conn.account}
              </Typography>
            )}
            <Chip
              size="small"
              label={checking ? 'checking…' : conn.status}
              color={conn.status === 'expired' || conn.status === 'error' ? 'warning' : 'default'}
            />
          </Box>
          <Typography variant="caption" component="div" sx={{ color: tokens.textMuted, mt: 0.5 }}>
            {takeoutStatusText(conn)}
          </Typography>
          <Typography variant="caption" component="div" sx={{ color: tokens.textMuted }}>
            {takeoutLastText(status?.lastExport ?? null)}
          </Typography>
          {conn.lastError && (
            <Alert severity="warning" sx={{ mt: 1 }}>
              {conn.lastError}
            </Alert>
          )}
          {status?.stale && (
            <Alert severity="info" sx={{ mt: 1 }}>
              No new export for over 70 days. Google ends a Takeout schedule after a year: check it
              at takeout.google.com.
            </Alert>
          )}
          <Box sx={{ display: 'flex', gap: 1, mt: 1, flexWrap: 'wrap' }}>
            {conn.status === 'expired' ? (
              <Button
                size="small"
                variant="contained"
                onClick={() => backendApiClient.connectTakeoutDrive()}
              >
                Allow Drive again
              </Button>
            ) : (
              <Button
                size="small"
                variant="outlined"
                disabled={busy || checking}
                onClick={() => void act(() => backendApiClient.atlasTakeoutDriveSync(), true)}
              >
                Check now
              </Button>
            )}
            <Button
              size="small"
              disabled={busy}
              onClick={() => void act(() => backendApiClient.atlasTakeoutDriveDisconnect())}
            >
              Disconnect
            </Button>
          </Box>
          <Typography variant="caption" component="div" sx={{ color: tokens.textMuted, mt: 1 }}>
            Disconnecting stops the checks; the imported reviews stay. To take back Drive access,
            use your Google Account&apos;s{' '}
            <Link
              href="https://myaccount.google.com/connections"
              target="_blank"
              rel="noopener noreferrer"
            >
              third-party connections
            </Link>
            .
          </Typography>
        </Box>
      ) : (
        status && (
          <Box>
            <Button
              variant="contained"
              disabled={busy}
              onClick={() =>
                status.granted
                  ? void act(() => backendApiClient.atlasTakeoutDriveConnect(), true)
                  : backendApiClient.connectTakeoutDrive()
              }
            >
              Connect Google Drive
            </Button>
            <Typography
              variant="caption"
              component="div"
              sx={{ color: tokens.textMuted, mt: 0.75 }}
            >
              Google asks to let Echo read your Drive. Echo is your own app and not reviewed by
              Google for this, so Google warns that it is unverified: choose Advanced, then go to
              Echo.
            </Typography>
          </Box>
        )
      )}
      {error && (
        <Alert severity="error" sx={{ mt: 1 }}>
          {error}
        </Alert>
      )}
    </Box>
  );
};

export default GoogleDriveTab;
