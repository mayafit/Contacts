/**
 * @fileoverview Editable fields of the contact card.
 * @module Contacts/components/card/ContactForm
 */

import React from 'react';
import {
  Autocomplete,
  Box,
  Button,
  IconButton,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import { Add, DeleteOutline } from '@mui/icons-material';
import type {
  Contact,
  ContactAddress,
  ContactEmail,
  ContactName,
  ContactPhone,
  ContactRelation,
  ContactUrl,
  ContactUserDefined,
} from '../../types/Contact';
import { tokens } from '../../theme/contactsTheme';

/** Props for ContactForm */
export interface ContactFormProps {
  /** Draft being edited */
  draft: Contact;
  /** Replace the draft */
  onChange: (draft: Contact) => void;
  /** Disable inputs while saving */
  disabled?: boolean;
}

const PHONE_TYPES = ['mobile', 'home', 'work', 'main', 'workMobile', 'other'];
const EMAIL_TYPES = ['home', 'work', 'other'];
const ADDRESS_TYPES = ['home', 'work', 'other'];
const URL_TYPES = ['homePage', 'work', 'blog', 'profile', 'other'];
const RELATION_TYPES = [
  'spouse',
  'child',
  'mother',
  'father',
  'parent',
  'brother',
  'sister',
  'friend',
  'relative',
  'partner',
  'assistant',
  'manager',
];

/**
 * Section heading.
 * @param props - Title and optional hint
 * @returns Heading element
 */
const Section: React.FC<{
  title: string;
  hint?: string;
  children: React.ReactNode;
  accent?: boolean;
}> = ({ title, hint, children, accent }) => (
  <Box
    component="section"
    sx={{
      py: 2,
      borderTop: `1px solid ${tokens.border}`,
      ...(accent && { mx: -3, px: 3, bgcolor: 'rgba(57,135,229,0.06)' }),
    }}
  >
    <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 1, mb: 1.5 }}>
      <Typography variant="subtitle2" sx={{ color: accent ? tokens.accent : tokens.textSecondary }}>
        {title}
      </Typography>
      {hint && (
        <Typography variant="caption" sx={{ color: tokens.textMuted }}>
          {hint}
        </Typography>
      )}
    </Box>
    {children}
  </Box>
);

/**
 * Single text input bound to a value.
 * @param props - Label, value, change handler
 * @returns Input element
 */
const Field: React.FC<{
  label: string;
  value: string | null | undefined;
  onChange: (value: string) => void;
  disabled?: boolean;
  multiline?: boolean;
  type?: string;
  autoFocus?: boolean;
}> = ({ label, value, onChange, disabled, multiline, type, autoFocus }) => (
  <TextField
    label={label}
    value={value ?? ''}
    onChange={(e) => onChange(e.target.value)}
    size="small"
    fullWidth
    disabled={disabled}
    multiline={multiline}
    minRows={multiline ? 3 : undefined}
    type={type}
    autoFocus={autoFocus}
    inputProps={{ dir: 'auto' }}
  />
);

/**
 * Free-text type picker suggesting Google's standard types.
 * @param props - Options, value and change handler
 * @returns Autocomplete element
 */
const TypeField: React.FC<{
  options: string[];
  value: string | null | undefined;
  onChange: (value: string) => void;
  disabled?: boolean;
}> = ({ options, value, onChange, disabled }) => (
  <Autocomplete
    freeSolo
    options={options}
    value={value ?? ''}
    onInputChange={(_, v) => onChange(v)}
    disabled={disabled}
    size="small"
    sx={{ width: 130, flexShrink: 0 }}
    renderInput={(params) => <TextField {...params} label="Type" />}
  />
);

/**
 * Generic editor for a list group: one row per entry, add and remove.
 * @param props - Items, row renderer, factory, change handler
 * @returns List editor
 */
