namespace ContactsApi.Core.Entities;

/// <summary>
/// Contact entity representing a Google People API contact
/// </summary>
public class Contact
{
    /// <summary>
    /// Resource name (e.g., "people/123")
    /// </summary>
    public string ResourceName { get; set; } = string.Empty;

    /// <summary>
    /// Contact names
    /// </summary>
    public List<ContactName>? Names { get; set; }

    /// <summary>
    /// Contact phone numbers
    /// </summary>
    public List<ContactPhone>? PhoneNumbers { get; set; }

    /// <summary>
    /// Contact email addresses
    /// </summary>
    public List<ContactEmail>? EmailAddresses { get; set; }

    /// <summary>
    /// Contact physical addresses
    /// </summary>
    public List<ContactAddress>? Addresses { get; set; }
}

/// <summary>
/// Contact name information
/// </summary>
public class ContactName
{
    /// <summary>
    /// Given name (first name)
    /// </summary>
    public string? GivenName { get; set; }

    /// <summary>
    /// Family name (last name)
    /// </summary>
    public string? FamilyName { get; set; }

    /// <summary>
    /// Display name (full name)
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Phonetic representation of full name
    /// </summary>
    public string? PhoneticFullName { get; set; }
}

/// <summary>
/// Contact phone number
/// </summary>
public class ContactPhone
{
    /// <summary>
    /// Phone number value
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Phone type (mobile, work, home, etc.)
    /// </summary>
    public string? Type { get; set; }
}

/// <summary>
/// Contact email address
/// </summary>
public class ContactEmail
{
    /// <summary>
    /// Email address value
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Email type (work, home, etc.)
    /// </summary>
    public string? Type { get; set; }
}

/// <summary>
/// Contact physical address
/// </summary>
public class ContactAddress
{
    /// <summary>
    /// Street address
    /// </summary>
    public string? Street { get; set; }

    /// <summary>
    /// City
    /// </summary>
    public string? City { get; set; }

    /// <summary>
    /// Country
    /// </summary>
    public string? Country { get; set; }

    /// <summary>
    /// Postal code
    /// </summary>
    public string? PostalCode { get; set; }
}
