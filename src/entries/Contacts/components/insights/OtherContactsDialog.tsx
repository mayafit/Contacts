/**
 * @fileoverview Review dialog: suggestions from Google's Other contacts (FR-92, AD-48)
 * @module Contacts/components/insights/OtherContactsDialog
 */

import React, { useEffect, useMemo, useRef, useState } from 'react';
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
  IconButton,
  Tooltip,
  Typography,
} from '@mui/material';
import { Close } from '@mui/icons-material';
import type { Contact, ContactGroup } from '../../types/Contact';
import {
  ApiRequestError,
  backendApiClient,
  type OtherContactsRead,
  type PersonActivity,
} from '../../../../services/api/BackendApiClient';
import { useAppDispatch } from '../../types/hooks';
import { contactAdded, updateContact } from '../../redux/slices/contacts/contactsSlice';
import { buildPatch } from '../card/cardModel';
import { newContactPatch, resolveChange } from '../../assistant/changes';
import { getDisplayName, splitFullName } from '../../model/contactView';
import { emailType } from '../../model/emailFind';
import { formatRelative } from '../../model/activity';
import {
  buildOtherSuggestions,
  REASON_LABEL,
  type SaveSuggestion,
  type UpdateSuggestion,
} from '../../model/otherContacts';
import { tokens } from '../../theme/contactsTheme';

/** Props for OtherContactsDialog */
export interface OtherContactsDialogProps {
  open: boolean;
  onClose: () => void;
  /** Active contacts (the targets) */
  contacts: Contact[];
  /** All contacts, Trash included (their addresses are known) */
  allContacts: Contact[];
  groups: Record<string, ContactGroup>;
  /** Activity per address, null without Gmail & Calendar */
  people: Record<string, PersonActivity> | null;
  /** Called with how many contacts got an address, and how many were created */
  onApplied: (emailsAdded: number, created: number) => void;
}

const fmt = new Intl.NumberFormat('en-US');

/**
 * One row: a tick box, who, what would be added and why, and Dismiss.
 * @param props - Row content
 * @returns Row
 */
const Row: React.FC<{
  label: string;
  title: string;
  detail: React.ReactNode;
  checked: boolean;
  onCheck: (checked: boolean) => void;
  onDismiss: () => void;
  error?: string;
}> = ({ label, title, detail, checked, onCheck, onDismiss, error }) => (
  <Box
    sx={{
      display: 'flex',
      alignItems: 'flex-start',
      gap: 1,
      py: 0.75,
      borderBottom: `1px solid ${tokens.border}`,
    }}
  >
    <Checkbox
      size="small"
      checked={checked}
      onChange={(e) => onCheck(e.target.checked)}
      inputProps={{ 'aria-label': label }}
      sx={{ mt: -0.5 }}
    />
    <Box sx={{ flex: 1, minWidth: 0 }}>
      <Typography variant="body2" dir="auto" sx={{ fontWeight: 600, textAlign: 'left' }}>
        {title}
      </Typography>
      <Typography variant="body2" component="div" dir="auto" sx={{ textAlign: 'left' }}>
        {detail}
      </Typography>
      {error && (
        <Typography variant="caption" sx={{ color: 'error.main' }}>
          {error}
        </Typography>
      )}
    </Box>
    <Tooltip title="Dismiss: don't suggest this again">
      <IconButton size="small" aria-label={`Dismiss ${title}`} onClick={onDismiss}>
        <Close fontSize="small" />
      </IconButton>
    </Tooltip>
  </Box>
);

/**
 * Suggestions from Other contacts.
 * @param props - Component props
 * @returns Dialog
 */
