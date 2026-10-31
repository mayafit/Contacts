/**
 * @fileoverview The contact card's dossier (AD-46 rule 6): profiles with their sources,
 * what the person says about themselves on Gravatar, the relationship, and the shared
 * context. Computed in the browser; a profile is added to the draft, saved with Save.
 * @module Contacts/components/card/Dossier
 */

import React, { useMemo, useState } from 'react';
import { Box, Button, ButtonBase, Chip, Collapse, Link, Typography } from '@mui/material';
import { ExpandLess, ExpandMore, Star } from '@mui/icons-material';
import type { Contact, ContactGroup } from '../../types/Contact';
import {
  backendApiClient,
  type GravatarProfile,
  type ThreadState,
} from '../../../../services/api/BackendApiClient';
import { describeGap, formatRelative, type ActivityIndex } from '../../model/activity';
import { getDisplayName, getJobTitle, getOrganization } from '../../model/contactView';
import {
  mergeProfiles,
  profilesOfGravatar,
  profilesOfSignature,
  type ContactProfile,
} from '../../model/profiles';
import { colleaguesOf, segmentsOf } from '../../model/dossier';
import { TIERS, tierOf, type Segment, type SegmentContext } from '../../model/segments';
import { awaitingReply, contactsByEmail } from '../../model/reachOut';
import { userLabels } from './LabelEditor';
import ContactAvatar from '../common/ContactAvatar';
import { isFavourite, STAR_COLOR } from '../common/FavouriteStar';
import { tokens } from '../../theme/contactsTheme';

/** What the dossier needs beyond the contact */
export interface DossierData {
  contacts: Contact[];
  activity: ActivityIndex | null;
  threads: ThreadState[];
  segments: Segment[];
  segmentCtx: SegmentContext;
  /** Gravatar profiles per contact */
  gravatar: Map<string, GravatarProfile[]>;
  /** Email content (T2) may be read */
  signaturesAllowed: boolean;
  onOpenCard: (resourceName: string) => void;
}

const SOURCE_NAMES: Record<string, string> = {
  contact: 'on the contact',
  gravatar: 'Gravatar',
  signature: 'their signature',
};

const Section: React.FC<{ title: string; children: React.ReactNode }> = ({ title, children }) => (
  <Box sx={{ mt: 1.5 }}>
    <Typography variant="caption" sx={{ color: tokens.textMuted, fontWeight: 600 }}>
      {title}
    </Typography>
    <Box sx={{ mt: 0.5 }}>{children}</Box>
  </Box>
);

/**
 * The dossier.
 * @param props - The contact, the draft and its change, the data, the labels
 * @returns Section
 */
