/**
 * @fileoverview Trash: contacts moved there by delete or merge; restore them
 * or delete them permanently.
 * @module Contacts/components/cleanup/TrashView
 */

import React, { useMemo, useState } from 'react';
import { Box, Button, Checkbox, Paper, Typography } from '@mui/material';
import { DeleteForever, RestoreFromTrash } from '@mui/icons-material';
import type { Contact } from '../../types/Contact';
import ContactAvatar from '../common/ContactAvatar';
import {
  compareByName,
  getDisplayName,
  getPrimaryEmail,
  getPrimaryPhone,
} from '../../model/contactView';
import { tokens } from '../../theme/contactsTheme';

/** Props for TrashView */
export interface TrashViewProps {
  /** Contacts in Trash */
  trashed: Contact[];
  /** Restore contacts */
  onRestore: (contacts: Contact[]) => void;
  /** Ask to delete contacts permanently */
  onDeleteForever: (contacts: Contact[]) => void;
  /** Contacts to tick and list first (a search's matches, opened from the contacts' empty state) */
  preselect?: string[];
}

/**
 * Trash view.
 * @param props - Component props
 * @returns View element
 */
const TrashView: React.FC<TrashViewProps> = ({
  trashed,
  onRestore,
  onDeleteForever,
  preselect,
}) => {
  const [checked, setChecked] = useState<Set<string>>(() => new Set(preselect));
  const sorted = useMemo(() => {
    const first = new Set(preselect);
    return [...trashed].sort(
      (a, b) =>
        Number(first.has(b.resourceName)) - Number(first.has(a.resourceName)) ||
        compareByName(a, b),
    );
  }, [trashed, preselect]);
  const selected = sorted.filter((c) => checked.has(c.resourceName));
  const all = sorted.length > 0 && selected.length === sorted.length;

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

  const target = selected.length ? selected : sorted;

  return (
    <Box sx={{ p: { xs: 2, md: 3 }, overflowY: 'auto', flex: 1 }}>
      <Box
        sx={{
          display: 'flex',
          alignItems: 'flex-start',
          gap: 2,
          flexWrap: 'wrap',
          mb: 2,
        }}
      >
        <Box sx={{ flex: 1, minWidth: 260 }}>
          <Typography variant="h1">Trash</Typography>
          <Typography variant="body2" sx={{ color: tokens.textSecondary, mt: 0.5, maxWidth: 640 }}>
            Contacts you deleted or merged away. They are hidden everywhere in this app; in Google
            Contacts they keep the label &quot;Contacts app – Trash&quot; until deleted permanently.
          </Typography>
        </Box>
        {sorted.length > 0 && (
          <Box sx={{ display: 'flex', gap: 1 }}>
            <Button startIcon={<RestoreFromTrash />} onClick={() => onRestore(target)}>
              {selected.length ? `Restore ${selected.length}` : 'Restore all'}
            </Button>
            <Button
              color="error"
              variant="outlined"
              startIcon={<DeleteForever />}
              onClick={() => onDeleteForever(target)}
            >
              {selected.length ? `Delete ${selected.length} permanently` : 'Empty Trash'}
            </Button>
          </Box>
        )}
      </Box>

      {sorted.length === 0 ? (
        <Typography variant="body1" sx={{ color: tokens.textMuted, mt: 4, textAlign: 'center' }}>
          Trash is empty.
        </Typography>
      ) : (
        <Paper variant="outlined" sx={{ borderColor: tokens.border, bgcolor: tokens.surface }}>
          <Box
            sx={{
              display: 'flex',
              alignItems: 'center',
              gap: 1,
              px: 1,
              py: 0.5,
              borderBottom: `1px solid ${tokens.border}`,
            }}
          >
            <Checkbox
              size="small"
              checked={all}
              indeterminate={selected.length > 0 && !all}
              onChange={() =>
                setChecked(all ? new Set() : new Set(sorted.map((c) => c.resourceName)))
              }
              inputProps={{ 'aria-label': 'Check all in Trash' }}
            />
            <Typography variant="caption" sx={{ color: tokens.textMuted }}>
              {selected.length ? `${selected.length} selected` : `${sorted.length} in Trash`}
            </Typography>
          </Box>
          {sorted.map((c) => (
            <Box
              key={c.resourceName}
              sx={{
                display: 'grid',
                gridTemplateColumns: '44px minmax(0, 1.4fr) minmax(0, 1fr) minmax(0, 1.2fr)',
                alignItems: 'center',
                gap: 1,
                px: 1,
                height: 52,
                borderBottom: `1px solid ${tokens.border}`,
                '&:hover': { bgcolor: tokens.hover },
              }}
            >
              <Checkbox
                size="small"
                checked={checked.has(c.resourceName)}
                onChange={() => toggle(c.resourceName)}
                inputProps={{ 'aria-label': `Check ${getDisplayName(c)}` }}
              />
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.25, minWidth: 0 }}>
                <ContactAvatar contact={c} />
                <Typography variant="body1" noWrap dir="auto" sx={{ fontWeight: 600 }}>
                  {getDisplayName(c)}
                </Typography>
              </Box>
              <Typography variant="body2" noWrap sx={{ color: tokens.textSecondary }}>
                {getPrimaryPhone(c) || '—'}
              </Typography>
              <Typography variant="body2" noWrap sx={{ color: tokens.textSecondary }}>
                {getPrimaryEmail(c) || '—'}
              </Typography>
            </Box>
          ))}
        </Paper>
      )}
    </Box>
  );
};

export default TrashView;
