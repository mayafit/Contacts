/**
 * @fileoverview Contact card: every field of one contact, editable, in a side
 * drawer. Saves only the groups that changed (Story 4.5).
 * @module Contacts/components/card/ContactCardDrawer
 */

import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Drawer,
  IconButton,
  Link,
  Tooltip,
  Typography,
} from '@mui/material';
import { Close, DeleteOutline, OpenInNew } from '@mui/icons-material';
import type { Contact, ContactGroup } from '../../types/Contact';
import { useAppDispatch, useAppSelector } from '../../types/hooks';
import { contactUpdated, updateContact } from '../../redux/slices/contacts/contactsSlice';
import { GoogleContactsService } from '../../services/GoogleContactsService';
import { selectContactEntities } from '../../redux/slices/contacts/selectors';
import ContactAvatar from '../common/ContactAvatar';
import ContactForm from './ContactForm';
import { buildPatch, changedGroups, createDraft, validateDraft } from './cardModel';
import {
  getDisplayName,
  getJobTitle,
  getLabelNames,
  getOrganization,
  getPhoneticName,
} from '../../model/contactView';
import { tokens } from '../../theme/contactsTheme';
import { logger } from '../../../../shared/logger';
import type { AppError } from '../../types';

/** Props for ContactCardDrawer */
export interface ContactCardDrawerProps {
  /** Contact to show; null closes the drawer */
  resourceName: string | null;
  /** Labels by resource name */
  groups: Record<string, ContactGroup>;
  /** Close request */
  onClose: () => void;
  /** Ask to delete this contact (opens the confirmation) */
  onDelete?: (contact: Contact) => void;
}

type SaveState =
  | { kind: 'idle' }
  | { kind: 'saving' }
  | { kind: 'saved' }
  | { kind: 'conflict' }
  | { kind: 'error'; message: string };

/**
 * Link to the contact in Google Contacts.
 * @param resourceName - e.g. "people/c123"
 * @returns URL
 */
const googleContactsUrl = (resourceName: string): string =>
  `https://contacts.google.com/person/${resourceName.replace(/^people\//, '')}`;

/**
 * Search links to find more about a contact (Story 6.4). LinkedIn profiles are
 * mostly in English, so it searches the phonetic name when there is one.
 * @param c - Contact
 * @returns Label and URL per site
 */
const lookupLinks = (c: Contact): { label: string; href: string }[] => {
  const name = getDisplayName(c);
  const latin = getPhoneticName(c) || name;
  const org = getOrganization(c);
  const q = (s: string) => encodeURIComponent(s.trim());
  return [
    { label: 'Google', href: `https://www.google.com/search?q=${q(`"${name}" ${org}`)}` },
    {
      label: 'LinkedIn',
      href: `https://www.linkedin.com/search/results/people/?keywords=${q(`${latin} ${org}`)}`,
    },
    { label: 'Facebook', href: `https://www.facebook.com/search/people/?q=${q(name)}` },
  ];
};

/**
 * Contact card drawer.
 * @param props - Component props
 * @returns Drawer element
 */
