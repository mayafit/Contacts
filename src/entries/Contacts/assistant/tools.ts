/**
 * @fileoverview The assistant's tools: declarations for Gemini and their implementations,
 * which run in the browser over the contacts already loaded
 * @module Contacts/assistant/tools
 */

import type { Contact, ContactGroup } from '../types/Contact';
import type { ContactStats } from '../model/stats';
import type { DuplicateGroup } from '../model/duplicates';
import type { DomainInsight } from '../model/organizations';
import type { ActivityIndex } from '../model/activity';
import {
  clean,
  formatBirthday,
  getDisplayName,
  getJobTitle,
  getLabelNames,
  getOrganization,
  getPhoneticName,
  hasName,
  matchesQuery,
  scriptOf,
  searchTextOf,
} from '../model/contactView';
import {
  newContactPatch,
  proposalName,
  resolveChange,
  type ContactChange,
  type DiffLine,
  type FieldChanges,
  type ResolvedChange,
  type TypedValue,
} from './changes';
import { backendApiClient } from '../../../services/api/BackendApiClient';

/** A change waiting for the user's approval */
export type Proposal =
  | {
      kind: 'update';
      id: string;
      summary: string;
      items: ResolvedChange[];
      warnings: string[];
    }
  | {
      kind: 'create';
      id: string;
      summary: string;
      patch: import('../types/Contact').ContactPatch;
      labels: string[];
      diff: DiffLine[];
      warnings: string[];
    }
  | {
      kind: 'labels';
      id: string;
      summary: string;
      label: string;
      add: boolean;
      contacts: Contact[];
      warnings: string[];
    }
  | {
      kind: 'trash';
      id: string;
      summary: string;
      contacts: Contact[];
      warnings: string[];
    };

/** What the tools can see and do */
export interface AssistantContext {
  /** Contacts outside Trash */
  contacts: Contact[];
  /** Contacts in Trash */
  trashed: Contact[];
  groups: Record<string, ContactGroup>;
  stats: ContactStats;
  duplicateGroups: DuplicateGroup[];
  orgInsights: DomainInsight[];
  /** Activity per contact, when connected */
  activity?: ActivityIndex;
  /** Whether Gmail/Calendar activity is connected and loaded */
  activityReady: boolean;
  /** Signed-in user */
  user: { name?: string; email?: string };
  /** Open a contact card */
  openContact: (resourceName: string) => void;
  /** Show contacts in the table */
  showContacts: (resourceNames: string[], title: string) => void;
  /** Open the merge dialog */
  openMerge: (contacts: Contact[]) => void;
  /** Add a proposal for the user to review */
  propose: (proposal: Proposal) => void;
}

/** Results of earlier searches in the conversation, by id */
export type SearchMemory = Map<string, string[]>;

const FIELD_ENUM = [
  'name',
  'phone',
  'email',
  'phonetic',
  'organization',
  'jobTitle',
  'birthday',
  'address',
  'label',
  'notes',
  'photo',
];

const S = (description: string) => ({ type: 'string', description });
const ARR = (items: object, description: string) => ({ type: 'array', items, description });
const TYPED = {
  type: 'object',
  properties: { value: S('The value'), type: S('home, work, mobile, other...') },
  required: ['value'],
};

const SET_FIELDS = {
  type: 'object',
  description: 'Single-value fields to set. An empty string clears the field.',
  properties: {
    givenName: S('First name'),
    middleName: S('Middle name'),
    familyName: S('Last name'),
    phoneticGivenName: S(
      'Phonetic first name (how the name is pronounced, Latin letters for Hebrew names)',
    ),
    phoneticMiddleName: S('Phonetic middle name'),
    phoneticFamilyName: S('Phonetic last name'),
    nickname: S('Nickname'),
    organization: S('Organization / company name'),
    jobTitle: S('Job title'),
    department: S('Department'),
    birthday: S('YYYY-MM-DD, or --MM-DD when the year is unknown'),
    notes: S('Notes (replaces the existing notes)'),
  },
};

