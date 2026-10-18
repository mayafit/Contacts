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

    /// <summary>The user saw which unmerged data the deleted contacts hold and accepted losing it</summary>
    public bool AcknowledgeDataLoss { get; set; }
}

/// <summary>
/// Result of a merge
/// </summary>
public class MergeResult
{
    /// <summary>The kept contact as saved</summary>
    public Contact Contact { get; set; } = new();

    /// <summary>Contacts deleted</summary>
    public List<string> Deleted { get; set; } = new();
}
