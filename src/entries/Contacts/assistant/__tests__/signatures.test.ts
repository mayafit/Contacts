import { signatureChange, withScheme } from '../signatures';
import type { Contact } from '../../types/Contact';

const motti = {
  resourceName: 'people/m',
  etag: 'e',
  names: [{ givenName: 'מוטי', familyName: 'גת' }],
  phoneNumbers: [{ value: '054-123-4567', canonicalForm: '+972541234567' }],
  organizations: [{ name: 'Acme', title: 'Engineer' }],
  urls: [{ value: 'https://www.acme.co.il/' }],
} as Contact;

describe('signature changes', () => {
  it('adds new phones and links, fills empty fields, reports different ones', () => {
    const r = signatureChange(motti, {
      phones: [
        { value: '+972-54-123-4567', type: 'Mobile' },
        { value: '03-555-1212', type: 'Office' },
        { value: '03 555 1212', type: 'Office' },
        { value: '1-700', type: '' },
      ],
      organization: 'acme',
      jobTitle: 'VP R&D',
      department: 'R&D',
      websites: ['www.acme.co.il'],
      linkedin: 'linkedin.com/in/mottigat',
      address: 'Herzliya',
    });
    expect(r.change.addPhones).toEqual([{ value: '03-555-1212', type: 'work' }]);
    expect(r.change.addUrls).toEqual([
      { value: 'https://linkedin.com/in/mottigat', type: 'profile' },
    ]);
    expect(r.change.set).toEqual({ department: 'R&D' });
    expect(r.conflicts).toEqual([
      { field: 'jobTitle', label: 'Job title', current: 'Engineer', found: 'VP R&D' },
    ]);
    expect(r.address).toBe('Herzliya');
  });

  it("does not put a new employer's title on the old one", () => {
    const acme = { ...motti, organizations: [{ name: 'Acme' }] } as Contact;
    const r = signatureChange(acme, { organization: 'Globex', jobTitle: 'CTO' });
    expect(r.change.set).toBeUndefined();
    expect(r.conflicts.map((c) => c.field)).toEqual(['organization', 'jobTitle']);
  });

  it('maps Hebrew phone labels and adds nothing for an empty signature', () => {
    const bare = { resourceName: 'people/d', etag: 'e' } as Contact;
    expect(
      signatureChange(bare, { phones: [{ value: '052-7654321', type: 'נייד' }] }).change.addPhones,
    ).toEqual([{ value: '052-7654321', type: 'mobile' }]);
    expect(signatureChange(bare, {}).change).toEqual({ id: 'people/d' });
    expect(withScheme('acme.co.il')).toBe('https://acme.co.il');
  });
});