/** Gemini function declarations */
export const TOOL_DECLARATIONS = [
  {
    name: 'get_overview',
    description:
      'Statistics about the whole address book: totals, field completeness, phone origins, name scripts, labels with counts, duplicates, organizations, activity availability.',
    parameters: { type: 'object', properties: {} },
  },
  {
    name: 'search_contacts',
    description:
      'Find contacts. All filters combine (AND). Returns a page of compact contacts, the total, and a searchId that other tools accept to act on ALL matches.',
    parameters: {
      type: 'object',
      properties: {
        text: S(
          'Free text matched against names, phonetic names, phones, emails, organization, notes',
        ),
        label: S('Label name (case-insensitive)'),
        organization: S('Part of the organization name or of an email domain'),
        missing: ARR({ type: 'string', enum: FIELD_ENUM }, 'Fields the contact must NOT have'),
        has: ARR({ type: 'string', enum: FIELD_ENUM }, 'Fields the contact must have'),
        nameScript: {
          type: 'string',
          enum: ['hebrew', 'latin', 'mixed', 'other'],
          description: 'Script of the name',
        },
        lastInTouchBefore: S('YYYY-MM-DD: last email/meeting before this date (requires activity)'),
        lastInTouchAfter: S('YYYY-MM-DD: last email/meeting after this date (requires activity)'),
        neverInTouch: {
          type: 'boolean',
          description:
            'Only contacts with an email address but no email or meeting in the last year',
        },
        updatedAfter: S('YYYY-MM-DD: contact changed after this date'),
        sortBy: {
          type: 'string',
          enum: ['name', 'lastInTouch', 'mostInTouch', 'recentlyUpdated'],
          description: 'Order of results (default name)',
        },
        limit: { type: 'integer', description: 'Contacts to return, default 25, max 100' },
        inTrash: { type: 'boolean', description: 'Search the Trash instead of active contacts' },
      },
    },
  },
  {
    name: 'get_contact',
    description: 'Every field of one contact, plus its email/meeting activity.',
    parameters: {
      type: 'object',
      properties: { id: S('Contact id (resource name, people/...)') },
      required: ['id'],
    },
  },
  {
    name: 'find_duplicates',
    description: 'Groups of contacts that look like the same person (shared phone, email or name).',
    parameters: {
      type: 'object',
      properties: { limit: { type: 'integer', description: 'Groups to return, default 20' } },
    },
  },
  {
    name: 'organization_suggestions',
    description:
      'Work email domains, the organization they suggest, and contacts whose organization is missing or different.',
    parameters: { type: 'object', properties: {} },
  },
  {
    name: 'web_search',
    description:
      'Search the web (Google) for public information about a person or company. Results can be wrong or about someone else with the same name: say so, and never propose changes from them without telling the user where they came from.',
    parameters: {
      type: 'object',
      properties: { query: S('What to look up') },
      required: ['query'],
    },
  },
  {
    name: 'propose_changes',
    description:
      'Propose field edits on one or more contacts. NOTHING is saved: the user sees a diff and clicks Apply. Use one call for a batch of related edits.',
    parameters: {
      type: 'object',
      properties: {
        summary: S('One line describing the batch, shown to the user'),
        changes: ARR(
          {
            type: 'object',
            properties: {
              id: S('Contact id'),
              set: SET_FIELDS,
              addEmails: ARR(TYPED, 'Emails to add'),
              removeEmails: ARR({ type: 'string' }, 'Emails to remove'),
              addPhones: ARR(TYPED, 'Phones to add'),
              removePhones: ARR({ type: 'string' }, 'Phones to remove'),
              addUrls: ARR(TYPED, 'Websites / profile links to add'),
              addLabels: ARR({ type: 'string' }, 'Label names to add (created if missing)'),
              removeLabels: ARR({ type: 'string' }, 'Label names to remove'),
            },
            required: ['id'],
          },
          'One entry per contact',
        ),
      },
      required: ['summary', 'changes'],
    },
  },
  {
    name: 'propose_label',
    description:
      'Propose adding many contacts to a label (or removing them), e.g. every result of a search. The user approves before anything changes.',
    parameters: {
      type: 'object',
      properties: {
        summary: S('One line shown to the user'),
        label: S('Label name (created if missing when adding)'),
        remove: { type: 'boolean', description: 'Remove from the label instead of adding' },
        searchId: S('Act on every result of this search'),
        ids: ARR({ type: 'string' }, 'Or these contact ids'),
      },
      required: ['summary', 'label'],
    },
  },
  {
    name: 'propose_new_contact',
    description: 'Propose creating a contact. The user approves before it is created.',
    parameters: {
      type: 'object',
      properties: {
        summary: S('One line shown to the user'),
        fields: SET_FIELDS,
        emails: ARR(TYPED, 'Email addresses'),
        phones: ARR(TYPED, 'Phone numbers'),
        labels: ARR({ type: 'string' }, 'Label names'),
      },
      required: ['summary', 'fields'],
    },
  },
  {
    name: 'propose_trash',
    description:
      "Propose moving contacts to the app's Trash (restorable). The user approves first.",
    parameters: {
      type: 'object',
      properties: {
        summary: S('One line shown to the user, with the reason'),
        ids: ARR({ type: 'string' }, 'Contact ids'),
        searchId: S('Or every result of this search'),
      },
      required: ['summary'],
    },
  },
  {
    name: 'open_merge',
    description:
      'Open the merge dialog for 2-20 contacts that are the same person. The user reviews and merges there.',
    parameters: {
      type: 'object',
      properties: { ids: ARR({ type: 'string' }, 'Contact ids') },
      required: ['ids'],
    },
  },
  {
    name: 'open_contact',
    description: "Open a contact's card on screen.",
    parameters: { type: 'object', properties: { id: S('Contact id') }, required: ['id'] },
  },
  {
    name: 'show_contacts',
    description: 'Show a set of contacts in the main table so the user can browse them.',
    parameters: {
      type: 'object',
      properties: {
        title: S('What the set is, shown above the table'),
        searchId: S('Every result of this search'),
        ids: ARR({ type: 'string' }, 'Or these contact ids'),
      },
      required: ['title'],
    },
  },
];

