namespace ContactsApi.Core.Entities;

/// <summary>
/// Contact entity representing a Google People API contact.
/// </summary>
/// <remarks>
/// Used both for reads and as the body of an update. On update, a group that is
/// <c>null</c> means "not changed" and is left out of Google's updatePersonFields,
/// so an edit to one field can never clear another. An empty list clears that
/// group. Element types carry every writable sub-field Google returns, so a group
/// that is edited round-trips without losing data the UI does not show.
/// </remarks>
public class Contact
{
    /// <summary>Resource name (e.g., "people/c123")</summary>
    public string ResourceName { get; set; } = string.Empty;

    /// <summary>Version tag; required by Google on update to detect concurrent edits</summary>
    public string? Etag { get; set; }

    /// <summary>Names (Google allows one per contact)</summary>
    public List<ContactName>? Names { get; set; }

    /// <summary>Nicknames</summary>
    public List<ContactNickname>? Nicknames { get; set; }

    /// <summary>Phone numbers</summary>
    public List<ContactPhone>? PhoneNumbers { get; set; }

    /// <summary>Email addresses</summary>
    public List<ContactEmail>? EmailAddresses { get; set; }

    /// <summary>Physical addresses</summary>
    public List<ContactAddress>? Addresses { get; set; }

    /// <summary>Organizations and job titles</summary>
    public List<ContactOrganization>? Organizations { get; set; }

    /// <summary>Birthdays (Google allows one per contact)</summary>
    public List<ContactBirthday>? Birthdays { get; set; }

    /// <summary>Websites</summary>
    public List<ContactUrl>? Urls { get; set; }

    /// <summary>Notes (Google allows one per contact)</summary>
    public List<ContactBiography>? Biographies { get; set; }

    /// <summary>Relations to other people</summary>
    public List<ContactRelation>? Relations { get; set; }

    /// <summary>User-defined key/value fields</summary>
    public List<ContactUserDefined>? UserDefined { get; set; }

    /// <summary>Photos (read-only here)</summary>
    public List<ContactPhoto>? Photos { get; set; }

    /// <summary>Contact group (label) resource names, e.g. "contactGroups/myContacts" (read-only here)</summary>
    public List<string>? Labels { get; set; }

    /// <summary>Last time the contact changed in Google, ISO 8601 (read-only)</summary>
    public string? UpdateTime { get; set; }
}

/// <summary>Contact name information</summary>
public class ContactName
{
    /// <summary>Full display name (computed by Google; read-only)</summary>
    public string? DisplayName { get; set; }

    /// <summary>Prefix, e.g. "Dr."</summary>
    public string? HonorificPrefix { get; set; }

    /// <summary>Given name (first name)</summary>
    public string? GivenName { get; set; }

    /// <summary>Middle name</summary>
    public string? MiddleName { get; set; }

    /// <summary>Family name (last name)</summary>
    public string? FamilyName { get; set; }

    /// <summary>Suffix, e.g. "Jr."</summary>
    public string? HonorificSuffix { get; set; }

    /// <summary>Phonetic full name (as Google reports it)</summary>
    public string? PhoneticFullName { get; set; }

    /// <summary>Phonetic given name</summary>
    public string? PhoneticGivenName { get; set; }

    /// <summary>Phonetic middle name</summary>
    public string? PhoneticMiddleName { get; set; }

    /// <summary>Phonetic family name</summary>
    public string? PhoneticFamilyName { get; set; }
}

/// <summary>Contact nickname</summary>
public class ContactNickname
{
    /// <summary>Nickname value</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Type (DEFAULT, MAIDEN_NAME, INITIALS, ...)</summary>
    public string? Type { get; set; }
}

/// <summary>Contact phone number</summary>
public class ContactPhone
{
    /// <summary>Phone number as entered</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Type (mobile, work, home, or a custom label)</summary>
    public string? Type { get; set; }

    /// <summary>E.164 form computed by Google when parseable (read-only)</summary>
    public string? CanonicalForm { get; set; }
}

/// <summary>Contact email address</summary>
public class ContactEmail
{
    /// <summary>Email address</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Type (home, work, other, or a custom label)</summary>
    public string? Type { get; set; }

    /// <summary>Display name for the address</summary>
    public string? DisplayName { get; set; }
}

