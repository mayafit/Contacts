/**
 * @fileoverview Owner-only system status bar (Epic 22). A compact traffic-light
 * that sits in the Atlas toolbar: a summary lamp that opens a popover with
 * per-component lamps (Nodes, Data, App, Vault, Monitoring) and an "Open Grafana"
 * button that wakes the scaled-to-zero Grafana on demand.
 *
 * It renders only for the system operator (selectIsAdmin). The health data comes
 * from GET /api/system/status, which the backend reads from Redis — the app has
 * no Kubernetes access. See
 * docs/planning/epic-22-ops-status-and-ondemand-grafana.md.
 * @module Contacts/atlas/SystemStatusBar
 */
import React, { useCallback, useEffect, useRef, useState } from 'react';
import { useSelector } from 'react-redux';
import {
  Box,
  ButtonBase,
  Button,
  CircularProgress,
  Divider,
  Popover,
  Tooltip,
  Typography,
} from '@mui/material';
import OpenInNew from '@mui/icons-material/OpenInNew';

import {
  backendApiClient,
  type LampState,
  type SystemStatus,
} from '../../../services/api/BackendApiClient';
import { selectIsAdmin } from '../redux/slices/auth/selectors';
import { tokens } from '../theme/contactsTheme';

/** The Cloudflare-Access hostname the owner configures for the tunnel (Epic 22). */
const GRAFANA_URL = 'https://grafana.mayaffit.com';
const POLL_MS = 30_000;
const WAKE_POLL_MS = 3_000;
const WAKE_TIMEOUT_MS = 120_000;

const LAMP_COLORS: Record<LampState, string> = {
  green: '#3fb950',
  yellow: tokens.attention, // '#e8a23a'
  red: '#e5534b',
  unknown: tokens.textMuted,
};

const LAMP_TEXT: Record<LampState, string> = {
  green: 'All systems healthy',
  yellow: 'Degraded',
  red: 'Attention needed',
  unknown: 'Status unavailable',
};

/** Normalise a (possibly untrusted) state string to a known lamp state. */
const KNOWN_STATES: LampState[] = ['green', 'yellow', 'red', 'unknown'];
const safeState = (s: string | undefined | null): LampState =>
  KNOWN_STATES.includes(s as LampState) ? (s as LampState) : 'unknown';

function Dot({ state, size = 10 }: { state: LampState; size?: number }) {
  const color = LAMP_COLORS[state];
  return (
    <Box
      component="span"
      sx={{
        width: size,
        height: size,
        borderRadius: '50%',
        bgcolor: color,
        flexShrink: 0,
        // A soft halo so a single small dot still reads as a status light.
        boxShadow: state === 'unknown' ? 'none' : `0 0 0 3px ${color}22`,
      }}
    />
  );
}

