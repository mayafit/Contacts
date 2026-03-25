using Google.Apis.Auth.OAuth2;
using Google.Apis.PeopleService.v1;
using Google.Apis.PeopleService.v1.Data;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using ContactsApi.Core.Entities;
using ContactsApi.Core.Exceptions;

namespace ContactsApi.Infrastructure.Google;

/// <summary>
/// Client for interacting with Google People API
/// </summary>
public class GooglePeopleApiClient
{
    private readonly PeopleServiceService _peopleService;
    private readonly ILogger<GooglePeopleApiClient> _logger;
    private const string PersonFields = "names,emailAddresses,phoneNumbers,addresses";

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
                "Access token cannot be null or empty");
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
            request.PersonFields = PersonFields;
            request.PageSize = pageSize;
            if (!string.IsNullOrEmpty(pageToken))
            {
                request.PageToken = pageToken;
            }

            var response = await request.ExecuteAsync();

            var contacts = new List<Contact>();
            if (response.Connections != null)
            {
                foreach (var person in response.Connections)
                {
                    contacts.Add(MapPersonToContact(person));
                }
            }

            _logger.LogInformation(
                "Successfully fetched {Count} contacts (hasMore: {HasMore})",
                contacts.Count,
                !string.IsNullOrEmpty(response.NextPageToken));

            return (contacts, response.NextPageToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch contacts from Google People API");
            throw new GoogleApiException(
                GoogleApiErrorCodes.FetchContactsFailed,
                "Failed to fetch contacts from Google People API",
                ex,
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
            request.PersonFields = PersonFields;

            var person = await request.ExecuteAsync();
            var contact = MapPersonToContact(person);

            _logger.LogInformation("Successfully fetched contact {ResourceName}", resourceName);

            return contact;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get contact {ResourceName}", resourceName);
            throw new GoogleApiException(
                GoogleApiErrorCodes.GetContactFailed,
                $"Failed to get contact {resourceName}",
                ex,
                new Dictionary<string, object>
                {
                    { "resourceName", resourceName }
                });
        }
    }

    /// <summary>
    /// Updates a contact
    /// </summary>
    /// <param name="resourceName">Contact resource name</param>
    /// <param name="updates">Contact updates</param>
    /// <returns>Updated contact</returns>
    public async Task<Contact> UpdateContactAsync(string resourceName, Contact updates)
    {
        try
        {
            _logger.LogInformation("Updating contact {ResourceName}", resourceName);

            var person = MapContactToPerson(updates);
            person.ResourceName = resourceName;

            var request = _peopleService.People.UpdateContact(person, resourceName);
            request.UpdatePersonFields = PersonFields;

            var updatedPerson = await request.ExecuteAsync();
            var updatedContact = MapPersonToContact(updatedPerson);

            _logger.LogInformation("Successfully updated contact {ResourceName}", resourceName);

            return updatedContact;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update contact {ResourceName}", resourceName);
            throw new GoogleApiException(
                GoogleApiErrorCodes.UpdateContactFailed,
                $"Failed to update contact {resourceName}",
                ex,
                new Dictionary<string, object>
                {
                    { "resourceName", resourceName }
                });
        }
    }

    /// <summary>
    /// Maps Google Person to Contact entity
    /// </summary>
    private static Contact MapPersonToContact(Person person)
    {
        return new Contact
        {
            ResourceName = person.ResourceName ?? string.Empty,
            Names = person.Names?.Select(n => new ContactName
            {
                GivenName = n.GivenName,
                FamilyName = n.FamilyName,
                DisplayName = n.DisplayName,
                PhoneticFullName = n.PhoneticFullName
            }).ToList(),
            PhoneNumbers = person.PhoneNumbers?.Select(p => new ContactPhone
            {
                Value = p.Value ?? string.Empty,
                Type = p.Type
            }).ToList(),
            EmailAddresses = person.EmailAddresses?.Select(e => new ContactEmail
            {
                Value = e.Value ?? string.Empty,
                Type = e.Type
            }).ToList(),
            Addresses = person.Addresses?.Select(a => new ContactAddress
            {
                Street = a.StreetAddress,
                City = a.City,
                Country = a.Country,
                PostalCode = a.PostalCode
            }).ToList()
        };
    }

    /// <summary>
    /// Maps Contact entity to Google Person
    /// </summary>
    private static Person MapContactToPerson(Contact contact)
    {
        return new Person
        {
            Names = contact.Names?.Select(n => new Name
            {
                GivenName = n.GivenName,
                FamilyName = n.FamilyName,
                DisplayName = n.DisplayName,
                PhoneticFullName = n.PhoneticFullName
            }).ToList(),
            PhoneNumbers = contact.PhoneNumbers?.Select(p => new PhoneNumber
            {
                Value = p.Value,
                Type = p.Type
            }).ToList(),
            EmailAddresses = contact.EmailAddresses?.Select(e => new EmailAddress
            {
                Value = e.Value,
                Type = e.Type
            }).ToList(),
            Addresses = contact.Addresses?.Select(a => new Address
            {
                StreetAddress = a.Street,
                City = a.City,
                Country = a.Country,
                PostalCode = a.PostalCode
            }).ToList()
        };
    }
}
