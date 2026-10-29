/**
 * @fileoverview Stage A over one Owner's records: pull, skip what did not change,
 * fetch tracks in batches, assess, write route_quality (AD-33 rule 5, AD-35, AD-36)
 * @module EchoRoutes.Worker.Assess
 */
using System.Text.Json;
using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker.Backend;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EchoRoutes.Worker.Assess;

/// <summary>What one batch did</summary>
/// <param name="Seen">Records the batch looked at</param>
/// <param name="Assessed">Records assessed and written</param>
/// <param name="Skipped">Records whose stored values already matched</param>
/// <param name="More">More work waits (another page of the feed)</param>
/// <param name="Failed">Records that could not be assessed or stored: logged and skipped, so they never stall the feed</param>
public record AssessBatchResult(int Seen, int Assessed, int Skipped, bool More, int Failed = 0);

/// <summary>
/// Stage A for one Owner at a time. A batch never holds two Owners' records, and
/// every query filters by the Owner first. Database work per batch: one read of the
/// stored rows, one of the matched routes, one save. A record that cannot be assessed
/// or stored is logged and skipped (the batch is then saved one record at a time), so
/// one poison record never holds the Owner's cursor back.
/// </summary>
public class AssessRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly RoutesDbContext _db;
    private readonly IRoutesBackend _backend;
    private readonly RegionCoverage _coverage;
    private readonly ILogger<AssessRunner> _logger;

    /// <summary>Creates the runner</summary>
    public AssessRunner(RoutesDbContext db, IRoutesBackend backend, RegionCoverage coverage, ILogger<AssessRunner>? logger = null)
    {
        _db = db;
        _backend = backend;
        _coverage = coverage;
        _logger = logger ?? NullLogger<AssessRunner>.Instance;
    }

    /// <summary>Records skipped by the last call, with the reason</summary>
    public List<(Guid Id, string Reason)> Failures { get; } = new();

    /// <summary>
    /// One page of the Owner's change feed (at most <paramref name="limit"/> records),
    /// assessed, with the cursor moved past it in the same save. Records that could not
    /// be assessed are named in the cursor's LastError and passed over.
    /// </summary>
    /// <exception cref="DerivationPendingException">The Owner's fusion is not settled</exception>
    /// <exception cref="BackendBusyException">Interactive reads are in flight</exception>
    public async Task<AssessBatchResult> RunFeedBatchAsync(string owner, int limit, bool interactive, CancellationToken ct)
    {
        Failures.Clear();
        var stored = await _db.Cursors.AsNoTracking().FirstOrDefaultAsync(c => c.OwnerId == owner, ct);
        var page = await _backend.ChangesAsync(owner, stored?.Cursor, limit, interactive, ct);
        var (assessed, skipped) = await AssessChangesAsync(owner, page.Items, interactive, ct);
        var now = DateTime.UtcNow;
        // The first orphan sweep is a week after the backfill starts
        var cursor = stored ?? new OwnerCursor { OwnerId = owner, SweptAt = now };
        cursor.Cursor = page.Next;
        cursor.UpdatedAt = now;
        cursor.LastRunAt = now;
        cursor.LastError = Failures.Count == 0
            ? null
            : Truncate($"skipped {Failures.Count} record(s) that could not be assessed; first {Failures[0].Id}: {Failures[0].Reason}");
        if (stored == null)
        {
            _db.Cursors.Add(cursor);
        }
        else
        {
            _db.Cursors.Update(cursor);
        }

        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return new AssessBatchResult(page.Items.Count, assessed, skipped, page.More, Failures.Count);
    }

    /// <summary>Assesses records named by id (a job); the tracks say what each record is now</summary>
    public async Task<AssessBatchResult> AssessIdsAsync(string owner, IReadOnlyList<Guid> ids, bool interactive, CancellationToken ct)
    {
        Failures.Clear();
        var tracks = await TracksAsync(owner, ids.Distinct().ToList(), interactive, ct);
        var stored = await StoredAsync(owner, tracks.Select(t => t.Id).ToList(), ct);
        var written = await WriteAsync(owner, tracks, stored, ct);
        return new AssessBatchResult(ids.Count, written.Count, 0, false, Failures.Count);
    }

    /// <summary>
    /// Assesses tracks already fetched (stage B fetches once and reuses them), writing
    /// route_quality; returns the assessment of each record stored (a record that could
    /// not be assessed is left out)
    /// </summary>
    public async Task<Dictionary<Guid, RouteAssessment>> AssessTracksAsync(string owner, List<RouteTrack> tracks, CancellationToken ct)
    {
        Failures.Clear();
        var stored = await StoredAsync(owner, tracks.Select(t => t.Id).ToList(), ct);
        return await WriteAsync(owner, tracks, stored, ct);
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
        var written = await WriteAsync(owner, tracks.Concat(trackless).ToList(), stored, ct, keys);
        return (written.Count, items.Count - todo.Count);
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

    /// <summary>
    /// Assesses and stores the tracks; returns the assessments of the records stored. A
    /// record whose assessment throws is skipped; when the batch's save fails, the batch
    /// is saved again one record at a time and only the record that fails is skipped.
    /// </summary>
    private async Task<Dictionary<Guid, RouteAssessment>> WriteAsync(string owner, List<RouteTrack> tracks, Dictionary<Guid, RouteQualityRow> stored,
        CancellationToken ct, IReadOnlyDictionary<Guid, string>? keys = null)
    {
        var result = new Dictionary<Guid, RouteAssessment>();
        if (tracks.Count == 0)
        {
            return result;
        }

        var ids = tracks.Select(t => t.Id).ToList();
        // A match whose points did not change only gets the record's new values (AD-34 rule 3)
        var matched = await _db.MatchedRoutes.Where(m => m.OwnerId == owner && ids.Contains(m.ActivityId)).ToDictionaryAsync(m => m.ActivityId, ct);
        var now = DateTime.UtcNow;
        var assessed = new List<(RouteTrack Track, RouteAssessment Assessment, string Hash)>(tracks.Count);
        foreach (var t in tracks)
        {
            RouteAssessment a;
            string hash;
            try
            {
                a = RouteQuality.Assess(t.Type, t.SourceType, t.Points, t.Before, t.After, _coverage.Covers);
                hash = RouteQuality.InputHash(t.Points);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Skip(owner, t.Id, ex);
                continue;
            }

            assessed.Add((t, a, hash));
            Apply(owner, t, a, hash, stored, matched.GetValueOrDefault(t.Id), keys, now);
            result[t.Id] = a;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
            return result;
        }
        catch (DbUpdateException ex) when (assessed.Count > 1)
        {
            _logger.LogWarning(ex, "Routes: a stage A batch of {Count} records did not save; saving them one at a time", assessed.Count);
        }
        catch (DbUpdateException ex)
        {
            _db.ChangeTracker.Clear();
            Skip(owner, assessed[0].Track.Id, ex);
            return new Dictionary<Guid, RouteAssessment>();
        }

        // One record at a time, each with its own rows and its own save (the failure path only)
        _db.ChangeTracker.Clear();
        result.Clear();
        foreach (var (t, a, hash) in assessed)
        {
            try
            {
                var one = await _db.Quality.Where(q => q.OwnerId == owner && q.ActivityId == t.Id).ToDictionaryAsync(q => q.ActivityId, ct);
                var m = await _db.MatchedRoutes.FirstOrDefaultAsync(x => x.OwnerId == owner && x.ActivityId == t.Id, ct);
                Apply(owner, t, a, hash, one, m, keys, now);
                await _db.SaveChangesAsync(ct);
                result[t.Id] = a;
            }
            catch (DbUpdateException ex)
            {
                Skip(owner, t.Id, ex);
            }
            finally
            {
                _db.ChangeTracker.Clear();
            }
        }

        return result;
    }

    /// <summary>Fills the record's route_quality row (adding it when new) and refreshes its current match</summary>
    private void Apply(string owner, RouteTrack t, RouteAssessment a, string hash, Dictionary<Guid, RouteQualityRow> stored, MatchedRouteRow? m,
        IReadOnlyDictionary<Guid, string>? keys, DateTime now)
    {
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

        if (m != null && m.InputHash == hash && m.FusedType == t.Type)
        {
            m.SourceUpdatedAt = t.UpdatedAt;
            m.SourcePointCount = t.PointCount;
        }
    }

    private void Skip(string owner, Guid id, Exception ex)
    {
        var reason = (ex.InnerException ?? ex).Message;
        Failures.Add((id, reason));
        _logger.LogWarning(ex, "Routes: record {ActivityId} of {Owner} could not be assessed; skipped", id, owner);
    }

    private static string Truncate(string s) => s.Length > 500 ? s[..500] : s;
}