export default function SystemStatusBar() {
  const isAdmin = useSelector(selectIsAdmin);
  const [status, setStatus] = useState<SystemStatus | null>(null);
  const [anchorEl, setAnchorEl] = useState<HTMLElement | null>(null);
  const [grafanaBusy, setGrafanaBusy] = useState(false);
  const [grafanaError, setGrafanaError] = useState<string | null>(null);
  const mounted = useRef(true);
  const wakeAbort = useRef(false);

  const refresh = useCallback(async () => {
    try {
      const s = await backendApiClient.getSystemStatus();
      if (mounted.current) {
        setStatus(s);
      }
      return s;
    } catch {
      // Treat an outright failure as "unknown" rather than hiding the bar.
      if (mounted.current) {
        setStatus(
          (prev) =>
            prev ?? {
              generatedAt: null,
              overall: 'unknown',
              lamps: [],
              grafana: { awake: false, ready: false, wakeUntil: null },
            },
        );
      }
      return null;
    }
  }, []);

  useEffect(() => {
    mounted.current = true;
    if (!isAdmin) {
      return undefined;
    }
    refresh();
    const id = window.setInterval(refresh, POLL_MS);
    return () => {
      mounted.current = false;
      window.clearInterval(id);
    };
  }, [isAdmin, refresh]);

  const openGrafana = useCallback(async () => {
    setGrafanaError(null);
    wakeAbort.current = false;
    // Already up — just open it (and extend the wake window).
    if (status?.grafana.ready) {
      backendApiClient.wakeGrafana().catch(() => undefined);
      window.open(GRAFANA_URL, '_blank', 'noopener,noreferrer');
      return;
    }
    setGrafanaBusy(true);
    try {
      await backendApiClient.wakeGrafana();
      const deadline = Date.now() + WAKE_TIMEOUT_MS;
      // Poll until the agent reports the pod ready, then open.

      while (Date.now() < deadline) {
        await new Promise((r) => setTimeout(r, WAKE_POLL_MS));
        if (!mounted.current || wakeAbort.current) {
          setGrafanaBusy(false);
          return;
        }
        const s = await refresh();
        if (s?.grafana.ready) {
          setGrafanaBusy(false);
          window.open(GRAFANA_URL, '_blank', 'noopener,noreferrer');
          return;
        }
      }
      if (mounted.current) {
        setGrafanaBusy(false);
        setGrafanaError('Grafana is taking longer than usual to start. Try again in a moment.');
      }
    } catch {
      if (mounted.current) {
        setGrafanaBusy(false);
        setGrafanaError('Could not start Grafana. Is the status store reachable?');
      }
    }
  }, [status, refresh]);

  if (!isAdmin) {
    return null;
  }

  const overall: LampState = safeState(status?.overall);
  const lamps = status?.lamps ?? [];

  return (
    <>
      <Tooltip title={`System: ${LAMP_TEXT[overall]}`}>
        <ButtonBase
          onClick={(e) => setAnchorEl(e.currentTarget)}
          aria-label={`System status: ${LAMP_TEXT[overall]}`}
          sx={{
            display: 'flex',
            alignItems: 'center',
            gap: 0.75,
            px: 1,
            height: 32,
            borderRadius: '8px',
            border: `1px solid ${tokens.border}`,
            bgcolor: tokens.page,
            '&:hover': { borderColor: tokens.borderStrong },
          }}
        >
          <Dot state={overall} />
          <Typography
            variant="body2"
            sx={{ color: tokens.textSecondary, display: { xs: 'none', lg: 'inline' } }}
          >
            System
          </Typography>
        </ButtonBase>
      </Tooltip>

      <Popover
        open={Boolean(anchorEl)}
        anchorEl={anchorEl}
        onClose={() => {
          // Closing the popover abandons any in-flight Grafana wake poll so it
          // can't pop a tab open after the user has moved on.
          wakeAbort.current = true;
          setAnchorEl(null);
        }}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
        transformOrigin={{ vertical: 'top', horizontal: 'right' }}
        slotProps={{
          paper: {
            sx: {
              bgcolor: tokens.raised,
              border: `1px solid ${tokens.border}`,
              minWidth: 280,
              p: 1.5,
            },
          },
        }}
      >
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 1 }}>
          <Dot state={overall} />
          <Typography variant="subtitle1">{LAMP_TEXT[overall]}</Typography>
        </Box>

        {status?.generatedAt && (
          <Typography variant="caption" sx={{ color: tokens.textMuted, display: 'block', mb: 1 }}>
            Updated {new Date(status.generatedAt).toLocaleTimeString()}
          </Typography>
        )}

        <Box sx={{ display: 'flex', flexDirection: 'column', gap: 0.75 }}>
          {lamps.length === 0 && (
            <Typography variant="body2" sx={{ color: tokens.textMuted }}>
              No status reported yet.
            </Typography>
          )}
          {lamps.map((l) => (
            <Box key={l.id} sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
              <Dot state={safeState(l.state)} size={8} />
              <Typography variant="body2" sx={{ minWidth: 80, color: tokens.text }}>
                {l.label}
              </Typography>
              <Typography
                variant="caption"
                sx={{ color: tokens.textMuted, flex: 1, textAlign: 'right' }}
              >
                {l.detail}
              </Typography>
            </Box>
          ))}
        </Box>

        <Divider sx={{ my: 1.5, borderColor: tokens.border }} />

        <Button
          fullWidth
          variant="outlined"
          size="small"
          onClick={openGrafana}
          disabled={grafanaBusy}
          startIcon={
            grafanaBusy ? <CircularProgress size={14} /> : <OpenInNew sx={{ fontSize: 16 }} />
          }
        >
          {grafanaBusy
            ? 'Starting Grafana…'
            : status?.grafana.ready
              ? 'Open Grafana'
              : 'Start & open Grafana'}
        </Button>
        {grafanaBusy && (
          <Typography
            variant="caption"
            sx={{ color: tokens.textMuted, display: 'block', mt: 0.75, textAlign: 'center' }}
          >
            Waking the dashboard — about a minute.
          </Typography>
        )}
        {grafanaError && (
          <Typography variant="caption" sx={{ color: LAMP_COLORS.red, display: 'block', mt: 0.75 }}>
            {grafanaError}
          </Typography>
        )}
      </Popover>
    </>
  );
}
