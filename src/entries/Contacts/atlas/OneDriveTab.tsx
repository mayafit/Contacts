/**
 * @fileoverview The import dialog's OneDrive tab: connect the account (Microsoft
 * sign-in), watch the photo sync, sync again, disconnect
 * @module Contacts/atlas/OneDriveTab
 */

import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, Box, Button, Chip, LinearProgress, Typography } from '@mui/material';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import type { OneDriveStatus, SourceConnection } from './types';
import { tokens } from '../theme/contactsTheme';

/** Props for OneDriveTab */
export interface OneDriveTabProps {
  /** Whether the dialog is open (polling stops when it closes) */
  open: boolean;
  /** Called when a sync has finished, so the map reloads */
  onChanged: () => void;
}

/** How often the connection is re-read while a sync runs */
const POLL_MS = 2000;

/** After a sync, the map reloads again this much later (the photos are placed by then) */
const PLACED_MS = 20000;

/** Status chip text and colour */
const STATUS: Record<
  SourceConnection['status'],
  { label: string; color: 'default' | 'primary' | 'success' | 'warning' | 'error' }
> = {
  idle: { label: 'Connected', color: 'success' },
  syncing: { label: 'Syncing…', color: 'primary' },
  error: { label: 'Sync failed', color: 'error' },
  expired: { label: 'Sign-in expired', color: 'warning' },
};

/**
 * OneDrive connection.
 * @param props - Component props
 * @returns Tab content
 */
const OneDriveTab: React.FC<OneDriveTabProps> = ({ open, onChanged }) => {
  const [state, setState] = useState<OneDriveStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const lastStatus = useRef<SourceConnection['status'] | null>(null);

  const load = useCallback(async () => {
    try {
      setState(await backendApiClient.atlasOneDrive());
    } catch (e) {
      setError((e as Error).message || 'Loading the OneDrive connection failed');
    }
  }, []);

  useEffect(() => {
    if (open) {
      setError(null);
      void load();
    }
  }, [open, load]);

  const conn = state?.connection ?? null;
  const status = conn?.status ?? null;

  // Poll while a sync runs and the dialog is open
  useEffect(() => {
    if (!open || status !== 'syncing') {
      return undefined;
    }
    const timer = window.setInterval(() => {
      backendApiClient
        .atlasOneDrive()
        .then(setState)
        .catch(() => {
          // A missed poll is retried on the next tick
        });
    }, POLL_MS);
    return () => window.clearInterval(timer);
  }, [open, status]);

  // A finished sync brought photos in: reload the map, and again once the server has
  // placed them on the tracks and visits (it does that right after the sync)
  useEffect(() => {
    if (lastStatus.current === 'syncing' && status !== 'syncing') {
      onChanged();
      const again = window.setTimeout(onChanged, PLACED_MS);
      lastStatus.current = status;
      return () => window.clearTimeout(again);
    }
    lastStatus.current = status;
    return undefined;
  }, [status, onChanged]);

  const run = async (action: () => Promise<void>, failed: string) => {
    setBusy(true);
    setError(null);
    try {
      await action();
    } catch (e) {
      setError((e as Error).message || failed);
    } finally {
      setBusy(false);
    }
  };

  const connect = () =>
    // Leaves the page for Microsoft's sign-in; Atlas opens again when it is done
    run(() => backendApiClient.atlasConnectOneDrive(), 'Connecting OneDrive failed');

  const sync = () =>
    run(
      async () => setState(await backendApiClient.atlasSyncOneDrive()),
      'Syncing OneDrive failed',
    );

  const disconnect = () => {
    if (
      !window.confirm(
        'Disconnect OneDrive? Atlas forgets the sign-in and the photos it found; the photos stay in OneDrive.',
      )
    ) {
      return;
    }
    void run(async () => {
      await backendApiClient.atlasDisconnectOneDrive();
      await load();
    }, 'Disconnecting OneDrive failed');
  };

  if (!state) {
    return error ? <Alert severity="error">{error}</Alert> : <LinearProgress sx={{ my: 2 }} />;
  }

  const syncing = status === 'syncing';
  const needsSignIn = !conn || status === 'expired';

  return (
    <Box>
      <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 1.5 }}>
        Atlas reads when and where each photo in your OneDrive was taken and links it to the rides,
        trips and places of that time. The photos stay in OneDrive; Atlas keeps only their details
        and shows thumbnails from there. New photos come in on their own every half hour.
      </Typography>

      {error && (
        <Alert severity="error" sx={{ mb: 1.5 }} onClose={() => setError(null)}>
          {error}
        </Alert>
      )}

      {!state.configured && (
        <Alert severity="info">
          OneDrive is not set up on the server yet (the Microsoft app&apos;s id and secret are
          missing).
        </Alert>
      )}

      {state.configured && conn && (
        <Box sx={{ p: 1.5, borderRadius: '8px', bgcolor: tokens.page, mb: 1.5 }}>
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 0.5 }}>
            <Typography variant="body2" sx={{ fontWeight: 600, flex: 1, minWidth: 0 }} noWrap>
              {conn.account || 'OneDrive'}
            </Typography>
            <Chip
              size="small"
              label={STATUS[conn.status]?.label ?? conn.status}
              color={STATUS[conn.status]?.color ?? 'default'}
            />
          </Box>
          <Typography variant="caption" sx={{ color: tokens.textMuted, display: 'block' }}>
            {conn.lastSyncAt
              ? `Last sync ${new Date(conn.lastSyncAt).toLocaleString('en-GB')}`
              : 'Not synced yet'}
          </Typography>
          {syncing && (
            <Box sx={{ mt: 1 }}>
              <LinearProgress />
              <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                {conn.itemsDone > 0
                  ? `${conn.itemsDone.toLocaleString()} files looked at · ${conn.itemsImported.toLocaleString()} photos new or changed`
                  : 'Reading the drive…'}
              </Typography>
            </Box>
          )}
          <Typography variant="caption" sx={{ color: tokens.textSecondary, display: 'block' }}>
            {state.photos.toLocaleString()} photos and videos ·{' '}
            {state.withPosition.toLocaleString()} on the map
          </Typography>
          {conn.lastError && !syncing && (
            <Alert severity={status === 'expired' ? 'warning' : 'error'} sx={{ mt: 1 }}>
              {conn.lastError}
            </Alert>
          )}
          <Box sx={{ display: 'flex', gap: 1, mt: 1.5 }}>
            {needsSignIn ? (
              <Button variant="contained" size="small" disabled={busy} onClick={connect}>
                Sign in again
              </Button>
            ) : (
              <Button variant="contained" size="small" disabled={busy || syncing} onClick={sync}>
                Sync again
              </Button>
            )}
            <Button size="small" color="error" disabled={busy} onClick={disconnect}>
              Disconnect
            </Button>
          </Box>
        </Box>
      )}

      {state.configured && !conn && (
        <Box>
          <Button variant="contained" disabled={busy} onClick={connect}>
            Connect OneDrive
          </Button>
          <Typography
            variant="caption"
            sx={{ color: tokens.textMuted, display: 'block', mt: 1, maxWidth: 440 }}
          >
            You sign in at Microsoft and allow Atlas to read your files. Atlas only reads; it never
            changes or deletes anything in OneDrive.
          </Typography>
        </Box>
      )}
      {busy && <LinearProgress sx={{ mt: 1.5 }} />}
    </Box>
  );
};

export default OneDriveTab;
