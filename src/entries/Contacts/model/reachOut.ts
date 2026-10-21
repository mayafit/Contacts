/**
 * @fileoverview Who to contact and why: overdue relationships, birthdays,
 * replies owed, unanswered emails and upcoming meetings
 * @module Contacts/model/reachOut
 */

import type { Contact } from '../types/Contact';
import type { ThreadState } from '../../../services/api/BackendApiClient';
import { describeGap, formatRelative, overdueOf, type ActivityIndex } from './activity';

/** Why a contact is on the list */
export type ReasonKind = 'oweReply' | 'birthday' | 'meetingSoon' | 'overdue' | 'waiting';

/** One reason, with the text shown */
export interface Reason {
  kind: ReasonKind;
  text: string;
  /** Higher sorts first */
  weight: number;
  /** The thread it is about (reply reasons) */
  thread?: ThreadState;
}

/** A contact to reach out to */
export interface ReachOutItem {
  contact: Contact;
  reasons: Reason[];
  score: number;
}

/** A thread waiting on someone, with the contact it is with */
export interface AwaitingItem {
  thread: ThreadState;
  contact: Contact | null;
  /** The first counterpart's address */
  email: string;
  daysSince: number;
}

const DAY = 86_400_000;

/** Contact by lower-case email address */
export const contactsByEmail = (contacts: Contact[]): Map<string, Contact> => {
  const map = new Map<string, Contact>();
  contacts.forEach((c) =>
    (c.emailAddresses ?? []).forEach((e) => e.value && map.set(e.value.trim().toLowerCase(), c)),
  );
  return map;
};

/**
 * Threads waiting on the user (they wrote last, still in the inbox, 1–30 days
 * old) and on others (the user wrote last, 4–30 days old, no answer yet).
 * @param threads - Recent threads
 * @param byEmail - Contact by email
 * @param now - Current time
 * @returns Both lists, oldest first
 */
export const awaitingReply = (
  threads: ThreadState[],
  byEmail: Map<string, Contact>,
  now: Date = new Date(),
): { owe: AwaitingItem[]; waiting: AwaitingItem[] } => {
  const owe: AwaitingItem[] = [];
  const waiting: AwaitingItem[] = [];
  threads.forEach((t) => {
    const daysSince = Math.floor((now.getTime() - Date.parse(t.lastAt)) / DAY);
    const email = t.counterparts[0] ?? '';
    const contact = t.counterparts.map((a) => byEmail.get(a)).find(Boolean) ?? null;
    const item = { thread: t, contact, email, daysSince };
    if (!t.lastFromMe && t.inInbox && daysSince >= 1 && daysSince <= 30) {
      owe.push(item);
    } else if (t.lastFromMe && daysSince >= 4 && daysSince <= 30) {
      waiting.push(item);
    }
  });
  const oldestFirst = (a: AwaitingItem, b: AwaitingItem) => b.daysSince - a.daysSince;
  return { owe: owe.sort(oldestFirst), waiting: waiting.sort(oldestFirst) };
};

/**
 * Days until the next birthday (0 = today), or null without a month and day.
 * @param c - Contact
 * @param now - Current time
 * @returns Days
 */
export const daysToBirthday = (c: Contact, now: Date = new Date()): number | null => {
  const d = c.birthdays?.find((b) => b.date?.month && b.date?.day)?.date;
  if (!d?.month || !d.day) {
    return null;
  }
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  let next = new Date(now.getFullYear(), d.month - 1, d.day);
  if (next < today) {
    next = new Date(now.getFullYear() + 1, d.month - 1, d.day);
  }
  return Math.round((next.getTime() - today.getTime()) / DAY);
};

/**
 * The reach-out list: every contact with a reason, most urgent first.
 * @param contacts - Active contacts
 * @param index - Activity per contact (optional: birthdays work without it)
 * @param threads - Recent threads (optional)
 * @param now - Current time
 * @param dismissed - Keys ("resourceName:kind") the user marked done
 * @returns Items
 */
export const buildReachOut = (
  contacts: Contact[],
  index: ActivityIndex | undefined,
  threads: ThreadState[] = [],
  now: Date = new Date(),
  dismissed: Set<string> = new Set(),
): ReachOutItem[] => {
  const byEmail = contactsByEmail(contacts);
  const { owe, waiting } = awaitingReply(threads, byEmail, now);
  const reasons = new Map<string, Reason[]>();
  const add = (c: Contact, r: Reason) => {
    if (dismissed.has(`${c.resourceName}:${r.kind}`)) {
      return;
    }
    const list = reasons.get(c.resourceName) ?? [];
    if (!list.some((x) => x.kind === r.kind)) {
      list.push(r);
    }
    reasons.set(c.resourceName, list);
  };

  owe.forEach((o) => {
    if (o.contact) {
      add(o.contact, {
        kind: 'oweReply',
        weight: 60 + Math.min(o.daysSince, 14),
        text: `wrote ${formatRelative(o.thread.lastAt, now)}${o.thread.subject ? `: “${o.thread.subject}”` : ''} – you have not replied`,
        thread: o.thread,
      });
    }
  });
  waiting.forEach((w) => {
    if (w.contact) {
      add(w.contact, {
        kind: 'waiting',
        weight: 20 + Math.min(w.daysSince, 14),
        text: `no answer to your email from ${formatRelative(w.thread.lastAt, now)}${w.thread.subject ? ` (“${w.thread.subject}”)` : ''}`,
        thread: w.thread,
      });
    }
  });
  contacts.forEach((c) => {
    const days = daysToBirthday(c, now);
    if (days !== null && days <= 7) {
      add(c, {
        kind: 'birthday',
        weight: days === 0 ? 90 : 70 - days * 3,
        text:
          days === 0
            ? 'birthday today'
            : days === 1
              ? 'birthday tomorrow'
              : `birthday in ${days} days`,
      });
    }
    const a = index?.get(c.resourceName);
    if (a?.nextMeetingAt) {
      const hours = (Date.parse(a.nextMeetingAt) - now.getTime()) / 3_600_000;
      if (hours >= 0 && hours <= 48) {
        add(c, {
          kind: 'meetingSoon',
          weight: 50,
          text: `meeting ${formatRelative(a.nextMeetingAt, now)}${a.nextMeetingTitle ? `: “${a.nextMeetingTitle}”` : ''}`,
        });
      }
    }
    const overdue = overdueOf(a, now);
    if (overdue) {
      add(c, {
        kind: 'overdue',
        // Long, regular relationships first
        weight:
          30 +
          Math.min(20, Math.round(overdue.ratio * 3)) +
          Math.min(10, a?.activeMonths.length ?? 0),
        text: `usually ${describeGap(overdue.typicalGapDays)}, last ${formatRelative(a!.lastInTouch, now)}`,
      });
    }
  });

  const all = new Map(contacts.map((c) => [c.resourceName, c]));
  return [...reasons.entries()]
    .map(([rn, list]) => ({
      contact: all.get(rn)!,
      reasons: list.sort((a, b) => b.weight - a.weight),
      score: Math.max(...list.map((r) => r.weight)) + (list.length - 1) * 5,
    }))
    .sort((a, b) => b.score - a.score);
};

/**
 * Gmail link to a thread.
 * @param threadId - Thread id
 * @returns URL
 */
export const gmailThreadUrl = (threadId: string): string =>
  `https://mail.google.com/mail/u/0/#all/${encodeURIComponent(threadId)}`;
