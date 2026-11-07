namespace ContactsApi.WebApi.Models.Responses;

/// <summary>
/// Authentication status response
/// </summary>
public class AuthStatusResponse
{
    public bool IsAuthenticated { get; set; }
    public UserInfo? User { get; set; }

    /// <summary>True when the signed-in account is an operator/admin (Echo:AdminEmails, Epic 22).
    /// Gates the owner-only system status bar + on-demand Grafana. Fail-closed: false when unset.</summary>
    public bool IsAdmin { get; set; }
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
