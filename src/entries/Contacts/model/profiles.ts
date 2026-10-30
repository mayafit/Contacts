/**
 * @fileoverview Recognising profile links and merging a contact's profiles from its own
 * links, Gravatar and its signatures (AD-46 rules 1, 2, 4, 6); exact rules only
 * @module Contacts/model/profiles
 */

import type { Contact } from '../types/Contact';
import type { GravatarProfile } from '../../../services/api/BackendApiClient';
import { classifyPhone, clean } from './contactView';
import { whatsappHref } from '../device/device';

/** The sites Echo recognises */
export type Network =
  | 'LinkedIn'
  | 'Facebook'
  | 'Instagram'
  | 'X'
  | 'GitHub'
  | 'TikTok'
  | 'YouTube'
  | 'Telegram'
  | 'Medium'
  | 'WhatsApp'
  | 'Website';

/** A recognised link */
export interface ProfileLinkInfo {
  network: Network;
  /** The person's handle on the site (a website: its host) */
  handle: string;
  /** https://host/path, without query, fragment or final / */
  url: string;
  /** The network and the handle (a website: host and path), lower case */
  key: string;
  /** A company or other page, not a person (LinkedIn pages) */
  page?: boolean;
}

/** Where a profile was found */
export type ProfileSource = 'contact' | 'gravatar' | 'signature';

/** A profile of a contact */
export interface ContactProfile extends ProfileLinkInfo {
  sources: ProfileSource[];
  /** One of the contact's own links already */
  onContact: boolean;
}

/** decodeURIComponent that keeps the text when it is not valid percent-encoding ("50%off") */
const decode = (s: string): string => {
  try {
    return decodeURIComponent(s);
  } catch {
    return s;
  }
};

const IG_RESERVED = new Set(['p', 'reel', 'reels', 'stories', 'explore', 'tv', 'accounts']);
const FB_RESERVED = new Set([
  'profile.php',
  'sharer',
  'share',
  'sharer.php',
  'groups',
  'events',
  'pages',
  'watch',
  'login',
  'home.php',
  'marketplace',
  'dialog',
]);
const X_RESERVED = new Set([
  'intent',
  'share',
  'home',
  'i',
  'search',
  'hashtag',
  'explore',
  'settings',
]);
const GH_RESERVED = new Set([
  'orgs',
  'sponsors',
  'features',
  'topics',
  'about',
  'pricing',
  'marketplace',
  'settings',
  'login',
]);

/**
 * Recognises a link (rule 1).
 * @param raw - URL as written (scheme optional)
 * @returns The link, or null when it is not an http(s) URL
 */
export const classifyUrl = (raw: string | null | undefined): ProfileLinkInfo | null => {
  const text = clean(raw);
  if (!text) {
    return null;
  }
  let u: URL;
  try {
    // A scheme is "x://"; "example.com:8080/x" is a host with a port
    u = new URL(
      /^[a-z][a-z0-9+.-]*:\/\//i.test(text) || /^mailto:/i.test(text) ? text : `https://${text}`,
    );
  } catch {
    return null;
  }
  if (u.protocol !== 'http:' && u.protocol !== 'https:') {
    return null;
  }
  const host = u.hostname
    .toLowerCase()
    .replace(/^(www|m|mobile)\./, '')
    .replace(/^[a-z]{2}\.(linkedin\.com)$/, '$1');
  if (!host.includes('.')) {
    return null;
  }
  const parts = u.pathname.split('/').filter(Boolean);
  const first = decode(parts[0] ?? '');
  const path = parts.length ? `/${parts.join('/')}` : '';
  const make = (network: Network, handle: string, url: string, page = false): ProfileLinkInfo => ({
    network,
    handle,
    url,
    key: `${network}:${handle}`.toLowerCase(),
    ...(page ? { page } : {}),
  });

  if (host === 'linkedin.com') {
    if (first === 'in' && parts[1]) {
      const id = decode(parts[1]);
      return make('LinkedIn', id, `https://linkedin.com/in/${parts[1]}`);
    }
    return make('LinkedIn', path || '/', `https://linkedin.com${path}`, true);
  }
  if (host === 'facebook.com' || host === 'fb.com') {
    if (first === 'profile.php' && u.searchParams.get('id')) {
      const id = u.searchParams.get('id')!;
      return make('Facebook', id, `https://facebook.com/profile.php?id=${id}`);
    }
    if (first && !FB_RESERVED.has(first.toLowerCase())) {
      return make('Facebook', first, `https://facebook.com/${parts[0]}`);
    }
  }
  if (host === 'instagram.com' && first && !IG_RESERVED.has(first.toLowerCase())) {
    return make('Instagram', first, `https://instagram.com/${parts[0]}`);
  }
  if (
    (host === 'x.com' || host === 'twitter.com') &&
    first &&
    !X_RESERVED.has(first.toLowerCase())
  ) {
    return make('X', first, `https://x.com/${parts[0]}`);
  }
  if (host === 'github.com' && first && !GH_RESERVED.has(first.toLowerCase())) {
    return make('GitHub', first, `https://github.com/${parts[0]}`);
  }
  if (host === 'tiktok.com' && first.startsWith('@')) {
    return make('TikTok', first.slice(1), `https://tiktok.com/${parts[0]}`);
  }
  if (host === 'youtube.com' && first) {
    if (first.startsWith('@')) {
      return make('YouTube', first.slice(1), `https://youtube.com/${parts[0]}`);
    }
    if (['c', 'channel', 'user'].includes(first) && parts[1]) {
      return make('YouTube', decode(parts[1]), `https://youtube.com/${parts[0]}/${parts[1]}`);
    }
  }
  if (host === 't.me' && first) {
    return make('Telegram', first, `https://t.me/${parts[0]}`);
  }
  if (host === 'medium.com' && first.startsWith('@')) {
    return make('Medium', first.slice(1), `https://medium.com/${parts[0]}`);
  }
  if (host.endsWith('.medium.com') && host !== 'www.medium.com') {
    const name = host.slice(0, -'.medium.com'.length);
    return make('Medium', name, `https://${host}`);
  }
  if (host === 'wa.me' && first) {
    return make('WhatsApp', first, `https://wa.me/${parts[0]}`);
  }
  const site = `${host}${path}`.replace(/\/$/, '');
  return {
    network: 'Website',
    handle: host,
    url: `https://${site}`,
    key: `website:${site}`.toLowerCase(),
  };
};