const Dossier: React.FC<{
  contact: Contact;
  draft: Contact;
  onDraftChange: (draft: Contact) => void;
  groups: Record<string, ContactGroup>;
  data: DossierData;
  disabled?: boolean;
}> = ({ contact, draft, onDraftChange, groups, data, disabled }) => {
  const [open, setOpen] = useState(true);
  const [signature, setSignature] = useState<
    | { state: 'idle' | 'busy' }
    | { state: 'done'; urls: string[]; emails: number; matched: number }
    | { state: 'error'; message: string }
  >({ state: 'idle' });
  const gravatar = data.gravatar.get(contact.resourceName) ?? [];
  const profiles = useMemo<ContactProfile[]>(
    () =>
      mergeProfiles(draft, [
        { source: 'gravatar', links: gravatar.flatMap(profilesOfGravatar) },
        {
          source: 'signature',
          links: signature.state === 'done' ? profilesOfSignature(signature.urls, draft) : [],
        },
      ]),
    [draft, gravatar, signature],
  );
  const activity = data.activity?.get(contact.resourceName);
  const tier = data.activity ? tierOf(activity, data.segmentCtx.now) : null;
  // Only this contact's threads: awaitingReply also returns unmatched ones (contact null)
  const awaiting = useMemo(() => {
    const all = awaitingReply(data.threads, contactsByEmail([contact]));
    return { owe: all.owe.filter((a) => a.contact), waiting: all.waiting.filter((a) => a.contact) };
  }, [data.threads, contact]);
  const colleagues = useMemo(
    () => colleaguesOf(contact, data.contacts, data.activity),
    [contact, data.contacts, data.activity],
  );
  const segments = useMemo(
    () => segmentsOf(contact, data.segments, data.segmentCtx),
    [contact, data.segments, data.segmentCtx],
  );
  const labels = userLabels(groups).filter((g) => (contact.labels ?? []).includes(g.resourceName));
  const emails = (contact.emailAddresses ?? []).map((e) => e.value ?? '').filter(Boolean);

  const add = (p: ContactProfile) =>
    onDraftChange({ ...draft, urls: [...(draft.urls ?? []), { value: p.url, type: p.network }] });

  const readSignatures = async () => {
    setSignature({ state: 'busy' });
    try {
      const r = await backendApiClient.signatureLinks(emails.slice(0, 5));
      setSignature({
        state: 'done',
        urls: r.urls,
        emails: r.emails,
        matched: r.matched ?? r.emails,
      });
    } catch (e) {
      setSignature({ state: 'error', message: (e as Error).message });
    }
  };

  const about = gravatar.find((g) => g.jobTitle || g.company || g.location || g.description);
  const signatureFound =
    signature.state === 'done' ? profilesOfSignature(signature.urls, draft).length : 0;

  return (
    <Box
      component="section"
      aria-label="Dossier"
      sx={{ py: 1.5, borderTop: `1px solid ${tokens.border}` }}
    >
      <ButtonBase
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        sx={{ width: '100%', justifyContent: 'flex-start', gap: 0.5, borderRadius: 1 }}
      >
        <Typography
          variant="subtitle2"
          sx={{ color: tokens.textSecondary, flex: 1, textAlign: 'left' }}
        >
          Dossier
        </Typography>
        {open ? <ExpandLess fontSize="small" /> : <ExpandMore fontSize="small" />}
      </ButtonBase>
      <Collapse in={open}>
        <Section title="Profiles">
          {profiles.length === 0 && (
            <Typography variant="body2" sx={{ color: tokens.textMuted }}>
              No profile links yet.
            </Typography>
          )}
          {profiles.map((p) => (
            <Box
              key={p.key}
              sx={{ display: 'flex', alignItems: 'center', gap: 1, py: 0.25, flexWrap: 'wrap' }}
            >
              <Chip label={p.network} size="small" sx={{ bgcolor: tokens.surface, minWidth: 74 }} />
              <Link
                href={p.url}
                target="_blank"
                rel="noopener noreferrer"
                underline="hover"
                variant="body2"
                sx={{ minWidth: 0, overflowWrap: 'anywhere' }}
              >
                {p.network === 'Website' ? p.url.replace(/^https:\/\//, '') : p.handle}
              </Link>
              <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                {p.sources.map((s) => SOURCE_NAMES[s]).join(', ')}
              </Typography>
              {!p.onContact && (
                <Button size="small" disabled={disabled} onClick={() => add(p)} sx={{ ml: 'auto' }}>
                  Add to contact
                </Button>
              )}
            </Box>
          ))}
          {emails.length > 0 && (
            <Box sx={{ mt: 0.5 }}>
              {data.signaturesAllowed ? (
                <Button
                  size="small"
                  disabled={signature.state === 'busy'}
                  onClick={() => void readSignatures()}
                >
                  {signature.state === 'busy'
                    ? 'Reading their last emails…'
                    : 'Find links in their signatures'}
                </Button>
              ) : (
                <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                  Allow reading email content (Insights) to find links in their signatures.
                </Typography>
              )}
              {signature.state === 'done' && (
                <Typography variant="caption" component="div" sx={{ color: tokens.textMuted }}>
                  {signature.matched === 0
                    ? 'No email from them in the last 3 years.'
                    : signature.emails === 0
                      ? `Their last ${signature.matched} emails were meeting invitations or had no text of their own.`
                      : `Read their last ${signature.emails} emails: ${signatureFound ? `${signatureFound} links` : 'no profile link'} in their signatures.`}
                </Typography>
              )}
              {signature.state === 'error' && (
                <Typography variant="caption" component="div" sx={{ color: tokens.attention }}>
                  {signature.message}
                </Typography>
              )}
            </Box>
          )}
        </Section>

        {(about || getOrganization(contact)) && (
          <Section title="About">
            {getOrganization(contact) && (
              <Typography variant="body2" dir="auto">
                {[getJobTitle(contact), getOrganization(contact)].filter(Boolean).join(' · ')}
              </Typography>
            )}
            {about && (
              <Typography variant="body2" dir="auto" sx={{ color: tokens.textSecondary }}>
                {[about.jobTitle, about.company, about.location].filter(Boolean).join(' · ')}
                {about.description ? ` — ${about.description}` : ''}
                <Typography component="span" variant="caption" sx={{ color: tokens.textMuted }}>
                  {' '}
                  (from Gravatar)
                </Typography>
              </Typography>
            )}
          </Section>
        )}

        {data.activity && (
          <Section title="Relationship">
            <Typography variant="body2">
              {TIERS.find((t) => t.id === tier)?.name}
              {activity && activity.emails12m + activity.meetings12m > 0
                ? ` · ${activity.emails12m} emails and ${activity.meetings12m} meetings in the last 12 months`
                : ''}
              {activity?.typicalGapDays ? ` · ${describeGap(activity.typicalGapDays)}` : ''}
            </Typography>
            {activity && (
              <Typography variant="caption" component="div" sx={{ color: tokens.textSecondary }}>
                {[
                  activity.lastSentAt && `You wrote ${formatRelative(activity.lastSentAt)}`,
                  activity.lastReceivedAt &&
                    `they wrote ${formatRelative(activity.lastReceivedAt)}`,
                  activity.lastMeetingAt &&
                    `met ${formatRelative(activity.lastMeetingAt)}${activity.lastMeetingTitle ? ` (${activity.lastMeetingTitle})` : ''}`,
                  activity.nextMeetingAt &&
                    `next meeting ${formatRelative(activity.nextMeetingAt)}${activity.nextMeetingTitle ? ` (${activity.nextMeetingTitle})` : ''}`,
                ]
                  .filter(Boolean)
                  .join(' · ')}
              </Typography>
            )}
            {[
              ...awaiting.owe.map((a) => ['You owe a reply', a] as const),
              ...awaiting.waiting.map((a) => ['Waiting on them', a] as const),
            ].map(([what, a]) => (
              <Typography
                key={a.thread.threadId}
                variant="caption"
                component="div"
                dir="auto"
                sx={{ color: tokens.attention }}
              >
                {what}: “{a.thread.subject || 'no subject'}” ({a.daysSince} days)
              </Typography>
            ))}
          </Section>
        )}

        <Section title="Shared context">
          <Box sx={{ display: 'flex', gap: 0.75, flexWrap: 'wrap', alignItems: 'center' }}>
            {isFavourite(contact) && (
              <Chip
                size="small"
                icon={<Star sx={{ color: `${STAR_COLOR} !important` }} />}
                label="Favourite"
              />
            )}
            {labels.map((g) => (
              <Chip key={g.resourceName} size="small" label={g.name} variant="outlined" />
            ))}
            {segments.map((s) => (
              <Chip
                key={s.id}
                size="small"
                label={`Segment: ${s.name}`}
                variant="outlined"
                color="primary"
              />
            ))}
            {!isFavourite(contact) && !labels.length && !segments.length && (
              <Typography variant="body2" sx={{ color: tokens.textMuted }}>
                No labels or segments.
              </Typography>
            )}
          </Box>
          {colleagues.length > 0 && (
            <Box sx={{ mt: 1 }}>
              <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                Colleagues
              </Typography>
              <Box sx={{ display: 'flex', gap: 0.75, flexWrap: 'wrap', mt: 0.5 }}>
                {colleagues.map((c) => (
                  <Chip
                    key={c.contact.resourceName}
                    size="small"
                    avatar={<ContactAvatar contact={c.contact} size={20} />}
                    label={getDisplayName(c.contact)}
                    title={c.via}
                    onClick={() => data.onOpenCard(c.contact.resourceName)}
                  />
                ))}
              </Box>
            </Box>
          )}
          {(contact.relations ?? []).some((r) => r.person) && (
            <Typography variant="body2" dir="auto" sx={{ mt: 1, color: tokens.textSecondary }}>
              {(contact.relations ?? [])
                .filter((r) => r.person)
                .map((r) => `${r.type ? `${r.type}: ` : ''}${r.person}`)
                .join(' · ')}
            </Typography>
          )}
        </Section>
      </Collapse>
    </Box>
  );
};

export default Dossier;
