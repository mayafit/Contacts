/**
 * @fileoverview Photos of a connected library: keeping their details, correcting
 * their times (see PhotoClock), placing each one at the activity or visit it was
 * taken during, and the queries the app makes
 * @module ContactsApi.Infrastructure.Atlas
 */
using Microsoft.EntityFrameworkCore;

namespace ContactsApi.Infrastructure.Atlas;

public partial class AtlasService
{
    /// <summary>A photo taken this long before or after an activity still belongs to it</summary>
    private static readonly TimeSpan PhotoSlack = TimeSpan.FromMinutes(2);

    /// <summary>A track point this close in time places a photo without a position</summary>
    private static readonly TimeSpan PhotoTrackGap = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Adds or updates photos from the library. A changed time or position makes
    /// the photo wait for placement again; the clock offset found before stays.
    /// </summary>
    /// <returns>How many were new or changed</returns>
    public async Task<int> UpsertPhotosAsync(string owner, string provider, IReadOnlyList<OneDrivePhoto> photos, CancellationToken ct = default)
    {
        if (photos.Count == 0)
        {
            return 0;
        }

        var ids = photos.Select(p => p.Id).Distinct().ToList();
        var existing = await _db.Photos.Where(p => p.OwnerId == owner && p.Provider == provider && ids.Contains(p.ExternalId))
            .ToDictionaryAsync(p => p.ExternalId, ct);
        var changed = 0;
        foreach (var p in photos.GroupBy(p => p.Id).Select(g => g.Last()))
        {
            if (!existing.TryGetValue(p.Id, out var row))
            {
                row = new AtlasPhoto { OwnerId = owner, Provider = provider, ExternalId = p.Id };
                _db.Photos.Add(row);
                existing[p.Id] = row;
            }
            else if (row.TakenAtRaw == p.TakenAt && row.Lat == p.Lat && row.Lon == p.Lon && row.Altitude == p.Altitude
                     && row.Name == p.Name && row.WebUrl == p.WebUrl && row.Folder == p.Folder && row.Width == p.Width
                     && row.Height == p.Height && row.Size == p.Size && row.MimeType == p.MimeType && row.IsVideo == p.IsVideo
                     && row.CameraMake == p.CameraMake && row.CameraModel == p.CameraModel)
            {
                continue;
            }

            if (row.TakenAtRaw != p.TakenAt || row.Lat != p.Lat || row.Lon != p.Lon)
            {
                row.Placed = false;
            }

            row.Name = p.Name;
            row.MimeType = p.MimeType;
            row.IsVideo = p.IsVideo;
            row.Size = p.Size;
            row.Width = p.Width;
            row.Height = p.Height;
            row.CameraMake = p.CameraMake;
            row.CameraModel = p.CameraModel;
            row.TakenAtRaw = p.TakenAt;
            row.TakenAt = p.TakenAt.AddMinutes(-row.ClockOffsetMin);
            row.Lat = p.Lat;
            row.Lon = p.Lon;
            row.Altitude = p.Altitude;
            row.WebUrl = p.WebUrl;
            row.Folder = p.Folder;
            row.UpdatedAt = DateTime.UtcNow;
            changed++;
        }

        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return changed;
    }

    /// <summary>Forgets photos the library no longer has (or that are no longer photos)</summary>
    public async Task<int> RemovePhotosAsync(string owner, string provider, IReadOnlyCollection<string> externalIds, CancellationToken ct = default) =>
        externalIds.Count == 0
            ? 0
            : await _db.Photos.Where(p => p.OwnerId == owner && p.Provider == provider && externalIds.Contains(p.ExternalId)).ExecuteDeleteAsync(ct);

    /// <summary>Forgets all of a library's photos (disconnecting it)</summary>
    public Task<int> DeleteAllPhotosAsync(string owner, string provider, CancellationToken ct = default) =>
        _db.Photos.Where(p => p.OwnerId == owner && p.Provider == provider).ExecuteDeleteAsync(ct);

