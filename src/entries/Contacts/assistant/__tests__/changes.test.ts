import { newContactPatch, parseBirthday, resolveChange } from '../changes';
import { buildPatch } from '../../components/card/cardModel';
import type { Contact, ContactGroup } from '../../types/Contact';

const groups: Record<string, ContactGroup> = {
  'contactGroups/work': {
    resourceName: 'contactGroups/work',
    name: 'Work',
    groupType: 'USER_CONTACT_GROUP',
  },
};

const dana: Contact = {
  resourceName: 'people/c1',
  etag: 'etag1',
  names: [{ givenName: 'דנה', familyName: 'כהן', displayName: 'דנה כהן' }],
  emailAddresses: [{ value: 'Dana@Elta.co.il', type: 'work' }],
  phoneNumbers: [{ value: '054-123-4567', type: 'mobile' }],
  organizations: [{ name: 'Elta', title: 'Engineer', location: 'Ashdod' }],
  labels: ['contactGroups/work'],
} as Contact;

describe('assistant changes', () => {
  it('sets phonetic names and keeps the rest of the name', () => {
    const r = resolveChange(
      dana,
      { id: dana.resourceName, set: { phoneticGivenName: 'Dana', phoneticFamilyName: 'Cohen' } },
      groups,
    );
    expect(r.draft.names?.[0]).toMatchObject({
      givenName: 'דנה',
      familyName: 'כהן',
      phoneticGivenName: 'Dana',
      phoneticFamilyName: 'Cohen',
      displayName: null,
    });
    expect(r.diff.map((d) => d.field)).toEqual(['Phonetic first name', 'Phonetic last name']);
    const patch = buildPatch(dana, r.draft, dana.etag);
    expect(Object.keys(patch ?? {}).sort()).toEqual(['etag', 'names']);
  });

  it('changes the organization without losing its other fields', () => {
    const r = resolveChange(
      dana,
      { id: dana.resourceName, set: { jobTitle: 'Team lead' } },
      groups,
    );
    expect(r.draft.organizations?.[0]).toMatchObject({
      name: 'Elta',
      title: 'Team lead',
      location: 'Ashdod',
    });
    expect(r.diff).toEqual([{ field: 'Job title', from: 'Engineer', to: 'Team lead' }]);
  });

  it('adds and removes emails and phones, skipping ones already there', () => {
    const r = resolveChange(
      dana,
      {
        id: dana.resourceName,
        addEmails: [{ value: 'dana@elta.co.il' }, { value: 'dana@gmail.com', type: 'home' }],
        removePhones: ['+972 54 123 4567'],
        addPhones: [{ value: '+972-52-000-0000' }],
      },
      groups,
    );
    expect(r.draft.emailAddresses?.map((e) => e.value)).toEqual([
      'Dana@Elta.co.il',
      'dana@gmail.com',
    ]);
    expect(r.draft.phoneNumbers?.map((p) => p.value)).toEqual(['+972-52-000-0000']);
    expect(r.warnings).toEqual([]);
  });

  it('reports labels to add and remove by name', () => {
    const r = resolveChange(
      dana,
      { id: dana.resourceName, addLabels: ['Friends', 'Work'], removeLabels: ['Work', 'Family'] },
      groups,
    );
    expect(r.addLabels).toEqual(['Friends']);
    expect(r.removeLabels).toEqual(['Work']);
    expect(r.warnings).toEqual(['Not labelled "Family"']);
    expect(buildPatch(dana, r.draft, dana.etag)).toBeNull();
  });

  it('parses birthdays with and without a year', () => {
    expect(parseBirthday('1985-03-07')).toEqual({ year: 1985, month: 3, day: 7 });
    expect(parseBirthday('--03-07')).toEqual({ month: 3, day: 7 });
    expect(parseBirthday('13/2020')).toBeNull();
    const r = resolveChange(dana, { id: dana.resourceName, set: { birthday: '--12-25' } }, groups);
    expect(r.draft.birthdays).toEqual([{ date: { month: 12, day: 25 } }]);
  });

  it('builds a new contact from fields', () => {
    const { patch, diff } = newContactPatch(
      { givenName: 'Avi', familyName: 'Levi', organization: 'IAI' },
      [{ value: 'avi@iai.co.il' }],
    );
    expect(patch.names?.[0]).toMatchObject({ givenName: 'Avi', familyName: 'Levi' });
    expect(patch.emailAddresses).toEqual([{ value: 'avi@iai.co.il', type: 'other' }]);
    expect(patch.organizations).toEqual([{ name: 'IAI' }]);
    expect(diff.length).toBe(4);
  });
});
