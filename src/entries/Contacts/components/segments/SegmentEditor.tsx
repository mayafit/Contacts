/**
 * @fileoverview Creating and editing a segment (AD-45 rules 3, 7, 8): its rules, all or
 * any, its live count, and the label it may be linked to
 * @module Contacts/components/segments/SegmentEditor
 */

import React, { useEffect, useMemo, useState } from 'react';
import {
  Alert,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  IconButton,
  MenuItem,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Tooltip,
  Typography,
} from '@mui/material';
import { Add, DeleteOutline } from '@mui/icons-material';
import type { Contact, ContactGroup } from '../../types/Contact';
import {
  MAX_RULES,
  TIERS,
  membersOf,
  needsActivity,
  newSegmentId,
  phoneCountriesOf,
  type Segment,
  type SegmentContext,
  type SegmentField,
  type SegmentRule,
} from '../../model/segments';
import { countryName } from '../../model/callingCodes';
import { userLabels } from '../card/LabelEditor';
import { tokens } from '../../theme/contactsTheme';

type Kind = SegmentRule['kind'];

const KINDS: { kind: Kind; name: string }[] = [
  { kind: 'favourite', name: 'Favourite' },
  { kind: 'label', name: 'Label' },
  { kind: 'organization', name: 'Organization' },
  { kind: 'emailDomain', name: 'Email domain' },
  { kind: 'phoneCountry', name: 'Phone country' },
  { kind: 'script', name: 'Name script' },
  { kind: 'field', name: 'Has / missing a field' },
  { kind: 'birthdayMonth', name: 'Birthday month' },
  { kind: 'lastTouch', name: 'Last in touch' },
  { kind: 'tier', name: 'Relationship tier' },
  { kind: 'text', name: 'Text' },
];

const FIELDS: { id: SegmentField; name: string }[] = [
  { id: 'phone', name: 'Phone' },
  { id: 'email', name: 'Email' },
  { id: 'phonetic', name: 'Phonetic name' },
  { id: 'birthday', name: 'Birthday' },
  { id: 'address', name: 'Address' },
  { id: 'organization', name: 'Organization' },
  { id: 'photo', name: 'Photo' },
  { id: 'website', name: 'Website' },
];

const MONTHS = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December',
];

/**
 * A new rule of a kind, with sensible values.
 * @param kind - Kind
 * @param labels - User labels
 * @param countries - Codes present
 * @returns Rule
 */
const defaultRule = (kind: Kind, labels: ContactGroup[], countries: string[]): SegmentRule => {
  switch (kind) {
    case 'label':
      return { kind, op: 'in', value: labels[0]?.resourceName ?? '' };
    case 'favourite':
      return { kind, value: true };
    case 'organization':
      return { kind, op: 'contains', value: '' };
    case 'emailDomain':
      return { kind, value: '' };
    case 'phoneCountry':
      return { kind, value: countries[0] ?? '972' };
    case 'script':
      return { kind, value: 'hebrew' };
    case 'field':
      return { kind, op: 'missing', value: 'phonetic' };
    case 'birthdayMonth':
      return { kind, value: new Date().getMonth() + 1 };
    case 'lastTouch':
      return { kind, op: 'olderThan', days: 365 };
    case 'tier':
      return { kind, value: 'inner' };
    default:
      return { kind: 'text', value: '' };
  }
};

const Pick: React.FC<{
  label: string;
  value: string | number;
  onChange: (v: string) => void;
  children: React.ReactNode;
  width?: number;
}> = ({ label, value, onChange, children, width = 150 }) => (
  <TextField
    select
    size="small"
    label={label}
    value={value}
    onChange={(e) => onChange(e.target.value)}
    sx={{ width }}
  >
    {children}
  </TextField>
);