/** Names of tools that change what the user sees or create proposals (shown in the chat) */
export const ACTION_TOOLS = new Set([
  'propose_changes',
  'propose_label',
  'propose_new_contact',
  'propose_trash',
  'open_merge',
  'open_contact',
  'show_contacts',
]);

let proposalCounter = 0;
const nextProposalId = () => `P${++proposalCounter}`;

const dateOf = (text: unknown): number | null => {
  if (typeof text !== 'string' || !/^\d{4}-\d{2}-\d{2}/.test(text)) {
    return null;
  }
  const t = Date.parse(text);
  return Number.isNaN(t) ? null : t;
};

/** Whether a contact has a field (for the has / missing filters) */
const hasField = (c: Contact, field: string, groups: Record<string, ContactGroup>): boolean => {
  switch (field) {
    case 'name':
      return hasName(c);
    case 'phone':
      return !!c.phoneNumbers?.some((p) => clean(p.value));
    case 'email':
      return !!c.emailAddresses?.some((e) => clean(e.value));
    case 'phonetic':
      return !!getPhoneticName(c);
    case 'organization':
      return !!getOrganization(c);
    case 'jobTitle':
      return !!getJobTitle(c);
    case 'birthday':
      return !!formatBirthday(c);
    case 'address':
      return !!c.addresses?.length;
    case 'label':
      return getLabelNames(c, groups).length > 0;
    case 'notes':
      return !!clean(c.biographies?.[0]?.value);
    case 'photo':
      return !!c.photos?.some((p) => p.url && !p.isDefault);
    default:
      return true;
  }
};

/** Compact view of a contact for the model */
export const summarize = (c: Contact, ctx: AssistantContext): Record<string, unknown> => {
  const a = ctx.activity?.get(c.resourceName);
  const out: Record<string, unknown> = {
    id: c.resourceName,
    name: getDisplayName(c),
    phonetic: getPhoneticName(c),
    emails: (c.emailAddresses ?? []).map((e) => e.value).filter(Boolean),
    phones: (c.phoneNumbers ?? []).map((p) => p.value).filter(Boolean),
    organization: getOrganization(c),
    jobTitle: getJobTitle(c),
    labels: getLabelNames(c, ctx.groups),
    birthday: formatBirthday(c),
    lastInTouch: a?.lastInTouch?.slice(0, 10),
    emails12m: a?.emails12m || undefined,
    meetings12m: a?.meetings12m || undefined,
  };
  Object.keys(out).forEach((k) => {
    const v = out[k];
    if (v === '' || v === undefined || v === null || (Array.isArray(v) && v.length === 0)) {
      delete out[k];
    }
  });
  return out;
};

