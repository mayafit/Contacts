/**
 * @fileoverview Atlas data service: ingest, search, details, health, CRUD
 * (the REST API and the MCP tools both call this)
 * @module ContactsApi.Infrastructure.Atlas
 */
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ContactsApi.Infrastructure.Atlas;

/// <summary>Thrown when a row does not exist or belongs to someone else</summary>
public class AtlasNotFoundException : Exception
{
    /// <summary>Creates the exception</summary>
    public AtlasNotFoundException(string what) : base($"{what} not found")
    {
    }
}

/// <summary>
/// Everything the app and AI clients do with Atlas data. Every method takes the
/// owner (Google account id) and only ever touches that owner's rows.
/// </summary>
public partial class AtlasService
{private readonly AtlasDbContext _db;
    private readonly ILogger<AtlasService> _logger;
    private readonly IFusionSignal? _signal;

    /// <summary>Creates the service</summary>
    /// <param name="signal">The fusion service; without it (tests, tools) changes are fused right away</param>
    public AtlasService(AtlasDbContext db, ILogger<AtlasService> logger, IFusionSignal? signal = null)
    {
        _db = db;
        _logger = logger;
        _signal = signal;
    }

    // ---------------------------------------------------------------- ingest

    /// <summary>
    /// Adds or updates one batch of imported items. Items are matched by
    /// (source, external id), so importing the same file again updates them.
    /// </summary>
    /// <param name="owner">Google account id</param>
    /// <param name="req">Batch</param>
    /// <param name="ct">Cancellation</param>
    /// <returns>Counts and the import id (pass it with later batches of the same import)</returns>
    public async Task<IngestResult> IngestAsync(string owner, IngestRequest req, CancellationToken ct = default)
    {
        // One ingest per owner at a time (no duplicate-key races between parallel
        // batches), all of it in one transaction (a failure leaves nothing half-done)
        var gate = Gates.GetOrAdd(owner, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var result = await IngestCoreAsync(owner, req, ct);
            await tx.CommitAsync(ct);

            // Hand the window this batch touched (activities, visits and health all
            // can join or enhance activities) to the fusion service. A failure here
            // never fails the import; the service's periodic check catches up.
            var times = req.Activities.Where(a => a.StartTime.HasValue).Select(a => Utc(a.StartTime!.Value))
                .Concat(req.Activities.Where(a => a.EndTime.HasValue).Select(a => Utc(a.EndTime!.Value)))
                .Concat(req.Activities.Where(a => a.Points is { Count: > 0 }).SelectMany(a => new[] { Utc(a.Points![0].T), Utc(a.Points[^1].T) }))
                .Concat(req.Visits.SelectMany(v => new[] { Utc(v.StartTime), Utc(v.EndTime) }))
                .Concat(req.Health.Select(h => Utc(h.StartTime)))
                .ToList();
            if (times.Count > 0)
            {
                try
                {
                    if (_signal == null)
                    {
                        await FuseCoreAsync(owner, times.Min(), times.Max(), ct);
                    }
                    else
                    {
                        _db.ChangeTracker.Clear();
                        _db.FusionQueue.Add(new FusionQueueItem { OwnerId = owner, From = times.Min(), To = times.Max() });
                        await _db.SaveChangesAsync(ct);
                        _db.ChangeTracker.Clear();
                        _signal.Notify();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Queueing fusion after ingest failed");
                    _db.ChangeTracker.Clear();
                }
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    private static readonly HashSet<string> GenericPlaceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Place", "Home", "Work", "Saved place", "Searched place"
    };

    private async Task<IngestResult> IngestCoreAsync(string owner, IngestRequest req, CancellationToken ct)
    {
        var source = Trim(string.IsNullOrWhiteSpace(req.Source) ? "manual" : req.Source.Trim().ToLowerInvariant(), 64)!;
        var run = req.ImportId == null
            ? null
            : await _db.Imports.FirstOrDefaultAsync(i => i.Id == req.ImportId && i.OwnerId == owner, ct);
        if (run == null)
        {
            run = new ImportRun { OwnerId = owner, Source = source, FileName = req.FileName };
            _db.Imports.Add(run);
        }

        // A visit that names its place without an id: the place is made (or found)
        // by name and position, like any other place
        if (req.Visits.Any(v => v.PlaceExternalId == null && !string.IsNullOrWhiteSpace(v.PlaceName)))
        {
            req.Visits = req.Visits
                .Select(v => v.PlaceExternalId == null && !string.IsNullOrWhiteSpace(v.PlaceName)
                    ? v with { PlaceExternalId = PlaceKey(v.PlaceName, v.Lat, v.Lon) }
                    : v)
                .ToList();
            req.Places.AddRange(req.Visits
                .Where(v => v.PlaceName != null && v.PlaceExternalId == PlaceKey(v.PlaceName, v.Lat, v.Lon))
                .Select(v => new PlaceInput(v.PlaceExternalId, v.PlaceName!, v.Lat, v.Lon))
                .Where(p => req.Places.All(x => x.ExternalId != p.ExternalId))
                .DistinctBy(p => p.ExternalId));
        }

        // Places first: visits refer to them
        var placeIds = new Dictionary<string, Guid>();
        var placeKeys = req.Places.Select(p => p.ExternalId ?? PlaceKey(p.Name, p.Lat, p.Lon)).Distinct().ToList();
        var existingPlaces = await _db.Places
            .Where(p => p.OwnerId == owner && p.Source == source && placeKeys.Contains(p.ExternalId!))
            .ToDictionaryAsync(p => p.ExternalId!, ct);
        foreach (var p in req.Places)
        {
            var key = p.ExternalId ?? PlaceKey(p.Name, p.Lat, p.Lon);
            if (!existingPlaces.TryGetValue(key, out var place))
            {
                place = new AtlasPlace { OwnerId = owner, Source = source, ExternalId = key, ImportId = run.Id };
                _db.Places.Add(place);
                existingPlaces[key] = place;
                run.Places++;
            }

            // A real name (from Takeout, or typed by the user) is not replaced by a
            // generic one ("Place", "Home") from another export
            var incoming = Trim(p.Name, 300);
            if (incoming != null && (string.IsNullOrEmpty(place.Name) || (GenericPlaceNames.Contains(place.Name) && !GenericPlaceNames.Contains(incoming))))
            {
                place.Name = incoming;
            }
            place.Lat = p.Lat;
            place.Lon = p.Lon;
            place.Address = Trim(p.Address, 500) ?? place.Address;
            place.Category = Trim(p.Category, 100) ?? place.Category;
            place.GooglePlaceId = p.GooglePlaceId ?? place.GooglePlaceId;
            place.UpdatedAt = DateTime.UtcNow;
            placeIds[key] = place.Id;
        }

        // Visits
        var visitKeys = req.Visits.Select(VisitKey).ToList();
        var existingVisits = await _db.Visits
            .Where(v => v.OwnerId == owner && v.Source == source && visitKeys.Contains(v.ExternalId!))
            .ToDictionaryAsync(v => v.ExternalId!, ct);
        var missingPlaceKeys = req.Visits.Select(v => v.PlaceExternalId).Where(k => k != null && !placeIds.ContainsKey(k)).Distinct().ToList();
        foreach (var p in await _db.Places
                     .Where(p => p.OwnerId == owner && p.Source == source && missingPlaceKeys.Contains(p.ExternalId!))
                     .Select(p => new { p.ExternalId, p.Id })
                     .ToListAsync(ct))
        {
            placeIds[p.ExternalId!] = p.Id;
        }

        foreach (var v in req.Visits)
        {
            var key = VisitKey(v);
            if (!existingVisits.TryGetValue(key, out var visit))
            {
                visit = new PlaceVisit { OwnerId = owner, Source = source, ExternalId = key, ImportId = run.Id };
                _db.Visits.Add(visit);
                existingVisits[key] = visit;
                run.Visits++;
            }

            visit.StartTime = Utc(v.StartTime);
            visit.EndTime = Utc(v.EndTime);
            visit.Lat = v.Lat;
            visit.Lon = v.Lon;
            visit.Confidence = v.Confidence;
            if (v.PlaceExternalId != null && placeIds.TryGetValue(v.PlaceExternalId, out var placeId))
            {
                visit.PlaceId = placeId;
            }
        }

        // Activities (points replaced on update)
        var activityKeys = req.Activities.Select(ActivityKey).ToList();
        var existingActivities = await _db.Activities
            .Where(a => a.OwnerId == owner && a.Source == source && activityKeys.Contains(a.ExternalId!))
            .ToDictionaryAsync(a => a.ExternalId!, ct);
        foreach (var a in req.Activities)
        {
            var key = ActivityKey(a);
            if (existingActivities.TryGetValue(key, out var activity))
            {
                await _db.TrackPoints.Where(p => p.ActivityId == activity.Id).ExecuteDeleteAsync(ct);
                // Points this context still tracks are gone from the database now:
                // forget them, or saving would try to delete them a second time
                foreach (var entry in _db.ChangeTracker.Entries<TrackPoint>().Where(e => e.Entity.ActivityId == activity.Id).ToList())
                {
                    entry.State = EntityState.Detached;
                }

                activity.Points = new List<TrackPoint>();
            }
            else
            {
                activity = new AtlasActivity { OwnerId = owner, Source = source, ExternalId = key, ImportId = run.Id };
                _db.Activities.Add(activity);
                existingActivities[key] = activity;
                run.Activities++;
            }

            // On re-import the track and numbers are refreshed; name, type and notes
            // stay as they are (the user may have changed them)
            if (_db.Entry(activity).State == EntityState.Added)
            {
                activity.Type = NormalizeType(a.Type);
                activity.Name = Trim(a.Name, 300);
            }
            else
            {
                activity.Name ??= Trim(a.Name, 300);
            }

            activity.Notes ??= Trim(a.Notes, 4000);
            activity.StartTime = a.StartTime.HasValue ? Utc(a.StartTime.Value) : default;
            activity.EndTime = a.EndTime.HasValue ? Utc(a.EndTime.Value) : default;
            activity.DistanceM = a.DistanceM;
            activity.DurationS = a.DurationS;
            activity.ElevationGainM = a.ElevationGainM;
            activity.AvgHeartRate = null;
            activity.MaxHeartRate = null;
            activity.UpdatedAt = DateTime.UtcNow;
            foreach (var p in a.Points ?? new List<PointInput>())
            {
                if (p.Lat is < -90 or > 90 || p.Lon is < -180 or > 180)
                {
                    continue;
                }

                activity.Points.Add(new TrackPoint
                {
                    ActivityId = activity.Id,
                    Time = Utc(p.T),
                    Lat = p.Lat,
                    Lon = p.Lon,
                    Elevation = p.Ele,
                    HeartRate = p.Hr is > 0 and < 260 ? p.Hr : null,
                    Speed = p.Speed,
                    Cadence = p.Cad
                });
            }

            Geo.Summarize(activity);
            // Heart rate the source summarised, when the track has none
            activity.AvgHeartRate ??= a.AvgHeartRate;
            activity.MaxHeartRate ??= a.MaxHeartRate;
            if (activity.StartTime == default)
            {
                // No time at all: it cannot be placed on the timeline
                _db.Activities.Remove(activity);
                continue;
            }

            if (activity.EndTime == default || activity.EndTime < activity.StartTime)
            {
                activity.EndTime = activity.StartTime.AddSeconds(activity.DurationS ?? 0);
            }
        }

        run.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Health samples: inserted in bulk, existing ones (same type, time and
        // source) skipped by the unique index
        var health = 0;
        var samples = req.Health
            .Where(h => !string.IsNullOrWhiteSpace(h.Type) && double.IsFinite(h.Value))
            .Select(h => (Type: Trim(h.Type.Trim().ToLowerInvariant(), 48)!, Start: Utc(h.StartTime),
                End: h.EndTime.HasValue ? Utc(h.EndTime.Value) : (DateTime?)null, h.Value, Unit: Trim(h.Unit, 32)))
            .DistinctBy(h => (h.Type, h.Start))
            .ToList();
        foreach (var chunk in samples.Chunk(1000))
        {
            var sql = new System.Text.StringBuilder(
                "INSERT INTO atlas.health_samples (\"OwnerId\",\"Type\",\"StartTime\",\"EndTime\",\"Value\",\"Unit\",\"Source\",\"ImportId\") VALUES ");
            // Typed parameters: a null end time or unit still needs its column type
            var args = new List<Npgsql.NpgsqlParameter>();
            for (var i = 0; i < chunk.Length; i++)
            {
                var b = args.Count;
                sql.Append(i == 0 ? "" : ",").Append($"(@p{b},@p{b + 1},@p{b + 2},@p{b + 3},@p{b + 4},@p{b + 5},@p{b + 6},@p{b + 7})");
                args.Add(new($"p{b}", NpgsqlTypes.NpgsqlDbType.Text) { Value = owner });
                args.Add(new($"p{b + 1}", NpgsqlTypes.NpgsqlDbType.Text) { Value = chunk[i].Type });
                args.Add(new($"p{b + 2}", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = chunk[i].Start });
                args.Add(new($"p{b + 3}", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)chunk[i].End ?? DBNull.Value });
                args.Add(new($"p{b + 4}", NpgsqlTypes.NpgsqlDbType.Double) { Value = chunk[i].Value });
                args.Add(new($"p{b + 5}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)chunk[i].Unit ?? DBNull.Value });
                args.Add(new($"p{b + 6}", NpgsqlTypes.NpgsqlDbType.Text) { Value = source });
                args.Add(new($"p{b + 7}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = run.Id });
            }

            sql.Append(" ON CONFLICT (\"OwnerId\",\"Type\",\"StartTime\",\"Source\") DO NOTHING");
            health += await _db.Database.ExecuteSqlRawAsync(sql.ToString(), args, ct);
        }

        if (health > 0)
        {
            run.HealthSamples += health;
            await _db.SaveChangesAsync(ct);
        }

        return new IngestResult(run.Id, req.Activities.Count, req.Places.Count, req.Visits.Count, health);
    }

    // ---------------------------------------------------------------- queries

    /// <summary>Activities and visits matching time, area, type and text together</summary>
    public async Task<SearchResult> SearchAsync(string owner, AtlasQuery q, CancellationToken ct = default)
    {
        var limit = Math.Clamp(q.Limit, 1, 2000);
        var activities = new List<ActivitySummary>();
        var visits = new List<VisitSummary>();
        int totalActivities = 0, totalVisits = 0;

        if (q.Activities)
        {
            var aq = FilterActivities(owner, q);
            if (q.From.HasValue)
            {
                var from = Utc(q.From.Value);
                aq = aq.Where(a => a.EndTime >= from);
            }

            if (q.To.HasValue)
            {
                var to = Utc(q.To.Value);
                aq = aq.Where(a => a.StartTime <= to);
            }

            totalActivities = await aq.CountAsync(ct);
            activities = await aq.OrderByDescending(a => a.StartTime).Take(limit).Select(ToSummary).ToListAsync(ct);
        }

        if (q.Visits && !q.ConflictsOnly)
        {
            var vq = FilterVisits(owner, q);
            if (q.From.HasValue)
            {
                var from = Utc(q.From.Value);
                vq = vq.Where(v => v.EndTime >= from);
            }

            if (q.To.HasValue)
            {
                var to = Utc(q.To.Value);
                vq = vq.Where(v => v.StartTime <= to);
            }

            totalVisits = await vq.CountAsync(ct);
            visits = await vq.OrderByDescending(v => v.StartTime).Take(limit)
                .Select(v => new VisitSummary(v.Id, v.PlaceId, v.Place != null ? v.Place.Name : null, v.Place != null ? v.Place.Address : null,
                    v.StartTime, v.EndTime, v.Lat, v.Lon, v.Source))
                .ToListAsync(ct);
        }

        return new SearchResult(activities, visits, totalActivities, totalVisits);
    }

    /// <summary>The owner's activities in the query's area, types and text (not its time)</summary>
    private IQueryable<AtlasActivity> FilterActivities(string owner, AtlasQuery q)
    {
        // One row per real-world activity: records that stand alone, and the primary of each fused one
        var aq = _db.Activities.AsNoTracking().Where(a => a.OwnerId == owner && (a.ClusterId == null || a.ClusterId == a.Id));
        if (q.ConflictsOnly)
        {
            aq = aq.Where(a => a.Fusion != null && a.Fusion.ConflictCount > 0);
        }

        if (HasBox(q))
        {
            aq = aq.Where(a => a.MaxLat >= q.MinLat && a.MinLat <= q.MaxLat && a.MaxLon >= q.MinLon && a.MinLon <= q.MaxLon);
        }

        if (q.Types is { Count: > 0 })
        {
            var types = q.Types.Select(NormalizeType).ToList();
            aq = aq.Where(a => types.Contains(a.Fusion != null && a.Fusion.Type != null ? a.Fusion.Type : a.Type));
        }

        var text = q.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            var pattern = $"%{EscapeLike(text)}%";
            aq = aq.Where(a => EF.Functions.ILike(a.Name ?? "", pattern) || EF.Functions.ILike(a.Notes ?? "", pattern)
                               || (a.Fusion != null && EF.Functions.ILike(a.Fusion.Name ?? "", pattern)));
        }

        return aq;
    }

    /// <summary>The owner's visits in the query's area and text (not its time)</summary>
    private IQueryable<PlaceVisit> FilterVisits(string owner, AtlasQuery q)
    {
        var vq = _db.Visits.AsNoTracking().Where(v => v.OwnerId == owner);
        if (HasBox(q))
        {
            vq = vq.Where(v => v.Lat >= q.MinLat && v.Lat <= q.MaxLat && v.Lon >= q.MinLon && v.Lon <= q.MaxLon);
        }

        var text = q.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            var pattern = $"%{EscapeLike(text)}%";
            vq = vq.Where(v => v.Place != null && (EF.Functions.ILike(v.Place.Name, pattern) || EF.Functions.ILike(v.Place.Address ?? "", pattern)));
        }

        return vq;
    }

    /// <summary>
    /// An activity with its track (evenly thinned to at most <paramref name="maxPoints"/>)
    /// and its heart rate: from the track, or else from health samples in the same minutes.
    /// </summary>
    public async Task<ActivityDetail> GetActivityAsync(string owner, Guid id, int maxPoints = 2000, CancellationToken ct = default)
    {
        var a = await _db.Activities.AsNoTracking().Include(x => x.Fusion).FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == owner, ct)
                ?? throw new AtlasNotFoundException("Activity");
        if (a.ClusterId.HasValue && a.ClusterId != a.Id)
        {
            // A member's id: show the fused activity it belongs to
            a = await _db.Activities.AsNoTracking().Include(x => x.Fusion).FirstOrDefaultAsync(x => x.Id == a.ClusterId && x.OwnerId == owner, ct) ?? a;
            id = a.Id;
        }

        var members = a.ClusterId.HasValue
            ? await _db.Activities.AsNoTracking().Where(x => x.OwnerId == owner && x.ClusterId == a.ClusterId).ToListAsync(ct)
            : new List<AtlasActivity> { a };
        var count = a.PointCount;
        var step = Math.Max(1, (int)Math.Ceiling(count / (double)Math.Clamp(maxPoints, 10, 20000)));
        var points = await _db.TrackPoints.AsNoTracking()
            .Where(p => p.ActivityId == id)
            .OrderBy(p => p.Time)
            .Select(p => new PointOutput(p.Time, p.Lat, p.Lon, p.Elevation, p.HeartRate, p.Speed))
            .ToListAsync(ct);
        if (step > 1)
        {
            points = points.Where((_, i) => i % step == 0 || i == points.Count - 1).ToList();
        }

        var fieldSources = a.Fusion != null
            ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(a.Fusion.FieldSources) ?? new()
            : new Dictionary<string, string> { ["track"] = a.Source };
        var conflicts = a.Fusion != null
            ? System.Text.Json.JsonSerializer.Deserialize<List<FusionConflict>>(a.Fusion.Conflicts, JsonCamel) ?? new()
            : new List<FusionConflict>();

        // Heart rate: the primary's track, else another member's track, else health samples
        string? hrSource = null;
        var hr = new List<SeriesPoint>();
        if (points.Any(p => p.Hr.HasValue))
        {
            hrSource = "track";
            hr = points.Where(p => p.Hr.HasValue).Select(p => new SeriesPoint(p.T, p.Hr!.Value)).ToList();
            fieldSources["heartRate"] = a.Source;
        }
        else
        {
            var other = members.Where(m => m.Id != a.Id && m.AvgHeartRate.HasValue && m.PointCount > 0)
                .OrderBy(m => SourceRank(m.Source)).FirstOrDefault();
            if (other != null)
            {
                hr = await _db.TrackPoints.AsNoTracking()
                    .Where(p => p.ActivityId == other.Id && p.HeartRate != null)
                    .OrderBy(p => p.Time)
                    .Select(p => new SeriesPoint(p.Time, p.HeartRate!.Value, null, null))
                    .ToListAsync(ct);
                if (hr.Count > 0)
                {
                    hrSource = "track";
                    fieldSources["heartRate"] = other.Source;
                }
            }

            if (hr.Count == 0)
            {
                var samples = await HealthAsync(owner, "heart_rate", a.Fusion?.StartTime ?? a.StartTime, a.Fusion?.EndTime ?? a.EndTime, 600, ct);
                if (samples.Count > 0)
                {
                    hrSource = "health";
                    hr = samples;
                    var healthSource = await _db.HealthSamples.AsNoTracking()
                        .Where(s => s.OwnerId == owner && s.Type == "heart_rate" && s.StartTime >= a.StartTime && s.StartTime <= a.EndTime)
                        .Select(s => s.Source).FirstOrDefaultAsync(ct);
                    fieldSources["heartRate"] = healthSource ?? "health";
                }
            }
        }

        var memberRows = members
            .OrderByDescending(m => m.Id == a.Id).ThenBy(m => SourceRank(m.Source))
            .Select(m => new ActivityMember(m.Id, m.Source, m.Type, m.Name, m.StartTime, m.EndTime, m.DistanceM, m.DurationS,
                m.AvgHeartRate, m.MaxHeartRate, m.PointCount, m.Id == a.Id, m.ImportId))
            .ToList();
        var places = await LinkedPlacesAsync(owner, a, points, ct);
        var evidence = a.Fusion != null
            ? System.Text.Json.JsonSerializer.Deserialize<List<MatchEvidence>>(a.Fusion.Evidence, JsonCamel) ?? new()
            : new List<MatchEvidence>();
        return new ActivityDetail(Summarize(a), a.Notes, points, hrSource, hr, memberRows, conflicts, fieldSources, places,
            evidence, a.Fusion?.MatchConfidence);
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonCamel = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// Visits around an activity: where it started, ended, or stopped on the way
    /// (within ~500 m of the track and 30 minutes of its times)
    /// </summary>
    private async Task<List<LinkedPlace>> LinkedPlacesAsync(string owner, AtlasActivity a, List<PointOutput> points, CancellationToken ct)
    {
        if (!a.MinLat.HasValue || !a.MaxLat.HasValue || !a.MinLon.HasValue || !a.MaxLon.HasValue)
        {
            return new List<LinkedPlace>();
        }

        const double pad = 0.005;
        var start = (a.Fusion?.StartTime ?? a.StartTime).AddMinutes(-30);
        var end = (a.Fusion?.EndTime ?? a.EndTime).AddMinutes(30);
        var visits = await _db.Visits.AsNoTracking().Include(v => v.Place)
            .Where(v => v.OwnerId == owner && v.EndTime >= start && v.StartTime <= end
                        && v.Lat >= a.MinLat - pad && v.Lat <= a.MaxLat + pad && v.Lon >= a.MinLon - pad && v.Lon <= a.MaxLon + pad)
            .OrderBy(v => v.StartTime)
            .Take(50)
            .ToListAsync(ct);
        var first = points.FirstOrDefault();
        var last = points.LastOrDefault();
        return visits
            .Where(v => points.Count == 0 || points.Any(p => Geo.Haversine(p.Lat, p.Lon, v.Lat, v.Lon) <= 500))
            .Select(v =>
            {
                var role = first != null && v.EndTime <= a.StartTime.AddMinutes(15) && Geo.Haversine(first.Lat, first.Lon, v.Lat, v.Lon) <= 500
                    ? "start"
                    : last != null && v.StartTime >= a.EndTime.AddMinutes(-15) && Geo.Haversine(last.Lat, last.Lon, v.Lat, v.Lon) <= 500
                        ? "end"
                        : "during";
                return new LinkedPlace(v.Id, v.Place?.Name, v.Place?.Address, role, v.StartTime, v.EndTime, v.Lat, v.Lon);
            })
            .ToList();
    }

    /// <summary>
    /// Health values of a type in a time range; averaged into time buckets (with
    /// min and max) when there are more than <paramref name="maxPoints"/>.
    /// </summary>
    public async Task<List<SeriesPoint>> HealthAsync(string owner, string type, DateTime from, DateTime to, int maxPoints = 1500, CancellationToken ct = default)
    {
        maxPoints = Math.Clamp(maxPoints, 10, 5000);from = Utc(from);
        to = Utc(to);
        type = type.Trim().ToLowerInvariant();
        var q = _db.HealthSamples.AsNoTracking().Where(s => s.OwnerId == owner && s.Type == type && s.StartTime >= from && s.StartTime <= to);
        var count = await q.CountAsync(ct);
        if (count <= maxPoints)
        {
            return await q.OrderBy(s => s.StartTime).Select(s => new SeriesPoint(s.StartTime, s.Value, null, null)).ToListAsync(ct);
        }

        var bucket = Math.Max(60, (int)Math.Ceiling((to - from).TotalSeconds / Math.Max(1, maxPoints)));
        return await _db.Database.SqlQuery<SeriesPoint>($@"
            SELECT date_bin(make_interval(secs => {bucket}), ""StartTime"", {from}) AS ""T"",
                   avg(""Value"") AS ""V"", min(""Value"") AS ""Min"", max(""Value"") AS ""Max""
            FROM atlas.health_samples
            WHERE ""OwnerId"" = {owner} AND ""Type"" = {type} AND ""StartTime"" >= {from} AND ""StartTime"" <= {to}
            GROUP BY 1 ORDER BY 1").ToListAsync(ct);
    }

    /// <summary>
    /// Counts per time bucket for the timeline: activities by type, visits, and
    /// health samples. Bucket is hour, day, week, month or year.
    /// </summary>
    /// <param name="timeZone">IANA zone for day/week/month boundaries (default UTC)</param>
    /// <param name="filter">Area, types and text, as in the search, so the timeline counts what the map shows</param>
    public async Task<List<HistogramRow>> HistogramAsync(string owner, DateTime from, DateTime to, string bucket, CancellationToken ct = default,
        string? timeZone = null, AtlasQuery? filter = null)
    {
        bucket = bucket is "hour" or "day" or "week" or "month" or "year" ? bucket : "day";
        var tz = timeZone != null && System.Text.RegularExpressions.Regex.IsMatch(timeZone, @"^[A-Za-z]+(/[A-Za-z0-9_+\-]+){0,2}$") ? timeZone : "UTC";
        var zone = FindZone(tz);
        from = Utc(from);
        to = Utc(to);
        filter ??= new AtlasQuery();

        // Activities and visits: few enough to bucket here, with every search filter
        var rows = new List<HistogramRow>();
        if (filter.Activities)
        {
            var starts = await FilterActivities(owner, filter)
                .Where(a => a.StartTime >= from && a.StartTime <= to)
                .Select(a => new { a.StartTime, Type = a.Fusion != null && a.Fusion.Type != null ? a.Fusion.Type : a.Type })
                .ToListAsync(ct);
            rows.AddRange(starts.GroupBy(a => (Bucket: BucketStart(a.StartTime, bucket, zone), a.Type))
                .Select(g => new HistogramRow(g.Key.Bucket, g.Key.Type, g.Count())));
        }

        if (filter.Visits && !filter.ConflictsOnly && filter.Types is not { Count: > 0 })
        {
            var starts = await FilterVisits(owner, filter)
                .Where(v => v.StartTime >= from && v.StartTime <= to)
                .Select(v => v.StartTime)
                .ToListAsync(ct);
            rows.AddRange(starts.GroupBy(t => BucketStart(t, bucket, zone))
                .Select(g => new HistogramRow(g.Key, "visit", g.Count())));
        }

        // Health: dense (a value every few seconds) and without a place, so counted
        // in the database and not filtered by area
        rows.AddRange(await _db.Database.SqlQuery<HistogramRow>($@"
            SELECT date_trunc({bucket}, ""StartTime"" AT TIME ZONE {tz}) AT TIME ZONE {tz} AS ""Bucket"", 'health' AS ""Kind"", count(*)::int AS ""Count""
              FROM atlas.health_samples WHERE ""OwnerId"" = {owner} AND ""StartTime"" BETWEEN {from} AND {to} GROUP BY 1").ToListAsync(ct));
        return rows.OrderBy(r => r.Bucket).ToList();
    }

    private static TimeZoneInfo FindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// Start of the hour/day/week (Monday)/month/year containing a moment, in a
    /// time zone, as UTC (like PostgreSQL's date_trunc ... AT TIME ZONE)
    /// </summary>
    internal static DateTime BucketStart(DateTime utc, string bucket, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        var start = bucket switch
        {
            "hour" => new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0),
            "week" => local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7)),
            "month" => new DateTime(local.Year, local.Month, 1),
            "year" => new DateTime(local.Year, 1, 1),
            _ => local.Date
        };
        start = DateTime.SpecifyKind(start, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(start))
        {
            // Midnight skipped by a clock change: the first valid moment after it
            start = start.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(start, zone);
    }

    /// <summary>
    /// What happened at a point: visits within the radius, and activities whose
    /// track passed within it.
    /// </summary>
    public async Task<NearResult> NearAsync(string owner, double lat, double lon, double radiusM, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        radiusM = Math.Clamp(radiusM, 10, 200_000);
        var dLat = radiusM / 111_320.0;
        var dLon = radiusM / (111_320.0 * Math.Max(0.01, Math.Cos(lat * Math.PI / 180)));
        var q = new AtlasQuery
        {
            From = from, To = to, MinLat = lat - dLat, MaxLat = lat + dLat, MinLon = lon - dLon, MaxLon = lon + dLon, Limit = 2000
        };
        var found = await SearchAsync(owner, q, ct);
        var visits = found.Visits.Where(v => Geo.Haversine(lat, lon, v.Lat, v.Lon) <= radiusM).ToList();

        var candidateIds = found.Activities.Select(a => a.Id).ToList();
        var passing = await _db.TrackPoints.AsNoTracking()
            .Where(p => candidateIds.Contains(p.ActivityId) && p.Lat >= q.MinLat && p.Lat <= q.MaxLat && p.Lon >= q.MinLon && p.Lon <= q.MaxLon)
            .Select(p => new { p.ActivityId, p.Lat, p.Lon })
            .ToListAsync(ct);
        var ids = passing.Where(p => Geo.Haversine(lat, lon, p.Lat, p.Lon) <= radiusM).Select(p => p.ActivityId).ToHashSet();
        return new NearResult(visits, found.Activities.Where(a => ids.Contains(a.Id)).ToList());
    }

    /// <summary>Totals, date range, per-type counts and the imports</summary>
    public async Task<AtlasSummary> SummaryAsync(string owner, CancellationToken ct = default)
    {
        var acts = _db.Activities.AsNoTracking().Where(a => a.OwnerId == owner && (a.ClusterId == null || a.ClusterId == a.Id));
        var byType = await acts.GroupBy(a => a.Fusion != null && a.Fusion.Type != null ? a.Fusion.Type : a.Type)
            .Select(g => new { g.Key, Count = g.Count(), Distance = g.Sum(a => a.DistanceM ?? 0) })
            .ToListAsync(ct);
        var firstA = await acts.MinAsync(a => (DateTime?)a.StartTime, ct);
        var lastA = await acts.MaxAsync(a => (DateTime?)a.EndTime, ct);
        var visits = _db.Visits.AsNoTracking().Where(v => v.OwnerId == owner);
        var firstV = await visits.MinAsync(v => (DateTime?)v.StartTime, ct);
        var lastV = await visits.MaxAsync(v => (DateTime?)v.EndTime, ct);
        var first = new[] { firstA, firstV }.Where(d => d.HasValue).Min();
        var last = new[] { lastA, lastV }.Where(d => d.HasValue).Max();
        return new AtlasSummary(
            byType.Sum(t => t.Count),
            await _db.Places.CountAsync(p => p.OwnerId == owner, ct),
            await visits.CountAsync(ct),
            await _db.HealthSamples.LongCountAsync(s => s.OwnerId == owner, ct),
            first, last,
            byType.ToDictionary(t => t.Key, t => new TypeTotal(t.Count, Math.Round(t.Distance / 1000, 1))),
            await _db.Imports.AsNoTracking().Where(i => i.OwnerId == owner).OrderByDescending(i => i.CreatedAt).Take(50).ToListAsync(ct));
    }

    // ---------------------------------------------------------------- CRUD

    /// <summary>Changes an activity's name, type or notes</summary>
    public async Task<ActivitySummary> UpdateActivityAsync(string owner, Guid id, ActivityPatch patch, CancellationToken ct = default)
    {
        var a = await _db.Activities.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == owner, ct) ?? throw new AtlasNotFoundException("Activity");
        if (patch.Name != null)
        {
            a.Name = Trim(patch.Name, 300);
        }

        if (patch.Type != null)
        {
            a.Type = NormalizeType(patch.Type);
            a.TypeByUser = true;
        }

        if (patch.Notes != null)
        {
            a.Notes = Trim(patch.Notes, 4000);
        }

        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        if (a.ClusterId.HasValue)
        {
            // The fused activity takes the new type / name
            var gate = Gates.GetOrAdd(owner, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                await FuseCoreAsync(owner, a.StartTime, a.EndTime, ct);
            }
            finally
            {
                gate.Release();
            }
        }

        var cluster = a.ClusterId ?? a.Id;
        return await _db.Activities.AsNoTracking().Where(x => x.Id == cluster && x.OwnerId == owner).Select(ToSummary).FirstAsync(ct);
    }

    /// <summary>Deletes an activity and its track; a fused activity with all its source records</summary>
    public async Task DeleteActivityAsync(string owner, Guid id, CancellationToken ct = default)
    {
        var cluster = await _db.Activities.Where(x => x.Id == id && x.OwnerId == owner).Select(x => x.ClusterId).FirstOrDefaultAsync(ct);
        var removed = cluster.HasValue
            ? await _db.Activities.Where(x => x.OwnerId == owner && (x.ClusterId == cluster || x.Id == id)).ExecuteDeleteAsync(ct)
            : await _db.Activities.Where(x => x.Id == id && x.OwnerId == owner).ExecuteDeleteAsync(ct);
        if (removed == 0)
        {
            throw new AtlasNotFoundException("Activity");
        }
    }

    /// <summary>Creates a place by hand</summary>
    public async Task<AtlasPlace> CreatePlaceAsync(string owner, PlaceInput input, CancellationToken ct = default)
    {
        var place = new AtlasPlace
        {
            OwnerId = owner, Source = "manual", Name = Trim(input.Name, 300) ?? "Place", Lat = input.Lat, Lon = input.Lon,
            Address = Trim(input.Address, 500), Category = Trim(input.Category, 100)
        };
        _db.Places.Add(place);
        await _db.SaveChangesAsync(ct);
        return place;
    }

    /// <summary>Changes a place</summary>
    public async Task<AtlasPlace> UpdatePlaceAsync(string owner, Guid id, PlacePatch patch, CancellationToken ct = default)
    {
        var p = await _db.Places.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == owner, ct) ?? throw new AtlasNotFoundException("Place");
        p.Name = Trim(patch.Name, 300) ?? p.Name;
        p.Address = patch.Address != null ? Trim(patch.Address, 500) : p.Address;
        p.Category = patch.Category != null ? Trim(patch.Category, 100) : p.Category;
        p.Notes = patch.Notes != null ? Trim(patch.Notes, 4000) : p.Notes;
        p.Lat = patch.Lat ?? p.Lat;
        p.Lon = patch.Lon ?? p.Lon;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return p;
    }

    /// <summary>Deletes a place (its visits stay, without the place)</summary>
    public async Task DeletePlaceAsync(string owner, Guid id, CancellationToken ct = default)
    {
        if (await _db.Places.Where(x => x.Id == id && x.OwnerId == owner).ExecuteDeleteAsync(ct) == 0)
        {
            throw new AtlasNotFoundException("Place");
        }
    }

    /// <summary>Deletes a visit</summary>
    public async Task DeleteVisitAsync(string owner, Guid id, CancellationToken ct = default)
    {
        if (await _db.Visits.Where(x => x.Id == id && x.OwnerId == owner).ExecuteDeleteAsync(ct) == 0)
        {
            throw new AtlasNotFoundException("Visit");
        }
    }

    /// <summary>Deletes an import and everything it brought in</summary>
    public async Task<int> DeleteImportAsync(string owner, Guid importId, CancellationToken ct = default)
    {
        if (!await _db.Imports.AnyAsync(i => i.Id == importId && i.OwnerId == owner, ct))
        {
            throw new AtlasNotFoundException("Import");
        }

        var removed = await _db.Visits.Where(x => x.OwnerId == owner && x.ImportId == importId).ExecuteDeleteAsync(ct);
        removed += await _db.Activities.Where(x => x.OwnerId == owner && x.ImportId == importId).ExecuteDeleteAsync(ct);
        removed += await _db.HealthSamples.Where(x => x.OwnerId == owner && x.ImportId == importId).ExecuteDeleteAsync(ct);
        removed += await _db.Places.Where(x => x.OwnerId == owner && x.ImportId == importId).ExecuteDeleteAsync(ct);
        await _db.Imports.Where(i => i.Id == importId && i.OwnerId == owner).ExecuteDeleteAsync(ct);
        // Records whose fused activity's primary was deleted stand alone until re-fused
        await _db.Activities.Where(x => x.OwnerId == owner && x.ClusterId != null && !_db.Activities.Any(p => p.Id == x.ClusterId))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ClusterId, (Guid?)null), ct);
        _logger.LogInformation("Deleted import with {Count} items", removed);
        await QueueFusionAsync(owner, null, null, CancellationToken.None);
        return removed;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// An activity as listed: the primary record, with what fusion added from the
    /// others (type by source priority, the wider times, values the primary lacks)
    /// </summary>
    private static readonly System.Linq.Expressions.Expression<Func<AtlasActivity, ActivitySummary>> ToSummary = a =>
        new ActivitySummary(a.Id,
            a.Fusion != null && a.Fusion.Type != null ? a.Fusion.Type : a.Type,
            a.Name ?? (a.Fusion != null ? a.Fusion.Name : null),
            a.Fusion != null ? a.Fusion.StartTime : a.StartTime,
            a.Fusion != null ? a.Fusion.EndTime : a.EndTime,
            a.DistanceM ?? (a.Fusion != null ? a.Fusion.DistanceM : null),
            a.DurationS,
            a.ElevationGainM ?? (a.Fusion != null ? a.Fusion.ElevationGainM : null),
            a.AvgHeartRate ?? (a.Fusion != null ? a.Fusion.AvgHeartRate : null),
            a.MaxHeartRate ?? (a.Fusion != null ? a.Fusion.MaxHeartRate : null),
            a.Source, a.Polyline, a.MinLat, a.MinLon, a.MaxLat, a.MaxLon, a.PointCount,
            SplitSources(a.Fusion != null ? a.Fusion.Sources : a.Source),
            a.Fusion != null ? a.Fusion.MemberCount : 1,
            a.Fusion != null ? a.Fusion.ConflictCount : 0);

    private static List<string> SplitSources(string sources) =>
        sources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static readonly Func<AtlasActivity, ActivitySummary> ToSummaryCompiled = ToSummary.Compile();

    private static ActivitySummary Summarize(AtlasActivity a) => ToSummaryCompiled(a);

    private static bool HasBox(AtlasQuery q) =>
        q.MinLat.HasValue && q.MaxLat.HasValue && q.MinLon.HasValue && q.MaxLon.HasValue;

    /// <summary>Known activity types; anything else is kept as given (lower case)</summary>
    public static string NormalizeType(string? type)
    {
        var t = (type ?? "other").Trim().ToLowerInvariant().Replace(' ', '_');
        return t switch
        {
            "cycling" or "biking" or "bike" or "mountain_biking" or "road_biking" or "e_biking" => "ride",
            "hiking" or "trekking" => "hike",
            "running" or "jogging" or "trail_running" => "run",
            "walking" or "on_foot" => "walk",
            "in_passenger_vehicle" or "in_vehicle" or "driving" or "car" or "motorcycling" => "drive",
            "flying" or "plane" => "flight",
            "in_bus" or "in_train" or "in_subway" or "in_tram" or "in_ferry" or "bus" or "train" => "transit",
            "skiing" or "snowboarding" => "ski",
            "swimming" => "swim",
            // Google Timeline's "could not tell"
            "" or "unknown" or "unknown_activity_type" or "unknown_activity" => "other",
            _ => t.Length > 32 ? t[..32] : t
        };
    }

    private static string ActivityKey(ActivityInput a) =>
        a.ExternalId ?? $"{a.StartTime:O}|{NormalizeType(a.Type)}";

    private static string VisitKey(VisitInput v) =>
        v.ExternalId ?? $"{Utc(v.StartTime):O}|{v.Lat:F5}|{v.Lon:F5}";

    private static string PlaceKey(string name, double lat, double lon) => $"{name.Trim().ToLowerInvariant()}|{lat:F4}|{lon:F4}";

    /// <summary>UTC, at PostgreSQL's microsecond precision (so stored and incoming times compare equal)</summary>
    private static DateTime Utc(DateTime d)
    {
        var utc = d.Kind switch
        {
            DateTimeKind.Utc => d,
            DateTimeKind.Local => d.ToUniversalTime(),
            _ => DateTime.SpecifyKind(d, DateTimeKind.Utc)
        };
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }

    private static string? Trim(string? s, int max)
    {
        var t = s?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