/** One rule's controls */
const RuleRow: React.FC<{
  rule: SegmentRule;
  onChange: (r: SegmentRule) => void;
  labels: ContactGroup[];
  countries: string[];
}> = ({ rule, onChange, labels, countries }) => {
  const r = rule;
  switch (r.kind) {
    case 'label':
      return (
        <>
          <Pick
            label="Is"
            value={r.op}
            onChange={(v) => onChange({ ...r, op: v as 'in' })}
            width={120}
          >
            <MenuItem value="in">has</MenuItem>
            <MenuItem value="notIn">has not</MenuItem>
          </Pick>
          <Pick
            label="Label"
            value={r.value}
            onChange={(v) => onChange({ ...r, value: v })}
            width={180}
          >
            {labels.map((g) => (
              <MenuItem key={g.resourceName} value={g.resourceName}>
                {g.name}
              </MenuItem>
            ))}
          </Pick>
        </>
      );
    case 'favourite':
      return (
        <Pick
          label="Is"
          value={r.value ? 'yes' : 'no'}
          onChange={(v) => onChange({ ...r, value: v === 'yes' })}
        >
          <MenuItem value="yes">a favourite</MenuItem>
          <MenuItem value="no">not a favourite</MenuItem>
        </Pick>
      );
    case 'organization':
      return (
        <>
          <Pick
            label="How"
            value={r.op}
            onChange={(v) => onChange({ ...r, op: v as 'is' })}
            width={120}
          >
            <MenuItem value="is">is</MenuItem>
            <MenuItem value="contains">contains</MenuItem>
          </Pick>
          <TextField
            size="small"
            label="Organization"
            value={r.value}
            onChange={(e) => onChange({ ...r, value: e.target.value })}
            inputProps={{ dir: 'auto' }}
          />
        </>
      );
    case 'emailDomain':
      return (
        <TextField
          size="small"
          label="Domain (e.g. elta.co.il)"
          value={r.value}
          onChange={(e) => onChange({ ...r, value: e.target.value.replace(/^@/, '') })}
        />
      );
    case 'phoneCountry':
      return (
        <Pick
          label="Country"
          value={r.value}
          onChange={(v) => onChange({ ...r, value: v })}
          width={220}
        >
          {[...new Set([r.value, ...countries])].map((code) => (
            <MenuItem key={code} value={code}>
              {countryName(code)}
            </MenuItem>
          ))}
        </Pick>
      );
    case 'script':
      return (
        <Pick
          label="Name in"
          value={r.value}
          onChange={(v) => onChange({ ...r, value: v as 'hebrew' })}
        >
          <MenuItem value="hebrew">Hebrew</MenuItem>
          <MenuItem value="latin">English</MenuItem>
          <MenuItem value="mixed">Mixed</MenuItem>
        </Pick>
      );
    case 'field':
      return (
        <>
          <Pick
            label="How"
            value={r.op}
            onChange={(v) => onChange({ ...r, op: v as 'has' })}
            width={120}
          >
            <MenuItem value="has">has</MenuItem>
            <MenuItem value="missing">has no</MenuItem>
          </Pick>
          <Pick
            label="Field"
            value={r.value}
            onChange={(v) => onChange({ ...r, value: v as SegmentField })}
          >
            {FIELDS.map((f) => (
              <MenuItem key={f.id} value={f.id}>
                {f.name}
              </MenuItem>
            ))}
          </Pick>
        </>
      );
    case 'birthdayMonth':
      return (
        <Pick label="Month" value={r.value} onChange={(v) => onChange({ ...r, value: Number(v) })}>
          {MONTHS.map((m, i) => (
            <MenuItem key={m} value={i + 1}>
              {m}
            </MenuItem>
          ))}
        </Pick>
      );
    case 'lastTouch':
      return (
        <>
          <Pick
            label="When"
            value={r.op}
            onChange={(v) => onChange({ ...r, op: v as 'within' })}
            width={150}
          >
            <MenuItem value="within">within</MenuItem>
            <MenuItem value="olderThan">more than</MenuItem>
            <MenuItem value="never">none found</MenuItem>
          </Pick>
          {r.op !== 'never' && (
            <TextField
              size="small"
              type="number"
              label="Days"
              value={r.days ?? 0}
              onChange={(e) => onChange({ ...r, days: Math.max(0, Number(e.target.value) || 0) })}
              sx={{ width: 100 }}
            />
          )}
        </>
      );
    case 'tier':
      return (
        <Pick
          label="Tier"
          value={r.value}
          onChange={(v) => onChange({ ...r, value: v as 'inner' })}
          width={180}
        >
          {TIERS.map((t) => (
            <MenuItem key={t.id} value={t.id}>
              {t.name}
            </MenuItem>
          ))}
        </Pick>
      );
    case 'text':
      return (
        <TextField
          size="small"
          label="Matches"
          value={r.value}
          onChange={(e) => onChange({ ...r, value: e.target.value })}
          inputProps={{ dir: 'auto' }}
        />
      );
    default:
      return null;
  }
};

/** Props for SegmentEditor */
export interface SegmentEditorProps {
  /** The segment to edit, a new one's start, or null when closed */
  segment: Segment | null;
  contacts: Contact[];
  groups: Record<string, ContactGroup>;
  ctx: SegmentContext;
  onSave: (segment: Segment) => Promise<void>;
  onDelete?: (id: string) => Promise<void>;
  onClose: () => void;
}

/**
 * The segment editor dialog.
 * @param props - Component props
 * @returns Dialog
 */
