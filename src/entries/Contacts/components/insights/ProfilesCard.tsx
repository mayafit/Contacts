/**
 * @fileoverview Insights: public profiles by email hash (AD-46 rules 5, 7): turning the
 * look-up on, its progress, reviewing the links to add, turning it off and forgetting
 * @module Contacts/components/insights/ProfilesCard
 */

import React, { useMemo, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  Paper,
  Typography,
} from '@mui/material';
import type { Contact } from '../../types/Contact';
import type { ProfilesState } from '../../hooks/useProfiles';
import { mergeProfiles, profilesOfGravatar, type ContactProfile } from '../../model/profiles';
import { getDisplayName } from '../../model/contactView';
import { useAppDispatch } from '../../types/hooks';
import { updateContact } from '../../redux/slices/contacts/contactsSlice';
import { buildPatch } from '../card/cardModel';
import { notify } from '../common/notify';
import { tokens } from '../../theme/contactsTheme';

const fmt = new Intl.NumberFormat('en-US');

/** A contact with profiles not on it yet */
interface ToAdd {
  contact: Contact;
  links: ContactProfile[];
}

/**
 * The Profiles card.
 * @param props - Contacts, the look-up's state, open a card
 * @returns Card
 */
const ProfilesCard: React.FC<{
  contacts: Contact[];
  profiles: ProfilesState;
  onOpenCard: (resourceName: string) => void;
}> = ({ contacts, profiles, onOpenCard }) => {
  const dispatch = useAppDispatch();
  const { status } = profiles;
  const [busy, setBusy] = useState(false);
  const [reviewing, setReviewing] = useState(false);
  const [ticked, setTicked] = useState<Set<string>>(new Set());
  const [error, setError] = useState<string | null>(null);

  const toAdd = useMemo<ToAdd[]>(
    () =>
      contacts
        .map((contact) => ({
          contact,
          links: mergeProfiles(contact, [
            {
              source: 'gravatar',
              links: (profiles.byContact.get(contact.resourceName) ?? []).flatMap(
                profilesOfGravatar,
              ),
            },
          ]).filter((p) => !p.onContact),
        }))
        .filter((t) => t.links.length > 0),
    [contacts, profiles.byContact],
  );
  const key = (t: ToAdd, p: ContactProfile) => `${t.contact.resourceName}|${p.key}`;

  const run = async (work: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    try {
      await work();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const openReview = () => {
    // The Owner ticks the links (FR-88); nothing starts ticked
    setTicked(new Set());
    setError(null);
    setReviewing(true);
  };

  const tickSocial = () =>
    setTicked(
      new Set(
        toAdd.flatMap((t) => t.links.filter((p) => p.network !== 'Website').map((p) => key(t, p))),
      ),
    );

  const apply = async () => {
    setBusy(true);
    setError(null);
    let done = 0;
    const failed: string[] = [];
    for (const t of toAdd) {
      const chosen = t.links.filter((p) => ticked.has(key(t, p)));
      if (!chosen.length) {
        continue;
      }
      const draft = {
        ...t.contact,
        urls: [
          ...(t.contact.urls ?? []),
          ...chosen.map((p) => ({ value: p.url, type: p.network })),
        ],
      };
      const patch = buildPatch(t.contact, draft, t.contact.etag);
      if (!patch) {
        continue;
      }
      try {
        await dispatch(
          updateContact({ resourceName: t.contact.resourceName, updates: patch }),
        ).unwrap();
        done += 1;
        // Saved: its ticks go, so a second try counts only what is left
        setTicked(
          (s) => new Set([...s].filter((k) => !k.startsWith(`${t.contact.resourceName}|`))),
        );
      } catch (e) {
        failed.push(
          `${getDisplayName(t.contact)}: ${(e as { message?: string }).message ?? 'failed'}`,
        );
      }
    }
    setBusy(false);
    notify(`Links added to ${done} ${done === 1 ? 'contact' : 'contacts'}`);
    if (failed.length) {
      setError(`${failed.length} not saved: ${failed.slice(0, 3).join('; ')}`);
      return;
    }
    setReviewing(false);
  };

  const hours =
    status && status.pending > 0 ? Math.max(1, Math.ceil(status.pending / status.hourlyLimit)) : 0;

  return (
    <Paper variant="outlined" sx={{ p: 2.5, borderColor: tokens.border, bgcolor: tokens.surface }}>
      <Typography variant="subtitle1">Public profiles</Typography>
      {!status?.enabled ? (
        <>
          <Typography variant="body2" sx={{ color: tokens.textSecondary, mt: 1 }}>
            Gravatar is a public profile people fill in themselves: their job, company, location and
            the accounts they verified (LinkedIn, X, GitHub…). Echo can look up each contact&rsquo;s
            email there. It sends Gravatar only a hash of each address (SHA-256), never the address,
            about 100 an hour in the background, and again every 30 days. Matches are exact: a
            profile belongs to that address.
          </Typography>
          <Button
            variant="outlined"
            sx={{ mt: 1.5 }}
            disabled={busy || !status}
            onClick={() => void run(profiles.enable)}
          >
            Find public profiles
          </Button>
        </>
      ) : (
        <>
          <Typography variant="body2" sx={{ color: tokens.textSecondary, mt: 1 }}>
            Checked {fmt.format(status.checked)} of {fmt.format(status.checked + status.pending)}{' '}
            addresses · {fmt.format(status.found)} profiles found
            {hours ? ` · about ${hours} ${hours === 1 ? 'hour' : 'hours'} to go` : ''}
          </Typography>
          <Box sx={{ display: 'flex', gap: 1, mt: 1.5, flexWrap: 'wrap' }}>
            {toAdd.length > 0 && (
              <Button variant="outlined" onClick={openReview} disabled={busy}>
                Review links ({toAdd.length} contacts)
              </Button>
            )}
            <Button color="inherit" disabled={busy} onClick={() => void run(profiles.forget)}>
              Turn off and forget
            </Button>
          </Box>
        </>
      )}
      {error && !reviewing && (
        <Alert severity="error" sx={{ mt: 1.5 }}>
          {error}
        </Alert>
      )}

      <Dialog
        open={reviewing}
        onClose={busy ? undefined : () => setReviewing(false)}
        maxWidth="md"
        fullWidth
      >
        <DialogTitle>Profile links to add</DialogTitle>
        <DialogContent>
          <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 2 }}>
            Found on Gravatar for each contact&rsquo;s email address. Ticked links are added to the
            contact&rsquo;s websites in Google, and so reach your phones.
          </Typography>
          <Button size="small" onClick={tickSocial} disabled={busy} sx={{ mb: 1 }}>
            Tick all social links (not websites)
          </Button>
          {toAdd.map((t) => (
            <Box
              key={t.contact.resourceName}
              sx={{ py: 1, borderBottom: `1px solid ${tokens.border}` }}
            >
              <Button
                size="small"
                onClick={() => onOpenCard(t.contact.resourceName)}
                sx={{ fontWeight: 600, px: 0, textTransform: 'none' }}
              >
                {getDisplayName(t.contact)}
              </Button>
              {t.links.map((p) => (
                <FormControlLabel
                  key={p.key}
                  sx={{ display: 'flex' }}
                  control={
                    <Checkbox
                      size="small"
                      checked={ticked.has(key(t, p))}
                      onChange={(e) =>
                        setTicked((s) => {
                          const next = new Set(s);
                          if (e.target.checked) {
                            next.add(key(t, p));
                          } else {
                            next.delete(key(t, p));
                          }
                          return next;
                        })
                      }
                    />
                  }
                  label={
                    <Typography variant="body2">
                      {p.network}: {p.url.replace(/^https:\/\//, '')}
                    </Typography>
                  }
                />
              ))}
            </Box>
          ))}
          {error && (
            <Alert severity="error" sx={{ mt: 2 }}>
              {error}
            </Alert>
          )}
        </DialogContent>
        <DialogActions>
          <Button color="inherit" onClick={() => setReviewing(false)} disabled={busy}>
            Cancel
          </Button>
          <Button
            variant="contained"
            onClick={() => void apply()}
            disabled={busy || ticked.size === 0}
          >
            Add {ticked.size} links
          </Button>
        </DialogActions>
      </Dialog>
    </Paper>
  );
};

export default ProfilesCard;
