/**
 * @fileoverview EchoMigrator (AD-10, amended by AD-49 rule 3): applies a schema's
 * migrations, from the migrate-only init container or (until it exists) at startup,
 * and holds the owner_isolation policy migration back until RowLevelSecurity__Policies
 * @module ContactsApi.Infrastructure.Tenancy
 */
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;

namespace ContactsApi.Infrastructure.Tenancy;

/// <summary>Applies migrations with the policy gate; the migrate-only entry point of both executables</summary>
public static class EchoMigrator
{
    /// <summary>The command-line switch of the init container (AD-49 rule 3)</summary>
    public const string MigrateOnlyFlag = "--migrate-only";

    /// <summary>Whether the process was started to migrate and exit</summary>
    public static bool MigrateOnlyRequested(string[] args) => args.Contains(MigrateOnlyFlag, StringComparer.Ordinal);

    /// <summary>
    /// The pending migrations the app must wait for: all of them, except the policy
    /// migration while its gate is closed
    /// </summary>
    public static async Task<List<string>> BlockingPendingAsync(DbContext db, string policyMigrationId, bool policies, CancellationToken ct)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        return policies ? pending : pending.Where(m => m != policyMigrationId).ToList();
    }

    /// <summary>
    /// Applies every pending migration; while <paramref name="policies"/> is off, stops just
    /// before <paramref name="policyMigrationId"/>. A migration after a held-back policy
    /// migration cannot apply (they are ordered), so that case throws.
    /// </summary>
    /// <returns>How many migrations were applied</returns>
    public static async Task<int> MigrateAsync(DbContext db, string policyMigrationId, bool policies, ILogger logger, CancellationToken ct)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count == 0)
        {
            return 0;
        }

        var at = pending.IndexOf(policyMigrationId);
        if (policies || at < 0)
        {
            await db.Database.MigrateAsync(ct);
            return pending.Count;
        }

        if (at > 0)
        {
            await db.GetService<IMigrator>().MigrateAsync(pending[at - 1], ct);
        }

        logger.LogWarning("Migration {Migration} (row-level security policies) is held back until RowLevelSecurity__Policies=true",
            policyMigrationId);
        if (at < pending.Count - 1)
        {
            throw new InvalidOperationException(
                $"{pending.Count - at - 1} migrations after {policyMigrationId} cannot apply while RowLevelSecurity__Policies is off.");
        }

        return at;
    }

    /// <summary>
    /// The migrate-only run (the init container): migrates with the migrator's connection
    /// string, retrying while Postgres is not reachable, and returns the exit code. No
    /// owner interceptor: the migrator owns the schema and bypasses row-level security.
    /// </summary>
    public static async Task<int> RunMigrateOnlyAsync<TContext>(string? migrationConnectionString,
        Func<string, DbContextOptions<TContext>> options, string policyMigrationId, bool policies, ILogger logger, CancellationToken ct = default)
        where TContext : DbContext
    {
        if (string.IsNullOrWhiteSpace(migrationConnectionString))
        {
            logger.LogError("--migrate-only: no migration connection string is configured");
            return 2;
        }

        var cs = OwnerConnectionStrings.Prepare(migrationConnectionString, "echo-migrator", 2);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var db = (TContext)Activator.CreateInstance(typeof(TContext), options(cs))!;
                // Some migrations fix data across all rows: more than the default 30 s
                db.Database.SetCommandTimeout(TimeSpan.FromMinutes(20));
                var applied = await MigrateAsync(db, policyMigrationId, policies, logger, ct);
                logger.LogInformation("--migrate-only: {Context} applied {Count} migrations", typeof(TContext).Name, applied);
                return 0;
            }
            catch (Npgsql.NpgsqlException ex) when (attempt < 10 && ex.IsTransient && !ct.IsCancellationRequested)
            {
                logger.LogWarning("--migrate-only: the database is not reachable ({Message}); retrying", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 3 * attempt)), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "--migrate-only: {Context} failed", typeof(TContext).Name);
                return 1;
            }
        }
    }
}
