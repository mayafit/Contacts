/**
 * @fileoverview Phone sync: compare the phone's contacts with Google, add what is
 * missing, save contacts to the phone, and how to make the phone sync by itself
 * @module Contacts/components/device/DeviceSyncDialog
 */

import React, { useState } from 'react';
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
  Divider,
  FormControlLabel,
  Typography,
} from '@mui/material';
import { Contacts as ContactsIcon, SaveAlt } from '@mui/icons-material';
import type { Contact, ContactGroup } from '../../types/Contact';
import { useAppDispatch } from '../../types/hooks';
import { contactAdded, updateContact } from '../../redux/slices/contacts/contactsSlice';
import { backendApiClient } from '../../../../services/api/BackendApiClient';
import { buildPatch } from '../card/cardModel';
import { newContactPatch, resolveChange } from '../../assistant/changes';
import { getDisplayName, splitFullName } from '../../model/contactView';
import {
  canPickDeviceContacts,
  compareWithDevice,
  pickDeviceContacts,
  platformOf,
  saveToDevice,
  type DeviceComparison,
} from '../../device/device';
import { tokens } from '../../theme/contactsTheme';

/** Props for DeviceSyncDialog */
export interface DeviceSyncDialogProps {
  open: boolean;
  onClose: () => void;
  /** All Google contacts, Trash included */
  allContacts: Contact[];
  groups: Record<string, ContactGroup>;
  /** Contacts in the current view, for "save to phone" */
  viewContacts: Contact[];
  /** Report what happened */
  onDone: (message: string) => void;
}

/**
 * Phone sync dialog.
 * @param props - Component props
 * @returns Dialog
 */
