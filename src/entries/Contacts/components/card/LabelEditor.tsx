/**
 * @fileoverview The card's labels, editable (AD-45 rule 2): × removes, the box adds an
 * existing label or creates one. Applied at once, not with Save.
 * @module Contacts/components/card/LabelEditor
 */

import React, { useMemo, useState } from 'react';
import { Autocomplete, Box, Chip, CircularProgress, TextField } from '@mui/material';
import type { Contact, ContactGroup } from '../../types/Contact';
import { useAppDispatch } from '../../types/hooks';
import { fetchContactGroups } from '../../redux/slices/contacts/contactsSlice';
import { changeMembership } from '../../redux/thunks/labelThunks';
import { backendApiClient } from '../../../../services/api/BackendApiClient';
import { notify } from '../common/notify';
import { tokens } from '../../theme/contactsTheme';

/** Label that holds the app's Trash (same name as the backend's) */
const TRASH = 'Contacts app – Trash';

/** The labels the Owner manages: their own, not the Trash */
export const userLabels = (groups: Record<string, ContactGroup>): ContactGroup[] =>
  Object.values(groups)
    .filter((g) => g.groupType === 'USER_CONTACT_GROUP' && g.name !== TRASH)
    .sort((a, b) => a.name.localeCompare(b.name));

type Option = { kind: 'label'; group: ContactGroup } | { kind: 'create'; name: string };

/**
 * The contact's labels as chips, and a box to add one.
 * @param props - The contact, the labels
 * @returns Editor
 */
const LabelEditor: React.FC<{ contact: Contact; groups: Record<string, ContactGroup> }> = ({
  contact,
  groups,
}) => {
  const dispatch = useAppDispatch();
  const [busy, setBusy] = useState(false);
  const [text, setText] = useState('');
  const own = useMemo(() => userLabels(groups), [groups]);
  const on = own.filter((g) => (contact.labels ?? []).includes(g.resourceName));

  const change = async (label: string, add: boolean, name: string) => {
    setBusy(true);
    const refused = await changeMembership(dispatch, [contact.resourceName], label, add);
    setBusy(false);
    if (refused) {
      notify(`Could not ${add ? 'add to' : 'remove from'} “${name}”: ${refused}`);
    }
  };

  const pick = async (o: Option | null) => {
    setText('');
    if (!o) {
      return;
    }
    if (o.kind === 'label') {
      await change(o.group.resourceName, true, o.group.name);
      return;
    }
    setBusy(true);
    try {
      const created = await backendApiClient.createLabel(o.name);
      await dispatch(fetchContactGroups());
      setBusy(false);
      await change(created.resourceName, true, created.name);
    } catch (e) {
      setBusy(false);
      notify(`Could not create “${o.name}”: ${(e as Error).message}`);
    }
  };

  const typed = text.trim();
  const options: Option[] = [
    ...own.filter((g) => !on.includes(g)).map((group): Option => ({ kind: 'label', group })),
    ...(typed && !own.some((g) => g.name.toLowerCase() === typed.toLowerCase())
      ? [{ kind: 'create', name: typed } as Option]
      : []),
  ];

  return (
    <Box sx={{ display: 'flex', gap: 0.75, flexWrap: 'wrap', alignItems: 'center' }}>
      {on.map((g) => (
        <Chip
          key={g.resourceName}
          label={g.name}
          size="small"
          disabled={busy}
          onDelete={() => void change(g.resourceName, false, g.name)}
          sx={{ bgcolor: tokens.surface }}
        />
      ))}
      <Autocomplete<Option>
        size="small"
        options={options}
        value={null}
        inputValue={text}
        onInputChange={(_, v) => setText(v)}
        onChange={(_, o) => void pick(o)}
        getOptionLabel={(o) => (o.kind === 'label' ? o.group.name : `Create “${o.name}”`)}
        filterOptions={(list) =>
          list.filter(
            (o) => o.kind === 'create' || o.group.name.toLowerCase().includes(typed.toLowerCase()),
          )
        }
        disabled={busy}
        blurOnSelect
        sx={{ width: 170 }}
        renderInput={(params) => (
          <TextField
            {...params}
            placeholder="Add label"
            variant="standard"
            InputProps={{
              ...params.InputProps,
              endAdornment: busy ? <CircularProgress size={14} /> : params.InputProps.endAdornment,
            }}
          />
        )}
      />
    </Box>
  );
};

export default LabelEditor;
