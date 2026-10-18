using ContactsApi.Core.Entities;

namespace ContactsApi.WebApi.Models.Responses;

/// <summary>
/// Response containing list of contacts with pagination info
/// </summary>
public class ContactsResponse
{
    public List<Contact> Contacts { get; set; } = new();
    public string? NextPageToken { get; set; }
    public int TotalCount { get; set; }
}

/// <summary>
/// Request to delete several contacts
/// </summary>
public class BatchDeleteRequest
{
    /// <summary>Contacts to delete</summary>
    public List<string> ResourceNames { get; set; } = new();
}

/// <summary>
/// Request to merge contacts into one
/// </summary>
public class MergeRequest
{
    /// <summary>Contact that is kept</summary>
    public string PrimaryResourceName { get; set; } = string.Empty;

    /// <summary>Etag plus the merged groups to write to the kept contact; null when nothing changes</summary>
    public Contact? Patch { get; set; }

    /// <summary>Contacts folded into the kept one and then deleted</summary>
    public List<string> DeleteResourceNames { get; set; } = new();

    /// <summary>Labels of the deleted contacts to add to the kept one</summary>
    public List<string> AddLabels { get; set; } = new();

    /// <summary>
    /// Contacts whose unmerged data (as reported in a previous 409) the user accepted
    /// losing. The acknowledgement covers exactly these contacts and nothing else.
    /// </summary>
    public List<string> AcknowledgedResourceNames { get; set; } = new();

    /// <summary>Move merged-away contacts to the app's Trash label instead of deleting them</summary>
    public bool MoveToTrash { get; set; } = true;

    /// <summary>Etags of the contacts to delete, as the client planned the merge with</summary>
    public Dictionary<string, string> DeleteEtags { get; set; } = new();
}

/// <summary>
/// Result of a merge
/// </summary>
public class MergeResult
{
    /// <summary>The kept contact as saved</summary>
    public Contact Contact { get; set; } = new();

    /// <summary>Contacts deleted (or moved to Trash, see TrashLabel)</summary>
    public List<string> Deleted { get; set; } = new();

    /// <summary>When the merged-away contacts were moved to Trash: the Trash label</summary>
    public string? TrashLabel { get; set; }

    /// <summary>
    /// Per merged-away contact, what could not be carried over to the kept one
    /// (a photo, a different gender). It stays on that contact in Trash.
    /// </summary>
    public Dictionary<string, List<string>> NotCopied { get; set; } = new();
}
