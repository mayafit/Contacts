/**
 * @fileoverview Review dialog: email addresses from mail for contacts that have none
 * @module Contacts/components/insights/SuggestEmailsDialog
 */

import React, { useEffect, useRef, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  Radio,
  RadioGroup,
  Typography,
} from '@mui/material';
import type { Contact, ContactGroup } from '../../types/Contact';
import { backendApiClient, type DirectoryMatch } from '../../../../services/api/BackendApiClient';
import { useAppDispatch } from '../../types/hooks';
import { updateContact } from '../../redux/slices/contacts/contactsSlice';
import { buildPatch } from '../card/cardModel';
import { resolveChange } from '../../assistant/changes';
import { clean, getDisplayName, getName, getPhoneticName } from '../../model/contactView';
import { formatRelative } from '../../model/activity';
import { tokens } from '../../theme/contactsTheme';

/** Props for SuggestEmailsDialog */
export interface SuggestEmailsDialogProps {
  open: boolean;
  onClose: () => void;
  /** Active contacts */
  contacts: Contact[];
  /** All contacts (an address already on any contact is not suggested) */
  allContacts: Contact[];
  groups: Record<string, ContactGroup>;
  /** Called with how many contacts got an email */
  onApplied: (count: number) => void;
}

/**
 * Names to look for: the name, the phonetic name, nickname + family name.
 * @param c - Contact
 * @returns Variants
 */
export const nameVariants = (c: Contact): string[] => {
  const n = getName(c);
  return [
    getDisplayName(c),
    [n?.givenName, n?.familyName].filter(Boolean).join(' '),
    [n?.givenName, n?.middleName, n?.familyName].filter(Boolean).join(' '),
    getPhoneticName(c),
    ...(c.nicknames ?? []).map((x) => [x.value, n?.familyName].filter(Boolean).join(' ')),
  ]
    .map(clean)
    .filter((v) => v.includes(' '))
    .filter((v, i, all) => all.indexOf(v) === i);
};

/**
 * Suggested emails dialog.
 * @param props - Component props
 * @returns Dialog
 */