const findContact = (ctx: AssistantContext, id: unknown): Contact | undefined =>
  typeof id === 'string'
    ? (ctx.contacts.find((c) => c.resourceName === id) ??
      ctx.trashed.find((c) => c.resourceName === id))
    : undefined;

/** The contacts a tool call refers to: a searchId and/or explicit ids */
const targetsOf = (
  args: Record<string, unknown>,
  ctx: AssistantContext,
  memory: SearchMemory,
): { contacts: Contact[]; missing: string[] } => {
  const ids = new Set<string>();
  if (typeof args.searchId === 'string') {
    (memory.get(args.searchId) ?? []).forEach((id) => ids.add(id));
  }
  (Array.isArray(args.ids) ? args.ids : []).forEach((id) => typeof id === 'string' && ids.add(id));
  const contacts: Contact[] = [];
  const missing: string[] = [];
  ids.forEach((id) => {
    const c = findContact(ctx, id);
    if (c) {
      contacts.push(c);
    } else {
      missing.push(id);
    }
  });
  return { contacts, missing };
};

const search = (args: Record<string, unknown>, ctx: AssistantContext, memory: SearchMemory) => {
  const pool = args.inTrash ? ctx.trashed : ctx.contacts;
  const text = typeof args.text === 'string' ? args.text.trim() : '';
  const label = typeof args.label === 'string' ? args.label.trim().toLowerCase() : '';
  const org = typeof args.organization === 'string' ? args.organization.trim().toLowerCase() : '';
  const missing = Array.isArray(args.missing) ? (args.missing as string[]) : [];
  const has = Array.isArray(args.has) ? (args.has as string[]) : [];
  const before = dateOf(args.lastInTouchBefore);
  const after = dateOf(args.lastInTouchAfter);
  const updatedAfter = dateOf(args.updatedAfter);
  const needsActivity = before !== null || after !== null || !!args.neverInTouch;
  if (needsActivity && !ctx.activityReady) {
    return {
      error:
        'Email and meeting activity is not available. The user can connect Gmail & Calendar on the Insights tab.',
    };
  }

  const matches = pool.filter((c) => {
    if (text && !matchesQuery(c, text, searchTextOf(c))) {
      return false;
    }
    if (label && !getLabelNames(c, ctx.groups).some((n) => n.toLowerCase() === label)) {
      return false;
    }
    if (
      org &&
      !getOrganization(c).toLowerCase().includes(org) &&
      !(c.emailAddresses ?? []).some((e) =>
        (e.value ?? '').toLowerCase().split('@')[1]?.includes(org),
      )
    ) {
      return false;
    }
    if (
      missing.some((f) => hasField(c, f, ctx.groups)) ||
      has.some((f) => !hasField(c, f, ctx.groups))
    ) {
      return false;
    }
    if (args.nameScript && scriptOf(getDisplayName(c)) !== args.nameScript) {
      return false;
    }
    if (updatedAfter !== null && !(c.updateTime && Date.parse(c.updateTime) > updatedAfter)) {
      return false;
    }
    const a = ctx.activity?.get(c.resourceName);
    const last = a?.lastInTouch ? Date.parse(a.lastInTouch) : null;
    if (before !== null && !(last !== null && last < before)) {
      return false;
    }
    if (after !== null && !(last !== null && last > after)) {
      return false;
    }
    if (args.neverInTouch && (a || !c.emailAddresses?.some((e) => clean(e.value)))) {
      return false;
    }
    return true;
  });

  const by = args.sortBy;
  const lastOf = (c: Contact) =>
    Date.parse(ctx.activity?.get(c.resourceName)?.lastInTouch ?? '') || 0;
  if (by === 'lastInTouch') {
    matches.sort((a, b) => lastOf(b) - lastOf(a));
  } else if (by === 'mostInTouch') {
    matches.sort(
      (a, b) =>
        (ctx.activity?.get(b.resourceName)?.score ?? 0) -
        (ctx.activity?.get(a.resourceName)?.score ?? 0),
    );
  } else if (by === 'recentlyUpdated') {
    matches.sort(
      (a, b) => (Date.parse(b.updateTime ?? '') || 0) - (Date.parse(a.updateTime ?? '') || 0),
    );
  } else {
    matches.sort((a, b) => getDisplayName(a).localeCompare(getDisplayName(b), ['he', 'en']));
  }

  const limit = Math.min(Math.max(Number(args.limit) || 25, 1), 100);
  const searchId = `S${memory.size + 1}`;
  memory.set(
    searchId,
    matches.map((c) => c.resourceName),
  );
  return {
    searchId,
    total: matches.length,
    returned: Math.min(limit, matches.length),
    contacts: matches.slice(0, limit).map((c) => summarize(c, ctx)),
  };
};

