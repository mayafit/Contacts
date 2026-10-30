/**
 * @fileoverview The import dialog's Sports Tracker tab: connect the account (password used
 * once, or the web app's session key), watch the sync, sync again, disconnect
 * @module Contacts/atlas/SportsTrackerTab
 */

import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  Chip,
  LinearProgress,
  Link,
  TextField,
  Typography,
} from '@mui/material';
import { ApiRequestError, backendApiClient } from '../../../services/api/BackendApiClient';
import type { SourceConnection } from './types';
import { SPORTS_TRACKER_ERRORS } from './types';
import { tokens } from '../theme/contactsTheme';
import { fs } from '../settings/uiSettings';

/** Props for SportsTrackerTab */
export interface SportsTrackerTabProps {
  /** Whether the dialog is open (polling stops when it closes) */
  open: boolean;
  /** Called when a sync has finished, so the map reloads */
  onChanged: () => void;
}

/** How often the connection is re-read while a sync runs */
const POLL_MS = 2000;

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
 * Error code of a failed API call.
 * @param e - What was thrown
 * @returns The backend error code, or ''
 */
const codeOf = (e: unknown): string => (e instanceof ApiRequestError ? e.code : '');

/** The three steps to find the session key */
const SessionKeySteps: React.FC = () => (
  <Box
    component="ol"
    sx={{ m: 0, mb: 1.5, pl: 2.5, color: tokens.textSecondary, fontSize: fs(14), lineHeight: 1.6 }}
  >
    <li>
      Sign in at{' '}
      <Link href="https://www.sports-tracker.com" target="_blank" rel="noopener noreferrer">
        www.sports-tracker.com
      </Link>{' '}
      in this browser.
    </li>
    <li>
      Open the browser&apos;s developer tools (F12) → <strong>Application</strong> (Chrome/Edge) or{' '}
      <strong>Storage</strong> (Firefox) → <strong>Cookies</strong> →{' '}
      <code>https://www.sports-tracker.com</code>.
    </li>
    <li>
      Copy the value of the cookie named <code>sessionkey</code> and paste it here.
    </li>
  </Box>
);

/**
 * Sports Tracker connection.
 * @param props - Component props
 * @returns Tab content
 */
