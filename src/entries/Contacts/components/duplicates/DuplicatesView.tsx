/**
 * @fileoverview Duplicate finder: groups of contacts that are probably the
 * same person, with why they matched (Story 4.7).
 * @module Contacts/components/duplicates/DuplicatesView
 */

import React, { useMemo, useState } from 'react';
import { Box, Button, Chip, Link, Paper, Typography } from '@mui/material';
import { Email, OpenInNew, Person, Phone } from '@mui/icons-material';
import type { Contact } from '../../types/Contact';
import type { DuplicateGroup, DuplicateReason } from '../../model/duplicates';
import ContactAvatar from '../common/ContactAvatar';
import {
  formatBirthday,
  getDisplayName,
  getOrganization,
  getPhoneticName,
} from '../../model/contactView';
import { tokens } from '../../theme/contactsTheme';

/** Props for DuplicatesView */
export interface DuplicatesViewProps {
  /** Duplicate groups */
  groups: DuplicateGroup[];
  /** Open a contact's card */
  onOpenCard: (resourceName: string) => void;
}

const PAGE = 40;

const REASON_META: Record<DuplicateReason, { label: string; icon: React.ReactElement }> = {
  phone: { label: 'Same phone', icon: <Phone sx={{ fontSize: 16 }} /> },
  email: { label: 'Same email', icon: <Email sx={{ fontSize: 16 }} /> },
  name: { label: 'Same name', icon: <Person sx={{ fontSize: 16 }} /> },
};

/**
 * One member of a group, as a compact column.
 * @param props - Contact and open handler
 * @returns Member element
 */
const Member: React.FC<{ contact: Contact; onOpen: () => void }> = ({ contact, onOpen }) => {
  const phonetic = getPhoneticName(contact);
  const lines = [
    ...(contact.phoneNumbers ?? []).map((p) => p.value),
    ...(contact.emailAddresses ?? []).map((e) => e.value),
    getOrganization(contact),
    formatBirthday(contact) && `Birthday ${formatBirthday(contact)}`,
  ].filter(Boolean);
  return (
    <Box
      sx={{
        flex: '1 1 220px',
        minWidth: 0,
        p: 1.5,
        borderRadius: '8px',
        border: `1px solid ${tokens.border}`,
        bgcolor: tokens.page,
        display: 'flex',
        flexDirection: 'column',
        gap: 0.75,
      }}
    >
      <Box sx={{ display: 'flex', gap: 1.25, alignItems: 'center', minWidth: 0 }}>
        <ContactAvatar contact={contact} size={36} />
        <Box sx={{ minWidth: 0 }}>
          <Typography variant="body1" noWrap dir="auto" align="left" sx={{ fontWeight: 600 }}>
            {getDisplayName(contact)}
          </Typography>
          <Typography
            variant="caption"
            noWrap
            dir="auto"
            align="left"
            sx={{ color: phonetic ? tokens.textSecondary : tokens.textMuted, display: 'block' }}
          >
            {phonetic || 'no phonetic name'}
          </Typography>
        </Box>
      </Box>
      <Box sx={{ display: 'flex', flexDirection: 'column', gap: 0.25 }}>
        {lines.slice(0, 5).map((l, i) => (
          <Typography
            key={i}
            variant="body2"
            noWrap
            dir="auto"
            align="left"
            sx={{ color: tokens.textSecondary }}
          >
            {l}
          </Typography>
        ))}
        {lines.length > 5 && (
          <Typography variant="caption" sx={{ color: tokens.textMuted }}>
            +{lines.length - 5} more
          </Typography>
        )}
      </Box>
      <Box sx={{ mt: 'auto' }}>
        <Button size="small" onClick={onOpen} sx={{ ml: -1 }}>
          Open card
        </Button>
      </Box>
    </Box>
  );
};

/**
 * Duplicates view.
 * @param props - Component props
 * @returns View element
 */
