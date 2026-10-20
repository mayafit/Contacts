/**
 * @fileoverview What the device the app runs on can do: call, message, email,
 * share and save contacts, and read the phone's own contacts (Contact Picker)
 * @module Contacts/device/device
 */

import type { Contact } from '../types/Contact';
import { clean, digitsOf, getDisplayName, getName } from '../model/contactView';

/** Platform, as far as the browser tells */
export type Platform = 'android' | 'ios' | 'desktop';

/**
 * The platform from the user agent.
 * @param ua - User agent (defaults to the browser's)
 * @returns Platform
 */
export const platformOf = (
  ua: string = typeof navigator !== 'undefined' ? navigator.userAgent : '',
): Platform => {
  if (/android/i.test(ua)) {
    return 'android';
  }
  if (
    /iphone|ipad|ipod/i.test(ua) ||
    (/macintosh/i.test(ua) && typeof navigator !== 'undefined' && navigator.maxTouchPoints > 1)
  ) {
    return 'ios';
  }
  return 'desktop';
};

/** Whether the browser offers the Contact Picker (Chrome on Android) */
export const canPickDeviceContacts = (): boolean =>
  typeof navigator !== 'undefined' && 'contacts' in navigator && 'ContactsManager' in window;

/**
 * A phone number in international digits for links that need it (WhatsApp):
 * the canonical form when Google has one, Israeli 0-prefixed numbers as 972…
 * @param value - Number as typed
 * @param canonical - Google's E.164 form, if any
 * @returns Digits only, '' when unusable
 */
export const internationalDigits = (value: string, canonical?: string | null): string => {
  const c = digitsOf(canonical ?? '');
  if (c.length >= 8) {
    return c;
  }
  const raw = clean(value);
  const d = digitsOf(raw);
  if (raw.startsWith('+')) {
    return d;
  }
  if (d.startsWith('00')) {
    return d.slice(2);
  }
  if (d.startsWith('0') && d.length >= 9) {
    return `972${d.slice(1)}`;
  }
  return d.length >= 8 ? d : '';
};

/**
 * tel: link.
 * @param value - Number as typed
 * @returns Link
 */
export const telHref = (value: string): string => `tel:${clean(value).replace(/[^\d+*#]/g, '')}`;

/**
 * sms: link, with an optional prefilled text (iOS and Android differ).
 * @param value - Number
 * @param body - Text
 * @param platform - Platform
 * @returns Link
 */
export const smsHref = (
  value: string,
  body?: string,
  platform: Platform = platformOf(),
): string => {
  const number = clean(value).replace(/[^\d+]/g, '');
  if (!body) {
    return `sms:${number}`;
  }
  return `sms:${number}${platform === 'ios' ? '&' : '?'}body=${encodeURIComponent(body)}`;
};

/**
 * WhatsApp chat link (opens the app on a phone, WhatsApp Web elsewhere).
 * @param value - Number
 * @param canonical - Google's E.164 form
 * @param text - Prefilled text
 * @returns Link, or '' when the number is not usable
 */
export const whatsappHref = (value: string, canonical?: string | null, text?: string): string => {
  const d = internationalDigits(value, canonical);
  return d ? `https://wa.me/${d}${text ? `?text=${encodeURIComponent(text)}` : ''}` : '';
};

/**
 * mailto: link.
 * @param email - Address
 * @param subject - Subject
 * @param body - Body
 * @returns Link
 */
export const mailtoHref = (email: string, subject?: string, body?: string): string => {
  const q = [
    subject && `subject=${encodeURIComponent(subject)}`,
    body && `body=${encodeURIComponent(body)}`,
  ]
    .filter(Boolean)
    .join('&');
  return `mailto:${clean(email)}${q ? `?${q}` : ''}`;
};

/**
 * Maps link for an address.
 * @param address - Address text
 * @returns Link
 */
export const mapsHref = (address: string): string =>
  `https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(clean(address))}`;

/** vCard text value escaping (RFC 6350 §3.4) */
const esc = (v: string | null | undefined): string =>
  clean(v).replace(/\\/g, '\\\\').replace(/\n/g, '\\n').replace(/,/g, '\\,').replace(/;/g, '\\;');