const SportsTrackerTab: React.FC<SportsTrackerTabProps> = ({ open, onChanged }) => {
  const [conn, setConn] = useState<SourceConnection | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [sessionKey, setSessionKey] = useState('');
  const [showKey, setShowKey] = useState(false);
  const [refused, setRefused] = useState(false);
  const [expired, setExpired] = useState(false);
  const lastStatus = useRef<SourceConnection['status'] | null>(null);

  const load = useCallback(async () => {
    try {
      const all = await backendApiClient.atlasConnections();
      setConn(all.find((c) => c.provider === 'sports-tracker') ?? null);
    } catch (e) {
      setError((e as Error).message || 'Loading the connection failed');
    } finally {
      setLoaded(true);
    }
  }, []);

  useEffect(() => {
    if (open) {
      setError(null);
      void load();
    }
  }, [open, load]);

  const status = conn?.status ?? null;

  // Poll while a sync runs and the dialog is open
  useEffect(() => {
    if (!open || status !== 'syncing') {
      return undefined;
    }
    const timer = window.setInterval(() => {
      backendApiClient
        .atlasConnections()
        .then((all) => setConn(all.find((c) => c.provider === 'sports-tracker') ?? null))
        .catch(() => {
          // A missed poll is retried on the next tick
        });
    }, POLL_MS);
    return () => window.clearInterval(timer);
  }, [open, status]);

  // A sync that finished brought data in: reload the map
  useEffect(() => {
    if (lastStatus.current === 'syncing' && status !== 'syncing') {
      onChanged();
    }
    if (status === 'expired') {
      setExpired(true);
    }
    lastStatus.current = status;
  }, [status, onChanged]);

  const connectAccount = async (credentials: {
    username?: string;
    password?: string;
    sessionKey?: string;
  }) => {
    setBusy(true);
    setError(null);
    try {
      const c = await backendApiClient.atlasConnectSportsTracker(credentials);
      setConn(c);
      setPassword('');
      setSessionKey('');
      setRefused(false);
      setExpired(false);
      setShowKey(false);
    } catch (e) {
      const code = codeOf(e);
      if (code === SPORTS_TRACKER_ERRORS.loginRefused) {
        setRefused(true);
        setShowKey(true);
        setPassword('');
      } else if (code === SPORTS_TRACKER_ERRORS.keyInvalid) {
        setError(
          'That session key did not work. Sign in at sports-tracker.com again and copy the current value of the sessionkey cookie.',
        );
      } else if (code === SPORTS_TRACKER_ERRORS.keyExpired) {
        setExpired(true);
      } else {
        setError((e as Error).message || 'Connecting Sports Tracker failed');
      }
    } finally {
      setBusy(false);
    }
  };

  const sync = async () => {
    setBusy(true);
    setError(null);
    try {
      setConn(await backendApiClient.atlasSyncSportsTracker());
    } catch (e) {
      if (codeOf(e) === SPORTS_TRACKER_ERRORS.keyExpired) {
        setExpired(true);
        void load();
      } else {
        setError((e as Error).message || 'Syncing Sports Tracker failed');
      }
    } finally {
      setBusy(false);
    }
  };

  const disconnect = async () => {
    if (
      !window.confirm(
        'Disconnect Sports Tracker? Atlas forgets the sign-in; the workouts already imported stay.',
      )
    ) {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await backendApiClient.atlasDisconnectSportsTracker();
      setConn(null);
      setExpired(false);
      setRefused(false);
      setShowKey(false);
    } catch (e) {
      setError((e as Error).message || 'Disconnecting Sports Tracker failed');
    } finally {
      setBusy(false);
    }
  };

  if (!loaded) {
    return <LinearProgress sx={{ my: 2 }} />;
  }

  const needsSignIn = !conn || expired || conn.status === 'expired';
  const syncing = conn?.status === 'syncing';

  return (
    <Box>
      <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 1.5 }}>
        Atlas signs in to Sports Tracker for you and imports your workouts with their tracks and
        heart rate. Syncing again updates them instead of adding copies.
      </Typography>

      {error && (
        <Alert severity="error" sx={{ mb: 1.5 }} onClose={() => setError(null)}>
          {error}
        </Alert>
      )}

      {conn && !needsSignIn && (
        <Box sx={{ p: 1.5, borderRadius: '8px', bgcolor: tokens.page, mb: 1.5 }}>
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 0.5 }}>
            <Typography variant="body2" sx={{ fontWeight: 600, flex: 1, minWidth: 0 }} noWrap>
              {conn.account || 'Sports Tracker'}
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
              <LinearProgress
                variant={conn.itemsTotal > 0 ? 'determinate' : 'indeterminate'}
                value={conn.itemsTotal > 0 ? (conn.itemsDone / conn.itemsTotal) * 100 : undefined}
              />
              <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                {conn.itemsTotal > 0
                  ? `${conn.itemsDone.toLocaleString()} of ${conn.itemsTotal.toLocaleString()} workouts`
                  : 'Listing workouts…'}
              </Typography>
            </Box>
          )}
          {!syncing && conn.lastSyncAt && (
            <Typography variant="caption" sx={{ color: tokens.textSecondary, display: 'block' }}>
              {conn.itemsTotal.toLocaleString()} workouts found ·{' '}
              {conn.itemsImported.toLocaleString()} new or updated
            </Typography>
          )}
          {conn.lastError && !syncing && (
            <Alert severity="error" sx={{ mt: 1 }}>
              {conn.lastError}
            </Alert>
          )}
          <Box sx={{ display: 'flex', gap: 1, mt: 1.5 }}>
            <Button variant="contained" size="small" disabled={busy || syncing} onClick={sync}>
              Sync again
            </Button>
            <Button size="small" color="error" disabled={busy} onClick={disconnect}>
              Disconnect
            </Button>
          </Box>
        </Box>
      )}

      {needsSignIn && (
        <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1.25, maxWidth: 440 }}>
          {expired && (
            <Alert severity="warning">
              The Sports Tracker sign-in has expired. Sign in again (or paste a new session key) to
              keep syncing; the workouts already imported stay.
            </Alert>
          )}
          {!showKey && (
            <>
              <TextField
                size="small"
                label="Username or email"
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                autoComplete="username"
              />
              <TextField
                size="small"
                label="Password"
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                autoComplete="current-password"
                helperText="Used once to sign in to Sports Tracker; never stored."
              />
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5 }}>
                <Button
                  variant="contained"
                  disabled={busy || !username.trim() || !password}
                  onClick={() => connectAccount({ username: username.trim(), password })}
                >
                  Connect and import
                </Button>
                <Link
                  component="button"
                  type="button"
                  variant="body2"
                  onClick={() => setShowKey(true)}
                >
                  Use a session key instead
                </Link>
              </Box>
            </>
          )}
          {showKey && (
            <>
              {refused ? (
                <Alert severity="info">
                  Sports Tracker refused a sign-in from the server (it may require its own web
                  app&apos;s sign-in). You can connect with the session key of your browser&apos;s
                  Sports Tracker sign-in instead:
                </Alert>
              ) : (
                <Typography variant="body2" sx={{ color: tokens.textSecondary }}>
                  Connect with the session key of your browser&apos;s Sports Tracker sign-in:
                </Typography>
              )}
              <SessionKeySteps />
              <TextField
                size="small"
                label="Session key"
                value={sessionKey}
                onChange={(e) => setSessionKey(e.target.value)}
                autoComplete="off"
                helperText="Stored encrypted so Sync again works; Disconnect deletes it."
              />
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5 }}>
                <Button
                  variant="contained"
                  disabled={busy || !sessionKey.trim()}
                  onClick={() =>
                    connectAccount({
                      sessionKey: sessionKey.trim(),
                      username: username.trim() || undefined,
                    })
                  }
                >
                  Connect with session key
                </Button>
                <Link
                  component="button"
                  type="button"
                  variant="body2"
                  onClick={() => {
                    setShowKey(false);
                    setRefused(false);
                  }}
                >
                  Use username and password
                </Link>
              </Box>
            </>
          )}
          {conn && (
            <Box>
              <Button size="small" color="error" disabled={busy} onClick={disconnect}>
                Disconnect
              </Button>
            </Box>
          )}
        </Box>
      )}
      {busy && <LinearProgress sx={{ mt: 1.5 }} />}
    </Box>
  );
};

export default SportsTrackerTab;