function ListEditor<T>({
  items,
  onChange,
  renderRow,
  create,
  addLabel,
  disabled,
}: {
  items: T[] | null | undefined;
  onChange: (items: T[]) => void;
  renderRow: (item: T, update: (item: T) => void) => React.ReactNode;
  create: () => T;
  addLabel: string;
  disabled?: boolean;
}) {
  const list = items ?? [];
  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1.25 }}>
      {list.map((item, i) => (
        <Box key={i} sx={{ display: 'flex', gap: 1, alignItems: 'flex-start' }}>
          <Box sx={{ flex: 1, display: 'flex', gap: 1, minWidth: 0, flexWrap: 'wrap' }}>
            {renderRow(item, (next) => onChange(list.map((x, j) => (j === i ? next : x))))}
          </Box>
          <Tooltip title="Remove">
            <span>
              <IconButton
                aria-label={`Remove ${addLabel.replace(/^Add /, '')}`}
                onClick={() => onChange(list.filter((_, j) => j !== i))}
                disabled={disabled}
                size="small"
                sx={{ mt: 0.5 }}
              >
                <DeleteOutline fontSize="small" />
              </IconButton>
            </span>
          </Tooltip>
        </Box>
      ))}
      <Box>
        <Button
          size="small"
          startIcon={<Add />}
          onClick={() => onChange([...list, create()])}
          disabled={disabled}
        >
          {addLabel}
        </Button>
      </Box>
    </Box>
  );
}

const grid2 = {
  display: 'grid',
  gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr' },
  gap: 1.25,
} as const;

/**
 * Contact card form.
 * @param props - Draft, change handler, disabled flag
 * @returns Form element
 */