/**
 * The profiles on a contact (rule 2): its links, and WhatsApp for its mobile numbers.
 * @param c - Contact
 * @returns Links
 */
export const profilesOnContact = (c: Contact): ProfileLinkInfo[] => {
  const links = (c.urls ?? [])
    .map((u) => classifyUrl(u.value))
    .filter((l): l is ProfileLinkInfo => !!l);
  (c.phoneNumbers ?? []).forEach((p) => {
    const kind = classifyPhone(p.value, p.canonicalForm);
    if (kind === 'il-mobile' || kind === 'international') {
      const href = whatsappHref(p.value ?? '', p.canonicalForm);
      const link = href ? classifyUrl(href) : null;
      if (link) {
        links.push(link);
      }
    }
  });
  return links;
};

/** Gravatar's links of a profile, recognised (its own page as a Website) */
export const profilesOfGravatar = (g: GravatarProfile): ProfileLinkInfo[] =>
  [...(g.verifiedAccounts ?? []), ...(g.links ?? [])]
    .map((a) => classifyUrl(a.url))
    .concat(g.profileUrl ? [classifyUrl(g.profileUrl)] : [])
    .filter((l): l is ProfileLinkInfo => !!l);

/**
 * The signature links worth keeping (rule 4): social profiles, and websites on (or under)
 * the domain of one of the person's addresses.
 * @param urls - Links from the signatures
 * @param c - The contact
 * @returns Links
 */
export const profilesOfSignature = (urls: string[], c: Contact): ProfileLinkInfo[] => {
  const domains = (c.emailAddresses ?? [])
    .map((e) => clean(e.value).toLowerCase().split('@')[1] ?? '')
    .filter(Boolean);
  return urls
    .map(classifyUrl)
    .filter((l): l is ProfileLinkInfo => !!l)
    .filter(
      (l) =>
        (l.network !== 'Website' && !l.page) ||
        domains.some((d) => l.handle === d || l.handle.endsWith(`.${d}`)),
    );
};

/**
 * A contact's profiles merged by key (rule 6), with their sources, those on the contact first.
 * @param c - The contact
 * @param found - Links found elsewhere, by source
 * @returns Profiles
 */
export const mergeProfiles = (
  c: Contact,
  found: { source: ProfileSource; links: ProfileLinkInfo[] }[],
): ContactProfile[] => {
  const onContact = new Set(
    (c.urls ?? []).map((u) => classifyUrl(u.value)?.key).filter((k): k is string => !!k),
  );
  const by = new Map<string, ContactProfile>();
  [{ source: 'contact' as ProfileSource, links: profilesOnContact(c) }, ...found].forEach(
    ({ source, links }) =>
      links.forEach((l) => {
        const had = by.get(l.key);
        if (had) {
          if (!had.sources.includes(source)) {
            had.sources.push(source);
          }
        } else {
          by.set(l.key, {
            ...l,
            sources: [source],
            // What comes from the contact (its links, its phones' WhatsApp) is on it already
            onContact: onContact.has(l.key) || source === 'contact',
          });
        }
      }),
  );
  return [...by.values()].sort((a, b) => Number(b.onContact) - Number(a.onContact));
};

/**
 * The SHA-256 hash Gravatar keys an address by: of the trimmed, lower-case address.
 * @param email - Address
 * @returns 64 hex characters
 */
export const emailHash = async (email: string): Promise<string> => {
  const bytes = new TextEncoder().encode(email.trim().toLowerCase());
  const digest = await crypto.subtle.digest('SHA-256', bytes);
  return Array.from(new Uint8Array(digest))
    .map((b) => b.toString(16).padStart(2, '0'))
    .join('');
};
