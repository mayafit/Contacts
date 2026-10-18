using ContactsApi.Core.Entities;
using ContactsApi.Core.Exceptions;
using ContactsApi.Infrastructure.Google;
using ContactsApi.WebApi.Models.Responses;
using ContactsApi.WebApi.Services;
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
    private readonly GoogleTokenService _tokens;

    public ContactsController(
        ILogger<ContactsController> logger,
        ILoggerFactory loggerFactory,
        GoogleTokenService tokens)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _tokens = tokens;
    }

    /// <summary>Name of the label that serves as this app's Trash</summary>
    public const string TrashLabelName = "Contacts app – Trash";

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
            var accessToken = await _tokens.GetAccessTokenAsync(HttpContext);
            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogWarning("No access token found in session");
                return Unauthorized(ApiResponse<ContactsResponse>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
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
        var accessToken = await _tokens.GetAccessTokenAsync(HttpContext);
        if (string.IsNullOrEmpty(accessToken))
        {
            return Unauthorized(ApiResponse<List<ContactGroup>>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
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
        var accessToken = await _tokens.GetAccessTokenAsync(HttpContext);
        if (string.IsNullOrEmpty(accessToken))
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
        }

        var names = (request.ResourceNames ?? new List<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
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
    /// Create a contact
    /// </summary>
    /// <param name="contact">Field groups of the new contact</param>
    /// <returns>The created contact</returns>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Contact contact)
    {
        var accessToken = await _tokens.GetAccessTokenAsync(HttpContext);
        if (string.IsNullOrEmpty(accessToken))
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
        }

        if (!(contact.Names?.Count > 0 || contact.EmailAddresses?.Count > 0 || contact.PhoneNumbers?.Count > 0
              || contact.Organizations?.Count > 0))
        {
            return BadRequest(ApiResponse<object>.ErrorResponse(
                "INVALID_REQUEST", "A new contact needs a name, email, phone or organization"));
        }

        var apiClient = new GooglePeopleApiClient(accessToken, _loggerFactory.CreateLogger<GooglePeopleApiClient>());
        var created = await apiClient.CreateContactAsync(contact);
        return Ok(ApiResponse<Contact>.SuccessResponse(created));
    }

    /// <summary>
    /// Add contacts to a label and/or remove them from it. The label is created
    /// when it is given by name, does not exist and contacts are added. System
    /// groups and the app's Trash cannot be changed here.
    /// </summary>
    /// <param name="request">Label and members</param>
    /// <returns>The label's resource name</returns>
    [HttpPost("labels")]
    public async Task<IActionResult> ModifyLabel([FromBody] LabelMembersRequest request)
    {
        var accessToken = await _tokens.GetAccessTokenAsync(HttpContext);
        if (string.IsNullOrEmpty(accessToken))
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
        }

        var add = (request.Add ?? new()).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        var remove = (request.Remove ?? new()).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        var name = request.LabelName?.Trim();
        if (add.Count + remove.Count is 0 or > 1000
            || (string.IsNullOrEmpty(name) == string.IsNullOrEmpty(request.LabelResourceName))
            || name == TrashLabelName)
        {
            return BadRequest(ApiResponse<object>.ErrorResponse(
                "INVALID_REQUEST", "Give one label (not Trash) and 1-1000 contacts to add or remove"));
        }

        var apiClient = new GooglePeopleApiClient(accessToken, _loggerFactory.CreateLogger<GooglePeopleApiClient>());
        string label;
        if (!string.IsNullOrEmpty(name))
        {
            var all = await apiClient.ListContactGroupsAsync();
            // A system group's name ("Starred", "Family"...) must not become a new user label
            if (all.Any(g => g.GroupType != "USER_CONTACT_GROUP" && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return BadRequest(ApiResponse<object>.ErrorResponse(
                    "INVALID_REQUEST", $"\"{name}\" is a system group; pass it by resource name"));
            }

            var existing = all.FirstOrDefault(g =>
                g.GroupType == "USER_CONTACT_GROUP" && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing == null && add.Count == 0)
            {
                return Ok(ApiResponse<object>.SuccessResponse(new { label = (string?)null }));
            }

            label = existing?.ResourceName ?? await apiClient.EnsureGroupAsync(name);
        }
        else
        {
            label = request.LabelResourceName!;
            var groups = await apiClient.ListContactGroupsAsync();
            var group = groups.FirstOrDefault(g => g.ResourceName == label);
            if (group == null || group.Name == TrashLabelName || !IsAssignableLabel(label))
            {
                return BadRequest(ApiResponse<object>.ErrorResponse("INVALID_REQUEST", "That label cannot be changed here"));
            }
        }

        await apiClient.ModifyGroupMembersAsync(label, add, remove);
        _logger.LogInformation("Label {Label}: +{Added} -{Removed}", label, add.Count, remove.Count);
        return Ok(ApiResponse<object>.SuccessResponse(new { label }));
    }

    /// <summary>
    /// Move contacts to the app's Trash label (soft delete) or back out of it
    /// </summary>
    /// <param name="request">Contacts (1-1000)</param>
    /// <param name="restore">True to take them out of Trash</param>
    /// <returns>The Trash label's resource name</returns>
    [HttpPost("trash")]
    public async Task<IActionResult> Trash([FromBody] BatchDeleteRequest request, [FromQuery] bool restore = false)
    {
        var accessToken = await _tokens.GetAccessTokenAsync(HttpContext);
        if (string.IsNullOrEmpty(accessToken))
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
        }

        var names = (request.ResourceNames ?? new List<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        if (names.Count is 0 or > 1000)
        {
            return BadRequest(ApiResponse<object>.ErrorResponse("INVALID_REQUEST", "Move between 1 and 1000 contacts at a time"));
        }

        var apiClient = new GooglePeopleApiClient(accessToken, _loggerFactory.CreateLogger<GooglePeopleApiClient>());
        var trash = await apiClient.EnsureGroupAsync(TrashLabelName);
        if (restore)
        {
            await apiClient.ModifyGroupMembersAsync(trash, Array.Empty<string>(), names);
        }
        else
        {
            await apiClient.ModifyGroupMembersAsync(trash, names, Array.Empty<string>());
        }

        _logger.LogInformation("{Action} {Count} contacts", restore ? "Restored" : "Trashed", names.Count);
        return Ok(ApiResponse<object>.SuccessResponse(new { trashLabel = trash, count = names.Count }));
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
        var accessToken = await _tokens.GetAccessTokenAsync(HttpContext);
        if (string.IsNullOrEmpty(accessToken))
        {
            return Unauthorized(ApiResponse<MergeResult>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
        }

        var deletes = (request.DeleteResourceNames ?? new List<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        var addLabels = (request.AddLabels ?? new List<string>()).Distinct().ToList();
        var acknowledged = new HashSet<string>(request.AcknowledgedResourceNames ?? new List<string>());
        var deleteEtags = request.DeleteEtags ?? new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(request.PrimaryResourceName) || deletes.Count is 0 or > 50
            || deletes.Contains(request.PrimaryResourceName))
        {
            return BadRequest(ApiResponse<MergeResult>.ErrorResponse(
                "INVALID_REQUEST", "Merge needs one contact to keep and 1-50 other contacts"));
        }

        var apiClient = new GooglePeopleApiClient(accessToken, _loggerFactory.CreateLogger<GooglePeopleApiClient>());

        // 1a. The merge was planned from the client's copies; refuse if any contact
        // to delete changed since (e.g. edited on a phone), or it would be deleted
        // with data the plan never saw.
        var current = await apiClient.GetEtagsAsync(deletes);
        var changed = deletes
            .Where(rn => deleteEtags.TryGetValue(rn, out var known) && known != current[rn])
            .ToList();
        if (changed.Count > 0)
        {
            throw new GoogleApiException(
                GoogleApiErrorCodes.ContactConflict,
                "Some contacts changed since they were loaded. Reload them and review the merge again. (Precondition Failed 412)",
                context: changed.ToDictionary(rn => rn, _ => (object)"changed"),
                statusCode: StatusCodes.Status412PreconditionFailed);
        }

        // 1b. Carry over what the app does not show (events, IM, app data, ...):
        // read the kept contact and the others with those fields and merge them.
        var keptExtras = await apiClient.GetPersonExtrasAsync(request.PrimaryResourceName);
        var otherExtras = new List<Google.Apis.PeopleService.v1.Data.Person>();
        foreach (var rn in deletes)
        {
            otherExtras.Add(await apiClient.GetPersonExtrasAsync(rn));
        }

        var extras = PersonExtrasMerger.Merge(keptExtras, otherExtras);

        // 1c. What cannot be carried over (a photo, a different gender) stays on the
        // merged-away contact. Moving it to Trash keeps it, so that is only reported;
        // deleting it needs an acknowledgement covering exactly those contacts.
        var unacknowledged = request.MoveToTrash
            ? new Dictionary<string, object>()
            : extras.NotCopied.Where(kv => !acknowledged.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        if (unacknowledged.Count > 0)
        {
            throw new GoogleApiException(
                GoogleApiErrorCodes.MergeWouldLoseData,
                "Some contacts to delete hold data this app cannot merge",
                context: unacknowledged,
                statusCode: StatusCodes.Status409Conflict);
        }

        // 2. Save the kept contact first; if this fails nothing has been deleted
        Contact kept;
        var hasPatch = request.Patch != null && HasGroups(request.Patch);
        if (hasPatch || extras.ChangedFields.Count > 0)
        {
            var patch = hasPatch
                ? request.Patch!
                : new Contact
                {
                    // No edits from the client: the etag the extras were just read
                    // with, so only a change made since then is a conflict
                    Etag = keptExtras.ETag
                };
            kept = await apiClient.UpdateContactAsync(
                request.PrimaryResourceName, patch, extras.Extras, extras.ChangedFields);
        }
        else
        {
            kept = await apiClient.GetContactAsync(request.PrimaryResourceName);
        }

        // 3. Carry over labels
        var existing = new HashSet<string>(kept.Labels ?? new List<string>());
        foreach (var label in addLabels.Where(l => !existing.Contains(l) && IsAssignableLabel(l)))
        {
            await apiClient.AddToGroupAsync(label, request.PrimaryResourceName);
        }

        // 4. Only now remove the merged-away contacts: to the app's Trash by
        // default (restorable), or deleted when the client asked for that
        string? trashLabel = null;
        if (request.MoveToTrash)
        {
            var trash = await apiClient.EnsureGroupAsync(TrashLabelName);
            trashLabel = trash;
            _logger.LogInformation("Merge into {Primary}: moving {Count} contacts to Trash", request.PrimaryResourceName, deletes.Count);
            await apiClient.ModifyGroupMembersAsync(trash, deletes, Array.Empty<string>());
        }
        else
        {
            _logger.LogWarning("Merge into {Primary}: deleting {Count} contacts", request.PrimaryResourceName, deletes.Count);
            await apiClient.BatchDeleteContactsAsync(deletes);
        }

        if (addLabels.Count > 0)
        {
            // The merge itself is done; a failed refresh must not report it as failed.
            try
            {
                kept = await apiClient.GetContactAsync(request.PrimaryResourceName);
            }
            catch (GoogleApiException ex)
            {
                _logger.LogWarning(ex, "Merged, but could not re-read {Primary}", request.PrimaryResourceName);
            }
        }

        return Ok(ApiResponse<MergeResult>.SuccessResponse(new MergeResult
        {
            Contact = kept,
            Deleted = deletes,
            TrashLabel = trashLabel,
            NotCopied = extras.NotCopied
        }));
    }

    /// <summary>
    /// Labels a contact can be added to: user labels and "starred". Google rejects
    /// adding members to the other system groups (friends, family, coworkers, ...).
    /// </summary>
    private static bool IsAssignableLabel(string label) =>
        label == "contactGroups/starred"
        || (label.StartsWith("contactGroups/", StringComparison.Ordinal)
            && !SystemGroups.Contains(label));

    private static readonly HashSet<string> SystemGroups = new()
    {
        "contactGroups/myContacts", "contactGroups/all", "contactGroups/starred", "contactGroups/friends",
        "contactGroups/family", "contactGroups/coworkers", "contactGroups/chatBuddies", "contactGroups/blocked"
    };

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
            var accessToken = await _tokens.GetAccessTokenAsync(HttpContext);
            if (string.IsNullOrEmpty(accessToken))
            {
                return Unauthorized(ApiResponse<Contact>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
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
        var accessToken = await _tokens.GetAccessTokenAsync(HttpContext);
        if (string.IsNullOrEmpty(accessToken))
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
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
            var accessToken = await _tokens.GetAccessTokenAsync(HttpContext);
            if (string.IsNullOrEmpty(accessToken))
            {
                return Unauthorized(ApiResponse<Contact>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
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
