/**
 * @fileoverview Insights: groups found in the contacts (AD-45 rule 9), each to save as a
 * segment or to create as a label
 * @module Contacts/components/insights/SuggestedSegments
 */

import React, { useMemo, useState } from 'react';
import { Box, Button, Chip, Paper, Typography } from '@mui/material';
import type { Contact, ContactGroup } from '../../types/Contact';
import {
  newSegmentId,
  suggestSegments,
  type Segment,
  type SegmentContext,
  type SuggestedSegment,
} from '../../model/segments';
import { useAppDispatch } from '../../types/hooks';
import { fetchContactGroups } from '../../redux/slices/contacts/contactsSlice';
import { changeMembership } from '../../redux/thunks/labelThunks';
import { backendApiClient } from '../../../../services/api/BackendApiClient';
import { notify } from '../common/notify';
import { tokens } from '../../theme/contactsTheme';

const KIND_NAMES: Record<SuggestedSegment['kind'], string> = {
  tier: 'By relationship (last 12 months of email and meetings)',
  organization: 'By organization',
  emailDomain: 'By work email domain',
  phoneCountry: 'By phone country',
  script: 'By name script',
};

const ORDER: SuggestedSegment['kind'][] = [
  'tier',
  'organization',
  'emailDomain',
  'phoneCountry',
  'script',
];

/**
 * The suggested segments card.
 * @param props - Contacts, context, saved segments, labels, actions
 * @returns Card
 */
const SuggestedSegments: React.FC<{
  contacts: Contact[];
  ctx: SegmentContext;
  segments: Segment[];
  groups: Record<string, ContactGroup>;
  onSave: (segment: Segment) => Promise<void>;
  onShow: (members: Contact[], title: string) => void;
}> = ({ contacts, ctx, segments, groups, onSave, onShow }) => {
  const dispatch = useAppDispatch();
  const [busy, setBusy] = useState<string | null>(null);
  const suggestions = useMemo(() => suggestSegments(contacts, ctx), [contacts, ctx]);
  // A suggestion already saved as a segment (same single rule) or as a label is hidden
  const saved = useMemo(
    () =>
      new Set(segments.filter((s) => s.rules.length === 1).map((s) => JSON.stringify(s.rules[0]))),
    [segments],
  );
  const labelNames = useMemo(
    () => new Set(Object.values(groups).map((g) => g.name.toLowerCase())),
    [groups],
  );
  const shown = suggestions.filter(
    (s) => !saved.has(JSON.stringify(s.rule)) && !labelNames.has(s.name.toLowerCase()),
  );
  if (!shown.length) {
    return null;
  }

  const saveSegment = async (s: SuggestedSegment) => {
    setBusy(s.key);
    try {
      await onSave({
        id: newSegmentId(),
        name: s.name,
        match: 'all',
        rules: [s.rule],
        label: null,
      });
      notify(`Segment “${s.name}” saved: choose it in Segments`);
    } catch (e) {
      notify(`Could not save “${s.name}”: ${(e as Error).message}`);
    } finally {
      setBusy(null);
    }
  };

  const createLabel = async (s: SuggestedSegment) => {
    setBusy(s.key);
    try {
      const g = await backendApiClient.createLabel(s.name);
      await dispatch(fetchContactGroups());
      const refused = await changeMembership(
        dispatch,
        s.members.map((m) => m.resourceName),
        g.resourceName,
        true,
      );
      notify(
        refused
          ? `Label “${s.name}” created, but its contacts were not added: ${refused}`
          : `Label “${s.name}” created with ${s.members.length} contacts`,
      );
    } catch (e) {
      notify(`Could not create “${s.name}”: ${(e as Error).message}`);
    } finally {
      setBusy(null);
    }
  };

  return (
    <Paper variant="outlined" sx={{ p: 2.5, borderColor: tokens.border, bgcolor: tokens.surface }}>
      <Typography variant="subtitle1">Suggested segments</Typography>
      <Typography variant="caption" sx={{ color: tokens.textMuted }}>
        Groups found in your contacts. Save one as a segment (it stays current) or create it as a
        Google label (it reaches your phones).
      </Typography>
      {ORDER.map((kind) => {
        const list = shown.filter((s) => s.kind === kind);
        return list.length ? (
          <Box key={kind} sx={{ mt: 2 }}>
            <Typography variant="subtitle2" sx={{ color: tokens.textSecondary, mb: 0.5 }}>
              {KIND_NAMES[kind]}
            </Typography>
            {list.map((s) => (
              <Box
                key={s.key}
                sx={{ display: 'flex', alignItems: 'center', gap: 1, py: 0.25, flexWrap: 'wrap' }}
              >
                <Chip
                  label={`${s.name} · ${s.members.length}`}
                  size="small"
                  onClick={() => onShow(s.members, s.name)}
                  sx={{ maxWidth: 320 }}
                />
                <Box sx={{ flex: 1 }} />
                <Button size="small" disabled={busy === s.key} onClick={() => void saveSegment(s)}>
                  Save as segment
                </Button>
                <Button size="small" disabled={busy === s.key} onClick={() => void createLabel(s)}>
                  Create label
                </Button>
              </Box>
            ))}
          </Box>
        ) : null;
      })}
    </Paper>
  );
};

export default SuggestedSegments;
