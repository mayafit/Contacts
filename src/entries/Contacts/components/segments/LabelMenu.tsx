/**
 * @fileoverview The selection toolbar's Label… menu (AD-45 rule 2): add the checked
 * contacts to a label (or a new one), or remove them from one they are in
 * @module Contacts/components/segments/LabelMenu
 */

import React, { useState } from 'react';
import { Box, Button, Divider, ListSubheader, Menu, MenuItem, TextField } from '@mui/material';
import { Label as LabelIcon } from '@mui/icons-material';
import type { Contact, ContactGroup } from '../../types/Contact';
import { useAppDispatch } from '../../types/hooks';
import { fetchContactGroups } from '../../redux/slices/contacts/contactsSlice';
import { changeMembership } from '../../redux/thunks/labelThunks';
import { backendApiClient } from '../../../../services/api/BackendApiClient';
import { userLabels } from '../card/LabelEditor';

/**
 * The Label… button and menu.
 * @param props - The checked contacts, the labels, a message sink
 * @returns Button and menu
 */
const LabelMenu: React.FC<{
  contacts: Contact[];
  groups: Record<string, ContactGroup>;
  onDone: (message: string) => void;
}> = ({ contacts, groups, onDone }) => {
  const dispatch = useAppDispatch();
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const [name, setName] = useState('');
  const labels = userLabels(groups);
  const held = labels.filter((g) =>
    contacts.some((c) => (c.labels ?? []).includes(g.resourceName)),
  );

  const change = async (g: { resourceName: string; name: string }, on: boolean) => {
    setAnchor(null);
    const targets = contacts
      .filter((c) => (c.labels ?? []).includes(g.resourceName) !== on)
      .map((c) => c.resourceName);
    const refused = await changeMembership(dispatch, targets, g.resourceName, on);
    onDone(
      refused
        ? `“${g.name}” not changed: ${refused}`
        : `${targets.length} ${on ? 'added to' : 'removed from'} “${g.name}”`,
    );
  };

  const create = async () => {
    const n = name.trim();
    setName('');
    setAnchor(null);
    try {
      const g = await backendApiClient.createLabel(n);
      await dispatch(fetchContactGroups());
      await change(g, true);
    } catch (e) {
      onDone(`Could not create “${n}”: ${(e as Error).message}`);
    }
  };

  return (
    <>
      <Button size="small" startIcon={<LabelIcon />} onClick={(e) => setAnchor(e.currentTarget)}>
        Label…
      </Button>
      <Menu anchorEl={anchor} open={!!anchor} onClose={() => setAnchor(null)}>
        <ListSubheader>Add to</ListSubheader>
        {labels.map((g) => (
          <MenuItem key={g.resourceName} onClick={() => void change(g, true)}>
            {g.name}
          </MenuItem>
        ))}
        <Box
          // Not a menu item: arrow keys move between labels, typing stays in the box
          onKeyDown={(e) => e.stopPropagation()}
          sx={{ display: 'flex', alignItems: 'center', gap: 1, px: 2, py: 1 }}
        >
          <TextField
            size="small"
            placeholder="New label"
            inputProps={{ 'aria-label': 'New label name', dir: 'auto', maxLength: 100 }}
            value={name}
            onChange={(e) => setName(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && name.trim() && void create()}
          />
          <Button size="small" disabled={!name.trim()} onClick={() => void create()}>
            Create
          </Button>
        </Box>
        {held.length > 0 && <Divider />}
        {held.length > 0 && <ListSubheader>Remove from</ListSubheader>}
        {held.map((g) => (
          <MenuItem key={`rm-${g.resourceName}`} onClick={() => void change(g, false)}>
            {g.name}
          </MenuItem>
        ))}
      </Menu>
    </>
  );
};

export default LabelMenu;
