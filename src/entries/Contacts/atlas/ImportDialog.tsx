/**
 * @fileoverview Import: files, zips or a folder, parsed in the browser and sent in batches;
 * or a connected Sports Tracker account, synced by the server
 * @module Contacts/atlas/ImportDialog
 */

import React, { useEffect, useRef, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  IconButton,
  LinearProgress,
  Radio,
  RadioGroup,
  Tab,
  Tabs,
  Tooltip,
  Typography,
} from '@mui/material';
import { DeleteOutline, FolderOpen, UploadFile } from '@mui/icons-material';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import type { ImportRun, ParsedData } from './types';
import { parseFile, readZip, type ImportFile } from './importers';
import { SamsungCollector, samsungKind } from './importers/samsung';
import { tokens } from '../theme/contactsTheme';
import { thinTrack, trackLength } from './geo';
import SportsTrackerTab from './SportsTrackerTab';
import OneDriveTab from './OneDriveTab';

/** Props for ImportDialog */
export interface ImportDialogProps {
  open: boolean;
  onClose: () => void;
  /** Called after anything was imported or deleted */
  onChanged: () => void;
  /** The tab shown when it opens (default: files) */
  initialTab?: ImportTab;
}

/** The dialog's tabs */
export type ImportTab = 'files' | 'sports-tracker' | 'onedrive';

/** Items per request: keeps each request well under Cloudflare's 100 MB */
const MAX_POINTS = 25_000;
const MAX_ITEMS = 4_000;

/**
 * Sends parsed data in batches, all under one import per source.
 * @param data - Parsed data
 * @param importIds - Import id per source (filled in by the first batch)
 * @param fileName - Shown in the import list
 * @param onSent - Progress callback
 */
const send = async (
  data: ParsedData,
  importIds: Map<string, string>,
  fileName: string,
  onSent: (n: number) => void,
): Promise<void> => {
  const batches: ParsedData[] = [];
  let current: ParsedData = {
    source: data.source,
    activities: [],
    places: [],
    visits: [],
    health: [],
  };
  let points = 0;
  let items = 0;
  const flush = () => {
    if (
      current.activities.length ||
      current.places.length ||
      current.visits.length ||
      current.health.length
    ) {
      batches.push(current);
    }
    current = { source: data.source, activities: [], places: [], visits: [], health: [] };
    points = 0;
    items = 0;
  };
  // Places before visits so visits find their places
  data.places.forEach((p) => {
    if (items >= MAX_ITEMS) {
      flush();
    }
    current.places.push(p);
    items += 1;
  });
  data.visits.forEach((v) => {
    if (items >= MAX_ITEMS) {
      flush();
    }
    current.visits.push(v);
    items += 1;
  });
  data.activities.forEach((original) => {
    // A very long recording (hours at one point a second) would not fit in a
    // request: keep its true distance, send fewer points
    const a =
      original.points && original.points.length > MAX_POINTS
        ? {
            ...original,
            distanceM: original.distanceM ?? trackLength(original.points),
            points: thinTrack(original.points, MAX_POINTS),
          }
        : original;
    const n = a.points?.length ?? 0;
    if (points + n > MAX_POINTS || items >= MAX_ITEMS) {
      flush();
    }
    current.activities.push(a);
    points += n;
    items += 1;
  });
  data.health.forEach((h) => {
    if (items >= MAX_ITEMS * 5) {
      flush();
    }
    current.health.push(h);
    items += 1;
  });
  flush();

  for (const b of batches) {
    const r = await backendApiClient.atlasIngest({
      ...b,
      importId: importIds.get(b.source) ?? null,
      fileName,
    });
    importIds.set(b.source, r.importId);
    onSent(b.activities.length + b.places.length + b.visits.length + b.health.length);
  }
};

/**
 * Import dialog.
 * @param props - Component props
 * @returns Dialog
 */