    /// <summary>Listed activities and visits with how many photos were placed in each</summary>
    private async Task<(List<ActivitySummary>, List<VisitSummary>)> WithPhotoCountsAsync(string owner,
        List<ActivitySummary> activities, List<VisitSummary> visits, CancellationToken ct)
    {
        if (activities.Count == 0 && visits.Count == 0)
        {
            return (activities, visits);
        }

        var activityIds = activities.Select(a => a.Id).ToList();
        var visitIds = visits.Select(v => v.Id).ToList();
        var rows = await _db.Photos
            .Where(p => p.OwnerId == owner && ((p.ActivityId != null && activityIds.Contains(p.ActivityId.Value))
                                               || (p.VisitId != null && visitIds.Contains(p.VisitId.Value))))
            .GroupBy(p => new { p.ActivityId, p.VisitId })
            .Select(g => new { g.Key.ActivityId, g.Key.VisitId, N = g.Count() })
            .ToListAsync(ct);
        var perActivity = rows.Where(r => r.ActivityId != null).GroupBy(r => r.ActivityId!.Value).ToDictionary(g => g.Key, g => g.Sum(r => r.N));
        var perVisit = rows.Where(r => r.VisitId != null).GroupBy(r => r.VisitId!.Value).ToDictionary(g => g.Key, g => g.Sum(r => r.N));
        return (
            activities.Select(a => perActivity.TryGetValue(a.Id, out var n) ? a with { PhotoCount = n } : a).ToList(),
            visits.Select(v => perVisit.TryGetValue(v.Id, out var n) ? v with { PhotoCount = n } : v).ToList());
    }

    /// <summary>How many photos the owner has, and how many have a position</summary>
    public async Task<(int Total, int WithPosition)> PhotoCountsAsync(string owner, CancellationToken ct = default)
    {
        var q = _db.Photos.Where(p => p.OwnerId == owner);
        return (await q.CountAsync(ct), await q.CountAsync(p => p.MapLat != null, ct));
    }

    /// <summary>Photos taken in a time window wait for placement again (new activities or visits there)</summary>
    public Task<int> RequeuePhotosAsync(string owner, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var q = _db.Photos.Where(p => p.OwnerId == owner && p.Placed);
        if (from.HasValue)
        {
            var f = from.Value.AddHours(-1);
            q = q.Where(p => p.TakenAt >= f);
        }

        if (to.HasValue)
        {
            var t = to.Value.AddHours(1);
            q = q.Where(p => p.TakenAt <= t);
        }

        return q.ExecuteUpdateAsync(s => s.SetProperty(p => p.Placed, false), ct);
    }

