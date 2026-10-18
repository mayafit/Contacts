/**
 * @fileoverview Confirmation dialog for deleting one or more contacts (Story 5.1).
 * @module Contacts/components/cleanup/DeleteDialog
 */

import React, { useState } from 'react';
import {
  Alert,
  Box,
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Typography,
} from '@mui/material';
import { DeleteOutline } from '@mui/icons-material';
import type { Contact } from '../../types/Contact';
import { useAppDispatch } from '../../types/hooks';
import { deleteContacts } from '../../redux/thunks/cleanupThunks';
import ContactAvatar from '../common/ContactAvatar';
import { getDisplayName, getPrimaryPhone } from '../../model/contactView';
import { tokens } from '../../theme/contactsTheme';

/** Props for DeleteDialog */
export interface DeleteDialogProps {
  /** Contacts to delete; null closes the dialog */
  contacts: Contact[] | null;
  /** Close without deleting */
  onClose: () => void;
  /** Called after a successful delete */
  onDeleted: (count: number) => void;
}

const SHOWN = 8;

/**
 * Delete confirmation dialog.
 * @param props - Component props
 * @returns Dialog element
 */
const DeleteDialog: React.FC<DeleteDialogProps> = ({ contacts, onClose, onDeleted }) => {
  const dispatch = useAppDispatch();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const list = contacts ?? [];
  const count = list.length;

  const confirm = async () => {
    setBusy(true);
    setError(null);
    try {
      const deleted = await dispatch(deleteContacts(list.map((c) => c.resourceName))).unwrap();
      onDeleted(deleted);
    } catch (e) {
      setError((e as { message?: string })?.message ?? 'Delete failed');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open={!!contacts} onClose={busy ? undefined : onClose} maxWidth="xs" fullWidth>
      <DialogTitle>Delete {count === 1 ? 'this contact' : `${count} contacts`}?</DialogTitle>
      <DialogContent>
        <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 1.5 }}>
          {count === 1 ? 'It is' : 'They are'} deleted from your Google account and disappear from
          every device that syncs Google Contacts.
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
          color="error"
          onClick={confirm}
          disabled={busy || count === 0}
          startIcon={busy ? <CircularProgress size={16} color="inherit" /> : <DeleteOutline />}
        >
          {busy ? 'Deleting…' : count === 1 ? 'Delete' : `Delete ${count} contacts`}
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default DeleteDialog;
