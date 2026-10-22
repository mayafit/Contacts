/**
 * @fileoverview Review dialog: phone numbers, titles and links from email signatures
 * @module Contacts/components/insights/SignatureDialog
 */

import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  Checkbox,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  LinearProgress,
  Typography,
} from '@mui/material';
import { DriveFileRenameOutline } from '@mui/icons-material';
import type { Contact, ContactGroup } from '../../types/Contact';
import { backendApiClient } from '../../../../services/api/BackendApiClient';
import { useAppDispatch } from '../../types/hooks';
import { updateContact } from '../../redux/slices/contacts/contactsSlice';
import { buildPatch } from '../card/cardModel';
import { resolveChange, type ContactChange, type ResolvedChange } from '../../assistant/changes';
import { signatureChange, type SignatureConflict } from '../../assistant/signatures';
import type { ActivityIndex } from '../../model/activity';
import { clean, getDisplayName } from '../../model/contactView';
import { tokens } from '../../theme/contactsTheme';

/** Props for SignatureDialog */
export interface SignatureDialogProps {
  /** Open with these contacts, or the most-emailed ones when 'auto' */
  targets: Contact[] | 'auto' | null;
  onClose: () => void;
  contacts: Contact[];
  groups: Record<string, ContactGroup>;
  index?: ActivityIndex;
  /** Email content access granted */
  connected: boolean;
  onApplied: (count: number) => void;
}

interface Row {
  contact: Contact;
  /** The additions found */
  change: ContactChange;
  /** That change applied, for the diff */
  resolved: ResolvedChange;
  conflicts: SignatureConflict[];
  address?: string;
  include: boolean;
  /** Conflicts to apply too */
  replace: Set<string>;
}

const BATCH = 10;

/** Contacts whose signatures were read recently (resourceName → time), skipped by the automatic run */
const CHECKED_KEY = 'contacts.signaturesChecked';
const RECHECK_DAYS = 30;

const loadChecked = (): Record<string, number> => {
  try {
    const all = JSON.parse(localStorage.getItem(CHECKED_KEY) ?? '{}') as Record<string, number>;
    const since = Date.now() - RECHECK_DAYS * 86_400_000;
    return Object.fromEntries(Object.entries(all).filter(([, t]) => t > since));
  } catch {
    return {};
  }
};

const rememberChecked = (resourceNames: string[]) => {
  try {
    const all = loadChecked();
    resourceNames.forEach((rn) => (all[rn] = Date.now()));
    localStorage.setItem(CHECKED_KEY, JSON.stringify(all));
  } catch {
    // Not remembered (private mode): the next run reads them again
  }
};
const PER_RUN = 50;

/**
 * Signature dialog.
 * @param props - Component props
 * @returns Dialog
 */