const OtherContactsDialog: React.FC<OtherContactsDialogProps> = ({
  open,
  onClose,
  contacts,
  allContacts,
  groups,
  people,
  onApplied,
}) => {
  const dispatch = useAppDispatch();
  const [loading, setLoading] = useState(false);
  const [notConnected, setNotConnected] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [read, setRead] = useState<OtherContactsRead | null>(null);
  const [dismissed, setDismissed] = useState<Set<string>>(new Set());
  const [done, setDone] = useState<Set<string>>(new Set());
  const [checked, setChecked] = useState<Set<string>>(new Set());
  const [failed, setFailed] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  // Read once per opening: applying changes the contacts, which must not rebuild the
  // list (and reset the ticks) while it runs; applying uses the latest contact
  const lists = useRef({ contacts, allContacts, people });
  lists.current = { contacts, allContacts, people };
  const [snapshot, setSnapshot] = useState(lists.current);

  useEffect(() => {
    if (!open) {
      return;
    }
    let alive = true;
    setSnapshot(lists.current);
    setLoading(true);
    setError(null);
    setNotConnected(false);
    setDone(new Set());
    setChecked(new Set());
    setFailed({});
    (async () => {
      try {
        const [r, d] = await Promise.all([
          backendApiClient.getOtherContacts(),
          backendApiClient.getDismissedOther(),
        ]);
        if (alive) {
          setRead(r);
          setDismissed(new Set(d));
        }
      } catch (e) {
        if (!alive) {
          return;
        }
        if (e instanceof ApiRequestError && e.code === 'NOT_CONNECTED') {
          setNotConnected(true);
        } else {
          setError((e as Error).message || 'Failed to read Other contacts');
        }
      } finally {
        if (alive) {
          setLoading(false);
        }
      }
    })();
    return () => {
      alive = false;
    };
  }, [open]);

  const suggestions = useMemo(
    () =>
      read
        ? buildOtherSuggestions(
            read.entries,
            snapshot.contacts,
            snapshot.allContacts,
            snapshot.people,
            dismissed,
          )
        : null,
    [read, snapshot, dismissed],
  );
  const updates = (suggestions?.updates ?? []).filter((u) => !done.has(u.key));
  const saves = (suggestions?.saves ?? []).filter((s) => !done.has(s.key));
  const missing = updates.filter((u) => u.kind === 'missing');
  const extra = updates.filter((u) => u.kind === 'extra');
  const visible = new Set([...updates, ...saves].map((x) => x.key));
  const chosen = [...checked].filter((k) => visible.has(k));

  const tick = (key: string, on: boolean) =>
    setChecked((s) => {
      const next = new Set(s);
      if (on) {
        next.add(key);
      } else {
        next.delete(key);
      }
      return next;
    });

  const dismiss = async (key: string) => {
    try {
      setDismissed(new Set(await backendApiClient.dismissOther([key])));
    } catch (e) {
      setError((e as Error).message || 'Failed to dismiss');
    }
  };

  const showDismissed = async () => {
    try {
      await backendApiClient.clearDismissedOther();
      setDismissed(new Set());
    } catch (e) {
      setError((e as Error).message || 'Failed to show dismissed suggestions');
    }
  };

  const applyUpdate = async (u: UpdateSuggestion) => {
    const current =
      lists.current.allContacts.find((c) => c.resourceName === u.contact.resourceName) ?? u.contact;
    const res = resolveChange(
      current,
      {
        id: current.resourceName,
        addEmails: u.emails.map((value) => ({ value, type: emailType(value) })),
        addPhones: u.phones.map((value) => ({ value, type: 'mobile' })),
      },
      groups,
    );
    const patch = buildPatch(current, res.draft, current.etag);
    if (patch) {
      await dispatch(
        updateContact({ resourceName: current.resourceName, updates: patch }),
      ).unwrap();
    }
  };

  const applySave = async (s: SaveSuggestion) => {
    const { first, last } = splitFullName(s.name ?? '');
    const { patch } = newContactPatch(
      s.name ? { givenName: first, familyName: last } : {},
      s.emails.map((value) => ({ value, type: emailType(value) })),
      s.phones.map((value) => ({ value, type: 'mobile' })),
    );
    dispatch(contactAdded(await backendApiClient.createContact(patch)));
  };

  const apply = async () => {
    setBusy(true);
    let emailsAdded = 0;
    let created = 0;
    const ok = new Set(done);
    const errors: Record<string, string> = {};
    for (const u of updates.filter((x) => checked.has(x.key))) {
      try {
        await applyUpdate(u);
        ok.add(u.key);
        if (u.emails.length) {
          emailsAdded += 1;
        }
      } catch (e) {
        errors[u.key] = (e as Error).message || 'Not saved';
      }
    }
    for (const s of saves.filter((x) => checked.has(x.key))) {
      try {
        await applySave(s);
        ok.add(s.key);
        created += 1;
      } catch (e) {
        errors[s.key] = (e as Error).message || 'Not saved';
      }
    }
    setDone(ok);
    setFailed(errors);
    setBusy(false);
    onApplied(emailsAdded, created);
    if (Object.keys(errors).length === 0) {
      onClose();
    }
  };

  const updateRow = (u: UpdateSuggestion) => (
    <Row
      key={u.key}
      label={`Add to ${getDisplayName(u.contact)}`}
      title={getDisplayName(u.contact)}
      checked={checked.has(u.key)}
      onCheck={(on) => tick(u.key, on)}
      onDismiss={() => void dismiss(u.key)}
      error={failed[u.key]}
      detail={
        <>
          Add <strong>{[...u.emails, ...u.phones].join(', ')}</strong>{' '}
          <Box component="span" sx={{ color: tokens.textMuted }}>
            · {u.reasons.map((r) => REASON_LABEL[r]).join(', ')}
            {u.names.length ? ` · as “${u.names.join('”, “')}”` : ''}
          </Box>
        </>
      }
    />
  );

  const section = (title: string, rows: React.ReactNode[], hint?: string) =>
    rows.length > 0 && (
      <Box sx={{ mb: 2 }}>
        <Typography variant="subtitle2" sx={{ mb: 0.5 }}>
          {title} ({fmt.format(rows.length)})
        </Typography>
        {hint && (
          <Typography variant="caption" sx={{ color: tokens.textMuted }}>
            {hint}
          </Typography>
        )}
        {rows}
      </Box>
    );

  const nothing = !!suggestions && missing.length === 0 && extra.length === 0 && saves.length === 0;

  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} maxWidth="md" fullWidth>
      <DialogTitle>Suggestions from Other contacts</DialogTitle>
      <DialogContent>
        <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 2 }}>
          Other contacts are people Gmail saved by itself when you wrote to someone not in your
          contacts. Nothing is added until you tick it and apply; check that it is the same person.
        </Typography>
        {loading && <CircularProgress size={24} />}
        {error && (
          <Alert severity="error" sx={{ mb: 2 }}>
            {error}
          </Alert>
        )}
        {notConnected && (
          <Alert
            severity="info"
            action={
              <Button
                color="inherit"
                size="small"
                onClick={() => backendApiClient.connectOtherContacts()}
              >
                Allow
              </Button>
            }
          >
            Echo needs permission to read your Other contacts (read only; nothing there is changed).
            You will sign in to Google once more.
          </Alert>
        )}
        {read && suggestions && (
          <Typography variant="caption" component="p" sx={{ color: tokens.textMuted, mb: 2 }}>
            Read {fmt.format(read.read)} Other contacts
            {read.capped ? ' (the first 20,000)' : ''}
            {read.automated ? ` · ${fmt.format(read.automated)} automated senders left out` : ''}
            {suggestions.ambiguous
              ? ` · ${fmt.format(suggestions.ambiguous)} left out because several contacts share the name or number`
              : ''}
          </Typography>
        )}
        {section('Email for contacts that have none', missing.map(updateRow))}
        {section('More addresses and phones', extra.map(updateRow))}
        {section(
          'People worth saving',
          saves.map((s) => (
            <Row
              key={s.key}
              label={`Save ${s.name ?? s.emails[0]}`}
              title={s.name ?? s.emails[0]}
              checked={checked.has(s.key)}
              onCheck={(on) => tick(s.key, on)}
              onDismiss={() => void dismiss(s.key)}
              error={failed[s.key]}
              detail={
                <>
                  <strong>{[...s.emails, ...s.phones].join(', ')}</strong>{' '}
                  <Box component="span" sx={{ color: tokens.textMuted }}>
                    · {s.sent} sent · {s.received} received
                    {s.meetings ? ` · ${s.meetings} meetings` : ''}
                    {s.lastAt ? ` · last ${formatRelative(s.lastAt)}` : ''}
                  </Box>
                </>
              }
            />
          )),
          'Not in your contacts; you wrote to them or met them in the scan period.',
        )}
        {read && !snapshot.people && (
          <Typography variant="body2" sx={{ color: tokens.textMuted, mb: 1 }}>
            People worth saving need Gmail & Calendar connected on this page.
          </Typography>
        )}
        {nothing && (
          <Typography variant="body2" sx={{ color: tokens.textMuted }}>
            No suggestions. Your contacts already have everything Other contacts could add.
          </Typography>
        )}
      </DialogContent>
      <DialogActions>
        {dismissed.size > 0 && (
          <Button
            color="inherit"
            onClick={() => void showDismissed()}
            disabled={busy}
            sx={{ mr: 'auto' }}
          >
            Show dismissed ({fmt.format(dismissed.size)})
          </Button>
        )}
        <Button color="inherit" onClick={onClose} disabled={busy}>
          Cancel
        </Button>
        <Button
          variant="contained"
          onClick={() => void apply()}
          disabled={busy || chosen.length === 0}
        >
          {busy ? <CircularProgress size={18} /> : `Apply ${chosen.length}`}
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default OtherContactsDialog;