const ContactCardDrawer: React.FC<ContactCardDrawerProps> = ({
  resourceName,
  groups,
  onClose,
  onDelete,
}) => {
  const dispatch = useAppDispatch();
  const entities = useAppSelector(selectContactEntities);
  const contact = (resourceName ? entities?.[resourceName] : undefined) as Contact | undefined;
  const [original, setOriginal] = useState<Contact | null>(null);
  const [draft, setDraft] = useState<Contact | null>(null);
  const [saveState, setSaveState] = useState<SaveState>({ kind: 'idle' });
  const [confirmClose, setConfirmClose] = useState(false);

  // Load a fresh draft whenever a different contact is opened
  useEffect(() => {
    if (contact) {
      setOriginal(contact);
      setDraft(createDraft(contact));
      setSaveState({ kind: 'idle' });
    } else {
      setOriginal(null);
      setDraft(null);
    }
  }, [resourceName]);

  const dirtyGroups = useMemo(
    () => (original && draft ? changedGroups(original, draft) : []),
    [original, draft],
  );
  const dirty = dirtyGroups.length > 0;
  const invalid = draft ? validateDraft(draft) : null;
  const saving = saveState.kind === 'saving';

  const save = useCallback(
    async (etagOverride?: string | null, base?: Contact) => {
      const from = base ?? original;
      if (!from || !draft || !resourceName) {
        return;
      }
      const patch = buildPatch(original ?? from, draft, etagOverride ?? from.etag);
      if (!patch) {
        return;
      }
      setSaveState({ kind: 'saving' });
      try {
        const saved = await dispatch(updateContact({ resourceName, updates: patch })).unwrap();
        setOriginal(saved);
        setDraft(createDraft(saved));
        setSaveState({ kind: 'saved' });
      } catch (err) {
        const message = (err as AppError)?.message ?? 'Failed to save';
        logger.warn(
          { context: 'ContactCardDrawer/save', metadata: { resourceName, message } },
          'Save failed',
        );
        setSaveState(
          /412|precondition/i.test(message) ? { kind: 'conflict' } : { kind: 'error', message },
        );
      }
    },
    [dispatch, draft, original, resourceName],
  );

  /** Conflict: take Google's current version and drop the edits */
  const reload = useCallback(async () => {
    if (!resourceName) {
      return;
    }
    const fresh = await GoogleContactsService.getContact(resourceName);
    if (fresh.success && fresh.data) {
      dispatch(contactUpdated(fresh.data));
      setOriginal(fresh.data);
      setDraft(createDraft(fresh.data));
      setSaveState({ kind: 'idle' });
    } else {
      setSaveState({ kind: 'error', message: fresh.error?.message ?? 'Failed to reload' });
    }
  }, [dispatch, resourceName]);

  /** Conflict: keep my edited groups, write them on top of Google's current version */
  const overwrite = useCallback(async () => {
    if (!resourceName) {
      return;
    }
    const fresh = await GoogleContactsService.getContact(resourceName);
    if (!fresh.success || !fresh.data) {
      setSaveState({ kind: 'error', message: fresh.error?.message ?? 'Failed to reload' });
      return;
    }
    dispatch(contactUpdated(fresh.data));
    await save(fresh.data.etag);
  }, [dispatch, resourceName, save]);

  const requestClose = () => (dirty ? setConfirmClose(true) : onClose());

  const open = !!resourceName && !!draft && !!original;
  const labels = original ? getLabelNames(original, groups) : [];
  const phonetic = original ? getPhoneticName(original) : '';
  const orgLine = original
    ? [getJobTitle(original), getOrganization(original)].filter(Boolean).join(' · ')
    : '';

  return (
    <>
      <Drawer
        anchor="right"
        open={open}
        onClose={requestClose}
        PaperProps={{
          sx: { width: { xs: '100vw', sm: 560 }, display: 'flex', flexDirection: 'column' },
        }}
      >
        {open && original && draft && (
          <>
            {/* Header */}
            <Box sx={{ display: 'flex', gap: 2, alignItems: 'flex-start', p: 3, pb: 2 }}>
              <ContactAvatar contact={original} size={60} />
              <Box sx={{ flex: 1, minWidth: 0 }}>
                <Typography
                  variant="h2"
                  dir="auto"
                  sx={{ wordBreak: 'break-word', textAlign: 'left' }}
                >
                  {getDisplayName(original)}
                </Typography>
                <Typography
                  variant="body2"
                  dir="auto"
                  sx={{
                    color: phonetic ? tokens.textSecondary : tokens.attention,
                    mt: 0.25,
                    textAlign: 'left',
                  }}
                >
                  {phonetic || 'No phonetic name yet'}
                </Typography>
                {orgLine && (
                  <Typography
                    variant="body2"
                    dir="auto"
                    sx={{ color: tokens.textMuted, mt: 0.25, textAlign: 'left' }}
                  >
                    {orgLine}
                  </Typography>
                )}
                <Box
                  sx={{ display: 'flex', gap: 0.75, flexWrap: 'wrap', mt: 1, alignItems: 'center' }}
                >
                  {labels.map((l) => (
                    <Chip key={l} label={l} size="small" sx={{ bgcolor: tokens.surface }} />
                  ))}
                  <Link
                    href={googleContactsUrl(original.resourceName)}
                    target="_blank"
                    rel="noopener noreferrer"
                    underline="hover"
                    sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.5, fontSize: 13 }}
                  >
                    Open in Google Contacts <OpenInNew sx={{ fontSize: 14 }} />
                  </Link>
                </Box>
                <Box
                  sx={{ display: 'flex', gap: 1, alignItems: 'center', mt: 0.75, flexWrap: 'wrap' }}
                >
                  <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                    Look up:
                  </Typography>
                  {lookupLinks(original).map((l) => (
                    <Link
                      key={l.label}
                      href={l.href}
                      target="_blank"
                      rel="noopener noreferrer"
                      underline="hover"
                      sx={{ fontSize: 13 }}
                    >
                      {l.label}
                    </Link>
                  ))}
                </Box>
              </Box>
              <Tooltip title="Close">
                <IconButton onClick={requestClose} aria-label="Close contact card">
                  <Close />
                </IconButton>
              </Tooltip>
            </Box>

            {/* Body */}
            <Box sx={{ flex: 1, overflowY: 'auto', px: 3, pb: 2 }}>
              {saveState.kind === 'conflict' && (
                <Alert
                  severity="warning"
                  sx={{ my: 1 }}
                  action={
                    <Box sx={{ display: 'flex', gap: 1 }}>
                      <Button color="inherit" size="small" onClick={reload}>
                        Reload
                      </Button>
                      <Button color="inherit" size="small" variant="outlined" onClick={overwrite}>
                        Overwrite
                      </Button>
                    </Box>
                  }
                >
                  This contact changed in Google since you opened it. Reload discards your edits.
                  Overwrite replaces each section you edited with your version, including anything
                  changed in those sections elsewhere.
                </Alert>
              )}
              {saveState.kind === 'error' && (
                <Alert severity="error" sx={{ my: 1 }}>
                  {saveState.message}
                </Alert>
              )}
              {invalid && dirty && (
                <Alert severity="warning" sx={{ my: 1 }}>
                  {invalid}
                </Alert>
              )}
              <ContactForm draft={draft} onChange={setDraft} disabled={saving} />
            </Box>

            {/* Footer */}
            <Box
              sx={{
                display: 'flex',
                alignItems: 'center',
                gap: 1.5,
                px: 3,
                py: 1.5,
                borderTop: `1px solid ${tokens.border}`,
                bgcolor: tokens.surface,
              }}
            >
              {onDelete && (
                <Tooltip title="Delete contact">
                  <span>
                    <IconButton
                      aria-label="Delete contact"
                      color="error"
                      onClick={() => onDelete(original)}
                      disabled={saving}
                      size="small"
                    >
                      <DeleteOutline fontSize="small" />
                    </IconButton>
                  </span>
                </Tooltip>
              )}
              <Typography variant="caption" sx={{ color: tokens.textMuted, flex: 1 }}>
                {saveState.kind === 'saved' && !dirty
                  ? 'Saved to Google'
                  : dirty
                    ? `${dirtyGroups.length} unsaved ${dirtyGroups.length === 1 ? 'section' : 'sections'}`
                    : original.updateTime
                      ? `Updated ${new Date(original.updateTime).toLocaleString('en-GB', { dateStyle: 'medium', timeStyle: 'short' })}`
                      : ''}
              </Typography>
              <Button
                color="inherit"
                onClick={() => {
                  setDraft(createDraft(original));
                  setSaveState({ kind: 'idle' });
                }}
                disabled={!dirty || saving}
              >
                Discard
              </Button>
              <Button
                variant="contained"
                onClick={() => save()}
                disabled={!dirty || saving || !!invalid}
                startIcon={saving ? <CircularProgress size={16} color="inherit" /> : undefined}
              >
                {saving ? 'Saving…' : 'Save'}
              </Button>
            </Box>
          </>
        )}
      </Drawer>

      <Dialog open={confirmClose} onClose={() => setConfirmClose(false)}>
        <DialogTitle>Discard unsaved changes?</DialogTitle>
        <DialogContent>
          <Typography variant="body2" sx={{ color: tokens.textSecondary }}>
            {dirtyGroups.length} {dirtyGroups.length === 1 ? 'section has' : 'sections have'} edits
            that are not saved to Google.
          </Typography>
        </DialogContent>
        <DialogActions>
          <Button color="inherit" onClick={() => setConfirmClose(false)}>
            Keep editing
          </Button>
          <Button
            color="warning"
            variant="contained"
            onClick={() => {
              setConfirmClose(false);
              onClose();
            }}
          >
            Discard
          </Button>
        </DialogActions>
      </Dialog>
    </>
  );
};

export default ContactCardDrawer;
