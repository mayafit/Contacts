using ContactsApi.Core.Entities;
using ContactsApi.Core.Exceptions;
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
    /// Get the user's contact groups (labels), used to show label names
    /// </summary>
    /// <returns>All contact groups</returns>
    [HttpGet("groups")]
    [ProducesResponseType(typeof(ApiResponse<List<ContactGroup>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<List<ContactGroup>>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetContactGroups()
    {
        var accessToken = HttpContext.Session.GetString("AccessToken");
        if (string.IsNullOrEmpty(accessToken))
        {
            return Unauthorized(ApiResponse<List<ContactGroup>>.ErrorResponse(
                "UNAUTHORIZED",
                "Not authenticated. Please sign in."));
        }

        var apiClient = new GooglePeopleApiClient(accessToken, _loggerFactory.CreateLogger<GooglePeopleApiClient>());
        var groups = await apiClient.ListContactGroupsAsync();
        return Ok(ApiResponse<List<ContactGroup>>.SuccessResponse(groups));
    }

    /// <summary>
    /// Delete several contacts
    /// </summary>
    /// <param name="request">Contacts to delete (1-500)</param>
    /// <returns>Number deleted</returns>
    [HttpPost("batch-delete")]
    public async Task<IActionResult> BatchDelete([FromBody] BatchDeleteRequest request)
    {
        var accessToken = HttpContext.Session.GetString("AccessToken");
        if (string.IsNullOrEmpty(accessToken))
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse("UNAUTHORIZED", "Not authenticated. Please sign in."));
        }

        var names = request.ResourceNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        if (names.Count is 0 or > 500)
        {
            return BadRequest(ApiResponse<object>.ErrorResponse("INVALID_REQUEST", "Delete between 1 and 500 contacts at a time"));
        }

        _logger.LogWarning("Deleting {Count} contacts", names.Count);
        var apiClient = new GooglePeopleApiClient(accessToken, _loggerFactory.CreateLogger<GooglePeopleApiClient>());
        var deleted = await apiClient.BatchDeleteContactsAsync(names);
        return Ok(ApiResponse<object>.SuccessResponse(new { deleted }));
    }

    /// <summary>
    /// Merge contacts: save the merged data on the kept contact, then delete the others.
    /// Nothing is deleted unless the kept contact was saved, and nothing the app does
    /// not merge is lost without explicit acknowledgement (409 otherwise).
    /// </summary>
    /// <param name="request">Merge request</param>
    /// <returns>The kept contact and the deleted names</returns>
    [HttpPost("merge")]
    public async Task<IActionResult> Merge([FromBody] MergeRequest request)
    {
        var accessToken = HttpContext.Session.GetString("AccessToken");
        if (string.IsNullOrEmpty(accessToken))
        {
            return Unauthorized(ApiResponse<MergeResult>.ErrorResponse("UNAUTHORIZED", "Not authenticated. Please sign in."));
        }

        var deletes = request.DeleteResourceNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        if (string.IsNullOrWhiteSpace(request.PrimaryResourceName) || deletes.Count is 0 or > 50
            || deletes.Contains(request.PrimaryResourceName))
        {
            return BadRequest(ApiResponse<MergeResult>.ErrorResponse(
                "INVALID_REQUEST", "Merge needs one contact to keep and 1-50 other contacts"));
        }

        var apiClient = new GooglePeopleApiClient(accessToken, _loggerFactory.CreateLogger<GooglePeopleApiClient>());

        // 1. Refuse to lose data the merge cannot carry over, unless the user accepted it
        if (!request.AcknowledgeDataLoss)
        {
            var unmerged = await apiClient.FindUnmergedDataAsync(deletes);
            if (unmerged.Count > 0)
            {
                throw new GoogleApiException(
                    GoogleApiErrorCodes.MergeWouldLoseData,
                    "Some contacts to delete hold data this app cannot merge",
                    context: unmerged.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                    statusCode: StatusCodes.Status409Conflict);
            }
        }

        // 2. Save the kept contact first; if this fails nothing has been deleted
        var kept = request.Patch != null && HasGroups(request.Patch)
            ? await apiClient.UpdateContactAsync(request.PrimaryResourceName, request.Patch)
            : await apiClient.GetContactAsync(request.PrimaryResourceName);

        // 3. Carry over labels
        var existing = new HashSet<string>(kept.Labels ?? new List<string>());
        foreach (var label in request.AddLabels.Distinct().Where(l => !existing.Contains(l)
                     && l is not "contactGroups/myContacts" and not "contactGroups/all"))
        {
            await apiClient.AddToGroupAsync(label, request.PrimaryResourceName);
        }

        // 4. Only now delete the merged-away contacts
        _logger.LogWarning("Merge into {Primary}: deleting {Count} contacts", request.PrimaryResourceName, deletes.Count);
        await apiClient.BatchDeleteContactsAsync(deletes);

        if (request.AddLabels.Count > 0)
        {
            kept = await apiClient.GetContactAsync(request.PrimaryResourceName);
        }

        return Ok(ApiResponse<MergeResult>.SuccessResponse(new MergeResult { Contact = kept, Deleted = deletes }));
    }

    /// <summary>True when a patch carries at least one field group</summary>
    private static bool HasGroups(Contact p) =>
        p.Names != null || p.Nicknames != null || p.PhoneNumbers != null || p.EmailAddresses != null
        || p.Addresses != null || p.Organizations != null || p.Birthdays != null || p.Urls != null
        || p.Biographies != null || p.Relations != null || p.UserDefined != null;

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
    /// Delete one contact
    /// </summary>
    /// <param name="resourceName">Contact resource name (URL encoded)</param>
    [HttpDelete("{resourceName}")]
    public async Task<IActionResult> DeleteContact(string resourceName)
    {
        var accessToken = HttpContext.Session.GetString("AccessToken");
        if (string.IsNullOrEmpty(accessToken))
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse("UNAUTHORIZED", "Not authenticated. Please sign in."));
        }

        var decoded = Uri.UnescapeDataString(resourceName);
        var apiClient = new GooglePeopleApiClient(accessToken, _loggerFactory.CreateLogger<GooglePeopleApiClient>());
        await apiClient.DeleteContactAsync(decoded);
        return Ok(ApiResponse<object>.SuccessResponse(new { deleted = 1 }));
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