const fullContact = (c: Contact, ctx: AssistantContext) => {
  const a = ctx.activity?.get(c.resourceName);
  return {
    ...summarize(c, ctx),
    inTrash: ctx.trashed.includes(c),
    names: c.names,
    nicknames: c.nicknames,
    phoneNumbers: c.phoneNumbers,
    emailAddresses: c.emailAddresses,
    addresses: c.addresses?.map(
      (x) => x.formattedValue || [x.streetAddress, x.city, x.country].filter(Boolean).join(', '),
    ),
    organizations: c.organizations,
    birthdays: c.birthdays,
    urls: c.urls,
    notes: c.biographies?.map((b) => b.value),
    relations: c.relations,
    customFields: c.userDefined,
    updated: c.updateTime,
    activity: a
      ? {
          lastEmail: a.lastEmailAt,
          lastSent: a.lastSentAt,
          lastReceived: a.lastReceivedAt,
          emailsSent: a.sent,
          emailsReceived: a.received,
          emailsLast12Months: a.emails12m,
          lastMeeting: a.lastMeetingAt,
          lastMeetingTitle: a.lastMeetingTitle,
          meetingsLast12Months: a.meetings12m,
          nextMeeting: a.nextMeetingAt,
          nextMeetingTitle: a.nextMeetingTitle,
        }
      : ctx.activityReady
        ? 'no email or meeting in the last year'
        : 'activity not connected',
  };
};

/**
 * Runs one tool call.
 * @param name - Tool name
 * @param args - Arguments from the model
 * @param ctx - What the tools can see and do
 * @param memory - Earlier search results
 * @returns Result for the model (always an object)
 */
