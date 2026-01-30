namespace ContactsApi.Core.Entities;

/// <summary>
/// User entity representing an authenticated user
/// </summary>
public class User
{
    /// <summary>
    /// Internal user ID
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Google user ID (from OAuth)
    /// </summary>
    public string GoogleId { get; set; } = string.Empty;

    /// <summary>
    /// User email address
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// User display name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Profile picture URL
    /// </summary>
    public string? ProfilePicture { get; set; }

    /// <summary>
    /// Account creation timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Last login timestamp
    /// </summary>
    public DateTime LastLoginAt { get; set; }
}
