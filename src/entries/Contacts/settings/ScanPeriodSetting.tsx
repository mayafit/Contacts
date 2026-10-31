/**
 * @fileoverview Settings → Email and calendar: how far back the activity scan reads
 * (FR-90, AD-47 rule 1). Saved on the server beside the activity; a change starts a
 * full scan.
 * @module Contacts/settings/ScanPeriodSetting
 */

import React, { useEffect, useState } from 'react';
import { MenuItem, TextField, Typography } from '@mui/material';
import {
  backendApiClient,
  type ActivityStatusResponse,
} from '../../../services/api/BackendApiClient';
import { tokens } from '../theme/contactsTheme';

/** The periods offered, in years */
export const SCAN_PERIODS = [1, 2, 3, 5] as const;

/**
 * The scan period setting.
 * @returns The control with its explanation
 */
const ScanPeriodSetting: React.FC = () => {
  const [status, setStatus] = useState<ActivityStatusResponse | null>(null);
  const [state, setState] = useState<
    { s: 'loading' | 'idle' | 'saving' | 'saved' } | { s: 'error'; message: string }
  >({ s: 'loading' });

  useEffect(() => {
    let live = true;
    backendApiClient
      .getActivityStatus()
      .then((s) => {
        if (live) {
          setStatus(s);
          setState({ s: 'idle' });
        }
      })
      .catch((e: Error) => live && setState({ s: 'error', message: e.message }));
    return () => {
      live = false;
    };
  }, []);

  const years = status?.windowYears ?? 1;
  const connected = !!status && (status.connected.gmail || status.connected.calendar);

  const change = async (next: number) => {
    setState({ s: 'saving' });
    try {
      setStatus(await backendApiClient.setActivityWindow(next));
      setState({ s: 'saved' });
    } catch (e) {
      setState({ s: 'error', message: (e as Error).message });
    }
  };

  return (
    <>
      <TextField
        select
        size="small"
        label="Scan period"
        value={years}
        disabled={state.s === 'loading' || state.s === 'saving'}
        onChange={(e) => void change(Number(e.target.value))}
        sx={{ minWidth: 180 }}
      >
        {SCAN_PERIODS.map((y) => (
          <MenuItem key={y} value={y}>
            {y === 1 ? '1 year' : `${y} years`}
            {y === 1 ? ' (default)' : ''}
          </MenuItem>
        ))}
      </TextField>
      <Typography variant="body2" sx={{ color: tokens.textSecondary, mt: 1, maxWidth: 640 }}>
        How far back Insights and the contact card read your Gmail and Calendar for the last email,
        the last meeting and who you are in touch with. A longer period takes longer: the first scan
        and the weekly full scan read every message of it (about 40 a second, at most 10,000 per
        year). The figures &quot;in the last 12 months&quot; stay 12 months. To see one
        person&apos;s whole history at any date, use <strong>Search all history</strong> in their
        card.
      </Typography>
      <Typography
        variant="caption"
        component="div"
        role="status"
        sx={{ color: state.s === 'error' ? tokens.attention : tokens.textMuted, mt: 0.5 }}
      >
        {state.s === 'error'
          ? state.message
          : state.s === 'saving'
            ? 'Saving…'
            : state.s === 'saved'
              ? connected
                ? 'Saved. A full scan has started; Insights shows its progress.'
                : 'Saved. It applies when Gmail or Calendar is connected (Insights).'
              : !connected && status
                ? 'Gmail and Calendar are not connected (Insights).'
                : ''}
      </Typography>
    </>
  );
};

export default ScanPeriodSetting;