const SegmentEditor: React.FC<SegmentEditorProps> = ({
  segment,
  contacts,
  groups,
  ctx,
  onSave,
  onDelete,
  onClose,
}) => {
  const [draft, setDraft] = useState<Segment | null>(segment);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    setDraft(segment);
    setError(null);
  }, [segment]);
  const labels = useMemo(() => userLabels(groups), [groups]);
  const countries = useMemo(() => {
    const counts = new Map<string, number>();
    contacts.forEach((c) =>
      phoneCountriesOf(c, ctx.mainCountry).forEach((code) =>
        counts.set(code, (counts.get(code) ?? 0) + 1),
      ),
    );
    return [...counts.entries()].sort((a, b) => b[1] - a[1]).map(([code]) => code);
  }, [contacts, ctx.mainCountry]);
  const count = useMemo(
    () => (draft ? membersOf(contacts, draft, ctx).length : 0),
    [draft, contacts, ctx],
  );
  if (!draft) {
    return null;
  }
  // A duplicate (a new id) is a new segment until saved
  const exists = !!segment && !!onDelete && segment.name !== '' && segment.id === draft.id;
  const set = (patch: Partial<Segment>) => setDraft({ ...draft, ...patch });

  const save = async () => {
    if (!draft.name.trim()) {
      setError('Give the segment a name');
      return;
    }
    setBusy(true);
    try {
      await onSave({ ...draft, name: draft.name.trim() });
      onClose();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open onClose={busy ? undefined : onClose} maxWidth="md" fullWidth>
      <DialogTitle>{exists ? 'Edit segment' : 'New segment'}</DialogTitle>
      <DialogContent>
        <Box sx={{ display: 'flex', gap: 2, alignItems: 'center', flexWrap: 'wrap', mt: 1 }}>
          <TextField
            size="small"
            label="Name"
            value={draft.name}
            onChange={(e) => set({ name: e.target.value })}
            autoFocus
            inputProps={{ dir: 'auto', maxLength: 100 }}
            sx={{ minWidth: 260 }}
          />
          <ToggleButtonGroup
            size="small"
            exclusive
            value={draft.match}
            onChange={(_, v) => v && set({ match: v })}
          >
            <ToggleButton value="all">All rules</ToggleButton>
            <ToggleButton value="any">Any rule</ToggleButton>
          </ToggleButtonGroup>
          <Typography variant="body2" sx={{ color: tokens.textSecondary }} aria-live="polite">
            {count} {count === 1 ? 'contact' : 'contacts'}
          </Typography>
        </Box>
        {needsActivity(draft) && !ctx.activity && (
          <Alert severity="info" sx={{ mt: 2 }}>
            Last-in-touch and tier rules need Gmail & Calendar (Insights); until then they match no
            one.
          </Alert>
        )}
        <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1.5, mt: 2 }}>
          {draft.rules.map((rule, i) => (
            <Box key={i} sx={{ display: 'flex', gap: 1, alignItems: 'center', flexWrap: 'wrap' }}>
              <Pick
                label="Rule"
                value={rule.kind}
                onChange={(k) =>
                  set({
                    rules: draft.rules.map((r, j) =>
                      j === i ? defaultRule(k as Kind, labels, countries) : r,
                    ),
                  })
                }
                width={200}
              >
                {KINDS.map((k) => (
                  <MenuItem key={k.kind} value={k.kind}>
                    {k.name}
                  </MenuItem>
                ))}
              </Pick>
              <RuleRow
                rule={rule}
                labels={labels}
                countries={countries}
                onChange={(next) => set({ rules: draft.rules.map((r, j) => (j === i ? next : r)) })}
              />
              <Tooltip title="Remove rule">
                <IconButton
                  aria-label="Remove rule"
                  onClick={() => set({ rules: draft.rules.filter((_, j) => j !== i) })}
                >
                  <DeleteOutline fontSize="small" />
                </IconButton>
              </Tooltip>
            </Box>
          ))}
          <Box>
            <Button
              size="small"
              startIcon={<Add />}
              disabled={draft.rules.length >= MAX_RULES}
              onClick={() =>
                set({ rules: [...draft.rules, defaultRule('favourite', labels, countries)] })
              }
            >
              Add rule
            </Button>
          </Box>
        </Box>
        <Box sx={{ mt: 3 }}>
          <Pick
            label="Keep a Google label like it"
            value={draft.label ?? ''}
            onChange={(v) => set({ label: v || null })}
            width={300}
          >
            <MenuItem value="">No label</MenuItem>
            {labels.map((g) => (
              <MenuItem key={g.resourceName} value={g.resourceName}>
                {g.name}
              </MenuItem>
            ))}
          </Pick>
          <Typography variant="caption" component="div" sx={{ color: tokens.textMuted, mt: 0.5 }}>
            A linked label shows when it differs from the segment; Update label makes them the same.
            Echo never changes it by itself.
          </Typography>
        </Box>
        {error && (
          <Alert severity="error" sx={{ mt: 2 }}>
            {error}
          </Alert>
        )}
      </DialogContent>
      <DialogActions>
        {exists && (
          <>
            <Button
              color="error"
              disabled={busy}
              onClick={async () => {
                setBusy(true);
                try {
                  await onDelete!(draft.id);
                  onClose();
                } catch (e) {
                  setError((e as Error).message);
                } finally {
                  setBusy(false);
                }
              }}
            >
              Delete
            </Button>
            <Button
              disabled={busy}
              onClick={() =>
                setDraft({
                  ...draft,
                  id: newSegmentId(),
                  name: `${draft.name} (copy)`,
                  label: null,
                })
              }
            >
              Duplicate
            </Button>
          </>
        )}
        <Box sx={{ flex: 1 }} />
        <Button color="inherit" onClick={onClose} disabled={busy}>
          Cancel
        </Button>
        <Button variant="contained" onClick={() => void save()} disabled={busy}>
          Save
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default SegmentEditor;
