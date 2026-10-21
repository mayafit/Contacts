/**
 * @fileoverview Insights: who to contact today and why, and emails waiting for a reply
 * @module Contacts/components/insights/ReachOutSection
 */

import React, { useMemo, useState } from 'react';
import { Box, Button, Chip, Link, Paper, Tooltip, Typography } from '@mui/material';
import {
  AlternateEmail,
  AutoAwesome,
  Cake,
  Check,
  Event,
  HourglassBottom,
  MarkEmailUnread,
  OpenInNew,
  Schedule,
} from '@mui/icons-material';
import type { Contact } from '../../types/Contact';
import type { ThreadState } from '../../../../services/api/BackendApiClient';
import {
  awaitingReply,
  buildReachOut,
  contactsByEmail,
  gmailThreadUrl,
  type ReachOutItem,
  type ReasonKind,
} from '../../model/reachOut';
import type { ActivityIndex } from '../../model/activity';
import { getDisplayName } from '../../model/contactView';
import ContactAvatar from '../common/ContactAvatar';
import ContactActions from '../device/ContactActions';
import { tokens } from '../../theme/contactsTheme';

/** Props for ReachOutSection */
export interface ReachOutSectionProps {
  contacts: Contact[];
  index?: ActivityIndex;
  threads: ThreadState[];
  /** Gmail & Calendar connected */
  connected: boolean;
  onOpenCard: (resourceName: string) => void;
  /** Ask the assistant to draft a message */
  onDraft: (prompt: string) => void;
  /** Open the suggested-emails review */
  onSuggestEmails: () => void;
  /** Contacts without an email address */
  withoutEmail: number;
}

const ICONS: Record<ReasonKind, React.ReactNode> = {
  oweReply: <MarkEmailUnread sx={{ fontSize: 16 }} />,
  waiting: <HourglassBottom sx={{ fontSize: 16 }} />,
  birthday: <Cake sx={{ fontSize: 16 }} />,
  meetingSoon: <Event sx={{ fontSize: 16 }} />,
  overdue: <Schedule sx={{ fontSize: 16 }} />,
};

const DISMISS_KEY = 'contacts.reachOutDismissed';
const DISMISS_DAYS = 7;

/** Dismissed "resourceName:kind" keys that have not expired */
const loadDismissed = (): Record<string, number> => {
  try {
    const all = JSON.parse(localStorage.getItem(DISMISS_KEY) ?? '{}') as Record<string, number>;
    const now = Date.now();
    return Object.fromEntries(Object.entries(all).filter(([, until]) => until > now));
  } catch {
    return {};
  }
};

/**
 * Prompt for the assistant to draft a message to a contact.
 * @param item - Reach-out item
 * @returns Prompt
 */
const draftPrompt = (item: ReachOutItem): string =>
  `Draft a short, warm message to [${getDisplayName(item.contact)}](contact:${item.contact.resourceName}). ` +
  `Context: ${item.reasons.map((r) => r.text).join('; ')}. ` +
  'Look at their contact details first, write it in the language I would use with them, and offer it with contact_person.';

/**
 * Reach out today, and replies waiting.
 * @param props - Component props
 * @returns Section element
 */