const ImportDialog: React.FC<ImportDialogProps> = ({
  open,
  onClose,
  onChanged,
  initialTab = 'files',
}) => {
  const [tab, setTab] = useState<ImportTab>(initialTab);
  useEffect(() => {
    if (open) {
      setTab(initialTab);
    }
  }, [open, initialTab]);
  const [source, setSource] = useState<'auto' | 'sports-tracker'>('auto');
  const [busy, setBusy] = useState(false);
  const [log, setLog] = useState<string[]>([]);
  const [sent, setSent] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [imports, setImports] = useState<ImportRun[]>([]);
  const fileInput = useRef<HTMLInputElement>(null);
  const folderInput = useRef<HTMLInputElement>(null);

  const loadImports = () =>
    backendApiClient
      .atlasSummary()
      .then((s) => setImports(s.imports))
      .catch(() => setImports([]));

  useEffect(() => {
    if (open) {
      setLog([]);
      setSent(0);
      setError(null);
      void loadImports();
    }
  }, [open]);

  const run = async (files: File[]) => {
    setBusy(true);
    setError(null);
    setSent(0);
    const importIds = new Map<string, string>();
    const note = (line: string) => setLog((l) => [...l.slice(-200), line]);
    const hint = source === 'sports-tracker' ? 'sports-tracker' : undefined;
    const label = files.length === 1 ? files[0].name : `${files.length} files`;
    let found = 0;
    // A Samsung Health export comes in many files (workouts, their heart rate and
    // GPS files): they are put together as they arrive
    const samsung = new SamsungCollector();
    const handle = async (f: ImportFile) => {
      try {
        const parsed = samsungKind(f.path)
          ? samsung.add(f.path, await f.text())
          : await parseFile(f, hint);
        if (!parsed) {
          return;
        }
        const n = parsed.activities.length + parsed.visits.length + parsed.health.length;
        if (!n) {
          return;
        }
        found += 1;
        note(
          `${f.path}: ${parsed.activities.length} activities, ${parsed.visits.length} visits, ${parsed.health.length} health values`,
        );
        await send(parsed, importIds, label, (k) => setSent((s) => s + k));
      } catch (e) {
        note(`${f.path}: ${(e as Error).message}`);
      }
    };
    try {
      for (const file of files) {
        const path =
          (file as File & { webkitRelativePath?: string }).webkitRelativePath || file.name;
        if (file.name.toLowerCase().endsWith('.zip')) {
          note(`Reading ${file.name}…`);
          await readZip(file, handle);
        } else {
          await handle({ path, text: () => file.text() });
        }
      }
      // Samsung workouts whose heart-rate or GPS file never came
      const rest = samsung.finish();
      if (rest.activities.length + rest.health.length > 0) {
        found += 1;
        note(`Samsung Health: ${rest.activities.length} more workouts`);
        await send(rest, importIds, label, (k) => setSent((s) => s + k));
      }
      note(found ? 'Done.' : 'No importable data found in these files.');
      onChanged();
      void loadImports();
    } catch (e) {
      setError((e as Error).message || 'Import failed');
    } finally {
      setBusy(false);
    }
  };

  const remove = async (id: string) => {
    try {
      await backendApiClient.atlasDeleteImport(id);
      onChanged();
      void loadImports();
    } catch (e) {
      setError((e as Error).message);
    }
  };

  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} maxWidth="md" fullWidth>
      <DialogTitle sx={{ pb: 0 }}>Import</DialogTitle>
      <Tabs
        value={tab}
        onChange={(_, v: ImportTab) => setTab(v)}
        sx={{ px: 3, borderBottom: `1px solid ${tokens.border}` }}
      >
        <Tab value="files" label="Files" disabled={busy} />
        <Tab value="sports-tracker" label="Sports Tracker" disabled={busy} />
        <Tab value="onedrive" label="OneDrive photos" disabled={busy} />
      </Tabs>
      <DialogContent sx={{ pt: '16px !important' }}>
        <Box sx={{ display: tab === 'sports-tracker' ? 'block' : 'none' }}>
          <SportsTrackerTab open={open} onChanged={onChanged} />
        </Box>
        <Box sx={{ display: tab === 'onedrive' ? 'block' : 'none' }}>
          <OneDriveTab open={open} onChanged={onChanged} />
        </Box>
        {tab === 'files' && (
          <>
            <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 1 }}>
              Files are read in this browser; only the activities, places, visits and health values
              are sent. Importing the same files again updates them instead of adding copies.
            </Typography>
            <Box
              component="ul"
              sx={{ m: 0, pl: 2.5, color: tokens.textSecondary, fontSize: 14, mb: 1.5 }}
            >
              <li>
                <strong>Google Timeline</strong>: <code>Timeline.json</code> exported from the phone
                (Settings → Location → Timeline → Export), or a Takeout zip with “Location History”.
              </li>
              <li>
                <strong>Google Fit / Fitbit</strong>: the Takeout zip (heart rate, steps, workouts).
              </li>
              <li>
                <strong>Sports Tracker</strong>: workouts exported as GPX (Workout → ⋯ → Export
                GPX), several at once.
              </li>
              <li>
                <strong>Samsung Health</strong>: the whole export folder or its zip (Settings →
                Download personal data): workouts with heart rate and GPS, and all-day heart rate.
              </li>
              <li>Any GPX or TCX file.</li>
            </Box>
            <RadioGroup
              row
              value={source}
              onChange={(e) => setSource(e.target.value as 'auto' | 'sports-tracker')}
            >
              <FormControlLabel
                value="auto"
                control={<Radio size="small" />}
                label="Recognize automatically"
              />
              <FormControlLabel
                value="sports-tracker"
                control={<Radio size="small" />}
                label="GPX/TCX are from Sports Tracker"
              />
            </RadioGroup>
            <Box sx={{ display: 'flex', gap: 1, my: 1.5 }}>
              <Button
                variant="contained"
                startIcon={<UploadFile />}
                disabled={busy}
                onClick={() => fileInput.current?.click()}
              >
                Choose files or zips
              </Button>
              <Button
                variant="outlined"
                startIcon={<FolderOpen />}
                disabled={busy}
                onClick={() => folderInput.current?.click()}
              >
                Choose a folder
              </Button>
              <input
                ref={fileInput}
                type="file"
                multiple
                accept=".zip,.json,.gpx,.tcx"
                hidden
                onChange={(e) => {
                  const files = Array.from(e.target.files ?? []);
                  e.target.value = '';
                  if (files.length) {
                    void run(files);
                  }
                }}
              />
              <input
                ref={folderInput}
                type="file"
                hidden
                // @ts-expect-error webkitdirectory is not in the React types
                webkitdirectory=""
                onChange={(e) => {
                  const files = Array.from(e.target.files ?? []);
                  e.target.value = '';
                  if (files.length) {
                    void run(files);
                  }
                }}
              />
            </Box>
            {busy && <LinearProgress sx={{ mb: 1 }} />}
            {(busy || sent > 0) && (
              <Typography
                variant="caption"
                sx={{ color: tokens.textMuted, display: 'block', mb: 1 }}
              >
                {sent.toLocaleString()} items saved
              </Typography>
            )}
            {error && (
              <Alert severity="error" sx={{ mb: 1 }}>
                {error}
              </Alert>
            )}
            {log.length > 0 && (
              <Box
                sx={{
                  maxHeight: 160,
                  overflowY: 'auto',
                  fontSize: 13,
                  fontFamily: 'monospace',
                  color: tokens.textSecondary,
                  bgcolor: tokens.page,
                  p: 1,
                  borderRadius: '6px',
                  mb: 2,
                }}
              >
                {log.map((l, i) => (
                  <div key={i}>{l}</div>
                ))}
              </Box>
            )}
            {imports.length > 0 && (
              <>
                <Typography variant="subtitle2" sx={{ mt: 1 }}>
                  Imports
                </Typography>
                {imports.map((imp) => (
                  <Box
                    key={imp.id}
                    sx={{
                      display: 'flex',
                      alignItems: 'center',
                      gap: 1,
                      py: 0.5,
                      borderBottom: `1px solid ${tokens.border}`,
                    }}
                  >
                    <Box sx={{ flex: 1, minWidth: 0 }}>
                      <Typography variant="body2" noWrap>
                        {imp.fileName || imp.source} · {imp.source}
                      </Typography>
                      <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                        {new Date(imp.createdAt).toLocaleString('en-GB')} · {imp.activities}{' '}
                        activities · {imp.visits} visits · {imp.places} places ·{' '}
                        {imp.healthSamples.toLocaleString()} health values
                      </Typography>
                    </Box>
                    <Tooltip title="Delete this import and everything it brought in">
                      <span>
                        <IconButton
                          size="small"
                          disabled={busy}
                          onClick={() => remove(imp.id)}
                          aria-label="Delete import"
                        >
                          <DeleteOutline fontSize="small" />
                        </IconButton>
                      </span>
                    </Tooltip>
                  </Box>
                ))}
              </>
            )}
          </>
        )}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={busy}>
          Close
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default ImportDialog;
