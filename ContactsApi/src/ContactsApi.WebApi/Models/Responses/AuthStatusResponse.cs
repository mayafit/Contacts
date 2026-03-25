namespace ContactsApi.WebApi.Models.Responses;

/// <summary>
/// Authentication status response
/// </summary>
public class AuthStatusResponse
{
    public bool IsAuthenticated { get; set; }
    public UserInfo? User { get; set; }
}

/// <summary>
/// User information
/// </summary>
public class UserInfo
{
    public string Id { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Picture { get; set; }
}
