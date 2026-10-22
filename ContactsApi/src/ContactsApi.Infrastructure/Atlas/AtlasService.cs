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
public class AtlasService
{
    private readonly AtlasDbContext _db;
    private readonly ILogger<AtlasService> _logger;

    /// <summary>Creates the service</summary>
    public AtlasService(AtlasDbContext db, ILogger<AtlasService> logger)
    {
        _db = db;
        _logger = logger;
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
        var text = q.Text?.Trim();

        if (q.Activities)
        {
            var aq = _db.Activities.AsNoTracking().Where(a => a.OwnerId == owner);
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

            if (HasBox(q))
            {
                aq = aq.Where(a => a.MaxLat >= q.MinLat && a.MinLat <= q.MaxLat && a.MaxLon >= q.MinLon && a.MinLon <= q.MaxLon);
            }

            if (q.Types is { Count: > 0 })
            {
                var types = q.Types.Select(NormalizeType).ToList();
                aq = aq.Where(a => types.Contains(a.Type));
            }

            if (!string.IsNullOrEmpty(text))
            {
                var pattern = $"%{EscapeLike(text)}%";
                aq = aq.Where(a => EF.Functions.ILike(a.Name ?? "", pattern) || EF.Functions.ILike(a.Notes ?? "", pattern));
            }

            totalActivities = await aq.CountAsync(ct);
            activities = await aq.OrderByDescending(a => a.StartTime).Take(limit).Select(ToSummary).ToListAsync(ct);
        }

        if (q.Visits)
        {
            var vq = _db.Visits.AsNoTracking().Where(v => v.OwnerId == owner);
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

            if (HasBox(q))
            {
                vq = vq.Where(v => v.Lat >= q.MinLat && v.Lat <= q.MaxLat && v.Lon >= q.MinLon && v.Lon <= q.MaxLon);
            }

            if (!string.IsNullOrEmpty(text))
            {
                var pattern = $"%{EscapeLike(text)}%";
                vq = vq.Where(v => v.Place != null && (EF.Functions.ILike(v.Place.Name, pattern) || EF.Functions.ILike(v.Place.Address ?? "", pattern)));
            }

            totalVisits = await vq.CountAsync(ct);
            visits = await vq.OrderByDescending(v => v.StartTime).Take(limit)
                .Select(v => new VisitSummary(v.Id, v.PlaceId, v.Place != null ? v.Place.Name : null, v.Place != null ? v.Place.Address : null,
                    v.StartTime, v.EndTime, v.Lat, v.Lon, v.Source))
                .ToListAsync(ct);
        }

        return new SearchResult(activities, visits, totalActivities, totalVisits);
    }

    /// <summary>
    /// An activity with its track (evenly thinned to at most <paramref name="maxPoints"/>)
    /// and its heart rate: from the track, or else from health samples in the same minutes.
    /// </summary>
    public async Task<ActivityDetail> GetActivityAsync(string owner, Guid id, int maxPoints = 2000, CancellationToken ct = default)
    {
        var a = await _db.Activities.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == owner, ct)
                ?? throw new AtlasNotFoundException("Activity");
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

        string? hrSource = null;
        var hr = new List<SeriesPoint>();
        if (points.Any(p => p.Hr.HasValue))
        {
            hrSource = "track";
            hr = points.Where(p => p.Hr.HasValue).Select(p => new SeriesPoint(p.T, p.Hr!.Value)).ToList();
        }
        else
        {
            var samples = await HealthAsync(owner, "heart_rate", a.StartTime, a.EndTime, 600, ct);
            if (samples.Count > 0)
            {
                hrSource = "health";
                hr = samples;
            }
        }

        return new ActivityDetail(Summarize(a), a.Notes, points, hrSource, hr);
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
    public async Task<List<HistogramRow>> HistogramAsync(string owner, DateTime from, DateTime to, string bucket, CancellationToken ct = default, string? timeZone = null)
    {
        bucket = bucket is "hour" or "day" or "week" or "month" or "year" ? bucket : "day";
        var tz = timeZone != null && System.Text.RegularExpressions.Regex.IsMatch(timeZone, @"^[A-Za-z]+(/[A-Za-z0-9_+\-]+){0,2}$") ? timeZone : "UTC";
        from = Utc(from);
        to = Utc(to);
        return await _db.Database.SqlQuery<HistogramRow>($@"
            SELECT date_trunc({bucket}, ""StartTime"" AT TIME ZONE {tz}) AT TIME ZONE {tz} AS ""Bucket"", ""Type"" AS ""Kind"", count(*)::int AS ""Count""
              FROM atlas.activities WHERE ""OwnerId"" = {owner} AND ""StartTime"" BETWEEN {from} AND {to} GROUP BY 1, 2
            UNION ALL
            SELECT date_trunc({bucket}, ""StartTime"" AT TIME ZONE {tz}) AT TIME ZONE {tz}, 'visit', count(*)::int
              FROM atlas.visits WHERE ""OwnerId"" = {owner} AND ""StartTime"" BETWEEN {from} AND {to} GROUP BY 1
            UNION ALL
            SELECT date_trunc({bucket}, ""StartTime"" AT TIME ZONE {tz}) AT TIME ZONE {tz}, 'health', count(*)::int
              FROM atlas.health_samples WHERE ""OwnerId"" = {owner} AND ""StartTime"" BETWEEN {from} AND {to} GROUP BY 1
            ORDER BY 1").ToListAsync(ct);
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
        var acts = _db.Activities.AsNoTracking().Where(a => a.OwnerId == owner);
        var byType = await acts.GroupBy(a => a.Type)
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
        }

        if (patch.Notes != null)
        {
            a.Notes = Trim(patch.Notes, 4000);
        }

        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Summarize(a);
    }

    /// <summary>Deletes an activity and its track</summary>
    public async Task DeleteActivityAsync(string owner, Guid id, CancellationToken ct = default)
    {
        if (await _db.Activities.Where(x => x.Id == id && x.OwnerId == owner).ExecuteDeleteAsync(ct) == 0)
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
        _logger.LogInformation("Deleted import with {Count} items", removed);
        return removed;
    }

    // ---------------------------------------------------------------- helpers

    private static readonly System.Linq.Expressions.Expression<Func<AtlasActivity, ActivitySummary>> ToSummary = a =>
        new ActivitySummary(a.Id, a.Type, a.Name, a.StartTime, a.EndTime, a.DistanceM, a.DurationS, a.ElevationGainM,
            a.AvgHeartRate, a.MaxHeartRate, a.Source, a.Polyline, a.MinLat, a.MinLon, a.MaxLat, a.MaxLon, a.PointCount);

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
            "" => "other",
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