export const executeTool = async (
  name: string,
  args: Record<string, unknown>,
  ctx: AssistantContext,
  memory: SearchMemory,
): Promise<Record<string, unknown>> => {
  switch (name) {
    case 'get_overview': {
      const s = ctx.stats;
      return {
        totalContacts: ctx.contacts.length,
        inTrash: ctx.trashed.length,
        stats: s,
        labels: Object.values(ctx.groups)
          .filter((g) => g.groupType === 'USER_CONTACT_GROUP')
          .map((g) => ({ name: g.name, members: g.memberCount ?? 0 })),
        duplicateGroups: ctx.duplicateGroups.length,
        workDomains: ctx.orgInsights.length,
        activity: ctx.activityReady
          ? 'connected'
          : 'not connected (Insights tab → Connect Gmail & Calendar)',
      };
    }
    case 'search_contacts':
      return search(args, ctx, memory);
    case 'get_contact': {
      const c = findContact(ctx, args.id);
      return c ? fullContact(c, ctx) : { error: `No contact ${String(args.id)}` };
    }
    case 'find_duplicates': {
      const limit = Math.min(Number(args.limit) || 20, 50);
      return {
        total: ctx.duplicateGroups.length,
        groups: ctx.duplicateGroups.slice(0, limit).map((g) => ({
          reasons: g.reasons,
          matched: g.matchedValues,
          contacts: g.members.map((m) => summarize(m, ctx)),
        })),
      };
    }
    case 'organization_suggestions':
      return {
        domains: ctx.orgInsights.slice(0, 40).map((d) => ({
          domain: d.domain,
          suggestedOrganization: d.suggested,
          contacts: d.members.length,
          needUpdate: d.needsUpdate.map((n) => ({
            id: n.contact.resourceName,
            name: getDisplayName(n.contact),
            reason: n.reason,
            currentOrganization: n.current,
          })),
        })),
      };
    case 'web_search': {
      const query = typeof args.query === 'string' ? args.query : '';
      const r = await backendApiClient.assistantSearch(query);
      return { answer: r.text, sources: r.sources };
    }
    case 'propose_changes': {
      const changes = Array.isArray(args.changes) ? (args.changes as ContactChange[]) : [];
      const items: ResolvedChange[] = [];
      const warnings: string[] = [];
      changes.forEach((ch) => {
        const c = findContact(ctx, ch.id);
        if (!c) {
          warnings.push(`No contact ${ch.id}`);
          return;
        }
        const r = resolveChange(c, ch, ctx.groups);
        r.warnings.forEach((w) => warnings.push(`${proposalName(c)}: ${w}`));
        if (r.diff.length) {
          items.push(r);
        }
      });
      if (!items.length) {
        return { error: 'Nothing to change', warnings };
      }
      const id = nextProposalId();
      ctx.propose({
        kind: 'update',
        id,
        summary: String(args.summary ?? 'Changes'),
        items,
        warnings,
      });
      return {
        proposalId: id,
        status: 'waiting for the user to apply',
        contacts: items.length,
        changes: items.reduce((s, i) => s + i.diff.length, 0),
        warnings,
      };
    }
    case 'propose_label': {
      const label = clean(String(args.label ?? ''));
      const { contacts, missing } = targetsOf(args, ctx, memory);
      const remove = !!args.remove;
      const affected = contacts.filter(
        (c) =>
          getLabelNames(c, ctx.groups).some((n) => n.toLowerCase() === label.toLowerCase()) ===
          remove,
      );
      if (!label || !affected.length) {
        return { error: affected.length ? 'No label given' : 'No contacts to change', missing };
      }
      const id = nextProposalId();
      ctx.propose({
        kind: 'labels',
        id,
        summary: String(args.summary ?? `${remove ? 'Remove from' : 'Add to'} ${label}`),
        label,
        add: !remove,
        contacts: affected,
        warnings: missing.map((m) => `No contact ${m}`),
      });
      return { proposalId: id, status: 'waiting for the user to apply', contacts: affected.length };
    }
    case 'propose_new_contact': {
      const { patch, diff, warnings } = newContactPatch(
        (args.fields ?? {}) as FieldChanges,
        (args.emails ?? []) as TypedValue[],
        (args.phones ?? []) as TypedValue[],
      );
      if (!diff.length) {
        return { error: 'A new contact needs at least a name, email or phone' };
      }
      const labels = (Array.isArray(args.labels) ? args.labels : []).map(String).filter(Boolean);
      labels.forEach((l) => diff.push({ field: 'Label', from: '', to: l }));
      const id = nextProposalId();
      ctx.propose({
        kind: 'create',
        id,
        summary: String(args.summary ?? 'New contact'),
        patch,
        labels,
        diff,
        warnings,
      });
      return { proposalId: id, status: 'waiting for the user to apply', warnings };
    }
    case 'propose_trash': {
      const { contacts, missing } = targetsOf(args, ctx, memory);
      const active = contacts.filter((c) => !ctx.trashed.includes(c));
      if (!active.length) {
        return { error: 'No contacts to move to Trash', missing };
      }
      const id = nextProposalId();
      ctx.propose({
        kind: 'trash',
        id,
        summary: String(args.summary ?? 'Move to Trash'),
        contacts: active,
        warnings: missing.map((m) => `No contact ${m}`),
      });
      return { proposalId: id, status: 'waiting for the user to apply', contacts: active.length };
    }
    case 'open_merge': {
      const { contacts } = targetsOf(args, ctx, memory);
      if (contacts.length < 2 || contacts.length > 20) {
        return { error: 'Merging needs 2 to 20 contacts' };
      }
      ctx.openMerge(contacts);
      return { status: 'merge dialog opened; the user reviews and merges there' };
    }
    case 'open_contact': {
      const c = findContact(ctx, args.id);
      if (!c) {
        return { error: `No contact ${String(args.id)}` };
      }
      ctx.openContact(c.resourceName);
      return { status: 'opened' };
    }
    case 'show_contacts': {
      const { contacts } = targetsOf(args, ctx, memory);
      if (!contacts.length) {
        return { error: 'No contacts to show' };
      }
      ctx.showContacts(
        contacts.map((c) => c.resourceName),
        String(args.title ?? 'Assistant selection'),
      );
      return { status: 'shown in the table', contacts: contacts.length };
    }
    default:
      return { error: `Unknown tool ${name}` };
  }
};
