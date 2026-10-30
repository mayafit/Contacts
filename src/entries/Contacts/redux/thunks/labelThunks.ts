/**
 * @fileoverview Labels and favourites in step with Google (AD-44): changing a membership,
 * reading the changed contacts again, and the check against Google's members
 * @module Contacts/redux/thunks/labelThunks
 */

import type { AppDispatch } from '../../types/store';
import type { Contact } from '../../types/Contact';
import {
  contactUpdated,
  fetchContactGroups,
  fetchContacts,
  membershipChanged,
} from '../slices/contacts/contactsSlice';
import { GoogleContactsService } from '../../services/GoogleContactsService';
import { backendApiClient } from '../../../../services/api/BackendApiClient';

/** Google's favourites */
export const STARRED = 'contactGroups/starred';

/** Above this many contacts a label change reloads everything instead of each contact */
export const REFETCH_LIMIT = 25;

/**
 * Re-reads contacts whose labels changed, so their etags stay current.
 * @param dispatch - Store dispatch
 * @param resourceNames - Changed contacts
 */
export const refreshContacts = async (
  dispatch: AppDispatch,
  resourceNames: string[],
): Promise<void> => {
  dispatch(fetchContactGroups());
  if (resourceNames.length > REFETCH_LIMIT) {
    await dispatch(fetchContacts());
    return;
  }
  await Promise.all(
    resourceNames.map(async (rn) => {
      const fresh = await GoogleContactsService.getContact(rn);
      if (fresh.success && fresh.data) {
        dispatch(contactUpdated(fresh.data));
      }
    }),
  );
};

// A change in flight: the check against Google waits (rule 3)
let changesInFlight = 0;

/** Whether a membership change is being written */
export const membershipChanging = (): boolean => changesInFlight > 0;

/**
 * Adds contacts to a label or removes them (AD-44 rule 2): the store first, then
 * Google (chunks of 1,000), then the changed contacts read again. Undone when
 * Google refuses.
 * @param dispatch - Store dispatch
 * @param resourceNames - Contacts
 * @param label - The label's resource name
 * @param on - Add (true) or remove
 * @returns Null, or why Google refused
 */
export const changeMembership = async (
  dispatch: AppDispatch,
  resourceNames: string[],
  label: string,
  on: boolean,
): Promise<string | null> => {
  if (!resourceNames.length) {
    return null;
  }
  changesInFlight += 1;
  dispatch(membershipChanged({ resourceNames, label, on }));
  try {
    for (let i = 0; i < resourceNames.length; i += 1000) {
      const chunk = resourceNames.slice(i, i + 1000);
      await backendApiClient.modifyLabel(
        on ? { labelResourceName: label, add: chunk } : { labelResourceName: label, remove: chunk },
      );
    }
  } catch (e) {
    dispatch(membershipChanged({ resourceNames, label, on: !on }));
    return (e as Error).message || 'Google refused the change';
  } finally {
    changesInFlight -= 1;
  }
  await refreshContacts(dispatch, resourceNames);
  return null;
};

/** What the check found */
export interface Reconcile {
  /** Contacts whose labels (among the checked ones) differ from Google's */
  changed: { resourceName: string; labels: string[] }[];
  /** Google has a member the store does not hold (a contact made elsewhere) */
  unknown: boolean;
}

/**
 * Compares the store's labels with Google's members of the checked labels (rule 3).
 * Labels that were not checked (system groups such as myContacts) are kept as they are.
 * @param contacts - The store's contacts
 * @param members - Label → its members, from Google
 * @returns What differs
 */
export const reconcileMembers = (
  contacts: Contact[],
  members: Record<string, string[]>,
): Reconcile => {
  const checked = new Set(Object.keys(members));
  const of = new Map<string, Set<string>>();
  Object.entries(members).forEach(([label, list]) =>
    list.forEach((rn) => {
      const set = of.get(rn) ?? new Set<string>();
      set.add(label);
      of.set(rn, set);
    }),
  );
  const known = new Set(contacts.map((c) => c.resourceName));
  const changed: Reconcile['changed'] = [];
  contacts.forEach((c) => {
    const current = c.labels ?? [];
    const google = of.get(c.resourceName) ?? new Set<string>();
    const mine = current.filter((l) => checked.has(l));
    const same = mine.length === google.size && mine.every((l) => google.has(l));
    if (!same) {
      changed.push({
        resourceName: c.resourceName,
        labels: [...current.filter((l) => !checked.has(l)), ...google],
      });
    }
  });
  return { changed, unknown: [...of.keys()].some((rn) => !known.has(rn)) };
};

/**
 * One check against Google (rule 3): the label list, then the members; changed contacts
 * are updated and read again; a contact made elsewhere reloads the list.
 * @param dispatch - Store dispatch
 * @param contacts - The store's contacts
 * @returns How many contacts changed
 */
export const checkLabels = async (dispatch: AppDispatch, contacts: Contact[]): Promise<number> => {
  dispatch(fetchContactGroups());
  const members = await backendApiClient.groupMembers();
  // A change made while Google answered is newer than the answer
  if (membershipChanging()) {
    return 0;
  }
  const { changed, unknown } = reconcileMembers(contacts, members);
  if (unknown) {
    await dispatch(fetchContacts());
    return changed.length;
  }
  if (changed.length) {
    changed.forEach(({ resourceName, labels }) =>
      dispatch(contactUpdated({ resourceName, labels } as Contact)),
    );
    await refreshContacts(
      dispatch,
      changed.map((c) => c.resourceName),
    );
  }
  return changed.length;
};
