using ContactsApi.Core.Entities;

namespace ContactsApi.Core.Interfaces;

/// <summary>
/// Service interface for Google People API operations
/// </summary>
public interface IGooglePeopleService
{
    /// <summary>
    /// Fetches all contacts for a user
    /// </summary>
    /// <param name="accessToken">OAuth access token</param>
    /// <param name="pageSize">Number of contacts per page</param>
    /// <param name="pageToken">Token for pagination</param>
    /// <returns>Tuple of contacts list and next page token</returns>
    Task<(List<Contact> Contacts, string? NextPageToken)> FetchAllContactsAsync(
        string accessToken,
        int pageSize = 1000,
        string? pageToken = null);

    /// <summary>
    /// Gets a single contact by resource name
    /// </summary>
    /// <param name="accessToken">OAuth access token</param>
    /// <param name="resourceName">Contact resource name</param>
    /// <returns>Contact entity</returns>
    Task<Contact> GetContactAsync(string accessToken, string resourceName);

    /// <summary>
    /// Updates a contact
    /// </summary>
    /// <param name="accessToken">OAuth access token</param>
    /// <param name="resourceName">Contact resource name</param>
    /// <param name="updates">Contact updates</param>
    /// <returns>Updated contact entity</returns>
    Task<Contact> UpdateContactAsync(string accessToken, string resourceName, Contact updates);
}
