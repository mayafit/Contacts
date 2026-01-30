namespace ContactsApi.Core.Entities;

/// <summary>
/// User token entity for storing encrypted OAuth tokens
/// </summary>
public class UserToken
{
    /// <summary>
    /// User ID (foreign key to User)
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted access token
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted refresh token
    /// </summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// Token expiration timestamp
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Last update timestamp
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Navigation property to User
    /// </summary>
    public User? User { get; set; }
}
