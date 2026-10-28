/**
 * @fileoverview Stage A over one Owner's records: pull, skip what did not change,
 * fetch tracks in batches, assess, write route_quality (AD-33 rule 5, AD-35, AD-36)
 * @module EchoRoutes.Worker.Assess
 */
using System.Text.Json;
using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker.Backend;
using Microsoft.EntityFrameworkCore;

namespace EchoRoutes.Worker.Assess;

/// <summary>What one batch did</summary>
/// <param name="Seen">Records the batch looked at</param>
/// <param name="Assessed">Records assessed and written</param>
/// <param name="Skipped">Records whose stored values already matched</param>
/// <param name="More">More work waits (another page of the feed)</param>
public record AssessBatchResult(int Seen, int Assessed, int Skipped, bool More);

/// <summary>
/// Stage A for one Owner at a time. A batch never holds two Owners' records, and
/// every query filters by the Owner first. Database work per batch: one read of the
/// stored rows, one of the matched routes, one save.
/// </summary>
public class AssessRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly RoutesDbContext _db;
    private readonly IRoutesBackend _backend;
    private readonly RegionCoverage _coverage;

    /// <summary>Creates the runner</summary>
    public AssessRunner(RoutesDbContext db, IRoutesBackend backend, RegionCoverage coverage)
    {
        _db = db;
        _backend = backend;
        _coverage = coverage;
    }

    /// <summary>
    /// One page of the Owner's change feed (at most <paramref name="limit"/> records),
    /// assessed, with the cursor moved past it in the same save
    /// </summary>
    /// <exception cref="DerivationPendingException">The Owner's fusion is not settled</exception>
    /// <exception cref="BackendBusyException">Interactive reads are in flight</exception>
    public async Task<AssessBatchResult> RunFeedBatchAsync(string owner, int limit, bool interactive, CancellationToken ct)
    {
        var cursor = await _db.Cursors.FirstOrDefaultAsync(c => c.OwnerId == owner, ct);
        var page = await _backend.ChangesAsync(owner, cursor?.Cursor, limit, interactive, ct);
        var (assessed, skipped) = await AssessChangesAsync(owner, page.Items, interactive, ct);
        if (cursor == null)
        {
            cursor = new OwnerCursor { OwnerId = owner };
            _db.Cursors.Add(cursor);
        }

        cursor.Cursor = page.Next;
        cursor.UpdatedAt = DateTime.UtcNow;
        cursor.LastRunAt = DateTime.UtcNow;
        cursor.LastError = null;
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return new AssessBatchResult(page.Items.Count, assessed, skipped, page.More);
    }

    /// <summary>Assesses records named by id (a job); the tracks say what each record is now</summary>
    public async Task<AssessBatchResult> AssessIdsAsync(string owner, IReadOnlyList<Guid> ids, bool interactive, CancellationToken ct)
    {
        var tracks = await TracksAsync(owner, ids.Distinct().ToList(), interactive, ct);
        var stored = await StoredAsync(owner, tracks.Select(t => t.Id).ToList(), ct);
        await WriteAsync(owner, tracks, stored, ct);
        return new AssessBatchResult(ids.Count, tracks.Count, 0, false);
    }

    /// <summary>
    /// Assesses the feed's candidates. A record whose UpdatedAt, point count and fused
    /// type equal the stored values (under the current rule) is skipped with no track
    /// fetched: after a re-fusion the feed returns the whole window, and the stored
    /// values decide (AD-33 rule 5).
    /// </summary>
    public async Task<(int Assessed, int Skipped)> AssessChangesAsync(string owner, IReadOnlyList<RouteChange> items, bool interactive, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return (0, 0);
        }

        var stored = await StoredAsync(owner, items.Select(i => i.Id).ToList(), ct);
        var todo = items.Where(i => !Unchanged(stored.GetValueOrDefault(i.Id), i)).ToList();
        if (todo.Count == 0)
        {
            return (0, items.Count);
        }

        // Records without a track need no points: the feed item says all there is
        var trackless = todo.Where(i => i.PointCount < 2)
            .Select(i => new RouteTrack(i.Id, string.Empty, null, i.Type, i.SourceType, default, default, i.UpdatedAt, i.PointCount,
                new List<RoutePoint>(), null, null))
            .ToList();
        var keys = todo.ToDictionary(i => i.Id, i => i.SourceKey);
        var tracks = await TracksAsync(owner, todo.Where(i => i.PointCount >= 2).Select(i => i.Id).ToList(), interactive, ct);
        await WriteAsync(owner, tracks.Concat(trackless).ToList(), stored, ct, keys);
        return (tracks.Count + trackless.Count, items.Count - todo.Count);
    }

    /// <summary>The stored row still describes the record (no re-assessment needed)</summary>
    public static bool Unchanged(RouteQualityRow? row, RouteChange item) =>
        row != null && row.RuleVersion == RouteQuality.RuleVersion && row.SourceUpdatedAt == item.UpdatedAt
        && row.PointCount == item.PointCount && row.Type == item.Type;

    private async Task<List<RouteTrack>> TracksAsync(string owner, List<Guid> ids, bool interactive, CancellationToken ct)
    {
        var tracks = new List<RouteTrack>(ids.Count);
        foreach (var chunk in ids.Chunk(RoutesFeedLimits.TracksMax))
        {
            tracks.AddRange(await _backend.TracksAsync(owner, chunk, interactive, ct));
        }

        return tracks;
    }

    private Task<Dictionary<Guid, RouteQualityRow>> StoredAsync(string owner, List<Guid> ids, CancellationToken ct) =>
        _db.Quality.Where(q => q.OwnerId == owner && ids.Contains(q.ActivityId)).ToDictionaryAsync(q => q.ActivityId, ct);

    private async Task WriteAsync(string owner, List<RouteTrack> tracks, Dictionary<Guid, RouteQualityRow> stored, CancellationToken ct,
        IReadOnlyDictionary<Guid, string>? keys = null)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        var ids = tracks.Select(t => t.Id).ToList();
        // A match whose points did not change only gets the record's new values (AD-34 rule 3)
        var matched = await _db.MatchedRoutes.Where(m => m.OwnerId == owner && ids.Contains(m.ActivityId)).ToDictionaryAsync(m => m.ActivityId, ct);
        var now = DateTime.UtcNow;
        foreach (var t in tracks)
        {
            var a = RouteQuality.Assess(t.Type, t.SourceType, t.Points, t.Before, t.After, _coverage.Covers);
            var hash = RouteQuality.InputHash(t.Points);
            if (!stored.TryGetValue(t.Id, out var row))
            {
                row = new RouteQualityRow { OwnerId = owner, ActivityId = t.Id };
                _db.Quality.Add(row);
                stored[t.Id] = row;
            }

            row.SourceKey = keys?.GetValueOrDefault(t.Id) ?? t.SourceKey;
            row.Type = t.Type;
            row.SourceType = t.SourceType;
            row.InputHash = hash;
            row.SourceUpdatedAt = t.UpdatedAt;
            row.PointCount = t.PointCount;
            row.TrackM = a.Metrics.PointCount >= 2 ? Math.Round(a.Metrics.TrackM, 1) : null;
            row.Metrics = JsonSerializer.Serialize(a.Metrics, Json);
            row.Score = a.Score;
            row.Grade = a.Grade;
            row.Flags = string.Join(",", a.Flags);
            row.NeedsImprovement = a.NeedsImprovement;
            row.Matchable = a.Matchable;
            row.RuleVersion = RouteQuality.RuleVersion;
            row.ComputedAt = now;

            if (matched.TryGetValue(t.Id, out var m) && m.InputHash == hash && m.FusedType == t.Type)
            {
                m.SourceUpdatedAt = t.UpdatedAt;
                m.SourcePointCount = t.PointCount;
            }
        }

        await _db.SaveChangesAsync(ct);
    }
}
