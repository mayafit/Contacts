/**
 * @fileoverview Insights: keeping in touch (Gmail + Calendar activity)
 * @module Contacts/components/insights/ActivitySection
 */

import React, { useMemo } from 'react';
import {
  Alert,
  Box,
  Button,
  LinearProgress,
  Link,
  Paper,
  Tooltip,
  Typography,
} from '@mui/material';
import { ArrowForward, MailOutline, Event, Refresh, PersonAddAlt } from '@mui/icons-material';
import type { Contact } from '../../types/Contact';
import type { ActivityState } from '../../hooks/useActivity';
import {
  correspondentsNotInContacts,
  formatRelative,
  goneQuiet,
  mostInTouch,
  neverInTouch,
  type ActivityIndex,
  type RankedContact,
} from '../../model/activity';
import { getDisplayName } from '../../model/contactView';
import ContactAvatar from '../common/ContactAvatar';
import { tokens } from '../../theme/contactsTheme';
import { fs } from '../../settings/uiSettings';

/** Props for ActivitySection */
export interface ActivitySectionProps {
  /** Activity state from useActivity */
  activity: ActivityState;
  /** Active contacts (not in Trash) */
  contacts: Contact[];
  /** All contacts, Trash included */
  allContacts: Contact[];
  /** Activity per contact */
  index?: ActivityIndex;
  /** Open a contact card */
  onOpenCard: (resourceName: string) => void;
  /** Show a set of contacts in the table */
  onShowContacts: (resourceNames: string[], title: string) => void;
  /** Create a contact for an email address */
  onAddContact: (email: string) => void;
}

const fmt = new Intl.NumberFormat('en-US');

const PHASES: Record<string, string> = {
  queued: 'Starting…',
  contacts: 'Reading your contacts',
  gmail: 'Reading Gmail (senders and recipients only)',
  calendar: 'Reading Google Calendar',
};

/** Renders text with a console link turned into a link */
const WithLinks: React.FC<{ text: string }> = ({ text }) => {
  const parts = text.split(/(https:\/\/\S+)/g);
  return (
    <>
      {parts.map((p, i) =>
        p.startsWith('https://') ? (
          <Link key={i} href={p} target="_blank" rel="noopener noreferrer">
            Google Cloud console
          </Link>
        ) : (
          <React.Fragment key={i}>{p}</React.Fragment>
        ),
      )}
    </>
  );
};

/** One ranked contact row */
const PersonRow: React.FC<{
  item: RankedContact;
  detail: string;
  onOpen: () => void;
}> = ({ item, detail, onOpen }) => (
  <Box
    role="button"
    tabIndex={0}
    onClick={onOpen}
    onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && onOpen()}
    sx={{
      display: 'flex',
      alignItems: 'center',
      gap: 1.25,
      py: 0.75,
      px: 1,
      borderRadius: '8px',
      cursor: 'var(--echo-hand, pointer)',
      '&:hover, &:focus-visible': { bgcolor: tokens.hover, outline: 'none' },
    }}
  >
    <ContactAvatar contact={item.contact} />
    <Box sx={{ minWidth: 0, flex: 1 }}>
      <Typography variant="body2" dir="auto" noWrap sx={{ fontWeight: 600, textAlign: 'left' }}>
        {getDisplayName(item.contact)}
      </Typography>
      <Typography variant="caption" noWrap sx={{ color: tokens.textMuted, display: 'block' }}>
        {detail}
      </Typography>
    </Box>
  </Box>
);

const plural = (n: number, word: string) => `${fmt.format(n)} ${word}${n === 1 ? '' : 's'}`;

/**
 * Keeping in touch: connect, scan progress, and the rankings.
 * @param props - Component props
 * @returns Section element
 */
