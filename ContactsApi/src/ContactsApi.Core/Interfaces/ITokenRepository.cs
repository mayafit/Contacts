using ContactsApi.Core.Entities;

namespace ContactsApi.Core.Interfaces;

/// <summary>
/// Repository interface for user token storage
/// </summary>
public interface ITokenRepository
{
    /// <summary>
    /// Stores encrypted tokens for a user
    /// </summary>
    /// <param name="userToken">User token entity</param>
    Task StoreTokenAsync(UserToken userToken);

    /// <summary>
    /// Retrieves tokens for a user
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <returns>User token entity or null</returns>
    Task<UserToken?> GetTokenAsync(string userId);

    /// <summary>
    /// Deletes tokens for a user
    /// </summary>
    /// <param name="userId">User ID</param>
    Task DeleteTokenAsync(string userId);

    /// <summary>
    /// Updates existing token
    /// </summary>
    /// <param name="userToken">Updated token entity</param>
    Task UpdateTokenAsync(UserToken userToken);
}
