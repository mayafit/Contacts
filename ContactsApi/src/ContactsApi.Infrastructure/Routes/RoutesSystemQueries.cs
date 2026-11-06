/**
 * @fileoverview Calls of the routes.sys_* definer functions (AD-49 rule 5), executable by
 * echo_routes only: the route service's cross-Owner discovery and the job prune
 * @module ContactsApi.Infrastructure.Routes
 */
using Microsoft.EntityFrameworkCore;

namespace ContactsApi.Infrastructure.Routes;

/// <summary>The routes.sys_* functions (callers without an Owner mark the work unscoped)</summary>
public static class RoutesSystemQueries
{
    /// <summary>Owners with an interactive job due at <paramref name="now"/>, in ordinal order</summary>
    public static async Task<List<string>> SysOwnersWithOpenInteractiveJobsAsync(this RoutesDbContext db, DateTime now, CancellationToken ct = default) =>
        Sorted(await db.Database.SqlQuery<string>(
            $"SELECT o AS \"Value\" FROM routes.sys_owners_with_open_interactive_jobs({DateTime.SpecifyKind(now, DateTimeKind.Utc)}) AS o").ToListAsync(ct));

    /// <summary>Every Owner the service keeps data for (a cursor or a job)</summary>
    public static async Task<List<string>> SysKnownOwnersAsync(this RoutesDbContext db, CancellationToken ct = default) =>
        Sorted(await db.Database.SqlQuery<string>($"SELECT o AS \"Value\" FROM routes.sys_known_owners() AS o").ToListAsync(ct));

    /// <summary>Deletes jobs that finished before <paramref name="before"/>; returns how many</summary>
    public static async Task<int> SysPruneFinishedJobsAsync(this RoutesDbContext db, DateTime before, CancellationToken ct = default) =>
        (await db.Database.SqlQuery<int>(
            $"SELECT routes.sys_prune_finished_jobs({DateTime.SpecifyKind(before, DateTimeKind.Utc)}) AS \"Value\"").ToListAsync(ct)).Single();

    private static List<string> Sorted(List<string> owners)
    {
        owners.Sort(StringComparer.Ordinal);
        return owners;
    }
}
