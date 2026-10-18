/**
 * @fileoverview Merge dialog: pick the contact to keep, choose members, preview
 * the result, merge (Story 5.2).
 * @module Contacts/components/cleanup/MergeDialog
 */

import React, { useEffect, useMemo, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  Checkbox,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Radio,
  Tooltip,
  Typography,
} from '@mui/material';
import { CallMerge, Edit } from '@mui/icons-material';
import type { Contact, ContactGroup } from '../../types/Contact';
import { useAppDispatch } from '../../types/hooks';
import { mergeContacts, type CleanupError } from '../../redux/thunks/cleanupThunks';
import { contactUpdated } from '../../redux/slices/contacts/contactsSlice';
import { GoogleContactsService } from '../../services/GoogleContactsService';
import ContactAvatar from '../common/ContactAvatar';
import { planMerge, suggestPrimary } from '../../model/merge';
import ContactForm from '../card/ContactForm';
import { buildPatch } from '../card/cardModel';
import {
  formatBirthday,
  getDisplayName,
  getLabelNames,
  getPhoneticName,
} from '../../model/contactView';
import { tokens } from '../../theme/contactsTheme';

/** Props for MergeDialog */
export interface MergeDialogProps {
  /** Candidate contacts; null closes the dialog */
  contacts: Contact[] | null;
  /** Labels by resource name */
  groups: Record<string, ContactGroup>;
  /** Close without merging */
  onClose: () => void;
  /** Called after a successful merge with the kept contact and how many were removed */
  onMerged: (kept: Contact, removed: number) => void;
}

/**
 * One row of the preview.
 * @param props - Label and values
 * @returns Row element
 */
