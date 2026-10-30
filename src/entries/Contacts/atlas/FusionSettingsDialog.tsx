/**
 * @fileoverview How sources are combined: the fusion service's status, each
 * source's time and location accuracy, the match threshold, re-fusing
 * @module Contacts/atlas/FusionSettingsDialog
 */

import React, { useEffect, useMemo, useRef, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  IconButton,
  InputAdornment,
  LinearProgress,
  Slider,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import { CheckCircleOutline, Restore, Sync } from '@mui/icons-material';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import type { FusionSettings, FusionStatus } from './types';
import { FILTER_COLOR, isSet, sourceLabel } from './types';
import {
  MATCH_THRESHOLD,
  describeFusionStatus,
  formatPercent,
  formatSeconds,
  fusionBusy,
  locationToleranceError,
  minutesTextToSeconds,
  secondsToMinutesText,
  timeToleranceError,
} from './fusionText';
import { tokens } from '../theme/contactsTheme';
import { fs } from '../settings/uiSettings';

/** How often the status is polled while the service works */
const POLL_MS = 2000;

/** Props for FusionSettingsDialog */
export interface FusionSettingsDialogProps {
  open: boolean;
  onClose: () => void;
  /** Called when the service finished work it did while the dialog was open: reload results */
  onChanged: () => void;
}

/** One source's tolerances as typed in the form */
interface Row {
  source: string;
  /** Minutes, as typed */
  time: string;
  /** Meters, as typed */
  location: string;
}

/** A source's built-in default, remembered from a server reply that reported it */
type Defaults = Record<string, { timeToleranceS: number; locationToleranceM: number }>;

/** Name of a source in the table ('other' covers every source without its own row) */
const rowLabel = (source: string): string =>
  source === 'other' ? 'Other sources' : sourceLabel(source);

/** The form rows of the server's settings */
const toRows = (s: FusionSettings): Row[] =>
  s.sources.map((x) => ({
    source: x.source,
    time: secondsToMinutesText(x.timeToleranceS),
    location: String(x.locationToleranceM),
  }));

/** Whether a row holds these values */
const rowEquals = (row: Row, timeS: number, locationM: number): boolean =>
  minutesTextToSeconds(row.time) === timeS && Number(row.location.trim()) === locationM;

/**
 * Fusion settings dialog.
 * @param props - Component props
 * @returns Dialog
 */
const FusionSettingsDialog: React.FC<FusionSettingsDialogProps> = ({
  open,
  onClose,
  onChanged,
}) => {
  const [loaded, setLoaded] = useState<FusionSettings | null>(null);
  const [rows, setRows] = useState<Row[]>([]);
  const [threshold, setThreshold] = useState(0.5);
  const [defaults, setDefaults] = useState<Defaults>({});
  const [status, setStatus] = useState<FusionStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  // Bumped to (re)start polling the status
  const [pollTick, setPollTick] = useState(0);
  // The service had work since the last reload: reload when it is idle again
  const worked = useRef(false);
  const changedRef = useRef(onChanged);
  changedRef.current = onChanged;

  /** Shows settings from the server and remembers the defaults it reports */
  const apply = (s: FusionSettings) => {
    setLoaded(s);
    setRows(toRows(s));
    setThreshold(s.matchThreshold);
    setDefaults((d) => {
      const next = { ...d };
      s.sources
        .filter((x) => x.isDefault)
        .forEach((x) => {
          next[x.source] = {
            timeToleranceS: x.timeToleranceS,
            locationToleranceM: x.locationToleranceM,
          };
        });
      return next;
    });
  };

  useEffect(() => {
    if (!open) {
      return undefined;
    }
    let alive = true;
    setError(null);
    backendApiClient
      .atlasFusionSettings()
      .then((s) => alive && apply(s))
      .catch((e) => alive && setError((e as Error).message));
    return () => {
      alive = false;
    };
  }, [open]);

  // The status: once on open and after an action, then every 2 s while the service works
  useEffect(() => {
    if (!open) {
      return undefined;
    }
    let alive = true;
    let timer: number | undefined;
    const poll = async () => {
      try {
        const st = await backendApiClient.atlasFusionStatus();
        if (!alive) {
          return;
        }
        setStatus(st);
        if (fusionBusy(st)) {
          worked.current = true;
          timer = window.setTimeout(poll, POLL_MS);
        } else if (worked.current) {
          worked.current = false;
          changedRef.current();
        }
      } catch (e) {
        if (alive) {
          setError((e as Error).message || 'Loading the fusion status failed');
          // Try again a little later rather than going stale
          timer = window.setTimeout(poll, POLL_MS * 3);
        }
      }
    };
    void poll();
    return () => {
      alive = false;
      window.clearTimeout(timer);
    };
  }, [open, pollTick]);

  /** Work was queued: follow it until the service is idle */
  const follow = () => {
    worked.current = true;
    setPollTick((n) => n + 1);
  };

  const errors = useMemo(
    () =>
      rows.map((r) => ({
        time: timeToleranceError(r.time),
        location: locationToleranceError(r.location),
      })),
    [rows],
  );
  const valid = errors.every((e) => !e.time && !e.location);
  const dirty =
    !!loaded &&
    (Math.abs(threshold - loaded.matchThreshold) > 1e-6 ||
      rows.some((r, i) => {
        const s = loaded.sources[i];
        return !s || !rowEquals(r, s.timeToleranceS, s.locationToleranceM);
      }));

  const setRow = (i: number, patch: Partial<Row>) =>
    setRows((rs) => rs.map((r, j) => (j === i ? { ...r, ...patch } : r)));

  const save = async () => {
    if (!loaded || !valid) {
      return;
    }
    setSaving(true);
    setError(null);
    try {
      const saved = await backendApiClient.atlasSaveFusionSettings({
        matchThreshold: threshold,
        sources: rows.map((r, i) => ({
          source: r.source,
          timeToleranceS: minutesTextToSeconds(r.time) ?? loaded.sources[i].timeToleranceS,
          locationToleranceM: Math.round(Number(r.location.trim())),
          isDefault: false,
        })),
      });
      apply(saved);
      follow();
    } catch (e) {
      setError((e as Error).message || 'Saving the fusion settings failed');
    } finally {
      setSaving(false);
    }
  };

  const reset = async () => {
    if (
      !window.confirm(
        'Reset every source’s accuracy and the match threshold to the defaults? Everything is fused again.',
      )
    ) {
      return;
    }
    setSaving(true);
    setError(null);
    try {
      apply(await backendApiClient.atlasResetFusionSettings());
      follow();
    } catch (e) {
      setError((e as Error).message || 'Resetting the fusion settings failed');
    } finally {
      setSaving(false);
    }
  };

  const refuse = async () => {
    setSaving(true);
    setError(null);
    try {
      setStatus(await backendApiClient.atlasFuse());
      follow();
    } catch (e) {
      setError((e as Error).message || 'Re-fusing failed');
    } finally {
      setSaving(false);
    }
  };

  const busy = fusionBusy(status);
  const grid = {
    display: 'grid',
    gridTemplateColumns: { xs: '1fr 1fr 32px', sm: 'minmax(0, 1fr) 128px 128px 32px' },
    columnGap: 1,
    rowGap: 0.5,
    alignItems: 'start',
  } as const;

  return (
    <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth>
      <DialogTitle>How sources are combined</DialogTitle>
      <DialogContent>
        <Box
          sx={{
            p: 1.25,
            mb: 2,
            borderRadius: '8px',
            bgcolor: tokens.page,
            border: `1px solid ${tokens.border}`,
          }}
        >
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
            {busy ? (
              <Sync sx={{ fontSize: fs(18), color: FILTER_COLOR }} />
            ) : (
              <CheckCircleOutline sx={{ fontSize: fs(18), color: tokens.textSecondary }} />
            )}
            <Typography variant="body2" aria-live="polite">
              {status ? describeFusionStatus(status) : 'Loading the fusion service status…'}
            </Typography>
          </Box>
          {busy && <LinearProgress sx={{ mt: 1, borderRadius: 1 }} />}
        </Box>

        <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 2 }}>
          Each source records times and positions with its own accuracy. Two records are taken to be
          the same activity when their start and end times agree within both sources’ time accuracy
          together, and their tracks, compared at the same moments, stay within both sources’
          location accuracy together. The resulting confidence must reach the match threshold.
        </Typography>

        {error && (
          <Alert severity="error" onClose={() => setError(null)} sx={{ mb: 2 }}>
            {error}
          </Alert>
        )}

        {loaded && (
          <>
            <Box sx={{ ...grid, display: { xs: 'none', sm: 'grid' }, mb: 0.5 }}>
              <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                Source
              </Typography>
              <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                Time ±
              </Typography>
              <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                Location ±
              </Typography>
              <span />
            </Box>
            {rows.map((r, i) => {
              const server = loaded.sources[i];
              const def = defaults[r.source];
              const unchanged =
                !!server && rowEquals(r, server.timeToleranceS, server.locationToleranceM);
              const atDefault = isSet(def)
                ? rowEquals(r, def.timeToleranceS, def.locationToleranceM)
                : unchanged && !!server?.isDefault;
              return (
                <Box
                  key={r.source}
                  sx={{ ...grid, py: 0.75, borderBottom: `1px solid ${tokens.border}` }}
                >
                  <Box
                    sx={{
                      gridColumn: { xs: '1 / -1', sm: 'auto' },
                      display: 'flex',
                      alignItems: 'center',
                      gap: 0.75,
                      minHeight: { sm: 40 },
                    }}
                  >
                    <Typography variant="body2" sx={{ fontWeight: 600 }}>
                      {rowLabel(r.source)}
                    </Typography>
                    {atDefault && (
                      <Chip
                        size="small"
                        label="default"
                        variant="outlined"
                        sx={{ height: 18, fontSize: fs(12), color: tokens.textMuted }}
                      />
                    )}
                  </Box>
                  <TextField
                    size="small"
                    value={r.time}
                    onChange={(e) => setRow(i, { time: e.target.value })}
                    error={!!errors[i]?.time}
                    helperText={errors[i]?.time ?? ' '}
                    inputProps={{
                      inputMode: 'decimal',
                      'aria-label': `${rowLabel(r.source)} time accuracy in minutes`,
                    }}
                    InputProps={{
                      endAdornment: <InputAdornment position="end">min</InputAdornment>,
                    }}
                    FormHelperTextProps={{ sx: { mx: 0.5 } }}
                  />
                  <TextField
                    size="small"
                    value={r.location}
                    onChange={(e) => setRow(i, { location: e.target.value })}
                    error={!!errors[i]?.location}
                    helperText={errors[i]?.location ?? ' '}
                    inputProps={{
                      inputMode: 'numeric',
                      'aria-label': `${rowLabel(r.source)} location accuracy in meters`,
                    }}
                    InputProps={{ endAdornment: <InputAdornment position="end">m</InputAdornment> }}
                    FormHelperTextProps={{ sx: { mx: 0.5 } }}
                  />
                  <Box sx={{ pt: 0.5 }}>
                    {isSet(def) && !atDefault && (
                      <Tooltip
                        title={`Back to the default: ±${formatSeconds(def.timeToleranceS)}, ±${def.locationToleranceM} m`}
                      >
                        <IconButton
                          size="small"
                          aria-label={`Restore the ${rowLabel(r.source)} default`}
                          onClick={() =>
                            setRow(i, {
                              time: secondsToMinutesText(def.timeToleranceS),
                              location: String(def.locationToleranceM),
                            })
                          }
                        >
                          <Restore fontSize="small" />
                        </IconButton>
                      </Tooltip>
                    )}
                  </Box>
                </Box>
              );
            })}

            <Box sx={{ mt: 2.5, px: 0.5 }}>
              <Typography variant="body2" sx={{ fontWeight: 600 }}>
                Match threshold: {formatPercent(threshold)}
              </Typography>
              <Typography variant="caption" sx={{ color: tokens.textMuted, display: 'block' }}>
                The lowest confidence for combining records automatically. Lower combines more
                readily; higher needs closer agreement.
              </Typography>
              <Slider
                value={Math.min(MATCH_THRESHOLD.max, Math.max(MATCH_THRESHOLD.min, threshold))}
                min={MATCH_THRESHOLD.min}
                max={MATCH_THRESHOLD.max}
                step={MATCH_THRESHOLD.step}
                onChange={(_, v) => setThreshold(Array.isArray(v) ? v[0] : v)}
                valueLabelDisplay="auto"
                valueLabelFormat={formatPercent}
                aria-label="Match threshold"
                sx={{ mt: 0.5 }}
              />
            </Box>
          </>
        )}
      </DialogContent>
      <DialogActions sx={{ flexWrap: 'wrap', gap: 0.5 }}>
        <Button onClick={reset} disabled={saving || !loaded}>
          Reset to defaults
        </Button>
        <Box sx={{ flex: 1 }} />
        <Button onClick={refuse} disabled={saving} startIcon={<Sync />}>
          Re-fuse now
        </Button>
        <Button onClick={onClose}>Close</Button>
        <Button variant="contained" onClick={save} disabled={saving || !dirty || !valid}>
          Save
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default FusionSettingsDialog;
