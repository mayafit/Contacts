/**
 * @fileoverview Google OAuth configuration options loaded from Vault
 * @module ContactsApi.WebApi.Configuration
 */

namespace ContactsApi.WebApi.Configuration;

/// <summary>
/// Google OAuth configuration options
/// Sensitive values (ClientSecret) loaded from Vault at runtime
/// </summary>
public class GoogleOAuthOptions
{
    /// <summary>
    /// Google OAuth Client ID (public, from configuration)
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Google OAuth Client Secret (sensitive, loaded from Vault)
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// OAuth redirect URI
    /// </summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// OAuth scopes
    /// </summary>
    public string[] Scopes { get; set; } = Array.Empty<string>();
}
