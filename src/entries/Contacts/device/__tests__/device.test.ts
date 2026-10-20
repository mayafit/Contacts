import {
  compareWithDevice,
  internationalDigits,
  mailtoHref,
  platformOf,
  smsHref,
  telHref,
  toVCard,
  whatsappHref,
} from '../device';
import type { Contact } from '../../types/Contact';

const dana = {
  resourceName: 'people/1',
  etag: 'e',
  names: [
    {
      givenName: 'דנה',
      familyName: 'כהן',
      phoneticGivenName: 'Dana',
      phoneticFamilyName: 'Cohen',
      displayName: 'דנה כהן',
    },
  ],
  phoneNumbers: [{ value: '054-123-4567', type: 'mobile', canonicalForm: '+972541234567' }],
  emailAddresses: [{ value: 'dana@x.com', type: 'home' }],
  organizations: [{ name: 'Elta; Systems', title: 'Engineer' }],
  birthdays: [{ date: { month: 3, day: 7 } }],
} as Contact;

describe('device helpers', () => {
  it('detects the platform', () => {
    expect(platformOf('Mozilla/5.0 (Linux; Android 14; Pixel 8)')).toBe('android');
    expect(platformOf('Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)')).toBe('ios');
    expect(platformOf('Mozilla/5.0 (Windows NT 10.0; Win64; x64)')).toBe('desktop');
  });

  it('builds call, sms, WhatsApp and email links', () => {
    expect(telHref('054-123 4567')).toBe('tel:0541234567');
    expect(smsHref('054-1234567', 'hi there', 'android')).toBe('sms:0541234567?body=hi%20there');
    expect(smsHref('054-1234567', 'hi', 'ios')).toBe('sms:0541234567&body=hi');
    expect(whatsappHref('054-123-4567')).toBe('https://wa.me/972541234567');
    expect(whatsappHref('054-123-4567', '+972541234567', 'שלום')).toBe(
      `https://wa.me/972541234567?text=${encodeURIComponent('שלום')}`,
    );
    expect(whatsappHref('*3456')).toBe('');
    expect(internationalDigits('+1 (415) 555-0100')).toBe('14155550100');
    expect(internationalDigits('00 44 20 7946 0000')).toBe('442079460000');
    expect(mailtoHref('a@b.com', 'Hello', 'Line')).toBe('mailto:a@b.com?subject=Hello&body=Line');
  });

  it('writes a vCard with phonetic names and escaped values', () => {
    const v = toVCard(dana);
    expect(v).toContain('N:כהן;דנה;;;');
    expect(v).toContain('X-PHONETIC-FIRST-NAME:Dana');
    expect(v).toContain('TEL;TYPE=CELL:+972541234567');
    expect(v).toContain('ORG:Elta\\; Systems');
    expect(v).toContain('BDAY:--03-07');
    expect(v.startsWith('BEGIN:VCARD\r\nVERSION:3.0')).toBe(true);
  });

  it('compares phone contacts with Google contacts', () => {
    const r = compareWithDevice(
      [
        { name: ['Dana'], tel: ['+972 54-123-4567', '+972 52 999 8888'], email: [] },
        { name: ['Dana again'], tel: ['0541234567'], email: ['DANA@x.com'] },
        { name: ['New person'], tel: ['050-111-2222'], email: [] },
        { name: ['Nothing'], tel: [], email: [] },
      ],
      [dana],
    );
    expect(r.differences).toHaveLength(1);
    expect(r.differences[0].addPhones).toEqual(['+972 52 999 8888']);
    expect(r.same).toBe(1);
    expect(r.missing.map((m) => m.name[0])).toEqual(['New person']);
  });
});
