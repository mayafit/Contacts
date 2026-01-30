using ContactsApi.Core.Entities;
using ContactsApi.Core.Exceptions;
using ContactsApi.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ContactsApi.Core.Services;

/// <summary>
/// Service for Google People API operations
/// </summary>
public class GooglePeopleService : IGooglePeopleService
{
    private readonly ILogger<GooglePeopleService> _logger;

    public GooglePeopleService(ILogger<GooglePeopleService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<(List<Contact> Contacts, string? NextPageToken)> FetchAllContactsAsync(
        string accessToken,
        int pageSize = 1000,
        string? pageToken = null)
    {
        try
        {
            _logger.LogInformation(
                "Starting to fetch contacts (pageSize: {PageSize}, hasPageToken: {HasPageToken})",
                pageSize,
                !string.IsNullOrEmpty(pageToken));

            // Note: GooglePeopleApiClient is instantiated per-request with the access token
            // This will be handled by the controller/service layer
            throw new NotImplementedException("This will be injected via factory pattern in the infrastructure layer");
        }
        catch (GoogleApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while fetching contacts");
            throw new GoogleApiException(
                GoogleApiErrorCodes.UnknownError,
                "An unexpected error occurred while fetching contacts",
                ex);
        }
    }

    /// <inheritdoc/>
    public async Task<Contact> GetContactAsync(string accessToken, string resourceName)
    {
        try
        {
            _logger.LogInformation("Starting to fetch contact {ResourceName}", resourceName);

            throw new NotImplementedException("This will be injected via factory pattern in the infrastructure layer");
        }
        catch (GoogleApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while fetching contact {ResourceName}", resourceName);
            throw new GoogleApiException(
                GoogleApiErrorCodes.UnknownError,
                $"An unexpected error occurred while fetching contact {resourceName}",
                ex);
        }
    }

    /// <inheritdoc/>
    public async Task<Contact> UpdateContactAsync(string accessToken, string resourceName, Contact updates)
    {
        try
        {
            _logger.LogInformation("Starting to update contact {ResourceName}", resourceName);

            throw new NotImplementedException("This will be injected via factory pattern in the infrastructure layer");
        }
        catch (GoogleApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while updating contact {ResourceName}", resourceName);
            throw new GoogleApiException(
                GoogleApiErrorCodes.UnknownError,
                $"An unexpected error occurred while updating contact {resourceName}",
                ex);
        }
    }
}