const PreviewRow: React.FC<{ label: string; values: string[] }> = ({ label, values }) =>
  values.length === 0 ? null : (
    <Box sx={{ display: 'grid', gridTemplateColumns: '110px 1fr', gap: 1.5, py: 0.5 }}>
      <Typography variant="caption" sx={{ color: tokens.textMuted, pt: '2px' }}>
        {label}
      </Typography>
      <Box sx={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
        {values.map((v, i) => (
          <Typography
            key={i}
            variant="body2"
            dir="auto"
            align="left"
            sx={{ wordBreak: 'break-word' }}
          >
            {v}
          </Typography>
        ))}
      </Box>
    </Box>
  );

/**
 * Merge dialog.
 * @param props - Component props
 * @returns Dialog element
 */
const MergeDialog: React.FC<MergeDialogProps> = ({ contacts, groups, onClose, onMerged }) => {
  const dispatch = useAppDispatch();
  const [primary, setPrimary] = useState<string>('');
  const [included, setIncluded] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<CleanupError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  // Fresh copies after a "changed since loaded" refusal; they replace the props
  const [refreshed, setRefreshed] = useState<Record<string, Contact>>({});

  useEffect(() => {
    if (contacts && contacts.length > 0) {
      setPrimary(suggestPrimary(contacts));
      setIncluded(new Set(contacts.map((c) => c.resourceName)));
      setError(null);
      setNotice(null);
      setRefreshed({});
    }
  }, [contacts]);

  // A warning or acknowledgement belongs to one exact set: any change voids it
  // Hand edits of the result, made on top of the plan. Changing who is kept or
  // included makes a new plan, so the edits are dropped with it.
  const [edited, setEdited] = useState<Contact | null>(null);
  useEffect(() => {
    setError(null);
    setEdited(null);
  }, [primary, included]);

  const list = (contacts ?? []).map((c) => refreshed[c.resourceName] ?? c);
  const tooMany = included.size > 20;
  const main = list.find((c) => c.resourceName === primary);
  const others = list.filter((c) => c.resourceName !== primary && included.has(c.resourceName));
  const plan = useMemo(
    () => (main && others.length ? planMerge(main, others) : null),
    [main, others],
  );

  const toggle = (rn: string) =>
    setIncluded((prev) => {
      const next = new Set(prev);
      if (next.has(rn)) {
        next.delete(rn);
      } else {
        next.add(rn);
      }
      return next;
    });

  /** Re-reads every member from Google after the server said they changed */
  const reloadMembers = async () => {
    const fresh: Record<string, Contact> = {};
    await Promise.all(
      (contacts ?? []).map(async (c) => {
        const r = await GoogleContactsService.getContact(c.resourceName);
        if (r.success && r.data) {
          fresh[c.resourceName] = r.data;
          dispatch(contactUpdated(r.data));
        }
      }),
    );
    setRefreshed(fresh);
    setError(null);
    setNotice('Reloaded the latest versions from Google. Review the result and merge again.');
  };

  const merge = async (acknowledged: string[]) => {
    if (!main || !plan) {
      return;
    }
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const kept = await dispatch(
        mergeContacts({
          primaryResourceName: main.resourceName,
          patch: edited && main ? buildPatch(main, edited) : plan.patch,
          deleteResourceNames: others.map((o) => o.resourceName),
          addLabels: plan.addLabels,
          moveToTrash: true,
          acknowledgedResourceNames: acknowledged,
          deleteEtags: Object.fromEntries(
            others.filter((o) => o.etag).map((o) => [o.resourceName, o.etag as string]),
          ),
        }),
      ).unwrap();
      onMerged(kept, others.length);
    } catch (e) {
      setError(e as CleanupError);
    } finally {
      setBusy(false);
    }
  };

  const nameOf = (rn: string) =>
    getDisplayName(list.find((c) => c.resourceName === rn) ?? { resourceName: rn });
  const m = edited ?? plan?.merged;
  const lost = error?.code === 'MERGE_WOULD_LOSE_DATA' ? error.lostData : undefined;
  const stale = error?.code === 'CONTACT_CONFLICT';

  return (
    <Dialog open={!!contacts} onClose={busy ? undefined : onClose} maxWidth="md" fullWidth>
      <DialogTitle>Merge contacts</DialogTitle>
      <DialogContent
        sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '1fr 1fr' }, gap: 3 }}
      >
        {/* Members */}
        <Box>
          <Typography variant="subtitle2" sx={{ color: tokens.textSecondary, mb: 1 }}>
            Keep · include
          </Typography>
          <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1 }}>
            {list.map((c) => {
              const isMain = c.resourceName === primary;
              const isIn = included.has(c.resourceName);
              const counts = [
                c.phoneNumbers?.length && `${c.phoneNumbers.length} phone`,
                c.emailAddresses?.length && `${c.emailAddresses.length} email`,
                c.organizations?.length && 'work',
                c.birthdays?.length && 'birthday',
              ].filter(Boolean);
              return (
                <Box
                  key={c.resourceName}
                  sx={{
                    display: 'flex',
                    alignItems: 'center',
                    gap: 1,
                    p: 1,
                    borderRadius: '8px',
                    border: `1px solid ${isMain ? tokens.accent : tokens.border}`,
                    bgcolor: isMain ? 'rgba(57,135,229,0.08)' : tokens.page,
                    opacity: isIn || isMain ? 1 : 0.55,
                  }}
                >
                  <Tooltip title="Keep this contact">
                    <Radio
                      size="small"
                      checked={isMain}
                      onChange={() => {
                        setPrimary(c.resourceName);
                        setIncluded((prev) => new Set(prev).add(c.resourceName));
                      }}
                      inputProps={{ 'aria-label': `Keep ${getDisplayName(c)}` }}
                    />
                  </Tooltip>
                  <ContactAvatar contact={c} size={30} />
                  <Box sx={{ minWidth: 0, flex: 1 }}>
                    <Typography
                      variant="body2"
                      noWrap
                      dir="auto"
                      align="left"
                      sx={{ fontWeight: 600 }}
                    >
                      {getDisplayName(c)}
                    </Typography>
                    <Typography
                      variant="caption"
                      noWrap
                      sx={{ color: tokens.textMuted, display: 'block' }}
                    >
                      {isMain ? 'Kept' : isIn ? 'Merged in, then moved to Trash' : 'Left out'}
                      {counts.length ? ` · ${counts.join(', ')}` : ''}
                    </Typography>
                  </Box>
                  {!isMain && (
                    <Tooltip title={isIn ? 'Leave out of this merge' : 'Include in this merge'}>
                      <Checkbox
                        size="small"
                        checked={isIn}
                        onChange={() => toggle(c.resourceName)}
                        inputProps={{ 'aria-label': `Include ${getDisplayName(c)}` }}
                      />
                    </Tooltip>
                  )}
                </Box>
              );
            })}
          </Box>
        </Box>

        {/* Preview */}
        <Box>
          <Box sx={{ display: 'flex', alignItems: 'center', mb: 1 }}>
            <Typography variant="subtitle2" sx={{ color: tokens.textSecondary, flex: 1 }}>
              Result
            </Typography>
            {plan && (
              <Button
                size="small"
                startIcon={<Edit />}
                onClick={() => setEdited(edited ? null : plan.merged)}
                disabled={busy}
              >
                {edited ? 'Discard edits' : 'Edit result'}
              </Button>
            )}
          </Box>
          {plan && plan.transliterated.length > 0 && (
            <Alert severity="info" sx={{ mb: 1 }}>
              Same person in Hebrew and English: {plan.transliterated.join(', ')} becomes the
              phonetic name.
            </Alert>
          )}
          {edited ? (
            <Box
              sx={{
                maxHeight: 460,
                overflowY: 'auto',
                px: 2,
                borderRadius: '10px',
                border: `1px solid ${tokens.accent}`,
                bgcolor: tokens.page,
              }}
            >
              <ContactForm draft={edited} onChange={setEdited} disabled={busy} />
            </Box>
          ) : m ? (
            <Box
              sx={{
                p: 2,
                borderRadius: '10px',
                border: `1px solid ${tokens.border}`,
                bgcolor: tokens.page,
              }}
            >
              <Box sx={{ display: 'flex', gap: 1.5, alignItems: 'center', mb: 1.5 }}>
                <ContactAvatar contact={main!} size={40} />
                <Box sx={{ minWidth: 0 }}>
                  <Typography variant="subtitle1" dir="auto" align="left" noWrap>
                    {[m.names?.[0]?.givenName, m.names?.[0]?.middleName, m.names?.[0]?.familyName]
                      .filter(Boolean)
                      .join(' ') || getDisplayName(m)}
                  </Typography>
                  <Typography variant="caption" sx={{ color: tokens.textSecondary }}>
                    {getPhoneticName(m) || 'no phonetic name'}
                  </Typography>
                </Box>
              </Box>
              <PreviewRow label="Phones" values={(m.phoneNumbers ?? []).map((p) => p.value)} />
              <PreviewRow label="Emails" values={(m.emailAddresses ?? []).map((e) => e.value)} />
              <PreviewRow
                label="Work"
                values={(m.organizations ?? []).map((o) =>
                  [o.title, o.name].filter(Boolean).join(' · '),
                )}
              />
              <PreviewRow label="Birthday" values={formatBirthday(m) ? [formatBirthday(m)] : []} />
              <PreviewRow
                label="Addresses"
                values={(m.addresses ?? []).map(
                  (a) =>
                    a.formattedValue ||
                    [a.streetAddress, a.city, a.country].filter(Boolean).join(', '),
                )}
              />
              <PreviewRow label="Websites" values={(m.urls ?? []).map((u) => u.value)} />
              <PreviewRow label="Relations" values={(m.relations ?? []).map((r) => r.person)} />
              <PreviewRow
                label="Custom"
                values={(m.userDefined ?? []).map((f) => `${f.key}: ${f.value}`)}
              />
              <Box sx={{ display: 'flex', gap: 0.75, flexWrap: 'wrap', mt: 1 }}>
                {getLabelNames(m, groups).map((l) => (
                  <Chip key={l} label={l} size="small" sx={{ bgcolor: tokens.raised }} />
                ))}
              </Box>
              {m.biographies?.[0]?.value && (
                <Typography
                  variant="caption"
                  dir="auto"
                  sx={{ color: tokens.textMuted, display: 'block', mt: 1, whiteSpace: 'pre-wrap' }}
                >
                  {m.biographies[0].value.slice(0, 300)}
                </Typography>
              )}
            </Box>
          ) : (
            <Typography variant="body2" sx={{ color: tokens.textMuted }}>
              Include at least one other contact.
            </Typography>
          )}
        </Box>

        {(error || others.length > 0) && (
          <Box sx={{ gridColumn: '1 / -1' }}>
            {lost ? (
              <Alert severity="warning">
                These contacts hold data this app cannot merge, which would be lost when they are
                deleted:
                <Box component="ul" sx={{ m: 0, mt: 0.5, pl: 2.5 }}>
                  {Object.entries(lost).map(([rn, fields]) => (
                    <li key={rn}>
                      <b dir="auto">{nameOf(rn)}</b>: {fields.join(', ')}
                    </li>
                  ))}
                </Box>
                Merge them in Google Contacts instead, or merge here and lose that data.
              </Alert>
            ) : stale ? (
              <Alert
                severity="warning"
                action={
                  <Button color="inherit" size="small" onClick={reloadMembers}>
                    Reload
                  </Button>
                }
              >
                Some of these contacts changed in Google since they were loaded (for example on your
                phone). Nothing was changed. Reload them and review the merge again.
              </Alert>
            ) : error ? (
              <Alert severity="error">{error.message}</Alert>
            ) : tooMany ? (
              <Alert severity="warning">Merge at most 20 contacts at a time.</Alert>
            ) : (
              <>
                {notice && (
                  <Alert severity="success" sx={{ mb: 1 }}>
                    {notice}
                  </Alert>
                )}
                {plan && plan.dropped.length > 0 && (
                  <Alert severity="warning" sx={{ mb: 1 }}>
                    Not kept (a contact has one name and one birthday):
                    <Box component="ul" sx={{ m: 0, mt: 0.5, pl: 2.5 }}>
                      {plan.dropped.map((d) => (
                        <li key={d} dir="auto">
                          {d}
                        </li>
                      ))}
                    </Box>
                  </Alert>
                )}
                <Alert severity="info" icon={false} sx={{ bgcolor: tokens.page }}>
                  The kept contact is saved first; only then{' '}
                  {others.length === 1
                    ? 'is the other contact'
                    : `are the other ${others.length} contacts`}{' '}
                  moved to Trash (restorable from the Trash tab).
                </Alert>
              </>
            )}
          </Box>
        )}
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2 }}>
        <Button color="inherit" onClick={onClose} disabled={busy}>
          Cancel
        </Button>
        {lost ? (
          <Button
            variant="contained"
            color="warning"
            onClick={() => merge(Object.keys(lost))}
            disabled={busy || !plan}
          >
            {busy ? 'Merging…' : 'Merge anyway'}
          </Button>
        ) : (
          <Button
            variant="contained"
            onClick={() => merge([])}
            disabled={busy || !plan || tooMany || stale}
            startIcon={busy ? <CircularProgress size={16} color="inherit" /> : <CallMerge />}
          >
            {busy ? 'Merging…' : `Merge ${others.length + 1} into 1`}
          </Button>
        )}
      </DialogActions>
    </Dialog>
  );
};

export default MergeDialog;
