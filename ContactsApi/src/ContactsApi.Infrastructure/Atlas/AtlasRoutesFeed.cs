/**
 * @fileoverview What the route-quality service may read of Atlas (AD-33 rule 5):
 * owners, the change feed, tracks and existence. Read-only, owner-checked, batched.
 * @module ContactsApi.Infrastructure.Atlas
 */
using System.Globalization;
using ContactsApi.Infrastructure.Routes;
using Microsoft.EntityFrameworkCore;

namespace ContactsApi.Infrastructure.Atlas;

/// <summary>The owner's fusion queue is not empty: routes wait for settled fusion</summary>
public class DerivationNotReadyException : Exception
{
    /// <summary>Creates the exception</summary>
    public DerivationNotReadyException() : base("Fusion is still running for this owner. Try again later.")
    {
    }
}

/// <summary>
/// Atlas reads for the route-quality service. Every query filters by owner first
/// and is backed by an index that starts with it; nothing is read per record.
/// </summary>
public class AtlasRoutesFeed
{
    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly AtlasDbContext _db;
    private readonly TimeProvider _clock;

    /// <summary>Creates the feed</summary>
    public AtlasRoutesFeed(AtlasDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Owners with Atlas data. There is no allowlist yet (AD-29, Phase 0), so this is
    /// every owner with an activity; the allowlist's active filter joins here when it exists.
    /// </summary>
    public Task<List<string>> OwnersAsync(CancellationToken ct = default) =>
        // A loose index scan over (OwnerId, StartTime): one index probe per owner
        _db.Database.SqlQuery<string>($@"
            WITH RECURSIVE o AS (
                (SELECT ""OwnerId"" AS v FROM atlas.activities ORDER BY ""OwnerId"" LIMIT 1)
                UNION ALL
                SELECT (SELECT ""OwnerId"" FROM atlas.activities WHERE ""OwnerId"" > o.v ORDER BY ""OwnerId"" LIMIT 1)
                  FROM o WHERE o.v IS NOT NULL)
            SELECT v AS ""Value"" FROM o WHERE v IS NOT NULL").ToListAsync(ct);

    /// <summary>
    /// Source records whose track or fused type may have changed after the cursor,
    /// oldest change first. Built from activities.UpdatedAt and activity_fusion.UpdatedAt.
    /// </summary>
    /// <remarks>
    /// UpdatedAt is stamped before its transaction commits, so a record stamped just before
    /// a read can become visible only after it. The last page's cursor therefore carries the
    /// time of the read, and the next read starts again <see cref="RoutesFeedLimits.Overlap"/>
    /// before that time while the cursor is within it: those records come again (the
    /// service's unchanged check skips them with no track read) and a late commit is never
    /// passed over. Once the cursor is older than the overlap, nothing is read twice.
    /// </remarks>
    /// <exception cref="DerivationNotReadyException">The owner's fusion is due or running</exception>
    /// <exception cref="ArgumentException">The cursor is not one this feed wrote</exception>
    public async Task<RouteChangesPage> ChangesAsync(string owner, string? after, int limit = RoutesFeedLimits.ChangesMax, CancellationToken ct = default)
    {
        var (t, id, readAt) = ParseCursor(after);
        var now = _clock.GetUtcNow().UtcDateTime;
        if (readAt.HasValue && t > readAt.Value - RoutesFeedLimits.Overlap)
        {
            t = readAt.Value - RoutesFeedLimits.Overlap;
            id = Guid.Empty;
        }

        limit = Math.Clamp(limit, 1, RoutesFeedLimits.ChangesMax);
        // Waiting fusion blocks the feed, but not an entry in backoff or one that keeps
        // failing (it would hold every route back until someone fixed it)
        if (await _db.FusionQueue.AnyAsync(q => q.OwnerId == owner && q.Attempts < RoutesFeedLimits.FusionStuckAttempts
                                                 && (q.NextAttemptAt == null || q.NextAttemptAt <= now), ct))
        {
            throw new DerivationNotReadyException();
        }

        var take = limit + 1;
        // Both halves use (OwnerId, UpdatedAt) indexes; members of a re-fused activity
        // come through (OwnerId, ClusterId). The fused type is the cluster primary's.
        var rows = await _db.Database.SqlQuery<ChangeRow>($@"
            SELECT x.""Id"", x.""T"", a.""UpdatedAt"", a.""PointCount"",
                   COALESCE(f.""Type"", a.""Type"") AS ""Type"", a.""Source"", a.""ExternalId"", a.""SourceType""
              FROM (SELECT c.""Id"", max(c.""T"") AS ""T""
                      FROM (SELECT a.""Id"", a.""UpdatedAt"" AS ""T""
                              FROM atlas.activities a
                             WHERE a.""OwnerId"" = {owner} AND a.""UpdatedAt"" >= {t}
                            UNION ALL
                            SELECT m.""Id"", f.""UpdatedAt""
                              FROM atlas.activity_fusion f
                              JOIN atlas.activities m ON m.""OwnerId"" = {owner} AND m.""ClusterId"" = f.""ActivityId""
                             WHERE f.""OwnerId"" = {owner} AND f.""UpdatedAt"" >= {t}) c
                     GROUP BY c.""Id"") x
              JOIN atlas.activities a ON a.""Id"" = x.""Id"" AND a.""OwnerId"" = {owner}
              LEFT JOIN atlas.activity_fusion f ON f.""ActivityId"" = COALESCE(a.""ClusterId"", a.""Id"")
             WHERE (x.""T"", x.""Id"") > ({t}, {id})
             ORDER BY x.""T"", x.""Id""
             LIMIT {take}").ToListAsync(ct);

        var more = rows.Count > limit;
        if (more)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = rows.Select(r => new RouteChange(r.Id, Utc(r.UpdatedAt), r.PointCount, r.Type,
            RouteKeys.SourceKey(r.Source, r.ExternalId, r.Id), r.SourceType)).ToList();
        string? next;
        if (more)
        {
            // More after this page: the next one simply follows it (no read time, no overlap)
            next = Cursor(Utc(rows[^1].T), rows[^1].Id);
        }
        else
        {
            // The last page: never behind the cursor asked with, and stamped with this read's time
            var (lastT, lastId, _) = ParseCursor(after);
            if (rows.Count > 0 && Utc(rows[^1].T) >= lastT)
            {
                (lastT, lastId) = (Utc(rows[^1].T), rows[^1].Id);
            }

            next = after == null && rows.Count == 0 ? null : Cursor(lastT, lastId, now);
        }

        return new RouteChangesPage(items, next, more);
    }

    /// <summary>
    /// The records' tracks (at most 25), with the Visits just before and after. Ids
    /// the owner does not have are left out. Three queries for the whole batch. Long
    /// tracks are thinned in the query (every n-th point by the record's PointCount, both
    /// ends kept), so at most <see cref="RoutesFeedLimits.PointsMax"/> points per record
    /// are ever read into memory; the thinning depends on the record alone, so its points,
    /// and the service's hash of them, are the same in any batch.
    /// </summary>
    public async Task<List<RouteTrack>> TracksAsync(string owner, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count > RoutesFeedLimits.TracksMax)
        {
            throw new ArgumentException($"At most {RoutesFeedLimits.TracksMax} ids.");
        }

        var wanted = ids.Distinct().ToList();
        var records = await (
                from a in _db.Activities.AsNoTracking()
                where a.OwnerId == owner && wanted.Contains(a.Id)
                join f in _db.Fusions.AsNoTracking() on (a.ClusterId ?? a.Id) equals f.ActivityId into fj
                from f in fj.DefaultIfEmpty()
                select new
                {
                    a.Id, a.Source, a.ExternalId, Type = f != null && f.Type != null ? f.Type : a.Type, a.SourceType,
                    a.StartTime, a.EndTime, a.UpdatedAt, a.PointCount
                })
            .ToListAsync(ct);
        if (records.Count == 0)
        {
            return new List<RouteTrack>();
        }

        // Only the owner's records reach the points query
        var owned = records.Select(r => r.Id).ToArray();
        var steps = records.Select(r => ThinStep(r.PointCount)).ToArray();
        var counts = records.Select(r => r.PointCount).ToArray();
        var points = (await _db.Database.SqlQuery<PointRow>($@"
                SELECT p.""ActivityId"", p.""Time"", p.""Lat"", p.""Lon""
                  FROM (SELECT tp.""ActivityId"", tp.""Time"", tp.""Lat"", tp.""Lon"", s.step, s.n,
                               row_number() OVER (PARTITION BY tp.""ActivityId"" ORDER BY tp.""Time"") AS rn
                          FROM unnest({owned}, {steps}, {counts}) AS s(id, step, n)
                          JOIN atlas.track_points tp ON tp.""ActivityId"" = s.id) p
                 WHERE p.step = 1 OR (p.rn - 1) % p.step = 0 OR p.rn = p.n
                 ORDER BY p.""ActivityId"", p.""Time""").ToListAsync(ct))
            .GroupBy(p => p.ActivityId)
            // Thin again only when PointCount understated the track
            .ToDictionary(g => g.Key, g => Thin(g.Select(p => new RoutePoint(Utc(p.Time), p.Lat, p.Lon)).ToList()));

        var idArray = records.Select(r => r.Id).ToArray();
        var starts = records.Select(r => Utc(r.StartTime)).ToArray();
        var ends = records.Select(r => Utc(r.EndTime)).ToArray();
        // The nearest Visit on each side, by (OwnerId, StartTime)
        var visits = (await _db.Database.SqlQuery<VisitRow>($@"
                SELECT v.id AS ""Id"",
                       b.""StartTime"" AS ""BStart"", b.""EndTime"" AS ""BEnd"", b.""Lat"" AS ""BLat"", b.""Lon"" AS ""BLon"",
                       n.""StartTime"" AS ""NStart"", n.""EndTime"" AS ""NEnd"", n.""Lat"" AS ""NLat"", n.""Lon"" AS ""NLon""
                  FROM unnest({idArray}, {starts}, {ends}) AS v(id, s, e)
                  LEFT JOIN LATERAL (SELECT ""StartTime"", ""EndTime"", ""Lat"", ""Lon"" FROM atlas.visits
                                      WHERE ""OwnerId"" = {owner} AND ""StartTime"" <= v.s
                                      ORDER BY ""StartTime"" DESC LIMIT 1) b ON true
                  LEFT JOIN LATERAL (SELECT ""StartTime"", ""EndTime"", ""Lat"", ""Lon"" FROM atlas.visits
                                      WHERE ""OwnerId"" = {owner} AND ""StartTime"" >= v.e
                                      ORDER BY ""StartTime"" LIMIT 1) n ON true").ToListAsync(ct))
            .ToDictionary(v => v.Id);

        return records.Select(r =>
        {
            var v = visits.GetValueOrDefault(r.Id);
            var before = v?.BStart != null ? new RouteVisit(Utc(v.BStart.Value), Utc(v.BEnd!.Value), v.BLat!.Value, v.BLon!.Value) : null;
            var next = v?.NStart != null ? new RouteVisit(Utc(v.NStart.Value), Utc(v.NEnd!.Value), v.NLat!.Value, v.NLon!.Value) : null;
            return new RouteTrack(r.Id, r.Source, r.ExternalId, r.Type, r.SourceType, Utc(r.StartTime), Utc(r.EndTime),
                Utc(r.UpdatedAt), r.PointCount, points.GetValueOrDefault(r.Id) ?? new List<RoutePoint>(), before, next);
        }).ToList();
    }

    /// <summary>
    /// The owner's source records that start in [from, to), oldest first, a page at a
    /// time (a date-range job). Ids only, over the (OwnerId, StartTime) index.
    /// </summary>
    /// <exception cref="ArgumentException">The cursor is not one this feed wrote</exception>
    public async Task<RouteIdsPage> RangeAsync(string owner, DateTime from, DateTime to, string? after, int limit = RoutesFeedLimits.ChangesMax,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, RoutesFeedLimits.ChangesMax);
        from = Utc(from);
        to = Utc(to);
        var (t, id, _) = after == null ? (from, Guid.Empty, (DateTime?)null) : ParseCursor(after);
        var rows = await _db.Activities.AsNoTracking()
            .Where(a => a.OwnerId == owner && a.StartTime >= from && a.StartTime < to
                        && (a.StartTime > t || (a.StartTime == t && a.Id.CompareTo(id) > 0)))
            .OrderBy(a => a.StartTime).ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.StartTime })
            .Take(limit + 1)
            .ToListAsync(ct);
        var more = rows.Count > limit;
        if (more)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var next = rows.Count > 0 ? Cursor(Utc(rows[^1].StartTime), rows[^1].Id) : after;
        return new RouteIdsPage(rows.Select(r => r.Id).ToList(), next, more);
    }

    /// <summary>Which of the ids still exist for the owner (the weekly orphan sweep)</summary>
    public Task<List<Guid>> ExistsAsync(string owner, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count > RoutesFeedLimits.ExistsMax)
        {
            throw new ArgumentException($"At most {RoutesFeedLimits.ExistsMax} ids.");
        }

        var wanted = ids.Distinct().ToList();
        return _db.Activities.AsNoTracking().Where(a => a.OwnerId == owner && wanted.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct);
    }

    /// <summary>Every how many points a track of <paramref name="pointCount"/> points is read (1: all)</summary>
    internal static int ThinStep(int pointCount) =>
        pointCount <= RoutesFeedLimits.PointsMax ? 1 : (int)Math.Ceiling((pointCount - 1) / (double)(RoutesFeedLimits.PointsMax - 1));

    /// <summary>A track thinned evenly to at most PointsMax points, keeping both ends</summary>
    internal static List<RoutePoint> Thin(List<RoutePoint> points, int max = RoutesFeedLimits.PointsMax)
    {
        if (points.Count <= max)
        {
            return points;
        }

        var thinned = new List<RoutePoint>(max);
        for (var i = 0; i < max; i++)
        {
            thinned.Add(points[(int)Math.Round(i * (points.Count - 1) / (double)(max - 1))]);
        }

        return thinned;
    }

    /// <summary>An opaque cursor: the change time and the record id, and on a last page the time of the read</summary>
    internal static string Cursor(DateTime t, Guid id, DateTime? readAt = null) =>
        $"{t.Ticks.ToString(CultureInfo.InvariantCulture)}_{id:N}"
        + (readAt.HasValue ? "_" + readAt.Value.Ticks.ToString(CultureInfo.InvariantCulture) : string.Empty);

    internal static (DateTime T, Guid Id, DateTime? ReadAt) ParseCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return (Epoch, Guid.Empty, null);
        }

        var parts = cursor.Split('_');
        if (parts.Length is < 2 or > 3 || !TryTicks(parts[0], out var t) || !Guid.TryParseExact(parts[1], "N", out var id))
        {
            throw new ArgumentException("Unknown cursor.");
        }

        DateTime? readAt = null;
        if (parts.Length == 3)
        {
            if (!TryTicks(parts[2], out var r))
            {
                throw new ArgumentException("Unknown cursor.");
            }

            readAt = r;
        }

        return (t, id, readAt);
    }

    private static bool TryTicks(string s, out DateTime t)
    {
        t = default;
        if (!long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks < Epoch.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        t = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    private static DateTime Utc(DateTime d) => d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d.ToUniversalTime(), DateTimeKind.Utc);

    internal sealed class ChangeRow
    {
        public Guid Id { get; set; }
        public DateTime T { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int PointCount { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string? ExternalId { get; set; }
        public string? SourceType { get; set; }
    }

    internal sealed class PointRow
    {
        public Guid ActivityId { get; set; }
        public DateTime Time { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
    }

    internal sealed class VisitRow
    {
        public Guid Id { get; set; }
        public DateTime? BStart { get; set; }
        public DateTime? BEnd { get; set; }
        public double? BLat { get; set; }
        public double? BLon { get; set; }
        public DateTime? NStart { get; set; }
        public DateTime? NEnd { get; set; }
        public double? NLat { get; set; }
        public double? NLon { get; set; }
    }
}
