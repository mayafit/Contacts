using ContactsApi.Core.Entities;
using ContactsApi.Infrastructure.Google;
using ContactsApi.WebApi.Models.Responses;
using Microsoft.AspNetCore.Mvc;

namespace ContactsApi.WebApi.Controllers;

/// <summary>
/// Contacts API controller
/// Provides endpoints for contact management via Google People API
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ContactsController : ControllerBase
{
    private readonly ILogger<ContactsController> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public ContactsController(
        ILogger<ContactsController> logger,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Get all contacts with pagination support
    /// </summary>
    /// <param name="pageSize">Number of contacts per page (max 1000)</param>
    /// <param name="pageToken">Token for next page</param>
    /// <returns>List of contacts with pagination info</returns>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<ContactsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<ContactsResponse>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<ContactsResponse>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAllContacts(
        [FromQuery] int pageSize = 1000,
        [FromQuery] string? pageToken = null)
    {
        try
        {
            _logger.LogInformation(
                "Fetching contacts (pageSize: {PageSize}, hasPageToken: {HasPageToken})",
                pageSize,
                !string.IsNullOrEmpty(pageToken));

            // TODO: Get access token from session/auth context
            // For now, this is a placeholder - will be implemented with AuthController
            var accessToken = HttpContext.Session.GetString("AccessToken");
            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogWarning("No access token found in session");
                return Unauthorized(ApiResponse<ContactsResponse>.ErrorResponse(
                    "UNAUTHORIZED",
                    "Not authenticated. Please sign in."));
            }

            // Validate page size
            if (pageSize < 1 || pageSize > 1000)
            {
                return BadRequest(ApiResponse<ContactsResponse>.ErrorResponse(
                    "INVALID_PAGE_SIZE",
                    "Page size must be between 1 and 1000"));
            }

            // Create API client and fetch contacts
            var apiClient = new GooglePeopleApiClient(accessToken, _loggerFactory.CreateLogger<GooglePeopleApiClient>());
            var (contacts, nextPageToken) = await apiClient.FetchAllContactsAsync(pageSize, pageToken);

            var response = new ContactsResponse
            {
                Contacts = contacts,
                NextPageToken = nextPageToken,
                TotalCount = contacts.Count
            };

            _logger.LogInformation(
                "Successfully fetched {Count} contacts",
                contacts.Count);

            return Ok(ApiResponse<ContactsResponse>.SuccessResponse(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching contacts");
            throw; // Let exception middleware handle it
        }
    }

    /// <summary>
    /// Get a single contact by resource name
    /// </summary>
    /// <param name="resourceName">Contact resource name (URL encoded)</param>
    /// <returns>Contact details</returns>
    [HttpGet("{resourceName}")]
    [ProducesResponseType(typeof(ApiResponse<Contact>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<Contact>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<Contact>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetContact(string resourceName)
    {
        try
        {
            _logger.LogInformation("Fetching contact {ResourceName}", resourceName);

            // TODO: Get access token from session/auth context
            var accessToken = HttpContext.Session.GetString("AccessToken");
            if (string.IsNullOrEmpty(accessToken))
            {
                return Unauthorized(ApiResponse<Contact>.ErrorResponse(
                    "UNAUTHORIZED",
                    "Not authenticated. Please sign in."));
            }

            // Decode resource name (it may be URL encoded)
            var decodedResourceName = Uri.UnescapeDataString(resourceName);

            // Create API client and fetch contact
            var apiClient = new GooglePeopleApiClient(accessToken, _loggerFactory.CreateLogger<GooglePeopleApiClient>());
            var contact = await apiClient.GetContactAsync(decodedResourceName);

            _logger.LogInformation("Successfully fetched contact {ResourceName}", decodedResourceName);

            return Ok(ApiResponse<Contact>.SuccessResponse(contact));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching contact {ResourceName}", resourceName);
            throw;
        }
    }

    /// <summary>
    /// Update a contact
    /// </summary>
    /// <param name="resourceName">Contact resource name (URL encoded)</param>
    /// <param name="updates">Contact updates</param>
    /// <returns>Updated contact</returns>
    [HttpPut("{resourceName}")]
    [ProducesResponseType(typeof(ApiResponse<Contact>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<Contact>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<Contact>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateContact(
        string resourceName,
        [FromBody] Contact updates)
    {
        try
        {
            _logger.LogInformation("Updating contact {ResourceName}", resourceName);

            // TODO: Get access token from session/auth context
            var accessToken = HttpContext.Session.GetString("AccessToken");
            if (string.IsNullOrEmpty(accessToken))
            {
                return Unauthorized(ApiResponse<Contact>.ErrorResponse(
                    "UNAUTHORIZED",
                    "Not authenticated. Please sign in."));
            }

            if (updates == null)
            {
                return BadRequest(ApiResponse<Contact>.ErrorResponse(
                    "INVALID_REQUEST",
                    "Contact updates cannot be null"));
            }

            // Decode resource name
            var decodedResourceName = Uri.UnescapeDataString(resourceName);

            // Create API client and update contact
            var apiClient = new GooglePeopleApiClient(accessToken, _loggerFactory.CreateLogger<GooglePeopleApiClient>());
            var updatedContact = await apiClient.UpdateContactAsync(decodedResourceName, updates);

            _logger.LogInformation("Successfully updated contact {ResourceName}", decodedResourceName);

            return Ok(ApiResponse<Contact>.SuccessResponse(updatedContact));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating contact {ResourceName}", resourceName);
            throw;
        }
    }
}
