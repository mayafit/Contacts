/**
 * @fileoverview Row-level security settings (AD-49 rule 8): the interceptor's mode and
 * whether the policy migrations may be applied
 * @module ContactsApi.Infrastructure.Tenancy
 */
using Npgsql;

namespace ContactsApi.Infrastructure.Tenancy;

/// <summary>What the interceptor does (AD-49 rule 8, steps 2 and 5)</summary>
public enum OwnerRlsMode
{
    /// <summary>Nothing: app.owner_id is never set (the emergency switch; useless once policies are on)</summary>
    Off,

    /// <summary>
    /// Log-only (rollout step 2): the Owner is set on every connection, and a command
    /// with no Owner in scope logs a warning with its stack ("owner scope missing") and
    /// runs with app.owner_id = '' instead of throwing
    /// </summary>
    Shadow,

    /// <summary>A command with no Owner in scope throws <see cref="OwnerScopeMissingException"/> (rollout step 5)</summary>
    Enforce
}

/// <summary>The RowLevelSecurity configuration section (RowLevelSecurity__Mode, RowLevelSecurity__Policies)</summary>
public class RowLevelSecurityOptions
{
    /// <summary>The configuration section</summary>
    public const string Section = "RowLevelSecurity";

    /// <summary>The interceptor's mode; Shadow until the logs prove every path sets its Owner</summary>
    public OwnerRlsMode Mode { get; set; } = OwnerRlsMode.Shadow;

    /// <summary>
    /// Whether the migrator may apply the owner_isolation policy migrations
    /// (AtlasRowLevelSecurity, RoutesRowLevelSecurity; rollout step 4). Off: the migrator
    /// stops before them, and readiness does not wait for them.
    /// </summary>
    public bool Policies { get; set; }

    /// <summary>
    /// Whether the API applies its migrations at startup (the pre-AD-49 way). Off once
    /// the migrate-only init container runs them (AD-49 rule 3): the API then only waits
    /// until none is pending.
    /// </summary>
    public bool MigrateOnStartup { get; set; } = true;
}

/// <summary>Connection string rules that keep the session variable safe (AD-49 rule 4)</summary>
public static class OwnerConnectionStrings
{
    /// <summary>
    /// Refuses Multiplexing and No Reset On Close (both share or keep session state
    /// across logical connections) and adds an Application Name and a Maximum Pool Size
    /// when the string has none. A blank or placeholder string is returned as it is.
    /// </summary>
    /// <exception cref="InvalidOperationException">Multiplexing or No Reset On Close is on</exception>
    public static string Prepare(string? connectionString, string applicationName, int maxPoolSize)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString == "Host=unset")
        {
            return connectionString ?? string.Empty;
        }

        var b = new NpgsqlConnectionStringBuilder(connectionString);
        if (b.Multiplexing)
        {
            throw new InvalidOperationException("Multiplexing=true shares one session among owners; it is refused (AD-49).");
        }

        if (b.NoResetOnClose)
        {
            throw new InvalidOperationException("No Reset On Close=true keeps app.owner_id on pooled connections; it is refused (AD-49).");
        }

        if (string.IsNullOrEmpty(b.ApplicationName))
        {
            b.ApplicationName = applicationName;
        }

        if (b.MaxPoolSize == new NpgsqlConnectionStringBuilder().MaxPoolSize)
        {
            b.MaxPoolSize = maxPoolSize;
        }

        return b.ConnectionString;
    }
}
