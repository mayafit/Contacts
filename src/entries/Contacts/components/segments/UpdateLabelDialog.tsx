/**
 * @fileoverview Making a linked label the same as its segment (AD-45 rule 8): who is added
 * and who is removed, shown first, applied on request
 * @module Contacts/components/segments/UpdateLabelDialog
 */

import React, { useState } from 'react';
import {
  Alert,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Typography,
} from '@mui/material';
import type { Contact } from '../../types/Contact';
import { useAppDispatch } from '../../types/hooks';
import { changeMembership } from '../../redux/thunks/labelThunks';
import { getDisplayName } from '../../model/contactView';
import { byName } from '../../model/segments';
import { tokens } from '../../theme/contactsTheme';

const Names: React.FC<{ title: string; list: Contact[] }> = ({ title, list }) =>
  list.length ? (
    <Box sx={{ mb: 2 }}>
      <Typography variant="subtitle2">
        {title} ({list.length})
      </Typography>
      <Typography variant="body2" dir="auto" sx={{ color: tokens.textSecondary }}>
        {byName(list).slice(0, 40).map(getDisplayName).join(', ')}
        {list.length > 40 ? ` and ${list.length - 40} more` : ''}
      </Typography>
    </Box>
  ) : null;

/**
 * The update dialog.
 * @param props - The label, the difference, close
 * @returns Dialog
 */
const UpdateLabelDialog: React.FC<{
  open: boolean;
  label: string;
  labelName: string;
  add: Contact[];
  remove: Contact[];
  onClose: () => void;
}> = ({ open, label, labelName, add, remove, onClose }) => {
  const dispatch = useAppDispatch();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const apply = async () => {
    setBusy(true);
    setError(null);
    const refusedAdd = await changeMembership(
      dispatch,
      add.map((c) => c.resourceName),
      label,
      true,
    );
    const refusedRemove = refusedAdd
      ? null
      : await changeMembership(
          dispatch,
          remove.map((c) => c.resourceName),
          label,
          false,
        );
    setBusy(false);
    if (refusedAdd || refusedRemove) {
      setError(refusedAdd ?? refusedRemove);
      return;
    }
    onClose();
  };

  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} maxWidth="sm" fullWidth>
      <DialogTitle>Update the label “{labelName}”</DialogTitle>
      <DialogContent>
        <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 2 }}>
          The label then holds exactly the segment’s contacts, here, in Google Contacts and on your
          phones.
        </Typography>
        <Names title="Added to the label" list={add} />
        <Names title="Removed from the label" list={remove} />
        {error && <Alert severity="error">{error}</Alert>}
      </DialogContent>
      <DialogActions>
        <Button color="inherit" onClick={onClose} disabled={busy}>
          Cancel
        </Button>
        <Button
          variant="contained"
          onClick={() => void apply()}
          disabled={busy || add.length + remove.length === 0}
        >
          Update label
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default UpdateLabelDialog;
