/**
 * @fileoverview Applies a proposal the user approved, through the app's normal save paths
 * @module Contacts/assistant/apply
 */

import type { AppDispatch } from '../types/store';
import type { Contact, ContactGroup } from '../types/Contact';
import { buildPatch } from '../components/card/cardModel';
import { contactAdded, updateContact } from '../redux/slices/contacts/contactsSlice';
import { trashContacts } from '../redux/thunks/cleanupThunks';
import { refreshContacts } from '../redux/thunks/labelThunks';
import { backendApiClient } from '../../../services/api/BackendApiClient';
import { proposalName } from './changes';
import type { Proposal } from './tools';

/** Outcome of applying a proposal */
export interface ApplyResult {
  /** Contacts changed */
  done: number;
  /** Per-contact failures */
  failed: { name: string; message: string }[];
}

const messageOf = (e: unknown): string => (e as { message?: string })?.message ?? 'Failed';

/**
 * The label to send for a name: an existing label (any kind, case-insensitive) by
 * resource name, otherwise the name (the backend creates a user label).
 * @param name - Label name
 * @param groups - Labels by resource name
 * @returns Request fields
 */
const labelTarget = (
  name: string,
  groups: Record<string, ContactGroup>,
): { labelResourceName: string } | { labelName: string } => {
  const lower = name.toLowerCase();
  const match =
    Object.values(groups).find(
      (g) => g.groupType === 'USER_CONTACT_GROUP' && g.name.toLowerCase() === lower,
    ) ?? Object.values(groups).find((g) => g.name.toLowerCase() === lower);
  return match ? { labelResourceName: match.resourceName } : { labelName: name };
};

/** Label changes grouped by label name */
const labelOps = (items: { rn: string; add: string[]; remove: string[] }[]) => {
  const ops = new Map<string, { add: string[]; remove: string[] }>();
  items.forEach(({ rn, add, remove }) => {
    add.forEach((l) => {
      const op = ops.get(l) ?? { add: [], remove: [] };
      op.add.push(rn);
      ops.set(l, op);
    });
    remove.forEach((l) => {
      const op = ops.get(l) ?? { add: [], remove: [] };
      op.remove.push(rn);
      ops.set(l, op);
    });
  });
  return ops;
};

/**
 * Applies a proposal. Field edits are written with the etag the proposal was made
 * from, so a contact edited since then is refused (412) instead of overwritten.
 * @param proposal - Approved proposal
 * @param dispatch - Store dispatch
 * @param current - Contacts as they are now, by resource name
 * @param groups - Labels by resource name
 * @returns What was done
 */
export const applyProposal = async (
  proposal: Proposal,
  dispatch: AppDispatch,
  current: Record<string, Contact | undefined>,
  groups: Record<string, ContactGroup>,
): Promise<ApplyResult> => {
  const result: ApplyResult = { done: 0, failed: [] };

  if (proposal.kind === 'update') {
    const labelled: { rn: string; add: string[]; remove: string[] }[] = [];
    for (const item of proposal.items) {
      const rn = item.base.resourceName;
      const name = proposalName(item.base);
      const now = current[rn];
      if (!now) {
        result.failed.push({ name, message: 'no longer exists' });
        continue;
      }
      if (now.etag !== item.base.etag) {
        result.failed.push({ name, message: 'changed since the proposal; ask again' });
        continue;
      }
      try {
        const patch = buildPatch(item.base, item.draft, item.base.etag);
        if (patch) {
          await dispatch(updateContact({ resourceName: rn, updates: patch })).unwrap();
        }
        if (item.addLabels.length || item.removeLabels.length) {
          labelled.push({ rn, add: item.addLabels, remove: item.removeLabels });
        }
        result.done += 1;
      } catch (e) {
        const message = messageOf(e);
        result.failed.push({
          name,
          message: /412|precondition|conflict/i.test(message)
            ? 'changed in Google since the proposal; ask again'
            : message,
        });
      }
    }
    for (const [label, op] of labelOps(labelled)) {
      try {
        await backendApiClient.modifyLabel({
          ...labelTarget(label, groups),
          add: op.add,
          remove: op.remove,
        });
      } catch (e) {
        result.failed.push({ name: `Label "${label}"`, message: messageOf(e) });
      }
    }
    if (labelled.length) {
      await refreshContacts(
        dispatch,
        labelled.map((l) => l.rn),
      );
    }
    return result;
  }

  if (proposal.kind === 'create') {
    const created = await backendApiClient.createContact(proposal.patch);
    dispatch(contactAdded(created));
    for (const label of proposal.labels) {
      try {
        await backendApiClient.modifyLabel({
          ...labelTarget(label, groups),
          add: [created.resourceName],
        });
      } catch (e) {
        result.failed.push({ name: `Label "${label}"`, message: messageOf(e) });
      }
    }
    if (proposal.labels.length) {
      await refreshContacts(dispatch, [created.resourceName]);
    }
    result.done = 1;
    return result;
  }

  if (proposal.kind === 'labels') {
    const names = proposal.contacts.map((c) => c.resourceName);
    const target = labelTarget(proposal.label, groups);
    for (let i = 0; i < names.length; i += 1000) {
      const chunk = names.slice(i, i + 1000);
      await backendApiClient.modifyLabel({
        ...target,
        add: proposal.add ? chunk : [],
        remove: proposal.add ? [] : chunk,
      });
      result.done += chunk.length;
    }
    await refreshContacts(dispatch, names);
    return result;
  }

  // trash
  await dispatch(
    trashContacts({ resourceNames: proposal.contacts.map((c) => c.resourceName), restore: false }),
  ).unwrap();
  result.done = proposal.contacts.length;
  return result;
};
