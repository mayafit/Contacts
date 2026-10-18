/**
 * @fileoverview Tests for organization suggestions from email domains
 */

import type { Contact } from '../../types/Contact';
import {
  analyzeOrganizations,
  domainOf,
  isWorkDomain,
  orgNameFromDomain,
  withOrganization,
} from '../organizations';

const c = (id: string, email: string, org?: string, extra: Partial<Contact> = {}): Contact => ({
  resourceName: `people/${id}`,
  names: [{ displayName: id }],
  emailAddresses: [{ value: email }],
  organizations: org ? [{ name: org }] : undefined,
  ...extra,
});

describe('domain helpers', () => {
  it('extracts and classifies domains', () => {
    expect(domainOf(' Dana@ELTA.co.il ')).toBe('elta.co.il');
    expect(domainOf('not-an-email')).toBe('');
    expect(isWorkDomain('elta.co.il')).toBe(true);
    expect(isWorkDomain('gmail.com')).toBe(false);
    expect(isWorkDomain('walla.co.il')).toBe(false);
  });

  it('derives a readable organization name', () => {
    expect(orgNameFromDomain('elta.co.il')).toBe('Elta');
    expect(orgNameFromDomain('mail.intel.com')).toBe('Intel');
    expect(orgNameFromDomain('iai.co.il')).toBe('IAI');
    expect(orgNameFromDomain('check-point.com')).toBe('Check Point');
  });
});

describe('analyzeOrganizations', () => {
  const contacts = [
    c('a', 'a@elta.co.il', 'Elta Systems'),
    c('b', 'b@elta.co.il', 'Elta Systems'),
    c('d', 'd@elta.co.il'),
    c('e', 'e@elta.co.il', 'IAI'),
    c('g', 'g@gmail.com'),
    c('w', 'w@wix.com'),
  ];
  const insights = analyzeOrganizations(contacts);

  it('suggests the name most contacts at the domain already use', () => {
    const elta = insights.find((i) => i.domain === 'elta.co.il')!;
    expect(elta.suggested).toBe('Elta Systems');
    expect(elta.fromContacts).toBe(true);
    expect(elta.members).toHaveLength(4);
    expect(elta.needsUpdate.map((n) => [n.contact.resourceName, n.reason])).toEqual([
      ['people/d', 'missing'],
      ['people/e', 'different'],
    ]);
  });

  it('falls back to the domain name and ignores personal mail', () => {
    const wix = insights.find((i) => i.domain === 'wix.com')!;
    expect(wix.suggested).toBe('Wix');
    expect(wix.fromContacts).toBe(false);
    expect(insights.some((i) => i.domain === 'gmail.com')).toBe(false);
  });

  it('lists domains needing updates first', () => {
    expect(insights[0].domain).toBe('elta.co.il');
  });
});

describe('withOrganization', () => {
  it('names an unnamed entry and keeps its other fields', () => {
    expect(
      withOrganization({ resourceName: 'x', organizations: [{ title: 'Engineer' }] }, 'Elta'),
    ).toEqual([{ title: 'Engineer', name: 'Elta' }]);
  });

  it('adds the organization first and keeps the existing ones', () => {
    expect(
      withOrganization(
        { resourceName: 'x', organizations: [{ name: 'IAI', title: 'PM' }] },
        'Elta',
      ),
    ).toEqual([{ name: 'Elta' }, { name: 'IAI', title: 'PM' }]);
  });

  it('changes nothing when the contact already has it', () => {
    const orgs = [{ name: 'elta' }];
    expect(withOrganization({ resourceName: 'x', organizations: orgs }, 'Elta')).toEqual(orgs);
  });
});