const TEL_TYPES: Record<string, string> = {
  mobile: 'CELL',
  home: 'HOME',
  work: 'WORK',
  main: 'VOICE',
  homeFax: 'FAX',
  workFax: 'FAX',
};

/**
 * One contact as a vCard 3.0 (understood by Android, iOS and Outlook), with the
 * phonetic name as X-PHONETIC fields (iOS shows them).
 * @param c - Contact
 * @returns vCard text (CRLF line ends)
 */
export const toVCard = (c: Contact): string => {
  const n = getName(c);
  const lines = ['BEGIN:VCARD', 'VERSION:3.0'];
  lines.push(
    `N:${esc(n?.familyName)};${esc(n?.givenName)};${esc(n?.middleName)};${esc(n?.honorificPrefix)};${esc(n?.honorificSuffix)}`,
  );
  lines.push(`FN:${esc(getDisplayName(c))}`);
  if (n?.phoneticGivenName) {
    lines.push(`X-PHONETIC-FIRST-NAME:${esc(n.phoneticGivenName)}`);
  }
  if (n?.phoneticFamilyName) {
    lines.push(`X-PHONETIC-LAST-NAME:${esc(n.phoneticFamilyName)}`);
  }
  (c.nicknames ?? []).slice(0, 1).forEach((x) => lines.push(`NICKNAME:${esc(x.value)}`));
  (c.phoneNumbers ?? []).forEach((p) => {
    if (clean(p.value)) {
      lines.push(
        `TEL;TYPE=${TEL_TYPES[p.type ?? ''] ?? 'VOICE'}:${esc(p.canonicalForm || p.value)}`,
      );
    }
  });
  (c.emailAddresses ?? []).forEach((e) => {
    if (clean(e.value)) {
      lines.push(`EMAIL;TYPE=${e.type === 'work' ? 'WORK' : 'HOME'},INTERNET:${esc(e.value)}`);
    }
  });
  const org = c.organizations?.[0];
  if (org?.name || org?.department) {
    lines.push(`ORG:${esc(org?.name)}${org?.department ? `;${esc(org.department)}` : ''}`);
  }
  if (org?.title) {
    lines.push(`TITLE:${esc(org.title)}`);
  }
  const b = c.birthdays?.[0]?.date;
  if (b?.month && b?.day) {
    const mm = String(b.month).padStart(2, '0');
    const dd = String(b.day).padStart(2, '0');
    lines.push(`BDAY:${b.year ? `${b.year}-${mm}-${dd}` : `--${mm}-${dd}`}`);
  }
  (c.addresses ?? []).forEach((a) => {
    lines.push(
      `ADR;TYPE=${a.type === 'work' ? 'WORK' : 'HOME'}:${esc(a.poBox)};${esc(a.extendedAddress)};${esc(a.streetAddress || (a.city ? '' : a.formattedValue))};${esc(a.city)};${esc(a.region)};${esc(a.postalCode)};${esc(a.country)}`,
    );
  });
  (c.urls ?? []).forEach((u) => clean(u.value) && lines.push(`URL:${esc(u.value)}`));
  const note = clean(c.biographies?.[0]?.value);
  if (note) {
    lines.push(`NOTE:${esc(note)}`);
  }
  lines.push('END:VCARD');
  return lines.join('\r\n');
};

/**
 * Saves contacts to the device: the share sheet on a phone (pick Contacts),
 * otherwise a .vcf download the system opens with its contacts app.
 * @param contacts - Contacts
 * @param fileName - File name without extension
 * @returns How it was handed over
 */
