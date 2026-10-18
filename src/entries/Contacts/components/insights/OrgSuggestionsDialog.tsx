/**
 * @fileoverview Review and apply organization suggestions for one work email
 * domain (Story 6.1).
 * @module Contacts/components/insights/OrgSuggestionsDialog
 */

import React, { useEffect, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  LinearProgress,
  TextField,
  Typography,
} from '@mui/material';
import { ArrowForward } from '@mui/icons-material';
import { useAppDispatch } from '../../types/hooks';
import { updateContact } from '../../redux/slices/contacts/contactsSlice';
import { withOrganization, type DomainInsight } from '../../model/organizations';
import ContactAvatar from '../common/ContactAvatar';
import { getDisplayName } from '../../model/contactView';
import { tokens } from '../../theme/contactsTheme';

/** Props for OrgSuggestionsDialog */
export interface OrgSuggestionsDialogProps {
  /** Domain to review; null closes the dialog */
  insight: DomainInsight | null;
  /** Close */
  onClose: () => void;
  /** Called after applying, with how many contacts were updated */
  onApplied: (count: number) => void;
}

/**
 * Organization suggestions dialog.
 * @param props - Component props
 * @returns Dialog element
 */
const OrgSuggestionsDialog: React.FC<OrgSuggestionsDialogProps> = ({
  insight,
  onClose,
  onApplied,
}) => {
  const dispatch = useAppDispatch();
  const [name, setName] = useState('');
  const [checked, setChecked] = useState<Set<string>>(new Set());
  const [progress, setProgress] = useState<number | null>(null);
  const [errors, setErrors] = useState<string[]>([]);

  useEffect(() => {
    if (insight) {
      setName(insight.suggested);
      setChecked(new Set(insight.needsUpdate.map((n) => n.contact.resourceName)));
      setProgress(null);
      setErrors([]);
    }
  }, [insight]);

  const rows = insight?.needsUpdate ?? [];
  const selected = rows.filter((r) => checked.has(r.contact.resourceName));
  const busy = progress !== null;

  const apply = async () => {
    const target = name.trim();
    if (!target) {
      return;
    }
    setErrors([]);
    let done = 0;
    let ok = 0;
    const failed: string[] = [];
    setProgress(0);
    // One at a time: each save carries that contact's own etag
    for (const r of selected) {
      try {
        await dispatch(
          updateContact({
            resourceName: r.contact.resourceName,
            updates: { etag: r.contact.etag, organizations: withOrganization(r.contact, target) },
          }),
        ).unwrap();
        ok += 1;
      } catch (e) {
        failed.push(
          `${getDisplayName(r.contact)}: ${(e as { message?: string })?.message ?? 'failed'}`,
        );
      }
      done += 1;
      setProgress(Math.round((done / selected.length) * 100));
    }
    setProgress(null);
    if (failed.length) {
      setErrors(failed);
    } else {
      onApplied(ok);
    }
  };

  const toggle = (rn: string) =>
    setChecked((prev) => {
      const next = new Set(prev);
      if (next.has(rn)) {
        next.delete(rn);
      } else {
        next.add(rn);
      }
      return next;
    });

  return (
    <Dialog open={!!insight} onClose={busy ? undefined : onClose} maxWidth="sm" fullWidth>
      <DialogTitle>Organization for @{insight?.domain}</DialogTitle>
      <DialogContent>
        <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 2 }}>
          {insight?.members.length} contacts have an address at this domain.{' '}
          {insight?.fromContacts
            ? 'The suggestion is the organization most of them already use.'
            : 'None lists an organization yet; the suggestion comes from the domain.'}{' '}
          A contact that lists a different organization keeps it; this one is added first.
        </Typography>
        <TextField
          label="Organization"
          value={name}
          onChange={(e) => setName(e.target.value)}
          size="small"
          fullWidth
          disabled={busy}
          inputProps={{ dir: 'auto' }}
          sx={{ mb: 2 }}
        />
        <Box sx={{ display: 'flex', flexDirection: 'column', gap: 0.5 }}>
          {rows.map((r) => (
            <Box
              key={r.contact.resourceName}
              sx={{ display: 'flex', alignItems: 'center', gap: 1, py: 0.5, minWidth: 0 }}
            >
              <Checkbox
                size="small"
                checked={checked.has(r.contact.resourceName)}
                onChange={() => toggle(r.contact.resourceName)}
                disabled={busy}
                inputProps={{ 'aria-label': `Update ${getDisplayName(r.contact)}` }}
              />
              <ContactAvatar contact={r.contact} size={28} />
              <Box sx={{ minWidth: 0, flex: 1 }}>
                <Typography variant="body2" noWrap dir="auto" align="left" sx={{ fontWeight: 600 }}>
                  {getDisplayName(r.contact)}
                </Typography>
                <Typography
                  variant="caption"
                  noWrap
                  sx={{ color: tokens.textMuted, display: 'block' }}
                >
                  {(r.contact.emailAddresses ?? [])
                    .map((e) => e.value)
                    .filter((v) => v.toLowerCase().endsWith(`@${insight?.domain}`))
                    .join(', ')}
                </Typography>
              </Box>
              <Typography
                variant="caption"
                noWrap
                dir="auto"
                sx={{
                  color: r.reason === 'missing' ? tokens.textMuted : tokens.attention,
                  maxWidth: 140,
                }}
              >
                {r.current || 'no organization'}
              </Typography>
              <ArrowForward sx={{ fontSize: 14, color: tokens.textMuted }} />
              <Typography
                variant="caption"
                noWrap
                dir="auto"
                sx={{ maxWidth: 140, fontWeight: 600 }}
              >
                {name.trim() || '—'}
              </Typography>
            </Box>
          ))}
        </Box>
        {busy && <LinearProgress variant="determinate" value={progress ?? 0} sx={{ mt: 2 }} />}
        {errors.length > 0 && (
          <Alert severity="error" sx={{ mt: 2 }}>
            Some contacts were not updated:
            <Box component="ul" sx={{ m: 0, mt: 0.5, pl: 2.5 }}>
              {errors.map((e) => (
                <li key={e} dir="auto">
                  {e}
                </li>
              ))}
            </Box>
          </Alert>
        )}
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2 }}>
        <Button color="inherit" onClick={onClose} disabled={busy}>
          {errors.length ? 'Close' : 'Cancel'}
        </Button>
        <Button
          variant="contained"
          onClick={apply}
          disabled={busy || selected.length === 0 || !name.trim()}
        >
          {busy
            ? 'Updating…'
            : `Update ${selected.length} ${selected.length === 1 ? 'contact' : 'contacts'}`}
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default OrgSuggestionsDialog;
