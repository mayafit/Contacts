using System.Net;
using Google.Apis.Auth.OAuth2;
using Google.Apis.PeopleService.v1;
using Google.Apis.PeopleService.v1.Data;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using ContactsApi.Core.Entities;
using ContactsApi.Core.Exceptions;
using ContactGroup = ContactsApi.Core.Entities.ContactGroup;

namespace ContactsApi.Infrastructure.Google;

/// <summary>
/// Client for interacting with Google People API
/// </summary>
public class GooglePeopleApiClient
{
    private readonly PeopleServiceService _peopleService;
    private readonly ILogger<GooglePeopleApiClient> _logger;

    /// <summary>Every person field the app reads</summary>
    private const string ReadPersonFields =
        "names,nicknames,phoneNumbers,emailAddresses,addresses,organizations,birthdays," +
        "urls,biographies,relations,userDefined,photos,memberships,metadata";

    /// <summary>
    /// Initializes a new instance of GooglePeopleApiClient
    /// </summary>
    /// <param name="accessToken">OAuth 2.0 access token</param>
    /// <param name="logger">Logger instance</param>
    public GooglePeopleApiClient(string accessToken, ILogger<GooglePeopleApiClient> logger)
    {
        _logger = logger;

        // Validate access token
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            _logger.LogError("Access token validation failed: token is null or empty");
            throw new GoogleApiException(
                GoogleApiErrorCodes.InvalidAccessToken,
                "Access token cannot be null or empty",
                statusCode: (int)HttpStatusCode.Unauthorized);
        }