/// <summary>Contact physical address</summary>
public class ContactAddress
{
    /// <summary>Unstructured address as Google formats it</summary>
    public string? FormattedValue { get; set; }

    /// <summary>Type (home, work, other, or a custom label)</summary>
    public string? Type { get; set; }

    /// <summary>Street address</summary>
    public string? StreetAddress { get; set; }

    /// <summary>Extended address, e.g. apartment number</summary>
    public string? ExtendedAddress { get; set; }

    /// <summary>P.O. box</summary>
    public string? PoBox { get; set; }

    /// <summary>City</summary>
    public string? City { get; set; }

    /// <summary>Region / state</summary>
    public string? Region { get; set; }

    /// <summary>Postal code</summary>
    public string? PostalCode { get; set; }

    /// <summary>Country name</summary>
    public string? Country { get; set; }

    /// <summary>ISO 3166-1 alpha-2 country code</summary>
    public string? CountryCode { get; set; }
}

/// <summary>Contact organization</summary>
public class ContactOrganization
{
    /// <summary>Organization name</summary>
    public string? Name { get; set; }

    /// <summary>Job title</summary>
    public string? Title { get; set; }

    /// <summary>Department</summary>
    public string? Department { get; set; }

    /// <summary>Type (work, school, or a custom label)</summary>
    public string? Type { get; set; }

    /// <summary>Job description</summary>
    public string? JobDescription { get; set; }

    // The fields below are not edited by the UI; they are carried so that saving
    // the organizations group never wipes them.

    /// <summary>Office location</summary>
    public string? Location { get; set; }

    /// <summary>Stock symbol</summary>
    public string? Symbol { get; set; }

    /// <summary>Organization domain</summary>
    public string? Domain { get; set; }

    /// <summary>Phonetic organization name</summary>
    public string? PhoneticName { get; set; }

    /// <summary>Cost center</summary>
    public string? CostCenter { get; set; }

    /// <summary>Whether this is the current organization</summary>
    public bool? Current { get; set; }

    /// <summary>Start date</summary>
    public ContactDate? StartDate { get; set; }

    /// <summary>End date</summary>
    public ContactDate? EndDate { get; set; }

    /// <summary>Full-time equivalent in millipercent</summary>
    public int? FullTimeEquivalentMillipercent { get; set; }
}

/// <summary>Calendar date where any component may be absent (e.g. a birthday with no year)</summary>
public class ContactDate
{
    /// <summary>Year, or null/0 when unknown</summary>
    public int? Year { get; set; }

    /// <summary>Month 1-12</summary>
    public int? Month { get; set; }

    /// <summary>Day of month</summary>
    public int? Day { get; set; }
}

/// <summary>Contact birthday</summary>
public class ContactBirthday
{
    /// <summary>Structured date</summary>
    public ContactDate? Date { get; set; }

    /// <summary>Free-text birthday when no structured date exists</summary>
    public string? Text { get; set; }
}

/// <summary>Contact website</summary>
public class ContactUrl
{
    /// <summary>URL</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Type (home, work, blog, or a custom label)</summary>
    public string? Type { get; set; }
}

/// <summary>Contact notes</summary>
public class ContactBiography
{
    /// <summary>Note text</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Content type (TEXT_PLAIN or TEXT_HTML)</summary>
    public string? ContentType { get; set; }
}

/// <summary>Relation to another person</summary>
public class ContactRelation
{
    /// <summary>Name of the related person</summary>
    public string Person { get; set; } = string.Empty;

    /// <summary>Type (spouse, child, mother, or a custom label)</summary>
    public string? Type { get; set; }
}

/// <summary>User-defined key/value field</summary>
public class ContactUserDefined
{
    /// <summary>Field key</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Field value</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>Contact photo</summary>
public class ContactPhoto
{
    /// <summary>Photo URL</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>True when Google shows a generated placeholder rather than a real photo</summary>
    public bool IsDefault { get; set; }
}

/// <summary>Contact group (label)</summary>
public class ContactGroup
{
    /// <summary>Resource name, e.g. "contactGroups/abc"</summary>
    public string ResourceName { get; set; } = string.Empty;

    /// <summary>Human-readable name (Google's formatted name for system groups)</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>USER_CONTACT_GROUP or SYSTEM_CONTACT_GROUP</summary>
    public string? GroupType { get; set; }

    /// <summary>Number of members</summary>
    public int MemberCount { get; set; }
}
