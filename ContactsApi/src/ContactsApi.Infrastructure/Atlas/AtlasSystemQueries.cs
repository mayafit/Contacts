/**
 * @fileoverview Calls of the atlas.sys_* definer functions (AD-49 rule 5): the only way
 * work that spans Owners learns who they are. They return owner ids or counts, never
 * rows; the work itself then runs per Owner, under that Owner's scope.
 * @module ContactsApi.Infrastructure.Atlas
 */
using Microsoft.EntityFrameworkCore;

namespace ContactsApi.Infrastructure.Atlas;

/// <summary>The kinds of <see cref="AtlasSystemQueries.SysOwnersAsync"/></summary>
public static class SysOwnerKinds
{
    /// <summary>Owners with activities (a loose index scan)</summary>
    public const string Activities = "activities";

    /// <summary>Owners with queued fusion work</summary>
    public const string FusionQueue = "fusion_queue";

    /// <summary>Owners with flight activities</summary>
    public const string Flights = "flights";

    /// <summary>Owners with photos waiting for placement</summary>
    public const string UnplacedPhotos = "unplaced_photos";

    /// <summary>Owners with places waiting for an identity (pending, or none found yet)</summary>
    public const string PlacesToIdentify = "places_to_identify";

    /// <summary>Every Owner with activities while no fused activity exists at all (data from before fusion)</summary>
    public const string FusionBackfill = "fusion_backfill";

    /// <summary>Every Owner with a source connection, expired ones included (the key-ring rotation, AD-50 rule 2)</summary>
    public const string SourceConnections = "source_connections";
}

/// <summary>
/// The atlas.sys_* functions. Callers without an Owner mark the work with
/// <see cref="Tenancy.OwnerScope.AllowUnscoped"/>; with an Owner set they work as well.
/// </summary>
public static class AtlasSystemQueries
{
    /// <summary>Owner ids of one kind of work, in ordinal order</summary>
    public static async Task<List<string>> SysOwnersAsync(this AtlasDbContext db, string kind, CancellationToken ct = default) =>
        Sorted(await db.Database.SqlQuery<string>($"SELECT o AS \"Value\" FROM atlas.sys_owners({kind}) AS o").ToListAsync(ct));

    /// <summary>Owner ids with a connection of the provider that has not expired</summary>
    public static async Task<List<string>> SysOwnersWithConnectionAsync(this AtlasDbContext db, string provider, CancellationToken ct = default) =>
        Sorted(await db.Database.SqlQuery<string>($"SELECT o AS \"Value\" FROM atlas.sys_owners_with_connection({provider}) AS o").ToListAsync(ct));

    /// <summary>Marks the provider's syncs cut short by a restart as failed; returns how many</summary>
    public static async Task<int> SysFailInterruptedSyncsAsync(this AtlasDbContext db, string provider, CancellationToken ct = default) =>
        (await db.Database.SqlQuery<int>($"SELECT atlas.sys_fail_interrupted_syncs({provider}) AS \"Value\"").ToListAsync(ct)).Single();

    /// <summary>The Owner of an access token's hash (and stamps its use, at most hourly), or null; null too once it has expired</summary>
    public static async Task<string?> SysResolveAccessTokenAsync(this AtlasDbContext db, string hash, CancellationToken ct = default) =>
        (await db.Database.SqlQuery<string?>($"SELECT atlas.sys_resolve_access_token({hash}) AS \"Value\"").ToListAsync(ct)).SingleOrDefault();

    private static List<string> Sorted(List<string> owners)
    {
        owners.Sort(StringComparer.Ordinal);
        return owners;
    }
}