export const saveToDevice = async (
  contacts: Contact[],
  fileName = 'contacts',
): Promise<'shared' | 'downloaded'> => {
  const text = contacts.map(toVCard).join('\r\n');
  const safe = fileName.replace(/[\\/:*?"<>|]+/g, ' ').trim() || 'contacts';
  const file = new File([text], `${safe}.vcf`, { type: 'text/vcard' });
  const nav = navigator as Navigator & { canShare?: (d: ShareData) => boolean };
  if (platformOf() !== 'desktop' && nav.canShare?.({ files: [file] })) {
    try {
      await navigator.share({ files: [file], title: safe });
      return 'shared';
    } catch (e) {
      if ((e as Error).name === 'AbortError') {
        return 'shared';
      }
    }
  }
  const url = URL.createObjectURL(file);
  const a = document.createElement('a');
  a.href = url;
  a.download = file.name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
  return 'downloaded';
};

/** A contact chosen from the phone (Contact Picker API) */
export interface DeviceContact {
  name: string[];
  tel: string[];
  email: string[];
}

/**
 * Lets the user choose contacts from the phone's address book (Chrome on Android).
 * Nothing is read without the user picking.
 * @returns The chosen contacts
 */
export const pickDeviceContacts = async (): Promise<DeviceContact[]> => {
  const picker = (
    navigator as unknown as {
      contacts: {
        select: (props: string[], opts: { multiple: boolean }) => Promise<Partial<DeviceContact>[]>;
      };
    }
  ).contacts;
  const chosen = await picker.select(['name', 'tel', 'email'], { multiple: true });
  return chosen.map((c) => ({ name: c.name ?? [], tel: c.tel ?? [], email: c.email ?? [] }));
};

/** Comparing the phone's contacts with Google's */
export interface DeviceComparison {
  /** On the phone, not in Google */
  missing: DeviceContact[];
  /** In both, but the phone has numbers or addresses Google lacks */
  differences: {
    contact: Contact;
    device: DeviceContact;
    addPhones: string[];
    addEmails: string[];
  }[];
  /** In both, nothing to add */
  same: number;
}

const phoneKey9 = (v: string) => digitsOf(v).slice(-9);

/**
 * Matches phone contacts to Google contacts by phone number (last 9 digits, so
 * 054… and +972 54… match) or email.
 * @param device - Contacts from the phone
 * @param google - Google contacts (Trash included, so nothing trashed is re-added)
 * @returns Comparison
 */
export const compareWithDevice = (device: DeviceContact[], google: Contact[]): DeviceComparison => {
  const byPhone = new Map<string, Contact>();
  const byEmail = new Map<string, Contact>();
  google.forEach((c) => {
    (c.phoneNumbers ?? []).forEach((p) => {
      [p.value, p.canonicalForm].forEach((v) => {
        const k = v ? phoneKey9(v) : '';
        if (k.length >= 7) {
          byPhone.set(k, c);
        }
      });
    });
    (c.emailAddresses ?? []).forEach(
      (e) => e.value && byEmail.set(e.value.trim().toLowerCase(), c),
    );
  });

  const result: DeviceComparison = { missing: [], differences: [], same: 0 };
  device.forEach((d) => {
    const tels = d.tel.filter((t) => phoneKey9(t).length >= 7);
    const emails = d.email.map((e) => e.trim().toLowerCase()).filter(Boolean);
    const match =
      tels.map((t) => byPhone.get(phoneKey9(t))).find(Boolean) ??
      emails.map((e) => byEmail.get(e)).find(Boolean);
    if (!match) {
      if (tels.length || emails.length) {
        result.missing.push(d);
      }
      return;
    }
    const have = new Set(
      (match.phoneNumbers ?? [])
        .flatMap((p) => [p.value, p.canonicalForm])
        .filter(Boolean)
        .map((v) => phoneKey9(v!)),
    );
    const haveEmail = new Set(
      (match.emailAddresses ?? []).map((e) => e.value.trim().toLowerCase()),
    );
    const addPhones = [
      ...new Map(
        tels.filter((t) => !have.has(phoneKey9(t))).map((t) => [phoneKey9(t), t]),
      ).values(),
    ];
    const addEmails = [...new Set(emails.filter((e) => !haveEmail.has(e)))];
    if (addPhones.length || addEmails.length) {
      // Several phone entries for one Google contact: one update, or the second
      // would be refused for a stale etag
      const earlier = result.differences.find((x) => x.contact.resourceName === match.resourceName);
      if (earlier) {
        addPhones.forEach(
          (t) =>
            !earlier.addPhones.some((x) => phoneKey9(x) === phoneKey9(t)) &&
            earlier.addPhones.push(t),
        );
        addEmails.forEach((e) => !earlier.addEmails.includes(e) && earlier.addEmails.push(e));
      } else {
        result.differences.push({ contact: match, device: d, addPhones, addEmails });
      }
    } else {
      result.same += 1;
    }
  });
  return result;
};
