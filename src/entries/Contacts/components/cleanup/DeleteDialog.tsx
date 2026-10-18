/**
 * @fileoverview "Are you sure?" dialog for removing contacts (Story 5.1).
 * By default contacts are moved to the app's Trash (restorable); deleting
 * permanently is an explicit choice, or the action of the Trash view.
 * @module Contacts/components/cleanup/DeleteDialog
 */

import React, { useEffect, useState } from 'react';
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
  Typography,
} from '@mui/material';
import { DeleteForever, DeleteOutline } from '@mui/icons-material';
import type { Contact } from '../../types/Contact';
import { useAppDispatch } from '../../types/hooks';
import { deleteContacts, trashContacts } from '../../redux/thunks/cleanupThunks';
import ContactAvatar from '../common/ContactAvatar';
import { getDisplayName, getPrimaryPhone } from '../../model/contactView';
import { tokens } from '../../theme/contactsTheme';

/** Props for DeleteDialog */
export interface DeleteDialogProps {
  /** Contacts to remove; null closes the dialog */
  contacts: Contact[] | null;
  /** Only permanent deletion is offered (used by the Trash view) */
  permanentOnly?: boolean;
  /** Close without removing */
  onClose: () => void;
  /** Called after success with the count and whether it was permanent */
  onDeleted: (count: number, permanent: boolean) => void;
}

const SHOWN = 8;

/**
 * Remove-contacts confirmation dialog.
 * @param props - Component props
 * @returns Dialog element
 */
const DeleteDialog: React.FC<DeleteDialogProps> = ({
  contacts,
  permanentOnly = false,
  onClose,
  onDeleted,
}) => {
  const dispatch = useAppDispatch();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [permanent, setPermanent] = useState(permanentOnly);
  const list = contacts ?? [];
  const count = list.length;
  const noun = count === 1 ? 'this contact' : `${count} contacts`;

  useEffect(() => {
    setPermanent(permanentOnly);
    setError(null);
  }, [contacts, permanentOnly]);

  const confirm = async () => {
    setBusy(true);
    setError(null);
    const names = list.map((c) => c.resourceName);
    try {
      if (permanent) {
        const deleted = await dispatch(deleteContacts(names)).unwrap();
        onDeleted(deleted, true);
      } else {
        await dispatch(trashContacts({ resourceNames: names, restore: false })).unwrap();
        onDeleted(names.length, false);
      }
    } catch (e) {
      setError((e as { message?: string })?.message ?? 'Failed');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open={!!contacts} onClose={busy ? undefined : onClose} maxWidth="xs" fullWidth>
      <DialogTitle>
        {permanent ? `Permanently delete ${noun}?` : `Move ${noun} to Trash?`}
      </DialogTitle>
      <DialogContent>
        <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 1.5 }}>
          {permanent
            ? 'They are deleted from your Google account and disappear from every device that syncs Google Contacts. This cannot be undone from this app.'
            : 'They disappear from this app and can be restored from the Trash tab. In Google Contacts they stay, marked with the label "Contacts app – Trash", until you delete them permanently.'}
        </Typography>
        <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1 }}>
          {list.slice(0, SHOWN).map((c) => (
            <Box
              key={c.resourceName}
              sx={{ display: 'flex', alignItems: 'center', gap: 1.25, minWidth: 0 }}
            >
              <ContactAvatar contact={c} size={28} />
              <Typography variant="body2" noWrap dir="auto" sx={{ fontWeight: 600, minWidth: 0 }}>
                {getDisplayName(c)}
              </Typography>
              <Typography
                variant="caption"
                noWrap
                sx={{ color: tokens.textMuted, ml: 'auto', flexShrink: 0 }}
              >
                {getPrimaryPhone(c)}
              </Typography>
            </Box>
          ))}
          {count > SHOWN && (
            <Typography variant="body2" sx={{ color: tokens.textMuted }}>
              and {count - SHOWN} more
            </Typography>
          )}
        </Box>
        {!permanentOnly && (
          <FormControlLabel
            sx={{ mt: 1.5 }}
            control={
              <Checkbox
                size="small"
                checked={permanent}
                onChange={(e) => setPermanent(e.target.checked)}
                disabled={busy}
              />
            }
            label={
              <Typography variant="body2" sx={{ color: tokens.textSecondary }}>
                Delete permanently instead
              </Typography>
            }
          />
        )}
        {error && (
          <Alert severity="error" sx={{ mt: 2 }}>
            {error}
          </Alert>
        )}
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2 }}>
        <Button color="inherit" onClick={onClose} disabled={busy}>
          Cancel
        </Button>
        <Button
          variant="contained"
          color={permanent ? 'error' : 'warning'}
          onClick={confirm}
          disabled={busy || count === 0}
          startIcon={
            busy ? (
              <CircularProgress size={16} color="inherit" />
            ) : permanent ? (
              <DeleteForever />
            ) : (
              <DeleteOutline />
            )
          }
        >
          {busy
            ? 'Working…'
            : permanent
              ? count === 1
                ? 'Delete permanently'
                : `Delete ${count} permanently`
              : 'Move to Trash'}
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default DeleteDialog;
