import { webcrypto } from 'crypto';
import type { Contact } from '../../types/Contact';
import {
  classifyUrl,
  emailHash,
  mergeProfiles,
  profilesOfGravatar,
  profilesOfSignature,
} from '../profiles';
import { colleaguesOf } from '../dossier';
import type { ActivityIndex, ContactActivity } from '../activity';

const k = (url: string) => {
  const l = classifyUrl(url);
  return l ? [l.network, l.handle, l.url] : null;
};

describe('recognising links (AD-46 rule 1)', () => {
  it('knows each network and its handle', () => {
    expect(k('https://il.linkedin.com/in/dana-cohen-123/')).toEqual([
      'LinkedIn',
      'dana-cohen-123',
      'https://linkedin.com/in/dana-cohen-123',
    ]);
    expect(k('www.facebook.com/dana.cohen?ref=bookmarks')).toEqual([
      'Facebook',
      'dana.cohen',
      'https://facebook.com/dana.cohen',
    ]);
    expect(k('https://m.facebook.com/profile.php?id=1000123&x=1')).toEqual([
      'Facebook',
      '1000123',
      'https://facebook.com/profile.php?id=1000123',
    ]);
    expect(k('instagram.com/dana_c')?.[0]).toBe('Instagram');
    expect(k('https://twitter.com/dana')).toEqual(['X', 'dana', 'https://x.com/dana']);
    expect(k('https://github.com/dana/repo')).toEqual([
      'GitHub',
      'dana',
      'https://github.com/dana',
    ]);
    expect(k('https://www.tiktok.com/@dana')?.slice(0, 2)).toEqual(['TikTok', 'dana']);
    expect(k('https://youtube.com/@dana')?.slice(0, 2)).toEqual(['YouTube', 'dana']);
    expect(k('https://youtube.com/channel/UC123')?.slice(0, 2)).toEqual(['YouTube', 'UC123']);
    expect(k('t.me/dana')?.[0]).toBe('Telegram');
    expect(k('https://medium.com/@dana')?.slice(0, 2)).toEqual(['Medium', 'dana']);
    expect(k('https://dana.medium.com')?.slice(0, 2)).toEqual(['Medium', 'dana']);
    expect(k('https://wa.me/972541234567')?.[0]).toBe('WhatsApp');
    expect(k('https://www.acme.com/team/dana/')).toEqual([
      'Website',
      'acme.com',
      'https://acme.com/team/dana',
    ]);
  });

  it('does not take share and system pages for people', () => {
    expect(k('https://facebook.com/sharer/sharer.php?u=x')?.[0]).toBe('Website');
    expect(k('https://twitter.com/intent/tweet')?.[0]).toBe('Website');
    expect(k('https://github.com/orgs/acme')?.[0]).toBe('Website');
    expect(classifyUrl('https://linkedin.com/company/acme')?.page).toBe(true);
    expect(classifyUrl('mailto:dana@x.com')).toBeNull();
    // Not valid percent-encoding, a port, profile.php without an id, Instagram posts
    expect(k('https://example.com/50%off')?.[0]).toBe('Website');
    expect(k('example.com:8080/team')).toEqual([
      'Website',
      'example.com',
      'https://example.com/team',
    ]);
    expect(k('https://facebook.com/profile.php')?.[0]).toBe('Website');
    expect(k('https://instagram.com/p/Cx123')?.[0]).toBe('Website');
    expect(classifyUrl('not a url')).toBeNull();
    expect(classifyUrl('')).toBeNull();
  });
});

describe('a contact’s profiles (AD-46 rules 2, 4, 6)', () => {
  const dana = {
    resourceName: 'people/1',
    urls: [{ value: 'linkedin.com/in/dana' }],
    phoneNumbers: [{ value: '054-123-4567' }, { value: '03-1234567' }],
    emailAddresses: [{ value: 'dana@acme.com' }],
  } as Contact;

  it('merges by key, with the sources, those on the contact first', () => {
    const profiles = mergeProfiles(dana, [
      {
        source: 'gravatar',
        links: profilesOfGravatar({
          verifiedAccounts: [
            { label: 'LinkedIn', url: 'https://www.linkedin.com/in/dana/' },
            { label: 'GitHub', url: 'https://github.com/dana' },
          ],
          profileUrl: 'https://gravatar.com/dana',
        }),
      },
    ]);
    const rows = profiles.map((p) => [p.network, p.sources.join('+'), p.onContact]);
    expect(rows).toEqual([
      ['LinkedIn', 'contact+gravatar', true],
      // Only the mobile number is a WhatsApp link
      ['WhatsApp', 'contact', true],
      ['GitHub', 'gravatar', false],
      ['Website', 'gravatar', false],
    ]);
  });

  it('offers a WhatsApp link found elsewhere, but not the phones’ own', () => {
    const p = mergeProfiles({ resourceName: 'p/9', phoneNumbers: [] } as Contact, [
      { source: 'signature', links: [classifyUrl('https://wa.me/972501112233')!] },
    ]);
    expect(p.map((x) => [x.network, x.onContact])).toEqual([['WhatsApp', false]]);
  });

  it('keeps from signatures the social links and their own company’s site', () => {
    const kept = profilesOfSignature(
      [
        'https://x.com/dana',
        'https://www.acme.com',
        'https://shop.acme.com/x',
        'https://newsletter.example.com/promo',
        'https://linkedin.com/company/acme',
      ],
      dana,
    );
    expect(kept.map((l) => l.url)).toEqual([
      'https://x.com/dana',
      'https://acme.com',
      'https://shop.acme.com/x',
    ]);
  });

  it('hashes an address as Gravatar does', async () => {
    Object.defineProperty(globalThis, 'crypto', { value: webcrypto, configurable: true });
    // SHA-256 of "dana@acme.com" (trimmed, lower case)
    expect(await emailHash('  Dana@Acme.com ')).toBe(
      'e82f07aaf15210e285b1ffb3e6238db4525ca69a166c885ab864caf18aa2b3c2',
    );
    expect(await emailHash('dana@acme.com')).toMatch(/^[0-9a-f]{64}$/);
  });
});

describe('colleagues (AD-46 rule 6)', () => {
  const c = (id: string, org: string, email: string): Contact =>
    ({
      resourceName: id,
      names: [{ displayName: id }],
      organizations: org ? [{ name: org }] : [],
      emailAddresses: [{ value: email }],
    }) as Contact;

  it('are the same organization or work domain, most in touch first', () => {
    const me = c('me', 'Elta Systems Ltd', 'me@elta.co.il');
    const all = [
      me,
      c('a', 'ELTA SYSTEMS', 'a@gmail.com'),
      c('b', '', 'b@elta.co.il'),
      c('z', 'Other', 'z@gmail.com'),
    ];
    const activity: ActivityIndex = new Map([['b', { score: 10 } as ContactActivity]]);
    expect(colleaguesOf(me, all, activity).map((x) => [x.contact.resourceName, x.via])).toEqual([
      ['b', '@elta.co.il'],
      ['a', 'ELTA SYSTEMS'],
    ]);
    // A personal address alone makes no colleagues
    expect(colleaguesOf(c('p', '', 'p@gmail.com'), all, null)).toEqual([]);
  });
});