    /// <summary>
    /// Decides whether the owner's photo times are UTC or the camera's clock (see
    /// <see cref="PhotoClock"/>) and corrects the times that change; those photos
    /// are placed again.
    /// </summary>
    public async Task<PhotoClock.Result> CalibratePhotoClocksAsync(string owner, CancellationToken ct = default)
    {
        var photos = await _db.Photos.AsNoTracking().Where(p => p.OwnerId == owner)
            .Select(p => new { p.Id, p.TakenAtRaw, p.Lat, p.Lon, p.IsVideo, p.ClockOffsetMin })
            .ToListAsync(ct);
        if (photos.Count == 0)
        {
            return new PhotoClock.Result(Array.Empty<int>(), 0, 0, false, false);
        }

        var history = await HistoryAsync(owner, ct);
        var result = PhotoClock.Decide(photos.Select(p => new PhotoClock.Sample(p.TakenAtRaw, p.Lat, p.Lon, p.IsVideo)).ToList(), history);
        var now = DateTime.UtcNow;
        var changes = photos.Select((p, i) => (p.Id, Old: p.ClockOffsetMin, New: result.Offsets[i]))
            .Where(c => c.Old != c.New).GroupBy(c => c.New);
        foreach (var g in changes)
        {
            var offset = g.Key;
            foreach (var chunk in g.Select(c => c.Id).Chunk(2000))
            {
                await _db.Photos.Where(p => p.OwnerId == owner && chunk.Contains(p.Id))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.ClockOffsetMin, offset)
                        .SetProperty(p => p.TakenAt, p => p.TakenAtRaw.AddMinutes(-offset))
                        .SetProperty(p => p.Placed, false)
                        .SetProperty(p => p.UpdatedAt, now), ct);
            }
        }

        return result;
    }

    /// <summary>Where the owner was over time: visits, and the areas of their activities</summary>
    private async Task<PhotoClock.History> HistoryAsync(string owner, CancellationToken ct)
    {
        var visits = await _db.Visits.AsNoTracking().Where(v => v.OwnerId == owner)
            .Select(v => new PhotoClock.Fix(v.StartTime, v.EndTime, v.Lat, v.Lon, v.Lat, v.Lon)).ToListAsync(ct);
        var acts = await _db.Activities.AsNoTracking()
            .Where(a => a.OwnerId == owner && (a.ClusterId == null || a.ClusterId == a.Id) && a.MinLat != null)
            .Select(a => new
            {
                Start = a.Fusion != null ? a.Fusion.StartTime : a.StartTime,
                End = a.Fusion != null ? a.Fusion.EndTime : a.EndTime,
                MinLat = a.Fusion != null && a.Fusion.MinLat != null ? a.Fusion.MinLat : a.MinLat,
                MinLon = a.Fusion != null && a.Fusion.MinLon != null ? a.Fusion.MinLon : a.MinLon,
                MaxLat = a.Fusion != null && a.Fusion.MaxLat != null ? a.Fusion.MaxLat : a.MaxLat,
                MaxLon = a.Fusion != null && a.Fusion.MaxLon != null ? a.Fusion.MaxLon : a.MaxLon
            }).ToListAsync(ct);
        visits.AddRange(acts.Select(a => new PhotoClock.Fix(a.Start, a.End, a.MinLat!.Value, a.MinLon!.Value, a.MaxLat!.Value, a.MaxLon!.Value)));
        return new PhotoClock.History(visits);
    }

    /// <summary>
    /// Places photos waiting for it (any owner): the activity and visit each was
    /// taken during, and where to show it — its own position, else the activity's
    /// track at that moment, else the visit's place. A photo changed meanwhile by a
    /// sync is left for the next run.
    /// </summary>
    /// <returns>How many were looked at (0: none waiting)</returns>
    public async Task<int> PlacePendingPhotosAsync(int limit, CancellationToken ct = default)
    {
        var batch = await _db.Photos.AsNoTracking().Where(p => !p.Placed)
            .OrderBy(p => p.OwnerId).ThenBy(p => p.TakenAt).Take(limit)
            .Select(p => new { p.Id, p.OwnerId, p.TakenAt, p.Lat, p.Lon, p.UpdatedAt })
            .ToListAsync(ct);
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        foreach (var group in batch.GroupBy(p => p.OwnerId))
        {
            var owner = group.Key;
            var from = group.Min(p => p.TakenAt) - PhotoSlack;
            var to = group.Max(p => p.TakenAt) + PhotoSlack;
            var acts = await _db.Activities.AsNoTracking()
                .Where(a => a.OwnerId == owner && (a.ClusterId == null || a.ClusterId == a.Id))
                .Select(a => new
                {
                    a.Id,
                    a.ClusterId,
                    Start = a.Fusion != null ? a.Fusion.StartTime : a.StartTime,
                    End = a.Fusion != null ? a.Fusion.EndTime : a.EndTime
                })
                .Where(a => a.Start <= to && a.End >= from)
                .ToListAsync(ct);
            var clusters = acts.Where(a => a.ClusterId.HasValue).Select(a => a.ClusterId!.Value).ToList();
            var members = (await _db.Activities.AsNoTracking()
                    .Where(a => a.OwnerId == owner && a.ClusterId != null && clusters.Contains(a.ClusterId.Value) && a.PointCount > 0)
                    .Select(a => new { a.Id, Cluster = a.ClusterId!.Value }).ToListAsync(ct))
                .GroupBy(m => m.Cluster).ToDictionary(g => g.Key, g => g.Select(m => m.Id).ToList());
            var visits = await _db.Visits.AsNoTracking()
                .Where(v => v.OwnerId == owner && v.StartTime <= to && v.EndTime >= from)
                .Select(v => new { v.Id, v.StartTime, v.EndTime, v.Lat, v.Lon }).ToListAsync(ct);

            foreach (var p in group)
            {
                var act = acts.Where(a => a.Start - PhotoSlack <= p.TakenAt && a.End + PhotoSlack >= p.TakenAt)
                    .OrderBy(a => a.End - a.Start).FirstOrDefault();
                var visit = visits.Where(v => v.StartTime <= p.TakenAt && v.EndTime >= p.TakenAt)
                    .OrderBy(v => v.EndTime - v.StartTime).FirstOrDefault();
                double? lat = p.Lat, lon = p.Lon;
                string? by = lat.HasValue ? "gps" : null;
                if (by == null && act != null)
                {
                    var tracks = members.TryGetValue(act.Id, out var m) ? m : new List<Guid> { act.Id };
                    var at = await TrackPositionAsync(tracks, p.TakenAt, ct);
                    if (at != null)
                    {
                        (lat, lon, by) = (at.Value.Lat, at.Value.Lon, "track");
                    }
                }

                if (by == null && visit != null)
                {
                    (lat, lon, by) = (visit.Lat, visit.Lon, "visit");
                }

                var actId = act?.Id;
                var visitId = visit?.Id;
                await _db.Photos.Where(x => x.Id == p.Id && x.UpdatedAt == p.UpdatedAt && !x.Placed)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.ActivityId, actId)
                        .SetProperty(x => x.VisitId, visitId)
                        .SetProperty(x => x.MapLat, lat)
                        .SetProperty(x => x.MapLon, lon)
                        .SetProperty(x => x.PlacedBy, by)
                        .SetProperty(x => x.Placed, true), ct);
            }
        }

        await tx.CommitAsync(ct);
        return batch.Count;
    }

    /// <summary>Where the tracks were at a time: between the points around it (null when none within 10 minutes)</summary>
    private async Task<(double Lat, double Lon)?> TrackPositionAsync(List<Guid> tracks, DateTime t, CancellationToken ct)
    {
        var lo = t - PhotoTrackGap;
        var hi = t + PhotoTrackGap;
        var before = await _db.TrackPoints.AsNoTracking().Where(x => tracks.Contains(x.ActivityId) && x.Time <= t && x.Time >= lo)
            .OrderByDescending(x => x.Time).Select(x => new { x.Time, x.Lat, x.Lon }).FirstOrDefaultAsync(ct);
        var after = await _db.TrackPoints.AsNoTracking().Where(x => tracks.Contains(x.ActivityId) && x.Time >= t && x.Time <= hi)
            .OrderBy(x => x.Time).Select(x => new { x.Time, x.Lat, x.Lon }).FirstOrDefaultAsync(ct);
        if (before == null || after == null)
        {
            var only = before ?? after;
            return only == null ? null : (only.Lat, only.Lon);
        }

        var span = (after.Time - before.Time).TotalSeconds;
        var f = span <= 0 ? 0 : (t - before.Time).TotalSeconds / span;
        return (before.Lat + (after.Lat - before.Lat) * f, before.Lon + (after.Lon - before.Lon) * f);
    }

    /// <summary>A photo of the owner (for its thumbnail)</summary>
    public async Task<AtlasPhoto> GetPhotoAsync(string owner, Guid id, CancellationToken ct = default) =>
        await _db.Photos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == owner, ct)
        ?? throw new AtlasNotFoundException("Photo");

    /// <summary>
    /// Photos taken during an activity (the fused one, also when a member's id is
    /// given), in time order. Those not placed yet are found by time.
    /// </summary>
    public async Task<List<PhotoSummary>> ActivityPhotosAsync(string owner, Guid id, CancellationToken ct = default)
    {
        var a = await _db.Activities.AsNoTracking().Include(x => x.Fusion).FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == owner, ct)
                ?? throw new AtlasNotFoundException("Activity");
        if (a.ClusterId.HasValue && a.ClusterId != a.Id)
        {
            a = await _db.Activities.AsNoTracking().Include(x => x.Fusion).FirstOrDefaultAsync(x => x.Id == a.ClusterId && x.OwnerId == owner, ct) ?? a;
        }

        var primary = a.Id;
        var from = (a.Fusion?.StartTime ?? a.StartTime) - PhotoSlack;
        var to = (a.Fusion?.EndTime ?? a.EndTime) + PhotoSlack;
        return await PhotoRows(_db.Photos.Where(p => p.OwnerId == owner
                && (p.ActivityId == primary || (!p.Placed && p.TakenAt >= from && p.TakenAt <= to)))
            .OrderBy(p => p.TakenAt).Take(500)).ToListAsync(ct);
    }

    /// <summary>Photos taken during one visit (one without a place shows these)</summary>
    public async Task<List<PhotoSummary>> VisitPhotosAsync(string owner, Guid visitId, CancellationToken ct = default)
    {
        if (!await _db.Visits.AnyAsync(v => v.Id == visitId && v.OwnerId == owner, ct))
        {
            throw new AtlasNotFoundException("Visit");
        }

        return await PhotoRows(_db.Photos.Where(p => p.OwnerId == owner && p.VisitId == visitId)
            .OrderBy(p => p.TakenAt).Take(500)).ToListAsync(ct);
    }

    /// <summary>Photos taken during visits to a place, newest first</summary>
    public async Task<List<PhotoSummary>> PlacePhotosAsync(string owner, Guid placeId, CancellationToken ct = default)
    {
        if (!await _db.Places.AnyAsync(p => p.Id == placeId && p.OwnerId == owner, ct))
        {
            throw new AtlasNotFoundException("Place");
        }

        var visits = _db.Visits.Where(v => v.OwnerId == owner && v.PlaceId == placeId).Select(v => v.Id);
        return await PhotoRows(_db.Photos.Where(p => p.OwnerId == owner && p.VisitId != null && visits.Contains(p.VisitId.Value))
            .OrderByDescending(p => p.TakenAt).Take(500)).ToListAsync(ct);
    }

    /// <summary>
    /// Photos for the map: placed ones in the query's time and area; with activity
    /// types, only those taken during such activities. Newest first, at most 3000.
    /// </summary>
    public async Task<PhotoMapData> MapPhotosAsync(string owner, AtlasQuery query, CancellationToken ct = default)
    {
        const int max = 3000;
        var q = _db.Photos.Where(p => p.OwnerId == owner && p.MapLat != null);
        if (query.From.HasValue)
        {
            q = q.Where(p => p.TakenAt >= query.From.Value);
        }

        if (query.To.HasValue)
        {
            q = q.Where(p => p.TakenAt <= query.To.Value);
        }

        if (query.MinLat.HasValue && query.MaxLat.HasValue && query.MinLon.HasValue && query.MaxLon.HasValue)
        {
            q = q.Where(p => p.MapLat >= query.MinLat && p.MapLat <= query.MaxLat && p.MapLon >= query.MinLon && p.MapLon <= query.MaxLon);
        }

        if (query.Types is { Count: > 0 })
        {
            var types = query.Types.Select(t => t.ToLowerInvariant()).ToList();
            var acts = _db.Activities.Where(a => a.OwnerId == owner && (a.ClusterId == null || a.ClusterId == a.Id)
                                                 && types.Contains(a.Fusion != null && a.Fusion.Type != null ? a.Fusion.Type : a.Type))
                .Select(a => a.Id);
            q = q.Where(p => p.ActivityId != null && acts.Contains(p.ActivityId.Value));
        }

        var rows = await PhotoRows(q.OrderByDescending(p => p.TakenAt).Take(max + 1)).ToListAsync(ct);
        var truncated = rows.Count > max;
        return new PhotoMapData(truncated ? rows.Take(max).ToList() : rows, truncated);
    }

    private static IQueryable<PhotoSummary> PhotoRows(IQueryable<AtlasPhoto> q) =>
        q.Select(p => new PhotoSummary(p.Id, p.Name, p.TakenAt, p.IsVideo, p.MapLat, p.MapLon, p.PlacedBy,
            p.Width, p.Height, p.WebUrl, p.ActivityId, p.VisitId,
            p.CameraModel != null ? (p.CameraMake != null && !p.CameraModel.StartsWith(p.CameraMake) ? p.CameraMake + " " + p.CameraModel : p.CameraModel) : p.CameraMake));
}