const SignatureDialog: React.FC<SignatureDialogProps> = ({
  targets,
  onClose,
  contacts,
  groups,
  index,
  connected,
  onApplied,
}) => {
  const dispatch = useAppDispatch();
  const [rows, setRows] = useState<Row[]>([]);
  const [progress, setProgress] = useState<{ done: number; total: number } | null>(null);
  const [checked, setChecked] = useState(0);
  const [noSignature, setNoSignature] = useState(0);
  // Contacts left out because they were read recently, and whether to read them again
  const [skipped, setSkipped] = useState(0);
  const [includeChecked, setIncludeChecked] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const offset = useRef(0);
  const queue = useRef<Contact[]>([]);
  // Bumped on every opening and closing: a run from an earlier opening stops
  const run = useRef(0);
  const open = targets !== null;
  // The automatic list needs the contacts and their activity loaded
  const ready = contacts.length > 0 && (targets !== 'auto' || !!index);

  const read = useCallback(
    async (list: Contact[]) => {
      const mine = run.current;
      setError(null);
      setProgress({ done: 0, total: list.length });
      for (let i = 0; i < list.length; i += BATCH) {
        if (run.current !== mine) {
          return;
        }
        let batch = list.slice(i, i + BATCH);
        try {
          const { results, rateLimited } = await backendApiClient.enrichSignatures(
            batch.map((c) => ({
              id: c.resourceName,
              name: getDisplayName(c),
              emails: (c.emailAddresses ?? []).map((e) => clean(e.value)).filter(Boolean),
            })),
          );
          const found: Row[] = [];
          let empty = 0;
          batch.forEach((c) => {
            const r = results[c.resourceName];
            if (!r) {
              return;
            }
            if (!r.found || !r.details) {
              empty += 1;
              return;
            }
            const sig = signatureChange(c, r.details);
            const resolved = resolveChange(c, sig.change, groups);
            if (resolved.diff.length || sig.conflicts.length || sig.address) {
              found.push({
                contact: c,
                change: sig.change,
                resolved,
                conflicts: sig.conflicts,
                address: sig.address,
                include: resolved.diff.length > 0,
                replace: new Set(),
              });
            } else {
              empty += 1;
            }
          });
          if (run.current !== mine) {
            return;
          }
          if (rateLimited) {
            // The rest of this batch was not read: wait for the per-minute limit, then go on
            const unread = batch.filter((c) => !results[c.resourceName]);
            list.splice(i + BATCH, 0, ...unread);
            batch = batch.filter((c) => results[c.resourceName]);
            setProgress({ done: i, total: list.length });
            await new Promise((r) => setTimeout(r, 30_000));
          }
          setRows((prev) => [...prev, ...found]);
          setNoSignature((n) => n + empty);
        } catch (e) {
          setError((e as Error).message || 'Reading signatures failed');
          break;
        }
        setChecked((n) => n + batch.length);
        rememberChecked(batch.map((c) => c.resourceName));
        setProgress({ done: Math.min(i + BATCH, list.length), total: list.length });
      }
      setProgress(null);
    },
    [groups],
  );

  useEffect(() => {
    run.current += 1;
    if (!open || !connected || !ready) {
      return;
    }
    setRows([]);
    setChecked(0);
    setNoSignature(0);
    offset.current = 0;
    if (targets === 'auto') {
      // Every contact with an email (Gmail's search reaches beyond the scanned
      // year): people who write to the user first, then people the user writes
      // to, then the rest; contacts read in the last 30 days are skipped
      const recent = includeChecked ? {} : loadChecked();
      const withEmail = contacts.filter((c) =>
        (c.emailAddresses ?? []).some((e) => clean(e.value)),
      );
      queue.current = withEmail
        .filter((c) => !recent[c.resourceName])
        .sort(
          (a, b) =>
            (index?.get(b.resourceName)?.received ?? 0) -
              (index?.get(a.resourceName)?.received ?? 0) ||
            (index?.get(b.resourceName)?.sent ?? 0) - (index?.get(a.resourceName)?.sent ?? 0),
        );
      setSkipped(withEmail.length - queue.current.length);
    } else {
      queue.current = (targets ?? []).filter((c) =>
        (c.emailAddresses ?? []).some((e) => clean(e.value)),
      );
    }
    const first = queue.current.slice(0, PER_RUN);
    offset.current = first.length;
    void read(first);
    // Start once per opening, when the data is there
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, connected, ready, includeChecked]);

  const more = () => {
    const next = queue.current.slice(offset.current, offset.current + PER_RUN);
    offset.current += next.length;
    void read(next);
  };

  const toggle = (rn: string, patch: Partial<Row>) =>
    setRows((list) => list.map((r) => (r.contact.resourceName === rn ? { ...r, ...patch } : r)));

  const apply = async () => {
    setBusy(true);
    let done = 0;
    const failed: string[] = [];
    for (const r of rows.filter((x) => x.include || x.replace.size > 0)) {
      // The additions when ticked, plus each different value the user chose
      const set: NonNullable<ContactChange['set']> = { ...(r.include ? (r.change.set ?? {}) : {}) };
      r.conflicts.filter((c) => r.replace.has(c.field)).forEach((c) => (set[c.field] = c.found));
      const change: ContactChange = r.include
        ? { ...r.change, set }
        : { id: r.contact.resourceName, set };
      const resolved = resolveChange(r.contact, change, groups);
      const patch = buildPatch(r.contact, resolved.draft, r.contact.etag);
      if (!patch) {
        continue;
      }
      try {
        await dispatch(
          updateContact({ resourceName: r.contact.resourceName, updates: patch }),
        ).unwrap();
        done += 1;
        // Saved: out of the list, so a retry after a failure does not send it again
        setRows((list) => list.filter((x) => x.contact.resourceName !== r.contact.resourceName));
      } catch (e) {
        failed.push(`${getDisplayName(r.contact)}: ${(e as Error).message ?? 'failed'}`);
      }
    }
    setBusy(false);
    onApplied(done);
    if (failed.length) {
      setError(`${failed.length} not saved: ${failed.slice(0, 3).join('; ')}`);
    } else {
      onClose();
    }
  };

  const selected = rows.filter((r) => r.include || r.replace.size > 0).length;
  const left = queue.current.length - offset.current;

  return (
    <Dialog open={open} onClose={busy || progress ? undefined : onClose} maxWidth="md" fullWidth>
      <DialogTitle>Details from email signatures</DialogTitle>
      <DialogContent>
        {!connected ? (
          <Box>
            <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 2 }}>
              Phone numbers, job titles, companies and LinkedIn links are often only in people’s
              email signatures. To read them, the app needs to read email content. For each contact
              it reads their 3 latest emails to you, keeps only the last lines they wrote (not
              quoted replies; for a short email that can be all of it), and has Gemini pick out the
              details. Nothing is stored, and nothing is changed until you apply it.
            </Typography>
            <Button
              variant="contained"
              startIcon={<DriveFileRenameOutline />}
              onClick={() => backendApiClient.connectSignatures()}
            >
              Allow reading email content
            </Button>
          </Box>
        ) : (
          <>
            <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 1 }}>
              {targets === 'auto'
                ? 'The people who email you most, 10 at a time. New phone numbers and links are added; empty company, title and department fields are filled. Different values are listed for you to choose.'
                : 'New phone numbers and links are added; empty fields are filled. Different values are listed for you to choose.'}
            </Typography>
            {progress && (
              <Box sx={{ mb: 2 }}>
                <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                  Reading signatures · {progress.done} of {progress.total}
                </Typography>
                <LinearProgress
                  variant="determinate"
                  value={(progress.done / Math.max(1, progress.total)) * 100}
                />
              </Box>
            )}
            {error && (
              <Alert severity="error" sx={{ mb: 2 }}>
                {error}
              </Alert>
            )}
            {targets === 'auto' && skipped > 0 && !includeChecked && (
              <Typography
                variant="caption"
                sx={{ display: 'block', color: tokens.textMuted, mb: 1 }}
              >
                {skipped} contacts read in the last {RECHECK_DAYS} days are skipped.{' '}
                <Button
                  size="small"
                  sx={{ p: 0, minWidth: 0, verticalAlign: 'baseline' }}
                  onClick={() => setIncludeChecked(true)}
                >
                  Read them again
                </Button>
              </Typography>
            )}
            {!progress && checked > 0 && (
              <Typography
                variant="caption"
                sx={{ display: 'block', color: tokens.textMuted, mb: 1 }}
              >
                {checked} contacts read · {rows.length} with new details · {noSignature} without a
                usable signature
              </Typography>
            )}
            {rows.map((r) => (
              <Box
                key={r.contact.resourceName}
                sx={{ py: 1, borderBottom: `1px solid ${tokens.border}` }}
              >
                <FormControlLabel
                  sx={{ m: 0, alignItems: 'flex-start' }}
                  control={
                    <Checkbox
                      size="small"
                      checked={r.include}
                      disabled={r.resolved.diff.length === 0}
                      onChange={(e) =>
                        toggle(r.contact.resourceName, { include: e.target.checked })
                      }
                    />
                  }
                  label={
                    <Box sx={{ pt: 0.75 }}>
                      <Typography
                        variant="body2"
                        dir="auto"
                        sx={{ fontWeight: 600, textAlign: 'left' }}
                      >
                        {getDisplayName(r.contact)}
                      </Typography>
                      {r.resolved.diff.map((d, i) => (
                        <Typography
                          key={i}
                          variant="caption"
                          dir="auto"
                          sx={{ display: 'block', color: tokens.textSecondary }}
                        >
                          {d.field}:{' '}
                          <Box component="span" sx={{ color: tokens.text }}>
                            {d.to}
                          </Box>
                        </Typography>
                      ))}
                      {r.address && (
                        <Typography
                          variant="caption"
                          dir="auto"
                          sx={{ display: 'block', color: tokens.textMuted }}
                        >
                          Address in signature (not added): {r.address}
                        </Typography>
                      )}
                    </Box>
                  }
                />
                {r.conflicts.map((c) => (
                  <FormControlLabel
                    key={c.field}
                    sx={{ display: 'flex', ml: 4 }}
                    control={
                      <Checkbox
                        size="small"
                        checked={r.replace.has(c.field)}
                        onChange={(e) => {
                          const next = new Set(r.replace);
                          if (e.target.checked) {
                            next.add(c.field);
                          } else {
                            next.delete(c.field);
                          }
                          toggle(r.contact.resourceName, { replace: next });
                        }}
                      />
                    }
                    label={
                      <Typography variant="caption" dir="auto">
                        {c.label}: replace “{c.current}” with “{c.found}”
                      </Typography>
                    }
                  />
                ))}
              </Box>
            ))}
            {!progress && left > 0 && (
              <Button size="small" sx={{ mt: 1 }} onClick={more}>
                Read the next {Math.min(PER_RUN, left)}
              </Button>
            )}
          </>
        )}
      </DialogContent>
      <DialogActions>
        <Button color="inherit" onClick={onClose} disabled={busy || !!progress}>
          Close
        </Button>
        {connected && (
          <Button
            variant="contained"
            onClick={apply}
            disabled={busy || !!progress || selected === 0}
          >
            {busy ? <CircularProgress size={18} /> : `Apply to ${selected}`}
          </Button>
        )}
      </DialogActions>
    </Dialog>
  );
};

export default SignatureDialog;