        try
        {
            // Initialize OAuth2 credentials
            var credential = GoogleCredential.FromAccessToken(accessToken);

            // Initialize People API service
            _peopleService = new PeopleServiceService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "ContactsApi"
            });

            _logger.LogInformation("Google People API client initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Google People API client");
            throw new GoogleApiException(
                GoogleApiErrorCodes.AuthenticationFailed,
                "Failed to initialize Google People API client",
                ex);
        }
    }

    /// <summary>
    /// Fetches all contacts with pagination support
    /// </summary>
    /// <param name="pageSize">Number of contacts per page (max 1000)</param>
    /// <param name="pageToken">Token for next page</param>
    /// <returns>Tuple of contacts and next page token</returns>
    public async Task<(List<Contact> Contacts, string? NextPageToken)> FetchAllContactsAsync(
        int pageSize = 1000,
        string? pageToken = null)
    {
        try
        {
            _logger.LogInformation(
                "Fetching contacts from Google People API (pageSize: {PageSize}, hasPageToken: {HasPageToken})",
                pageSize,
                !string.IsNullOrEmpty(pageToken));

            var request = _peopleService.People.Connections.List("people/me");
            request.PersonFields = ReadPersonFields;
            // Contact data only. The default also merges in the person's Google
            // profile; those entries would then be written back as contact data
            // the first time a group is saved (e.g. the profile name replacing
            // the name the user gave the contact).
            request.Sources = PeopleResource.ConnectionsResource.ListRequest.SourcesEnum.READSOURCETYPECONTACT;
            request.PageSize = pageSize;
            if (!string.IsNullOrEmpty(pageToken))
            {
                request.PageToken = pageToken;
            }

            var response = await request.ExecuteAsync();

            var contacts = response.Connections?.Select(MapPersonToContact).ToList() ?? new List<Contact>();

            _logger.LogInformation(
                "Successfully fetched {Count} contacts (hasMore: {HasMore})",
                contacts.Count,
                !string.IsNullOrEmpty(response.NextPageToken));

            return (contacts, response.NextPageToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch contacts from Google People API");
            throw Translate(
                ex,
                GoogleApiErrorCodes.FetchContactsFailed,
                "Failed to fetch contacts from Google People API",
                new Dictionary<string, object>
                {
                    { "pageSize", pageSize },
                    { "hasPageToken", !string.IsNullOrEmpty(pageToken) }
                });
        }
    }

    /// <summary>
    /// Gets a single contact by resource name
    /// </summary>
    /// <param name="resourceName">Contact resource name (e.g., "people/123")</param>
    /// <returns>Contact entity</returns>
    public async Task<Contact> GetContactAsync(string resourceName)
    {
        try
        {
            _logger.LogInformation("Fetching contact {ResourceName}", resourceName);

            var request = _peopleService.People.Get(resourceName);
            request.PersonFields = ReadPersonFields;
            request.Sources = PeopleResource.GetRequest.SourcesEnum.READSOURCETYPECONTACT;

            var person = await request.ExecuteAsync();
            var contact = MapPersonToContact(person);

            _logger.LogInformation("Successfully fetched contact {ResourceName}", resourceName);

            return contact;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get contact {ResourceName}", resourceName);
            throw Translate(
                ex,
                GoogleApiErrorCodes.GetContactFailed,
                $"Failed to get contact {resourceName}",
                new Dictionary<string, object> { { "resourceName", resourceName } });
        }
    }

    /// <summary>
    /// Updates only the field groups present (non-null) in <paramref name="patch"/>.
    /// </summary>
    /// <param name="resourceName">Contact resource name</param>
    /// <param name="patch">Etag plus the groups to replace; null groups are left untouched</param>
    /// <returns>The full updated contact, including its new etag</returns>
    /// <exception cref="ArgumentException">No etag, or no groups to update</exception>
    public async Task<Contact> UpdateContactAsync(string resourceName, Contact patch)
    {
        if (string.IsNullOrWhiteSpace(patch.Etag))
        {
            throw new ArgumentException("etag is required to update a contact", nameof(patch));
        }

        var (person, updateFields) = BuildUpdate(patch);
        if (updateFields.Count == 0)
        {
            throw new ArgumentException("The update contains no fields", nameof(patch));
        }

        person.ResourceName = resourceName;
        person.ETag = patch.Etag;

        try
        {
            _logger.LogInformation(
                "Updating contact {ResourceName} fields {Fields}",
                resourceName,
                string.Join(",", updateFields));

            var request = _peopleService.People.UpdateContact(person, resourceName);
            request.UpdatePersonFields = string.Join(",", updateFields);
            request.PersonFields = ReadPersonFields;
            request.Sources = PeopleResource.UpdateContactRequest.SourcesEnum.READSOURCETYPECONTACT;

            var updatedPerson = await request.ExecuteAsync();

            _logger.LogInformation("Successfully updated contact {ResourceName}", resourceName);

            return MapPersonToContact(updatedPerson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update contact {ResourceName}", resourceName);
            throw Translate(
                ex,
                GoogleApiErrorCodes.UpdateContactFailed,
                $"Failed to update contact {resourceName}",
                new Dictionary<string, object>
                {
                    { "resourceName", resourceName },
                    { "fields", string.Join(",", updateFields) }
                });
        }
    }

    /// <summary>
    /// Person fields this app does not edit or merge. A contact deleted by a
    /// merge loses whatever it holds in these, so merge checks them first.
    /// </summary>
    private const string UnmergedPersonFields =
        "photos,events,imClients,sipAddresses,locations,occupations,interests,skills," +
        "miscKeywords,externalIds,calendarUrls,genders";

    /// <summary>
    /// Deletes one contact
    /// </summary>
    /// <param name="resourceName">Contact resource name</param>
    public async Task DeleteContactAsync(string resourceName)
    {
        try
        {
            _logger.LogInformation("Deleting contact {ResourceName}", resourceName);
            await _peopleService.People.DeleteContact(resourceName).ExecuteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete contact {ResourceName}", resourceName);
            throw Translate(
                ex,
                GoogleApiErrorCodes.DeleteContactFailed,
                $"Failed to delete contact {resourceName}",
                new Dictionary<string, object> { { "resourceName", resourceName } });
        }
    }

    /// <summary>
    /// Deletes many contacts, in Google's batches of at most 500
    /// </summary>
    /// <param name="resourceNames">Contacts to delete</param>
    /// <returns>Number deleted</returns>
    public async Task<int> BatchDeleteContactsAsync(IReadOnlyList<string> resourceNames)
    {
        var deleted = 0;
        try
        {
            foreach (var chunk in resourceNames.Chunk(500))
            {
                await _peopleService.People
                    .BatchDeleteContacts(new BatchDeleteContactsRequest { ResourceNames = chunk.ToList() })
                    .ExecuteAsync();
                deleted += chunk.Length;
            }

            _logger.LogInformation("Deleted {Count} contacts", deleted);
            return deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch delete failed after {Deleted} of {Total}", deleted, resourceNames.Count);
            throw Translate(
                ex,
                GoogleApiErrorCodes.DeleteContactFailed,
                $"Failed to delete contacts ({deleted} of {resourceNames.Count} were deleted)",
                new Dictionary<string, object> { { "deleted", deleted }, { "requested", resourceNames.Count } });
        }
    }

    /// <summary>
    /// Adds a contact to a contact group (label)
    /// </summary>
    /// <param name="groupResourceName">e.g. "contactGroups/abc"</param>
    /// <param name="resourceName">Contact to add</param>
    public async Task AddToGroupAsync(string groupResourceName, string resourceName)
    {
        try
        {
            await _peopleService.ContactGroups.Members
                .Modify(new ModifyContactGroupMembersRequest { ResourceNamesToAdd = new List<string> { resourceName } },
                    groupResourceName)
                .ExecuteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add {ResourceName} to {Group}", resourceName, groupResourceName);
            throw Translate(
                ex,
                GoogleApiErrorCodes.ModifyGroupFailed,
                $"Failed to add the contact to label {groupResourceName}",
                new Dictionary<string, object> { { "group", groupResourceName } });
        }
    }

    /// <summary>
    /// For each contact, the person fields it has data in that this app does
    /// not merge (a real photo, events, IM, ...). Empty when nothing would be lost.
    /// </summary>
    /// <param name="resourceNames">Contacts that a merge would delete</param>
    /// <returns>Resource name to field names, only for contacts with such data</returns>
    public async Task<Dictionary<string, List<string>>> FindUnmergedDataAsync(IEnumerable<string> resourceNames)
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var resourceName in resourceNames)
        {
            Person person;
            try
            {
                var request = _peopleService.People.Get(resourceName);
                request.PersonFields = UnmergedPersonFields;
                request.Sources = PeopleResource.GetRequest.SourcesEnum.READSOURCETYPECONTACT;
                person = await request.ExecuteAsync();
            }
            catch (Exception ex)
            {
                throw Translate(
                    ex,
                    GoogleApiErrorCodes.GetContactFailed,
                    $"Failed to check contact {resourceName} before merging",
                    new Dictionary<string, object> { { "resourceName", resourceName } });
            }

            var fields = new List<string>();
            void Check(string label, int count)
            {
                if (count > 0) fields.Add(label);
            }

            Check("photo", person.Photos?.Count(p => p.Default__ != true && FromContact(p.Metadata)) ?? 0);
            Check("events", person.Events?.Count(x => FromContact(x.Metadata)) ?? 0);
            Check("instant messaging", person.ImClients?.Count(x => FromContact(x.Metadata)) ?? 0);
            Check("SIP addresses", person.SipAddresses?.Count(x => FromContact(x.Metadata)) ?? 0);
            Check("locations", person.Locations?.Count(x => FromContact(x.Metadata)) ?? 0);
            Check("occupations", person.Occupations?.Count(x => FromContact(x.Metadata)) ?? 0);
            Check("interests", person.Interests?.Count(x => FromContact(x.Metadata)) ?? 0);
            Check("skills", person.Skills?.Count(x => FromContact(x.Metadata)) ?? 0);
            Check("keywords", person.MiscKeywords?.Count(x => FromContact(x.Metadata)) ?? 0);
            Check("external IDs", person.ExternalIds?.Count(x => FromContact(x.Metadata)) ?? 0);
            Check("calendar links", person.CalendarUrls?.Count(x => FromContact(x.Metadata)) ?? 0);
            Check("gender", person.Genders?.Count(x => FromContact(x.Metadata)) ?? 0);

            if (fields.Count > 0)
            {
                result[resourceName] = fields;
            }
        }

        return result;
    }

    /// <summary>
    /// Lists the user's contact groups (labels)
    /// </summary>
    /// <returns>All contact groups</returns>
    public async Task<List<ContactGroup>> ListContactGroupsAsync()
    {
        try
        {
            var groups = new List<ContactGroup>();
            string? pageToken = null;
            do
            {
                var request = _peopleService.ContactGroups.List();
                request.PageSize = 1000;
                request.GroupFields = "name,groupType,memberCount";
                request.PageToken = pageToken;

                var response = await request.ExecuteAsync();
                groups.AddRange(response.ContactGroups?.Select(g => new ContactGroup
                {
                    ResourceName = g.ResourceName ?? string.Empty,
                    Name = g.FormattedName ?? g.Name ?? string.Empty,
                    GroupType = g.GroupType,
                    MemberCount = g.MemberCount ?? 0
                }) ?? Enumerable.Empty<ContactGroup>());
                pageToken = response.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));

            _logger.LogInformation("Fetched {Count} contact groups", groups.Count);
            return groups;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list contact groups");
            throw Translate(ex, GoogleApiErrorCodes.FetchGroupsFailed, "Failed to list contact groups", null);
        }
    }

    /// <summary>
    /// Converts a Google SDK failure into a GoogleApiException with an HTTP status
    /// that tells the client what actually happened.
    /// </summary>
    private static GoogleApiException Translate(
        Exception ex,
        string defaultCode,
        string message,
        Dictionary<string, object>? context)
    {
        if (ex is GoogleApiException already)
        {
            return already;
        }

        if (ex is global::Google.GoogleApiException google)
        {
            var status = (int)google.HttpStatusCode;
            var googleMessage = google.Error?.Message ?? google.Message ?? string.Empty;

            // Google reports a stale etag as 400 FAILED_PRECONDITION, not 412.
            if (status == 412 || (status == 400 && googleMessage.Contains("etag", StringComparison.OrdinalIgnoreCase)))
            {
                return new GoogleApiException(
                    GoogleApiErrorCodes.ContactConflict,
                    "The contact was changed elsewhere. Reload it and try again. (Precondition Failed 412)",
                    ex, context, (int)HttpStatusCode.PreconditionFailed);
            }

            if (status == 403 && googleMessage.Contains("scope", StringComparison.OrdinalIgnoreCase))
            {
                return new GoogleApiException(
                    GoogleApiErrorCodes.InsufficientScope,
                    "Google did not grant access to your contacts. Sign out and sign in again, and allow contacts access.",
                    ex, context, (int)HttpStatusCode.Forbidden);
            }

            if (status == 404)
            {
                return new GoogleApiException(
                    GoogleApiErrorCodes.ContactNotFound, "Contact not found", ex, context, (int)HttpStatusCode.NotFound);
            }

            if (status == 401)
            {
                return new GoogleApiException(
                    GoogleApiErrorCodes.InvalidAccessToken, "Google session expired. Sign in again.", ex, context,
                    (int)HttpStatusCode.Unauthorized);
            }

            if (status == 429)
            {
                return new GoogleApiException(
                    GoogleApiErrorCodes.RateLimited, "Google rate limit reached. Try again in a minute.", ex, context, 429);
            }

            if (status >= 500)
            {
                return new GoogleApiException(defaultCode, message, ex, context, (int)HttpStatusCode.BadGateway);
            }
        }

        return new GoogleApiException(defaultCode, message, ex, context);
    }

    /// <summary>
    /// Builds the Person to send and the matching updatePersonFields list from the
    /// non-null groups of <paramref name="patch"/>.
    /// </summary>
    private static (Person Person, List<string> Fields) BuildUpdate(Contact patch)
    {
        var person = new Person();
        var fields = new List<string>();

        if (patch.Names != null)
        {
            fields.Add("names");
            person.Names = patch.Names.Select(n =>
            {
                var hasPhoneticParts = !string.IsNullOrWhiteSpace(n.PhoneticGivenName)
                    || !string.IsNullOrWhiteSpace(n.PhoneticMiddleName)
                    || !string.IsNullOrWhiteSpace(n.PhoneticFamilyName);
                return new Name
                {
                    HonorificPrefix = Blank(n.HonorificPrefix),
                    GivenName = Blank(n.GivenName),
                    MiddleName = Blank(n.MiddleName),
                    FamilyName = Blank(n.FamilyName),
                    HonorificSuffix = Blank(n.HonorificSuffix),
                    PhoneticGivenName = Blank(n.PhoneticGivenName),
                    PhoneticMiddleName = Blank(n.PhoneticMiddleName),
                    PhoneticFamilyName = Blank(n.PhoneticFamilyName),
                    // The parts are what the UI edits; sending a stale full form next
                    // to new parts would let Google keep the old one. Only pass the
                    // full form through for contacts that have nothing else.
                    PhoneticFullName = hasPhoneticParts ? null : Blank(n.PhoneticFullName)
                };
            }).ToList();
        }

        if (patch.Nicknames != null)
        {
            fields.Add("nicknames");
            person.Nicknames = patch.Nicknames.Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .Select(x => new Nickname { Value = x.Value, Type = Blank(x.Type) }).ToList();
        }

        if (patch.PhoneNumbers != null)
        {
            fields.Add("phoneNumbers");
            person.PhoneNumbers = patch.PhoneNumbers.Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .Select(x => new PhoneNumber { Value = x.Value.Trim(), Type = Blank(x.Type) }).ToList();
        }

        if (patch.EmailAddresses != null)
        {
            fields.Add("emailAddresses");
            person.EmailAddresses = patch.EmailAddresses.Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .Select(x => new EmailAddress
                {
                    Value = x.Value.Trim(),
                    Type = Blank(x.Type),
                    DisplayName = Blank(x.DisplayName)
                }).ToList();
        }

        if (patch.Addresses != null)
        {
            fields.Add("addresses");
            person.Addresses = patch.Addresses.Select(a => new Address
            {
                Type = Blank(a.Type),
                StreetAddress = Blank(a.StreetAddress),
                ExtendedAddress = Blank(a.ExtendedAddress),
                PoBox = Blank(a.PoBox),
                City = Blank(a.City),
                Region = Blank(a.Region),
                PostalCode = Blank(a.PostalCode),
                Country = Blank(a.Country),
                CountryCode = Blank(a.CountryCode),
                // Passed through as read. Dropping it whenever any structured part
                // was set lost the whole free-text address of entries such as
                // { formattedValue: "Herzl 5, Tel Aviv", countryCode: "IL" }. The
                // card clears it itself when the user edits a structured part.
                FormattedValue = Blank(a.FormattedValue)
            }).Where(a => a.FormattedValue != null || HasStructuredAddress(a)).ToList();
        }

        if (patch.Organizations != null)
        {
            fields.Add("organizations");
            person.Organizations = patch.Organizations.Select(o => new Organization
            {
                Name = Blank(o.Name),
                Title = Blank(o.Title),
                Department = Blank(o.Department),
                Type = Blank(o.Type),
                JobDescription = Blank(o.JobDescription),
                Location = Blank(o.Location),
                Symbol = Blank(o.Symbol),
                Domain = Blank(o.Domain),
                PhoneticName = Blank(o.PhoneticName),
                CostCenter = Blank(o.CostCenter),
                Current = o.Current,
                StartDate = ToDate(o.StartDate),
                EndDate = ToDate(o.EndDate),
                FullTimeEquivalentMillipercent = o.FullTimeEquivalentMillipercent
            }).Where(o => !IsEmpty(o)).ToList();
        }

        if (patch.Birthdays != null)
        {
            // A date with only a day or only a month would be filtered out below
            // and clear the birthday; refuse it instead of losing data.
            if (patch.Birthdays.Any(b => b.Date != null
                && ((b.Date.Day ?? 0) > 0) != ((b.Date.Month ?? 0) > 0)
                && string.IsNullOrWhiteSpace(b.Text)))
            {
                throw new ArgumentException("A birthday needs both a day and a month", nameof(patch));
            }

            fields.Add("birthdays");
            person.Birthdays = patch.Birthdays.Select(b => new Birthday
            {
                Date = b.Date is { Month: > 0, Day: > 0 }
                    ? new Date { Year = b.Date.Year is > 0 ? b.Date.Year : null, Month = b.Date.Month, Day = b.Date.Day }
                    : null,
                Text = b.Date is { Month: > 0, Day: > 0 } ? null : Blank(b.Text)
            }).Where(b => b.Date != null || b.Text != null).ToList();
        }

        if (patch.Urls != null)
        {
            fields.Add("urls");
            person.Urls = patch.Urls.Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .Select(x => new Url { Value = x.Value.Trim(), Type = Blank(x.Type) }).ToList();
        }

        if (patch.Biographies != null)
        {
            fields.Add("biographies");
            person.Biographies = patch.Biographies.Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .Select(x => new Biography { Value = x.Value, ContentType = Blank(x.ContentType) ?? "TEXT_PLAIN" })
                .ToList();
        }

        if (patch.Relations != null)
        {
            fields.Add("relations");
            person.Relations = patch.Relations.Where(x => !string.IsNullOrWhiteSpace(x.Person))
                .Select(x => new Relation { Person = x.Person.Trim(), Type = Blank(x.Type) }).ToList();
        }

        if (patch.UserDefined != null)
        {
            fields.Add("userDefined");
            person.UserDefined = patch.UserDefined.Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .Select(x => new UserDefined { Key = x.Key.Trim(), Value = x.Value }).ToList();
        }

        return (person, fields);
    }

    /// <summary>Converts an optional date; null when no part is set</summary>
    private static Date? ToDate(ContactDate? d) =>
        d is null || ((d.Year ?? 0) == 0 && (d.Month ?? 0) == 0 && (d.Day ?? 0) == 0)
            ? null
            : new Date
            {
                Year = d.Year is > 0 ? d.Year : null,
                Month = d.Month is > 0 ? d.Month : null,
                Day = d.Day is > 0 ? d.Day : null
            };

    /// <summary>Maps a Google date to the entity</summary>
    private static ContactDate? FromDate(Date? d) =>
        d == null ? null : new ContactDate { Year = d.Year, Month = d.Month, Day = d.Day };

    /// <summary>True when an organization entry carries nothing at all</summary>
    private static bool IsEmpty(Organization o) =>
        new[] { o.Name, o.Title, o.Department, o.Type, o.JobDescription, o.Location, o.Symbol, o.Domain, o.PhoneticName, o.CostCenter }
            .All(string.IsNullOrWhiteSpace)
        && o.Current == null && o.StartDate == null && o.EndDate == null && o.FullTimeEquivalentMillipercent == null;

    /// <summary>
    /// True for entries that belong to the contact itself. Reads already ask for
    /// contact sources only; this is the second guard against writing profile
    /// data back into the contact.
    /// </summary>
    private static bool FromContact(FieldMetadata? metadata) =>
        metadata?.Source?.Type is null or "CONTACT";

    /// <summary>Returns null for empty/whitespace strings so Google treats them as absent</summary>
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>True when any structured address component is set</summary>
    private static bool HasStructuredAddress(ContactAddress a) =>
        new[] { a.StreetAddress, a.ExtendedAddress, a.PoBox, a.City, a.Region, a.PostalCode, a.Country, a.CountryCode }
            .Any(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>True when any structured address component is set</summary>
    private static bool HasStructuredAddress(Address a) =>
        new[] { a.StreetAddress, a.ExtendedAddress, a.PoBox, a.City, a.Region, a.PostalCode, a.Country, a.CountryCode }
            .Any(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// Maps Google Person to Contact entity
    /// </summary>
    private static Contact MapPersonToContact(Person person)
    {
        return new Contact
        {
            ResourceName = person.ResourceName ?? string.Empty,
            Etag = person.ETag,
            Names = person.Names?.Where(x => FromContact(x.Metadata)).Select(n => new ContactName
            {
                DisplayName = n.DisplayName,
                HonorificPrefix = n.HonorificPrefix,
                GivenName = n.GivenName,
                MiddleName = n.MiddleName,
                FamilyName = n.FamilyName,
                HonorificSuffix = n.HonorificSuffix,
                PhoneticFullName = n.PhoneticFullName,
                PhoneticGivenName = n.PhoneticGivenName,
                PhoneticMiddleName = n.PhoneticMiddleName,
                PhoneticFamilyName = n.PhoneticFamilyName
            }).ToList(),
            Nicknames = person.Nicknames?.Where(x => FromContact(x.Metadata)).Select(n => new ContactNickname
            {
                Value = n.Value ?? string.Empty,
                Type = n.Type
            }).ToList(),
            PhoneNumbers = person.PhoneNumbers?.Where(x => FromContact(x.Metadata)).Select(p => new ContactPhone
            {
                Value = p.Value ?? string.Empty,
                Type = p.Type,
                CanonicalForm = p.CanonicalForm
            }).ToList(),
            EmailAddresses = person.EmailAddresses?.Where(x => FromContact(x.Metadata)).Select(e => new ContactEmail
            {
                Value = e.Value ?? string.Empty,
                Type = e.Type,
                DisplayName = e.DisplayName
            }).ToList(),
            Addresses = person.Addresses?.Where(x => FromContact(x.Metadata)).Select(a => new ContactAddress
            {
                FormattedValue = a.FormattedValue,
                Type = a.Type,
                StreetAddress = a.StreetAddress,
                ExtendedAddress = a.ExtendedAddress,
                PoBox = a.PoBox,
                City = a.City,
                Region = a.Region,
                PostalCode = a.PostalCode,
                Country = a.Country,
                CountryCode = a.CountryCode
            }).ToList(),
            Organizations = person.Organizations?.Where(x => FromContact(x.Metadata)).Select(o => new ContactOrganization
            {
                Name = o.Name,
                Title = o.Title,
                Department = o.Department,
                Type = o.Type,
                JobDescription = o.JobDescription,
                Location = o.Location,
                Symbol = o.Symbol,
                Domain = o.Domain,
                PhoneticName = o.PhoneticName,
                CostCenter = o.CostCenter,
                Current = o.Current,
                StartDate = FromDate(o.StartDate),
                EndDate = FromDate(o.EndDate),
                FullTimeEquivalentMillipercent = o.FullTimeEquivalentMillipercent
            }).ToList(),
            Birthdays = person.Birthdays?.Where(x => FromContact(x.Metadata)).Select(b => new ContactBirthday
            {
                Date = b.Date == null ? null : new ContactDate { Year = b.Date.Year, Month = b.Date.Month, Day = b.Date.Day },
                Text = b.Text
            }).ToList(),
            Urls = person.Urls?.Where(x => FromContact(x.Metadata)).Select(u => new ContactUrl { Value = u.Value ?? string.Empty, Type = u.Type }).ToList(),
            Biographies = person.Biographies?.Where(x => FromContact(x.Metadata)).Select(b => new ContactBiography
            {
                Value = b.Value ?? string.Empty,
                ContentType = b.ContentType
            }).ToList(),
            Relations = person.Relations?.Where(x => FromContact(x.Metadata)).Select(r => new ContactRelation
            {
                Person = r.Person ?? string.Empty,
                Type = r.Type
            }).ToList(),
            UserDefined = person.UserDefined?.Where(x => FromContact(x.Metadata)).Select(u => new ContactUserDefined
            {
                Key = u.Key ?? string.Empty,
                Value = u.Value ?? string.Empty
            }).ToList(),
            Photos = person.Photos?.Select(p => new ContactPhoto
            {
                Url = p.Url ?? string.Empty,
                IsDefault = p.Default__ ?? false
            }).ToList(),
            Labels = person.Memberships?
                .Select(m => m.ContactGroupMembership?.ContactGroupResourceName)
                .Where(r => !string.IsNullOrEmpty(r))
                .Select(r => r!)
                .ToList(),
            UpdateTime = person.Metadata?.Sources?
                .Select(s => s.UpdateTimeRaw)
                .Where(t => !string.IsNullOrEmpty(t))
                .OrderByDescending(t => t, StringComparer.Ordinal)
                .FirstOrDefault()
        };
    }
}