const ActivitySection: React.FC<ActivitySectionProps> = ({
  activity,
  contacts,
  allContacts,
  index,
  onOpenCard,
  onShowContacts,
  onAddContact,
}) => {
  const { status, people, connected } = activity;
  const ranked = useMemo(() => {
    if (!index || !people) {
      return null;
    }
    return {
      top: mostInTouch(contacts, index, 8),
      quiet: goneQuiet(contacts, index),
      never: neverInTouch(contacts, index),
      inTouch: contacts.filter((c) => (index.get(c.resourceName)?.score ?? 0) > 0),
      strangers: correspondentsNotInContacts(people, allContacts, 8),
    };
  }, [index, people, contacts, allContacts]);

  if (!status) {
    return null;
  }

  const running = status.status === 'running';
  const card = (children: React.ReactNode) => (
    <Paper
      variant="outlined"
      sx={{ p: 2.5, mb: 2, borderColor: tokens.border, bgcolor: tokens.surface }}
    >
      <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 1, flexWrap: 'wrap', mb: 1.5 }}>
        <Typography variant="subtitle1">Keeping in touch</Typography>
        {connected && status.updatedAt && !running && (
          <Typography variant="caption" sx={{ color: tokens.textMuted }}>
            Updated {formatRelative(status.updatedAt)}
            {status.since
              ? ` · email since ${new Date(status.since).toLocaleDateString('en-GB', { month: 'short', year: 'numeric' })}`
              : ''}
          </Typography>
        )}
        <Box sx={{ flex: 1 }} />
        {connected && !running && (
          <>
            <Button size="small" startIcon={<Refresh />} onClick={() => activity.refresh()}>
              Refresh
            </Button>
            <Tooltip title="Delete the stored activity. Google access stays until you remove it in your Google account.">
              <Button size="small" color="inherit" onClick={() => activity.forget()}>
                Forget
              </Button>
            </Tooltip>
          </>
        )}
      </Box>
      {children}
    </Paper>
  );

  if (!connected) {
    return card(
      <Box sx={{ display: 'flex', gap: 2, alignItems: 'center', flexWrap: 'wrap' }}>
        <Box sx={{ flex: 1, minWidth: 260 }}>
          <Typography variant="body2" sx={{ color: tokens.textSecondary }}>
            See when you last emailed or met each contact, who you are most in touch with, who has
            gone quiet, and which emails wait for a reply. From Gmail the app reads senders,
            recipients, dates and subjects, never message content; from Google Calendar, meeting
            times, titles and guests.
          </Typography>
        </Box>
        <Button variant="contained" onClick={activity.connect} startIcon={<MailOutline />}>
          Connect Gmail & Calendar
        </Button>
      </Box>,
    );
  }

  return card(
    <>
      {running && (
        <Box sx={{ mb: 2 }}>
          <Typography variant="body2" sx={{ color: tokens.textSecondary, mb: 1 }}>
            {PHASES[status.progress?.phase ?? 'queued'] ?? 'Working…'}
            {status.progress?.done ? ` · ${fmt.format(status.progress.done)}` : ''}
            {status.progress?.phase === 'gmail' ? ' messages' : ''}
            {status.progress?.phase === 'calendar' ? ' events' : ''}
          </Typography>
          <LinearProgress aria-label="Reading activity" />
          <Typography variant="caption" sx={{ color: tokens.textMuted, display: 'block', mt: 1 }}>
            The first scan reads a year of email and can take a few minutes. You can keep working.
          </Typography>
        </Box>
      )}
      {status.status === 'error' && status.error && (
        <Alert
          severity={status.errorCode === 'NOT_CONNECTED' ? 'warning' : 'error'}
          sx={{ mb: 2 }}
          action={
            status.errorCode === 'NOT_CONNECTED' ? (
              <Button color="inherit" size="small" onClick={activity.connect}>
                Connect
              </Button>
            ) : (
              <Button color="inherit" size="small" onClick={() => activity.refresh()}>
                Retry
              </Button>
            )
          }
        >
          <WithLinks text={status.error} />
        </Alert>
      )}
      {activity.error && (
        <Alert severity="error" sx={{ mb: 2 }}>
          {activity.error}
        </Alert>
      )}
      {ranked && (
        <>
          <Box
            sx={{
              display: 'grid',
              gridTemplateColumns: { xs: '1fr 1fr', md: 'repeat(3, 1fr)' },
              gap: 1.5,
              mb: 2,
            }}
          >
            {[
              {
                label: 'In touch this year',
                value: ranked.inTouch.length,
                ids: ranked.inTouch.map((c) => c.resourceName),
                title: 'In touch in the last 12 months',
              },
              {
                label: 'Gone quiet',
                value: ranked.quiet.length,
                ids: ranked.quiet.map((r) => r.contact.resourceName),
                title: 'Gone quiet (nothing for 6 months)',
              },
              {
                label: 'None found',
                value: ranked.never.length,
                ids: ranked.never.map((c) => c.resourceName),
                title: 'No email or meeting in the scan period (Settings)',
              },
            ].map((t) => (
              <Box
                key={t.label}
                sx={{
                  p: 1.5,
                  borderRadius: '10px',
                  bgcolor: tokens.page,
                  border: `1px solid ${tokens.border}`,
                }}
              >
                <Typography variant="caption" sx={{ color: tokens.textSecondary }}>
                  {t.label}
                </Typography>
                <Typography sx={{ fontSize: fs(26), fontWeight: 600, lineHeight: 1.2 }}>
                  {fmt.format(t.value)}
                </Typography>
                {t.value > 0 && (
                  <Button
                    size="small"
                    endIcon={<ArrowForward />}
                    sx={{ ml: -1 }}
                    onClick={() => onShowContacts(t.ids, t.title)}
                  >
                    Show
                  </Button>
                )}
              </Box>
            ))}
          </Box>

          <Box
            sx={{
              display: 'grid',
              gridTemplateColumns: { xs: '1fr', lg: 'repeat(3, 1fr)' },
              gap: 2,
            }}
          >
            <Box>
              <Typography variant="subtitle2" sx={{ mb: 0.5 }}>
                Most in touch · 12 months
              </Typography>
              {ranked.top.length === 0 && (
                <Typography variant="body2" sx={{ color: tokens.textMuted }}>
                  No emails or meetings with your contacts yet.
                </Typography>
              )}
              {ranked.top.map((r) => (
                <PersonRow
                  key={r.contact.resourceName}
                  item={r}
                  onOpen={() => onOpenCard(r.contact.resourceName)}
                  detail={[
                    r.activity.emails12m ? plural(r.activity.emails12m, 'email') : '',
                    r.activity.meetings12m ? plural(r.activity.meetings12m, 'meeting') : '',
                    `last ${formatRelative(r.activity.lastInTouch)}`,
                  ]
                    .filter(Boolean)
                    .join(' · ')}
                />
              ))}
            </Box>
            <Box>
              <Typography variant="subtitle2" sx={{ mb: 0.5 }}>
                Gone quiet
              </Typography>
              {ranked.quiet.length === 0 && (
                <Typography variant="body2" sx={{ color: tokens.textMuted }}>
                  Nobody you were in touch with has gone quiet for 6 months.
                </Typography>
              )}
              {ranked.quiet.slice(0, 8).map((r) => (
                <PersonRow
                  key={r.contact.resourceName}
                  item={r}
                  onOpen={() => onOpenCard(r.contact.resourceName)}
                  detail={`last ${formatRelative(r.activity.lastInTouch)} · ${plural(
                    r.activity.sent + r.activity.received,
                    'email',
                  )} before`}
                />
              ))}
            </Box>
            <Box>
              <Typography variant="subtitle2" sx={{ mb: 0.5 }}>
                You write to them, but they are not contacts
              </Typography>
              {ranked.strangers.length === 0 && (
                <Typography variant="body2" sx={{ color: tokens.textMuted }}>
                  Everyone you write to regularly is in your contacts.
                </Typography>
              )}
              {ranked.strangers.map((s) => (
                <Box
                  key={s.email}
                  sx={{ display: 'flex', alignItems: 'center', gap: 1, py: 0.75, px: 1 }}
                >
                  <Box sx={{ minWidth: 0, flex: 1 }}>
                    <Typography variant="body2" noWrap sx={{ fontWeight: 600 }}>
                      {s.email}
                    </Typography>
                    <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                      {plural(s.sent, 'email')} sent · last {formatRelative(s.lastEmailAt)}
                    </Typography>
                  </Box>
                  <Tooltip title="Add as a contact">
                    <Button
                      size="small"
                      startIcon={<PersonAddAlt />}
                      onClick={() => onAddContact(s.email)}
                    >
                      Add
                    </Button>
                  </Tooltip>
                </Box>
              ))}
            </Box>
          </Box>
          <Typography
            variant="caption"
            sx={{
              color: tokens.textMuted,
              display: 'flex',
              gap: 0.5,
              alignItems: 'center',
              mt: 1.5,
            }}
          >
            <Event sx={{ fontSize: fs(15) }} /> A meeting counts as three emails. Events with more
            than 20 guests are not counted.
          </Typography>
        </>
      )}
    </>,
  );
};

export default ActivitySection;
