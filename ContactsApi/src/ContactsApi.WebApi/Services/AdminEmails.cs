/**
 * @fileoverview The owner/admin allowlist for operator-only features (Epic 22:
 * the system status bar and on-demand Grafana). Read from configuration
 * (Vault: Echo__AdminEmails), never from the repository. Fail-closed: an empty
 * or missing list means nobody is an admin, so the whole feature stays hidden.
 * @module ContactsApi.WebApi.Services
 */
namespace ContactsApi.WebApi.Services;

/// <summary>
/// The accounts that may use operator-only features. Separate from
/// <see cref="AllowedEmails"/> (who may sign in at all): every allowed account is
/// a peer family member who owns their own data, but only an admin sees the ops
/// controls. The list comes from configuration (<c>Echo:AdminEmails</c>, in the
/// cluster the Vault key <c>Echo__AdminEmails</c>), never from Git. Entries are
/// separated by commas, semicolons or white space and compared case-insensitively.
/// An empty or missing list admits nobody (fail closed).
/// </summary>
public class AdminEmails
{
    /// <summary>The configuration key</summary>
    public const string ConfigKey = "Echo:AdminEmails";

    private static readonly char[] Separators = { ',', ';', ' ', '\t', '\r', '\n', '[', ']', '"', '\'' };

    private readonly HashSet<string> _emails;

    /// <summary>Creates the list from its configured text</summary>
    /// <param name="configured">The raw configuration value (may be null)</param>
    public AdminEmails(string? configured)
    {
        _emails = (configured ?? string.Empty)
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(e => e.Length > 0 && e.Contains('@'))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Reads the list from configuration</summary>
    public static AdminEmails FromConfiguration(IConfiguration configuration) => new(configuration[ConfigKey]);

    /// <summary>Whether any admin address is configured (when not, the feature is hidden for all)</summary>
    public bool IsConfigured => _emails.Count > 0;

    /// <summary>How many addresses are on the list (for the startup log; never the addresses)</summary>
    public int Count => _emails.Count;

    /// <summary>
    /// Whether the signed-in account is an admin. The email comes from the
    /// validated session (already allowlisted and verified), so no re-check of
    /// <c>email_verified</c> is needed here.
    /// </summary>
    /// <param name="email">The signed-in account's email</param>
    public bool IsAdmin(string? email) =>
        !string.IsNullOrWhiteSpace(email) && _emails.Contains(Normalize(email));

    /// <summary>The form addresses are compared in</summary>
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