const DuplicatesView: React.FC<DuplicatesViewProps> = ({ groups, onOpenCard }) => {
  const [reason, setReason] = useState<DuplicateReason | 'all'>('all');
  const [shown, setShown] = useState(PAGE);

  const filtered = useMemo(
    () => (reason === 'all' ? groups : groups.filter((g) => g.reasons.includes(reason))),
    [groups, reason],
  );
  const count = (r: DuplicateReason) => groups.filter((g) => g.reasons.includes(r)).length;
  const contactCount = groups.reduce((s, g) => s + g.members.length, 0);

  return (
    <Box sx={{ p: { xs: 2, md: 3 }, overflowY: 'auto', flex: 1 }}>
      <Box
        sx={{
          display: 'flex',
          alignItems: 'flex-start',
          justifyContent: 'space-between',
          gap: 2,
          flexWrap: 'wrap',
          mb: 2,
        }}
      >
        <Box>
          <Typography variant="h1">Possible duplicates</Typography>
          <Typography variant="body2" sx={{ color: tokens.textSecondary, mt: 0.5, maxWidth: 640 }}>
            {groups.length
              ? `${groups.length} groups, ${contactCount} contacts. Contacts are grouped when they share a phone number (in any format), an email address, or the exact same name.`
              : 'No contacts share a phone number, an email address or a name.'}
          </Typography>
        </Box>
        <Link
          href="https://contacts.google.com/suggestions"
          target="_blank"
          rel="noopener noreferrer"
          underline="hover"
          sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.5, fontSize: 14, mt: 0.5 }}
        >
          Merge in Google Contacts <OpenInNew sx={{ fontSize: 15 }} />
        </Link>
      </Box>

      {groups.length > 0 && (
        <Box sx={{ display: 'flex', gap: 1, flexWrap: 'wrap', mb: 2 }}>
          <Chip
            label={`All ${groups.length}`}
            onClick={() => setReason('all')}
            color={reason === 'all' ? 'primary' : 'default'}
            variant={reason === 'all' ? 'filled' : 'outlined'}
          />
          {(Object.keys(REASON_META) as DuplicateReason[]).map((r) => (
            <Chip
              key={r}
              icon={REASON_META[r].icon}
              label={`${REASON_META[r].label} ${count(r)}`}
              onClick={() => setReason(r)}
              color={reason === r ? 'primary' : 'default'}
              variant={reason === r ? 'filled' : 'outlined'}
              disabled={count(r) === 0}
            />
          ))}
        </Box>
      )}

      <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1.5 }}>
        {filtered.slice(0, shown).map((group) => (
          <Paper
            key={group.id}
            variant="outlined"
            sx={{ p: 2, borderColor: tokens.border, bgcolor: tokens.surface }}
          >
            <Box sx={{ display: 'flex', gap: 1, alignItems: 'center', flexWrap: 'wrap', mb: 1.5 }}>
              {group.reasons.map((r) => (
                <Chip
                  key={r}
                  size="small"
                  icon={REASON_META[r].icon}
                  label={REASON_META[r].label}
                  sx={{ bgcolor: tokens.raised }}
                />
              ))}
              <Typography
                variant="caption"
                dir="auto"
                align="left"
                noWrap
                sx={{ color: tokens.textMuted, minWidth: 0 }}
              >
                {group.matchedValues.slice(0, 3).join(' · ')}
              </Typography>
            </Box>
            <Box sx={{ display: 'flex', gap: 1.5, flexWrap: 'wrap' }}>
              {group.members.map((m) => (
                <Member
                  key={m.resourceName}
                  contact={m}
                  onOpen={() => onOpenCard(m.resourceName)}
                />
              ))}
            </Box>
          </Paper>
        ))}
      </Box>

      {filtered.length > shown && (
        <Box sx={{ display: 'flex', justifyContent: 'center', mt: 2 }}>
          <Button onClick={() => setShown((s) => s + PAGE)}>
            Show more ({filtered.length - shown} left)
          </Button>
        </Box>
      )}
    </Box>
  );
};

export default DuplicatesView;
