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
