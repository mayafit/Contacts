/**
 * @fileoverview Creating, renaming and deleting the Owner's labels (AD-45 rule 1); deleting a
 * label never deletes its contacts
 * @module Contacts/components/segments/LabelsManager
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
  IconButton,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import { Check, Close, DeleteOutline, Edit } from '@mui/icons-material';
import type { ContactGroup } from '../../types/Contact';
import { useAppDispatch } from '../../types/hooks';
import { fetchContactGroups, labelRemoved } from '../../redux/slices/contacts/contactsSlice';
import { backendApiClient } from '../../../../services/api/BackendApiClient';
import { userLabels } from '../card/LabelEditor';
import { tokens } from '../../theme/contactsTheme';

/**
 * The labels manager dialog.
 * @param props - Open flag, labels, close
 * @returns Dialog
 */
const LabelsManager: React.FC<{
  open: boolean;
  groups: Record<string, ContactGroup>;
  onClose: () => void;
}> = ({ open, groups, onClose }) => {
  const dispatch = useAppDispatch();
  const [editing, setEditing] = useState<{ resourceName: string; name: string } | null>(null);
  const [deleting, setDeleting] = useState<ContactGroup | null>(null);
  const [newName, setNewName] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const labels = userLabels(groups);

  const run = async (work: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    try {
      await work();
      await dispatch(fetchContactGroups());
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} maxWidth="sm" fullWidth>
      <DialogTitle>Labels</DialogTitle>
      <DialogContent>
        <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 2 }}>
          Labels are Google Contacts’ groups: your phones and Google Contacts show the same ones.
        </Typography>
        <Box sx={{ display: 'flex', gap: 1, mb: 2 }}>
          <TextField
            size="small"
            label="New label"
            value={newName}
            onChange={(e) => setNewName(e.target.value)}
            inputProps={{ dir: 'auto', maxLength: 100 }}
            sx={{ flex: 1 }}
          />
          <Button
            variant="outlined"
            disabled={busy || !newName.trim()}
            onClick={() =>
              void run(async () => {
                await backendApiClient.createLabel(newName.trim());
                setNewName('');
              })
            }
          >
            Create
          </Button>
        </Box>
        {labels.length === 0 && (
          <Typography variant="body2" sx={{ color: tokens.textMuted }}>
            No labels yet.
          </Typography>
        )}
        {labels.map((g) => (
          <Box
            key={g.resourceName}
            sx={{
              display: 'flex',
              alignItems: 'center',
              gap: 1,
              py: 0.5,
              borderBottom: `1px solid ${tokens.border}`,
            }}
          >
            {editing?.resourceName === g.resourceName ? (
              <>
                <TextField
                  size="small"
                  value={editing.name}
                  autoFocus
                  onChange={(e) => setEditing({ ...editing, name: e.target.value })}
                  inputProps={{
                    dir: 'auto',
                    maxLength: 100,
                    'aria-label': `New name for ${g.name}`,
                  }}
                  sx={{ flex: 1 }}
                />
                <IconButton
                  aria-label="Save the name"
                  disabled={busy || !editing.name.trim()}
                  onClick={() =>
                    void run(async () => {
                      await backendApiClient.renameLabel(g.resourceName, editing.name.trim());
                      setEditing(null);
                    })
                  }
                >
                  <Check fontSize="small" />
                </IconButton>
                <IconButton aria-label="Cancel" onClick={() => setEditing(null)}>
                  <Close fontSize="small" />
                </IconButton>
              </>
            ) : (
              <>
                <Typography variant="body2" dir="auto" sx={{ flex: 1 }}>
                  {g.name}
                </Typography>
                <Typography variant="caption" sx={{ color: tokens.textMuted, mr: 1 }}>
                  {g.memberCount ?? 0}
                </Typography>
                <Tooltip title="Rename">
                  <IconButton
                    aria-label={`Rename ${g.name}`}
                    disabled={busy}
                    onClick={() => setEditing({ resourceName: g.resourceName, name: g.name })}
                  >
                    <Edit fontSize="small" />
                  </IconButton>
                </Tooltip>
                <Tooltip title="Delete the label (not its contacts)">
                  <IconButton
                    aria-label={`Delete ${g.name}`}
                    disabled={busy}
                    onClick={() => setDeleting(g)}
                  >
                    <DeleteOutline fontSize="small" />
                  </IconButton>
                </Tooltip>
              </>
            )}
          </Box>
        ))}
        {deleting && (
          <Alert
            severity="warning"
            sx={{ mt: 2 }}
            action={
              <>
                <Button color="inherit" size="small" onClick={() => setDeleting(null)}>
                  Keep
                </Button>
                <Button
                  color="error"
                  size="small"
                  disabled={busy}
                  onClick={() =>
                    void run(async () => {
                      await backendApiClient.deleteLabel(deleting.resourceName);
                      dispatch(labelRemoved(deleting.resourceName));
                      setDeleting(null);
                    })
                  }
                >
                  Delete label
                </Button>
              </>
            }
          >
            Delete “{deleting.name}”? Its {deleting.memberCount ?? 0} contacts stay; only the label
            goes, here, in Google Contacts and on your phones.
          </Alert>
        )}
        {error && (
          <Alert severity="error" sx={{ mt: 2 }}>
            {error}
          </Alert>
        )}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={busy}>
          Done
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default LabelsManager;