const ReachOutSection: React.FC<ReachOutSectionProps> = ({
  contacts,
  index,
  threads,
  connected,
  onOpenCard,
  onDraft,
  onSuggestEmails,
  withoutEmail,
}) => {
  const [dismissed, setDismissed] = useState<Record<string, number>>(loadDismissed);
  const [all, setAll] = useState(false);
  const items = useMemo(
    () => buildReachOut(contacts, index, threads, new Date(), new Set(Object.keys(dismissed))),
    [contacts, index, threads, dismissed],
  );
  const awaiting = useMemo(
    () => awaitingReply(threads, contactsByEmail(contacts)),
    [threads, contacts],
  );

  const done = (item: ReachOutItem) => {
    const until = Date.now() + DISMISS_DAYS * 86_400_000;
    const next = { ...dismissed };
    item.reasons.forEach((r) => (next[`${item.contact.resourceName}:${r.kind}`] = until));
    setDismissed(next);
    try {
      localStorage.setItem(DISMISS_KEY, JSON.stringify(next));
    } catch {
      // Not persisted (private mode): dismissed for this visit only
    }
  };

  const shown = all ? items : items.slice(0, 8);

  return (
    <>
      <Paper
        variant="outlined"
        sx={{ p: 2.5, mb: 2, borderColor: tokens.border, bgcolor: tokens.surface }}
      >
        <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 1, mb: 1, flexWrap: 'wrap' }}>
          <Typography variant="subtitle1">Reach out today</Typography>
          <Typography variant="caption" sx={{ color: tokens.textMuted }}>
            {items.length ? `${items.length} people` : 'Nobody needs you today'}
          </Typography>
          <Box sx={{ flex: 1 }} />
          {connected && withoutEmail > 0 && (
            <Button size="small" startIcon={<AlternateEmail />} onClick={onSuggestEmails}>
              Find emails for contacts
            </Button>
          )}
        </Box>
        {!connected && (
          <Typography variant="body2" sx={{ color: tokens.textMuted, mb: 1 }}>
            Showing birthdays only. Connect Gmail & Calendar below to add replies you owe, people
            overdue for a catch-up, and upcoming meetings.
          </Typography>
        )}
        <Box sx={{ display: 'flex', flexDirection: 'column' }}>
          {shown.map((item) => (
            <Box
              key={item.contact.resourceName}
              sx={{
                display: 'flex',
                alignItems: 'center',
                gap: 1.25,
                py: 1,
                borderBottom: `1px solid ${tokens.border}`,
                flexWrap: { xs: 'wrap', md: 'nowrap' },
              }}
            >
              <Box
                role="button"
                tabIndex={0}
                onClick={() => onOpenCard(item.contact.resourceName)}
                onKeyDown={(e) => e.key === 'Enter' && onOpenCard(item.contact.resourceName)}
                sx={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: 1.25,
                  flex: 1,
                  minWidth: 0,
                  cursor: 'pointer',
                }}
              >
                <ContactAvatar contact={item.contact} />
                <Box sx={{ minWidth: 0 }}>
                  <Typography
                    variant="body2"
                    dir="auto"
                    noWrap
                    sx={{ fontWeight: 600, textAlign: 'left' }}
                  >
                    {getDisplayName(item.contact)}
                  </Typography>
                  {item.reasons.map((r) => (
                    <Typography
                      key={r.kind}
                      variant="caption"
                      dir="auto"
                      sx={{
                        display: 'flex',
                        alignItems: 'center',
                        gap: 0.5,
                        color: tokens.textSecondary,
                        textAlign: 'left',
                      }}
                    >
                      {ICONS[r.kind]} {r.text}
                    </Typography>
                  ))}
                </Box>
              </Box>
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.25, flexShrink: 0 }}>
                <ContactActions contact={item.contact} channels={['call', 'whatsapp', 'email']} />
                {item.reasons.find((r) => r.thread) && (
                  <Tooltip title="Open the email thread in Gmail">
                    <Button
                      size="small"
                      href={gmailThreadUrl(item.reasons.find((r) => r.thread)!.thread!.threadId)}
                      target="_blank"
                      rel="noopener noreferrer"
                      startIcon={<OpenInNew sx={{ fontSize: 16 }} />}
                    >
                      Gmail
                    </Button>
                  </Tooltip>
                )}
                <Tooltip title="Have the assistant draft a message">
                  <Button
                    size="small"
                    startIcon={<AutoAwesome sx={{ fontSize: 16 }} />}
                    onClick={() => onDraft(draftPrompt(item))}
                  >
                    Draft
                  </Button>
                </Tooltip>
                <Tooltip title={`Done – hide for ${DISMISS_DAYS} days`}>
                  <Button
                    size="small"
                    color="inherit"
                    startIcon={<Check sx={{ fontSize: 16 }} />}
                    onClick={() => done(item)}
                  >
                    Done
                  </Button>
                </Tooltip>
              </Box>
            </Box>
          ))}
        </Box>
        {items.length > 8 && (
          <Button size="small" sx={{ mt: 1 }} onClick={() => setAll((v) => !v)}>
            {all ? 'Show fewer' : `Show all ${items.length}`}
          </Button>
        )}
      </Paper>

      {connected && (awaiting.owe.length > 0 || awaiting.waiting.length > 0) && (
        <Paper
          variant="outlined"
          sx={{ p: 2.5, mb: 2, borderColor: tokens.border, bgcolor: tokens.surface }}
        >
          <Typography variant="subtitle1" sx={{ mb: 1 }}>
            Awaiting reply
          </Typography>
          <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '1fr 1fr' }, gap: 2 }}>
            {(
              [
                ['You owe a reply', awaiting.owe, 'They wrote last'],
                ['Waiting on them', awaiting.waiting, 'You wrote last, no answer yet'],
              ] as const
            ).map(([title, list, hint]) => (
              <Box key={title}>
                <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 1, mb: 0.5 }}>
                  <Typography variant="subtitle2">{title}</Typography>
                  <Chip size="small" label={list.length} />
                  <Typography variant="caption" sx={{ color: tokens.textMuted }}>
                    {hint}
                  </Typography>
                </Box>
                {list.slice(0, 8).map((a) => (
                  <Box
                    key={a.thread.threadId}
                    sx={{ display: 'flex', alignItems: 'center', gap: 1, py: 0.5 }}
                  >
                    <Box sx={{ minWidth: 0, flex: 1 }}>
                      <Typography
                        variant="body2"
                        dir="auto"
                        noWrap
                        sx={{ fontWeight: 600, textAlign: 'left' }}
                      >
                        {a.contact ? getDisplayName(a.contact) : a.email}
                      </Typography>
                      <Typography
                        variant="caption"
                        dir="auto"
                        noWrap
                        sx={{ display: 'block', color: tokens.textMuted, textAlign: 'left' }}
                      >
                        {a.daysSince} days · {a.thread.subject || '(no subject)'}
                      </Typography>
                    </Box>
                    <Link
                      href={gmailThreadUrl(a.thread.threadId)}
                      target="_blank"
                      rel="noopener noreferrer"
                      sx={{ fontSize: 13, flexShrink: 0 }}
                    >
                      Open
                    </Link>
                  </Box>
                ))}
              </Box>
            ))}
          </Box>
        </Paper>
      )}
    </>
  );
};

export default ReachOutSection;