const ContactForm: React.FC<ContactFormProps> = ({ draft, onChange, disabled }) => {
  const name: ContactName = draft.names?.[0] ?? {};
  const setName = (patch: Partial<ContactName>) =>
    onChange({ ...draft, names: [{ ...name, ...patch }] });
  const set = <K extends keyof Contact>(key: K, value: Contact[K]) =>
    onChange({ ...draft, [key]: value });
  const org = draft.organizations?.[0] ?? {};
  const setOrg = (patch: Record<string, string>) =>
    set('organizations', [{ ...org, ...patch }, ...(draft.organizations ?? []).slice(1)]);
  const birthday = draft.birthdays?.[0];
  const date = birthday?.date ?? {};
  const setDate = (part: 'day' | 'month' | 'year', text: string) => {
    const n = parseInt(text, 10);
    set('birthdays', [{ ...birthday, date: { ...date, [part]: Number.isFinite(n) ? n : null } }]);
  };

  return (
    <Box>
      <Section title="Name">
        <Box sx={grid2}>
          <Field
            label="First name"
            value={name.givenName}
            onChange={(v) => setName({ givenName: v })}
            disabled={disabled}
          />
          <Field
            label="Last name"
            value={name.familyName}
            onChange={(v) => setName({ familyName: v })}
            disabled={disabled}
          />
          <Field
            label="Middle name"
            value={name.middleName}
            onChange={(v) => setName({ middleName: v })}
            disabled={disabled}
          />
          <Field
            label="Nickname"
            value={draft.nicknames?.[0]?.value}
            onChange={(v) => set('nicknames', [{ value: v }, ...(draft.nicknames ?? []).slice(1)])}
            disabled={disabled}
          />
          <Field
            label="Prefix"
            value={name.honorificPrefix}
            onChange={(v) => setName({ honorificPrefix: v })}
            disabled={disabled}
          />
          <Field
            label="Suffix"
            value={name.honorificSuffix}
            onChange={(v) => setName({ honorificSuffix: v })}
            disabled={disabled}
          />
        </Box>
      </Section>

      <Section
        title="Phonetic name"
        hint="How the name is pronounced; used for sorting and voice"
        accent
      >
        <Box sx={grid2}>
          <Field
            label="Phonetic first name"
            value={name.phoneticGivenName}
            onChange={(v) => setName({ phoneticGivenName: v, phoneticFullName: null })}
            disabled={disabled}
          />
          <Field
            label="Phonetic last name"
            value={name.phoneticFamilyName}
            onChange={(v) => setName({ phoneticFamilyName: v, phoneticFullName: null })}
            disabled={disabled}
          />
          <Field
            label="Phonetic middle name"
            value={name.phoneticMiddleName}
            onChange={(v) => setName({ phoneticMiddleName: v, phoneticFullName: null })}
            disabled={disabled}
          />
        </Box>
      </Section>

      <Section title="Phone">
        <ListEditor<ContactPhone>
          items={draft.phoneNumbers}
          onChange={(v) => set('phoneNumbers', v)}
          create={() => ({ value: '', type: 'mobile' })}
          addLabel="Add phone"
          disabled={disabled}
          renderRow={(p, update) => (
            <>
              <TypeField
                options={PHONE_TYPES}
                value={p.type}
                onChange={(type) => update({ ...p, type })}
                disabled={disabled}
              />
              <Box sx={{ flex: 1, minWidth: 160 }}>
                <Field
                  label="Phone"
                  value={p.value}
                  onChange={(value) => update({ ...p, value, canonicalForm: null })}
                  disabled={disabled}
                />
              </Box>
            </>
          )}
        />
      </Section>

      <Section title="Email">
        <ListEditor<ContactEmail>
          items={draft.emailAddresses}
          onChange={(v) => set('emailAddresses', v)}
          create={() => ({ value: '', type: 'home' })}
          addLabel="Add email"
          disabled={disabled}
          renderRow={(e, update) => (
            <>
              <TypeField
                options={EMAIL_TYPES}
                value={e.type}
                onChange={(type) => update({ ...e, type })}
                disabled={disabled}
              />
              <Box sx={{ flex: 1, minWidth: 160 }}>
                <Field
                  label="Email"
                  type="email"
                  value={e.value}
                  onChange={(value) => update({ ...e, value })}
                  disabled={disabled}
                />
              </Box>
            </>
          )}
        />
      </Section>

      <Section title="Work">
        <Box sx={grid2}>
          <Field
            label="Company"
            value={org.name}
            onChange={(v) => setOrg({ name: v })}
            disabled={disabled}
          />
          <Field
            label="Job title"
            value={org.title}
            onChange={(v) => setOrg({ title: v })}
            disabled={disabled}
          />
          <Field
            label="Department"
            value={org.department}
            onChange={(v) => setOrg({ department: v })}
            disabled={disabled}
          />
        </Box>
      </Section>

      <Section title="Birthday">
        {birthday?.text && !date.month ? (
          <Field
            label="Birthday"
            value={birthday.text}
            onChange={(v) => set('birthdays', [{ text: v }])}
            disabled={disabled}
          />
        ) : (
          <Box
            sx={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1.4fr', gap: 1.25, maxWidth: 320 }}
          >
            <Field
              label="Day"
              type="number"
              value={date.day ? String(date.day) : ''}
              onChange={(v) => setDate('day', v)}
              disabled={disabled}
            />
            <Field
              label="Month"
              type="number"
              value={date.month ? String(date.month) : ''}
              onChange={(v) => setDate('month', v)}
              disabled={disabled}
            />
            <Field
              label="Year"
              type="number"
              value={date.year ? String(date.year) : ''}
              onChange={(v) => setDate('year', v)}
              disabled={disabled}
            />
          </Box>
        )}
      </Section>

      <Section title="Address">
        <ListEditor<ContactAddress>
          items={draft.addresses}
          onChange={(v) => set('addresses', v)}
          create={() => ({ type: 'home' })}
          addLabel="Add address"
          disabled={disabled}
          renderRow={(a, update) => {
            const structured = !!(
              a.streetAddress ||
              a.city ||
              a.postalCode ||
              a.country ||
              !a.formattedValue
            );
            return (
              <Box sx={{ display: 'flex', flexDirection: 'column', gap: 1.25, width: '100%' }}>
                <TypeField
                  options={ADDRESS_TYPES}
                  value={a.type}
                  onChange={(type) => update({ ...a, type })}
                  disabled={disabled}
                />
                {structured ? (
                  <Box sx={grid2}>
                    <Field
                      label="Street"
                      value={a.streetAddress}
                      onChange={(v) => update({ ...a, streetAddress: v })}
                      disabled={disabled}
                    />
                    <Field
                      label="City"
                      value={a.city}
                      onChange={(v) => update({ ...a, city: v })}
                      disabled={disabled}
                    />
                    <Field
                      label="Postal code"
                      value={a.postalCode}
                      onChange={(v) => update({ ...a, postalCode: v })}
                      disabled={disabled}
                    />
                    <Field
                      label="Country"
                      value={a.country}
                      onChange={(v) => update({ ...a, country: v })}
                      disabled={disabled}
                    />
                  </Box>
                ) : (
                  <Field
                    label="Address"
                    value={a.formattedValue}
                    onChange={(v) => update({ ...a, formattedValue: v })}
                    disabled={disabled}
                    multiline
                  />
                )}
              </Box>
            );
          }}
        />
      </Section>

      <Section title="Websites">
        <ListEditor<ContactUrl>
          items={draft.urls}
          onChange={(v) => set('urls', v)}
          create={() => ({ value: '', type: 'homePage' })}
          addLabel="Add website"
          disabled={disabled}
          renderRow={(u, update) => (
            <>
              <TypeField
                options={URL_TYPES}
                value={u.type}
                onChange={(type) => update({ ...u, type })}
                disabled={disabled}
              />
              <Box sx={{ flex: 1, minWidth: 160 }}>
                <Field
                  label="URL"
                  value={u.value}
                  onChange={(value) => update({ ...u, value })}
                  disabled={disabled}
                />
              </Box>
            </>
          )}
        />
      </Section>

      <Section title="Relations">
        <ListEditor<ContactRelation>
          items={draft.relations}
          onChange={(v) => set('relations', v)}
          create={() => ({ person: '', type: 'spouse' })}
          addLabel="Add relation"
          disabled={disabled}
          renderRow={(r, update) => (
            <>
              <TypeField
                options={RELATION_TYPES}
                value={r.type}
                onChange={(type) => update({ ...r, type })}
                disabled={disabled}
              />
              <Box sx={{ flex: 1, minWidth: 160 }}>
                <Field
                  label="Name"
                  value={r.person}
                  onChange={(person) => update({ ...r, person })}
                  disabled={disabled}
                />
              </Box>
            </>
          )}
        />
      </Section>

      <Section title="Custom fields">
        <ListEditor<ContactUserDefined>
          items={draft.userDefined}
          onChange={(v) => set('userDefined', v)}
          create={() => ({ key: '', value: '' })}
          addLabel="Add field"
          disabled={disabled}
          renderRow={(f, update) => (
            <>
              <Box sx={{ width: 150, flexShrink: 0 }}>
                <Field
                  label="Label"
                  value={f.key}
                  onChange={(key) => update({ ...f, key })}
                  disabled={disabled}
                />
              </Box>
              <Box sx={{ flex: 1, minWidth: 140 }}>
                <Field
                  label="Value"
                  value={f.value}
                  onChange={(value) => update({ ...f, value })}
                  disabled={disabled}
                />
              </Box>
            </>
          )}
        />
      </Section>

      <Section title="Notes">
        <Field
          label="Notes"
          value={draft.biographies?.[0]?.value}
          onChange={(v) => set('biographies', [{ ...(draft.biographies?.[0] ?? {}), value: v }])}
          disabled={disabled}
          multiline
        />
      </Section>
    </Box>
  );
};

export default ContactForm;