const SuggestEmailsDialog: React.FC<SuggestEmailsDialogProps> = ({
  open,
  onClose,
  contacts,
  allContacts,
  groups,
  onApplied,
}) => {
  const dispatch = useAppDispatch();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [rows, setRows] = useState<{ contact: Contact; options: DirectoryMatch[] }[]>([]);
  const [choice, setChoice] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  // Read once per opening: saving an email changes the contact list, which must
  // not reload the suggestions (and reset the choices) while applying
  const lists = useRef({ contacts, allContacts });
  lists.current = { contacts, allContacts };

  useEffect(() => {
    if (!open) {
      return;
    }
    let alive = true;
    const { contacts, allContacts } = lists.current;
    (async () => {
      setLoading(true);
      setError(null);
      try {
        const known = new Set(
          allContacts.flatMap((c) =>
            (c.emailAddresses ?? []).map((e) => clean(e.value).toLowerCase()),
          ),
        );
        const without = contacts.filter(
          (c) => !(c.emailAddresses ?? []).some((e) => clean(e.value)),
        );
        const queries = without
          .map((c) => ({ id: c.resourceName, names: nameVariants(c) }))
          .filter((q) => q.names.length > 0);
        const found: Record<string, DirectoryMatch[]> = {};
        for (let i = 0; i < queries.length; i += 500) {
          const r = await backendApiClient.suggestEmails(queries.slice(i, i + 500));
          Object.assign(found, r.suggestions);
        }
        const byId = new Map(without.map((c) => [c.resourceName, c]));
        const next = Object.entries(found)
          .map(([id, options]) => ({
            contact: byId.get(id)!,
            options: options.filter((o) => !known.has(o.email)),
          }))
          .filter((r) => r.contact && r.options.length > 0)
          .sort((a, b) =>
            getDisplayName(a.contact).localeCompare(getDisplayName(b.contact), ['he', 'en']),
          );
        if (alive) {
          setRows(next);
          // Pre-select only strong matches (same name, or the address spells it) that
          // no other contact is offered too
          const offered = new Map<string, number>();
          next.forEach((r) =>
            r.options.forEach((o) => offered.set(o.email, (offered.get(o.email) ?? 0) + 1)),
          );
          setChoice(
            Object.fromEntries(
              next.map((r) => {
                const best = r.options[0];
                return [
                  r.contact.resourceName,
                  (best.score ?? 0) >= 2 && offered.get(best.email) === 1 ? best.email : '',
                ];
              }),
            ),
          );
        }
      } catch (e) {
        if (alive) {
          setError((e as Error).message || 'Failed to look for emails');
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

  const chosen = rows.filter((r) => choice[r.contact.resourceName]);

  const apply = async () => {
    setBusy(true);
    let done = 0;
    const failed: string[] = [];
    for (const r of chosen) {
      const email = choice[r.contact.resourceName];
      const res = resolveChange(
        r.contact,
        { id: r.contact.resourceName, addEmails: [{ value: email }] },
        groups,
      );
      const patch = buildPatch(r.contact, res.draft, r.contact.etag);
      if (!patch) {
        continue;
      }
      try {
        await dispatch(
          updateContact({ resourceName: r.contact.resourceName, updates: patch }),
        ).unwrap();
        done += 1;
        setRows((list) => list.filter((x) => x.contact.resourceName !== r.contact.resourceName));
      } catch (e) {
        failed.push(`${getDisplayName(r.contact)}: ${(e as Error).message ?? 'failed'}`);
      }
    }
    setBusy(false);
    if (failed.length) {
      setError(`${failed.length} not saved: ${failed.slice(0, 3).join('; ')}`);
    }
    onApplied(done);
    if (!failed.length) {
      onClose();
    }
  };

  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} maxWidth="md" fullWidth>
      <DialogTitle>Email addresses for contacts that have none</DialogTitle>
      <DialogContent>
        <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 2 }}>
          Found by matching each contact’s name (and phonetic name) with the names people used in
          your mail and meetings. Check that it is the same person before applying.
        </Typography>
        {loading && <CircularProgress size={24} />}
        {error && (
          <Alert severity="error" sx={{ mb: 2 }}>
            {error}
          </Alert>
        )}
        {!loading && rows.length === 0 && !error && (
          <Typography variant="body2" sx={{ color: tokens.textMuted }}>
            No matches. Names from mail are collected by the scan; if it ran before this feature,
            refresh it on Insights.
          </Typography>
        )}
        {rows.map((r) => (
          <Box
            key={r.contact.resourceName}
            sx={{ py: 1, borderBottom: `1px solid ${tokens.border}` }}
          >
            <Typography variant="body2" dir="auto" sx={{ fontWeight: 600, textAlign: 'left' }}>
              {getDisplayName(r.contact)}
              {getPhoneticName(r.contact) ? ` · ${getPhoneticName(r.contact)}` : ''}
            </Typography>
            <RadioGroup
              value={choice[r.contact.resourceName] ?? ''}
              onChange={(e) =>
                setChoice((c) => ({ ...c, [r.contact.resourceName]: e.target.value }))
              }
            >
              {r.options.map((o) => (
                <FormControlLabel
                  key={o.email}
                  value={o.email}
                  control={<Radio size="small" />}
                  label={
                    <Typography variant="body2" dir="auto">
                      <strong>{o.email}</strong>{' '}
                      <Box component="span" sx={{ color: tokens.textMuted }}>
                        as “{o.names.join('”, “') || '—'}” · {o.sent} sent · {o.received} received
                        {o.meetings ? ` · ${o.meetings} meetings` : ''}
                        {o.lastSeen ? ` · last ${formatRelative(o.lastSeen)}` : ''}
                      </Box>
                    </Typography>
                  }
                />
              ))}
              <FormControlLabel
                value=""
                control={<Radio size="small" />}
                label={<Typography variant="body2">None of these</Typography>}
              />
            </RadioGroup>
          </Box>
        ))}
      </DialogContent>
      <DialogActions>
        <Button color="inherit" onClick={onClose} disabled={busy}>
          Cancel
        </Button>
        <Button variant="contained" onClick={apply} disabled={busy || chosen.length === 0}>
          {busy ? <CircularProgress size={18} /> : `Add ${chosen.length} emails`}
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default SuggestEmailsDialog;
