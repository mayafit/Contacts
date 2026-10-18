/**
 * @fileoverview Draft handling for the contact card: an editable copy of a
 * contact, and the patch of groups that actually changed.
 * @module Contacts/components/card/cardModel
 */

import type { Contact, ContactPatch, EditableGroup } from '../../types/Contact';
import { EDITABLE_GROUPS } from '../../types/Contact';
import { editableName } from '../../model/contactView';

/** Properties Google computes; they never count as an edit */
const READ_ONLY_KEYS = new Set(['displayName', 'canonicalForm']);

/**
 * Editable deep copy of a contact. Names get phonetic parts split out of the
 * full phonetic form so the card can show and edit them.
 * @param contact - Contact from the store
 * @returns Draft
 */
export const createDraft = (contact: Contact): Contact => {
  const draft: Contact = JSON.parse(JSON.stringify(contact));
  draft.names = [editableName(contact.names?.[0] ?? undefined)];
  return draft;
};

/**
 * Canonical form of a group for comparison: no empty strings, nulls, read-only
 * keys or entries left empty by them; object keys sorted.
 * @param value - Group value
 * @returns Comparable value
 */
export const normalizeGroup = (value: unknown): unknown => {
  if (Array.isArray(value)) {
    const items = value.map(normalizeGroup).filter((v) => v !== undefined);
    return items.length ? items : undefined;
  }
  if (value && typeof value === 'object') {
    const entries = Object.entries(value as Record<string, unknown>)
      .filter(([k]) => !READ_ONLY_KEYS.has(k))
      .map(([k, v]) => [k, normalizeGroup(v)] as const)
      .filter(([, v]) => v !== undefined)
      .sort(([a], [b]) => a.localeCompare(b));
    return entries.length ? Object.fromEntries(entries) : undefined;
  }
  if (typeof value === 'string') {
    const t = value.trim();
    return t ? t : undefined;
  }
  if (value === null || value === undefined || value === 0) {
    return undefined;
  }
  return value;
};

/**
 * Groups whose content differs between the original and the draft.
 * @param original - Contact as loaded
 * @param draft - Edited draft
 * @returns Changed groups
 */
export const changedGroups = (original: Contact, draft: Contact): EditableGroup[] => {
  const base = createDraft(original);
  return EDITABLE_GROUPS.filter(
    (g) => JSON.stringify(normalizeGroup(base[g])) !== JSON.stringify(normalizeGroup(draft[g])),
  );
};

/**
 * Problems that must be fixed before saving, or null.
 * A birthday with a day but no month (or the reverse) would otherwise be
 * rejected by the server rather than silently cleared.
 * @param draft - Edited draft
 * @returns Message or null
 */
export const validateDraft = (draft: Contact): string | null => {
  const date = draft.birthdays?.[0]?.date;
  const day = date?.day ?? 0;
  const month = date?.month ?? 0;
  if (day > 0 !== month > 0) {
    return 'Birthday needs both a day and a month (or neither).';
  }
  if (month > 12 || day > 31 || month < 0 || day < 0) {
    return 'Birthday day or month is out of range.';
  }
  return null;
};

/**
 * Patch with the given etag and the draft's value for every changed group.
 * A group emptied in the card is sent as [] so Google clears it.
 * @param original - Contact as loaded
 * @param draft - Edited draft
 * @param etag - Etag to send (the original's, or a fresh one when overwriting)
 * @returns Patch, or null when nothing changed
 */
export const buildPatch = (
  original: Contact,
  draft: Contact,
  etag?: string | null,
): ContactPatch | null => {
  const groups = changedGroups(original, draft);
  if (groups.length === 0) {
    return null;
  }
  const patch: ContactPatch = { etag: etag ?? original.etag };
  groups.forEach((g) => {
    (patch as Record<string, unknown>)[g] = draft[g] ?? [];
  });
  return patch;
};
