/**
 * @fileoverview Invite-only sign-in, first increment (AD-50 rule 6, AD-29): the
 * allowlist is read from configuration (Vault: Echo__AllowedEmails)
 * @module ContactsApi.WebApi.Services
 */
namespace ContactsApi.WebApi.Services;

/// <summary>
/// The accounts that may sign in. The list comes from configuration (<c>Echo:AllowedEmails</c>,
/// in the cluster the Vault key <c>Echo__AllowedEmails</c>), never from the repository, Helm
/// values or a ConfigMap. Entries are separated by commas, semicolons or white space (a
/// Vault list rendered by the agent as <c>[a b]</c> works too) and compared case-insensitively.
/// Only a <b>verified</b> email matches. An empty or missing list admits nobody (fail closed),
/// and the app logs a warning at startup saying so.
/// </summary>
public class AllowedEmails
{
    /// <summary>The configuration key</summary>
    public const string ConfigKey = "Echo:AllowedEmails";

    private static readonly char[] Separators = { ',', ';', ' ', '\t', '\r', '\n', '[', ']', '"', '\'' };

    private readonly HashSet<string> _emails;

    /// <summary>Creates the list from its configured text</summary>
    /// <param name="configured">The raw configuration value (may be null)</param>
    public AllowedEmails(string? configured)
    {
        _emails = (configured ?? string.Empty)
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(e => e.Length > 0 && e.Contains('@'))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Reads the list from configuration</summary>
    public static AllowedEmails FromConfiguration(IConfiguration configuration) => new(configuration[ConfigKey]);

    /// <summary>Whether any address is configured (when not, nobody can sign in)</summary>
    public bool IsConfigured => _emails.Count > 0;

    /// <summary>How many addresses are on the list (for the startup log; never the addresses)</summary>
    public int Count => _emails.Count;

    /// <summary>Whether an account may sign in</summary>
    /// <param name="email">The email from the validated ID token</param>
    /// <param name="emailVerified">The ID token's <c>email_verified</c></param>
    /// <returns>True only for a verified address on the list</returns>
    public bool IsAllowed(string? email, bool emailVerified) =>
        emailVerified && !string.IsNullOrWhiteSpace(email) && _emails.Contains(Normalize(email));

    /// <summary>The form addresses are compared in</summary>
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