const DeviceSyncDialog: React.FC<DeviceSyncDialogProps> = ({
  open,
  onClose,
  allContacts,
  groups,
  viewContacts,
  onDone,
}) => {
  const dispatch = useAppDispatch();
  const platform = platformOf();
  const [result, setResult] = useState<DeviceComparison | null>(null);
  const [skip, setSkip] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const reset = () => {
    setResult(null);
    setSkip(new Set());
    setError(null);
  };

  const pick = async () => {
    setError(null);
    try {
      const chosen = await pickDeviceContacts();
      setResult(compareWithDevice(chosen, allContacts));
      setSkip(new Set());
    } catch (e) {
      setError((e as Error).message || 'Could not read the phone contacts');
    }
  };

  const keyOf = (kind: 'add' | 'new', i: number) => `${kind}:${i}`;
  const toggle = (k: string) =>
    setSkip((s) => {
      const n = new Set(s);
      if (n.has(k)) {
        n.delete(k);
      } else {
        n.add(k);
      }
      return n;
    });

  const apply = async () => {
    if (!result) {
      return;
    }
    setBusy(true);
    let updated = 0;
    let created = 0;
    const failed: string[] = [];
    for (const [i, d] of result.differences.entries()) {
      if (skip.has(keyOf('add', i))) {
        continue;
      }
      const r = resolveChange(
        d.contact,
        {
          id: d.contact.resourceName,
          addPhones: d.addPhones.map((value) => ({ value })),
          addEmails: d.addEmails.map((value) => ({ value })),
        },
        groups,
      );
      const patch = buildPatch(d.contact, r.draft, d.contact.etag);
      if (!patch) {
        continue;
      }
      try {
        await dispatch(
          updateContact({ resourceName: d.contact.resourceName, updates: patch }),
        ).unwrap();
        updated += 1;
      } catch (e) {
        failed.push(`${getDisplayName(d.contact)}: ${(e as Error).message ?? 'failed'}`);
      }
    }
    for (const [i, d] of result.missing.entries()) {
      if (skip.has(keyOf('new', i))) {
        continue;
      }
      const { first, last } = splitFullName(d.name[0] ?? '');
      const { patch } = newContactPatch(
        { givenName: first, familyName: last },
        d.email.map((value) => ({ value })),
        d.tel.map((value) => ({ value, type: 'mobile' })),
      );
      try {
        dispatch(contactAdded(await backendApiClient.createContact(patch)));
        created += 1;
      } catch (e) {
        failed.push(`${d.name[0] ?? d.tel[0]}: ${(e as Error).message ?? 'failed'}`);
      }
    }
    setBusy(false);
    reset();
    onDone(
      `Phone sync: ${created} added to Google, ${updated} updated${failed.length ? `, ${failed.length} failed (${failed.slice(0, 2).join('; ')})` : ''}`,
    );
    onClose();
  };

  const saveView = async () => {
    const how = await saveToDevice(
      viewContacts,
      viewContacts.length === 1 ? getDisplayName(viewContacts[0]) : 'contacts',
    );
    onDone(
      how === 'shared'
        ? 'Sent to the share sheet: choose Contacts to save'
        : `Downloaded ${viewContacts.length} contacts as a .vcf file`,
    );
  };

  return (
    <Dialog
      open={open}
      onClose={
        busy
          ? undefined
          : () => {
              reset();
              onClose();
            }
      }
      maxWidth="sm"
      fullWidth
    >
      <DialogTitle>Phone sync</DialogTitle>
      <DialogContent sx={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
        <Alert severity="info" icon={false}>
          <Typography variant="body2" sx={{ fontWeight: 600, mb: 0.5 }}>
            Let the phone sync by itself
          </Typography>
          {platform === 'ios' ? (
            <Typography variant="body2">
              Settings → Contacts → Accounts → add your Google account and turn on Contacts. Then
              set Settings → Contacts → Default Account to Gmail, so new contacts reach Google (and
              this app) instead of iCloud.
            </Typography>
          ) : (
            <Typography variant="body2">
              Android syncs Google contacts both ways. Make sure new contacts are saved to your
              Google account: in the Contacts app, Settings → Default account (Samsung: Settings →
              Default storage location) → Google. Contacts saved to “Phone” or “Samsung account”
              never reach Google; move them with Settings → Move contacts → Google.
            </Typography>
          )}
        </Alert>

        <Box>
          <Typography variant="subtitle2" sx={{ mb: 0.5 }}>
            Compare with this phone
          </Typography>
          {canPickDeviceContacts() ? (
            <>
              <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 1 }}>
                Choose contacts in the phone’s picker (select all if you like). The app shows which
                are missing from Google or have extra numbers, and changes nothing until you apply.
              </Typography>
              {!result && (
                <Button variant="outlined" startIcon={<ContactsIcon />} onClick={pick}>
                  Choose contacts from this phone
                </Button>
              )}
            </>
          ) : (
            <Typography variant="body2" sx={{ color: tokens.textSecondary }}>
              This browser cannot read the phone’s contacts. On Android, open the app in Chrome.
              iPhone browsers do not allow it; use the Google account sync above instead.
            </Typography>
          )}
          {error && (
            <Alert severity="error" sx={{ mt: 1 }}>
              {error}
            </Alert>
          )}
          {result && (
            <Box sx={{ mt: 1 }}>
              <Typography variant="body2" sx={{ mb: 1 }}>
                {result.same} already match · {result.differences.length} have extra details ·{' '}
                {result.missing.length} not in Google
              </Typography>
              {result.differences.map((d, i) => (
                <FormControlLabel
                  key={keyOf('add', i)}
                  sx={{ display: 'flex', alignItems: 'flex-start', m: 0 }}
                  control={
                    <Checkbox
                      size="small"
                      checked={!skip.has(keyOf('add', i))}
                      onChange={() => toggle(keyOf('add', i))}
                    />
                  }
                  label={
                    <Typography variant="body2" dir="auto" sx={{ pt: 0.75 }}>
                      Add {[...d.addPhones, ...d.addEmails].join(', ')} to{' '}
                      <strong>{getDisplayName(d.contact)}</strong>
                    </Typography>
                  }
                />
              ))}
              {result.missing.map((d, i) => (
                <FormControlLabel
                  key={keyOf('new', i)}
                  sx={{ display: 'flex', alignItems: 'flex-start', m: 0 }}
                  control={
                    <Checkbox
                      size="small"
                      checked={!skip.has(keyOf('new', i))}
                      onChange={() => toggle(keyOf('new', i))}
                    />
                  }
                  label={
                    <Typography variant="body2" dir="auto" sx={{ pt: 0.75 }}>
                      Create <strong>{d.name[0] || d.tel[0] || d.email[0]}</strong>{' '}
                      {[...d.tel, ...d.email].join(', ')}
                    </Typography>
                  }
                />
              ))}
            </Box>
          )}
        </Box>

        <Divider />
        <Box>
          <Typography variant="subtitle2" sx={{ mb: 0.5 }}>
            Save to this device
          </Typography>
          <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 1 }}>
            The {viewContacts.length} contacts in the current view as a contact card (.vcf), with
            phonetic names. On a phone the share sheet opens; choose Contacts.
          </Typography>
          <Button
            variant="outlined"
            startIcon={<SaveAlt />}
            onClick={saveView}
            disabled={viewContacts.length === 0}
          >
            Save {viewContacts.length} to device
          </Button>
        </Box>
      </DialogContent>
      <DialogActions>
        <Button
          color="inherit"
          onClick={() => {
            reset();
            onClose();
          }}
          disabled={busy}
        >
          Close
        </Button>
        {result && (result.differences.length > 0 || result.missing.length > 0) && (
          <Button variant="contained" onClick={apply} disabled={busy}>
            {busy ? <CircularProgress size={18} /> : 'Apply'}
          </Button>
        )}
      </DialogActions>
    </Dialog>
  );
};

export default DeviceSyncDialog;
