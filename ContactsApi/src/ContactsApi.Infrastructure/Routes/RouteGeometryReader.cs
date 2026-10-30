/**
 * @fileoverview IRouteGeometryReader over the routes schema (AD-34 rule 3): one
 * set-based query per read, by record ids, checked with the stored UpdatedAt, point
 * count and fused type; only the stored line the read needs, no points read and
 * nothing hashed.
 * @module ContactsApi.Infrastructure.Routes
 */
using ContactsApi.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ContactsApi.Infrastructure.Routes;

/// <summary>
/// Whether the backend reads matched routes (the backend's routes.enabled), and whether
/// reads are suspended for now (a singleton, shared by every request's reader)
/// </summary>
public class RouteGeometryOptions
{
    private readonly object _lock = new();
    private DateTime _suspendedUntil = DateTime.MinValue;

    /// <summary>On when the route-quality service is configured</summary>
    public bool Enabled { get; set; }

    /// <summary>How long reads stay off after the routes schema or its grants were found missing</summary>
    public TimeSpan SuspendFor { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Reads are suspended at <paramref name="now"/></summary>
    public bool Suspended(DateTime now)
    {
        lock (_lock)
        {
            return now < _suspendedUntil;
        }
    }

    /// <summary>Suspends reads for <see cref="SuspendFor"/>; returns false when they already were (log once)</summary>
    public bool Suspend(DateTime now)
    {
        lock (_lock)
        {
            var already = now < _suspendedUntil;
            _suspendedUntil = now + SuspendFor;
            return !already;
        }
    }
}

/// <summary>The Routes module's reader of matched geometry for Atlas</summary>
public class RouteGeometryReader : IRouteGeometryReader
{
    private readonly RoutesDbContext _db;
    private readonly RouteGeometryOptions _options;
    private readonly ILogger<RouteGeometryReader> _logger;

    /// <summary>Creates the reader</summary>
    public RouteGeometryReader(RoutesDbContext db, RouteGeometryOptions options, ILogger<RouteGeometryReader> logger)
    {
        _db = db;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>Off for a few minutes after the routes schema or its grants were found missing</remarks>
    public bool Enabled => _options.Enabled && !_options.Suspended(DateTime.UtcNow);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, ChosenRoute>> ChooseAsync(string owner, IReadOnlyCollection<RouteRecord> records,
        RouteLine line, CancellationToken ct = default)
    {
        var chosen = new Dictionary<Guid, ChosenRoute>();
        if (!Enabled || records.Count == 0)
        {
            return chosen;
        }

        var byId = new Dictionary<Guid, RouteRecord>();
        foreach (var r in records)
        {
            byId[r.Id] = r;
        }

        try
        {
            var rows = await _db.Database.SqlQueryRaw<ChoiceRow>(ChoiceSql(line),
                    new NpgsqlParameter("owner", owner), new NpgsqlParameter("ids", byId.Keys.ToArray()))
                .ToListAsync(ct);
            if (rows.Count == 0)
            {
                return chosen;
            }

            var settings = RouteSettingsDoc.Parse(rows[0].Settings);
            foreach (var m in rows)
            {
                var record = byId[m.ActivityId];
                var row = new MatchedRouteRow
                {
                    OwnerId = owner, ActivityId = m.ActivityId, SourceUpdatedAt = m.SourceUpdatedAt, SourcePointCount = m.SourcePointCount,
                    FusedType = m.FusedType, Status = m.Status, Confidence = m.Confidence, Polyline = m.Line, Pieces = m.Pieces ?? "[]"
                };
                if (!RouteChoice.Drawable(row, owner, record.UpdatedAt, record.PointCount, record.FusedType, m.Decision, settings))
                {
                    continue;
                }

                var pieces = line == RouteLine.Detail
                    ? RouteChoice.Pieces(row).Select(p => new RouteGeometryPiece(p.FromM, p.ToM, p.Kind)).ToList()
                    : null;
                var roads = line == RouteLine.Detail ? RouteRoads.Parse(m.Roads) : null;
                // A row stored before its simplified lines existed (until the service fills them):
                // simplified here, so a read never sends more than its budget
                var polyline = m.Coarse ? Coarsen(m.Line!, line) : m.Line!;
                chosen[m.ActivityId] = new ChosenRoute(m.ActivityId, polyline, m.MatchedDistanceM, m.Confidence, pieces, roads);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missing schema (the service never ran), a revoked grant or a lost
            // connection: draw what was recorded, as without route quality
            Unavailable(ex, "Matched routes unavailable; drawing the recorded lines");
            chosen.Clear();
        }

        return chosen;
    }

    /// <summary>
    /// A read failed. A missing schema, table or column, or a missing grant, will not
    /// heal by itself: reads are suspended for a few minutes (every map, list and panel
    /// draws the recorded lines meanwhile) and it is logged once. Anything else (a lost
    /// connection) is logged for that read only.
    /// </summary>
    private void Unavailable(Exception ex, string message)
    {
        if (!IsSetupMissing(ex))
        {
            _logger.LogWarning(ex, message);
            return;
        }

        if (_options.Suspend(DateTime.UtcNow))
        {
            _logger.LogWarning(ex, "The routes schema or the backend's grants on it are missing ({SqlState}); matched routes are off for {Minutes} min. "
                + "See helm/contacts/charts/routes/README.md, step 1", (ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState,
                _options.SuspendFor.TotalMinutes);
        }
    }

    /// <summary>undefined_table, invalid_schema_name, undefined_column, insufficient_privilege</summary>
    internal static bool IsSetupMissing(Exception ex) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState is "42P01" or "3F000" or "42703" or "42501";

    /// <summary>
    /// The read-time choice as one query: the rows by (OwnerId, ActivityId), the decision
    /// by (OwnerId, SourceKey) and the Owner's settings as a scalar subquery (evaluated
    /// once). Only the stored line the read needs is selected (COALESCE is lazy, so the
    /// full line is read only for a row stored before its simplified lines), and the
    /// pieces only for the detail view.
    /// </summary>
    internal static string ChoiceSql(RouteLine line)
    {
        var (lineSql, coarseSql, piecesSql, roadsSql) = line switch
        {
            RouteLine.Detail => (@"m.""Polyline""", "false", @"m.""Pieces""::text", @"m.""Roads""::text"),
            RouteLine.List => (@"COALESCE(m.""ListPolyline"", m.""Polyline"")", @"m.""ListPolyline"" IS NULL", "NULL::text", "NULL::text"),
            _ => (@"COALESCE(m.""DensityPolyline"", m.""ListPolyline"", m.""Polyline"")", @"m.""DensityPolyline"" IS NULL", "NULL::text", "NULL::text")
        };
        return $@"
            SELECT m.""ActivityId"", m.""SourceUpdatedAt"", m.""SourcePointCount"", m.""FusedType"", m.""Status"", m.""Confidence"",
                   {lineSql} AS ""Line"", {coarseSql} AS ""Coarse"", {piecesSql} AS ""Pieces"", {roadsSql} AS ""Roads"", m.""MatchedDistanceM"",
                   d.""Kind"" AS ""Decision"",
                   (SELECT s.""Json""::text FROM routes.settings s WHERE s.""OwnerId"" = @owner) AS ""Settings""
              FROM routes.matched_routes m
              LEFT JOIN routes.route_decisions d ON d.""OwnerId"" = @owner AND d.""SourceKey"" = m.""SourceKey""
             WHERE m.""OwnerId"" = @owner AND m.""ActivityId"" = ANY(@ids)
               AND m.""Polyline"" IS NOT NULL AND m.""Status"" <> '{RouteStatuses.Failed}'";
    }

    private static string Coarsen(string polyline, RouteLine line)
    {
        var points = Atlas.AtlasService.DecodePolyline(polyline);
        return line == RouteLine.Density
            ? RouteLines.Density(points)
            : Atlas.Geo.EncodePolyline(Atlas.Geo.Simplify(points, RouteLines.ListVertices));
    }

    /// <summary>One row of the read-time choice</summary>
    internal sealed class ChoiceRow
    {
        public Guid ActivityId { get; set; }
        public DateTime SourceUpdatedAt { get; set; }
        public int SourcePointCount { get; set; }
        public string FusedType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public double? Confidence { get; set; }
        public string? Line { get; set; }
        public bool Coarse { get; set; }
        public string? Pieces { get; set; }
        public string? Roads { get; set; }
        public double? MatchedDistanceM { get; set; }
        public string? Decision { get; set; }
        public string? Settings { get; set; }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RouteRecord>> OnRoadAsync(string owner, string road, CancellationToken ct = default)
    {
        var name = RouteRoads.Normalize(road);
        if (!Enabled || name == null)
        {
            return Array.Empty<RouteRecord>();
        }

        try
        {
            var rows = await _db.Database.SqlQueryRaw<RoadRow>(OnRoadSql(RouteRoads.IsRef(name)),
                    new NpgsqlParameter("owner", owner), new NpgsqlParameter("road", RouteRoads.IsRef(name) ? name : $"%{EscapeLike(name)}%"))
                .ToListAsync(ct);
            if (rows.Count == 0)
            {
                return Array.Empty<RouteRecord>();
            }

            var settings = RouteSettingsDoc.Parse(rows[0].Settings);
            var result = new List<RouteRecord>(rows.Count);
            foreach (var m in rows)
            {
                // As the read-time choice, except "shown by default": the roads of a right match
                // count whether or not the Owner draws matched lines (the caller checks the record)
                var row = new MatchedRouteRow
                {
                    OwnerId = owner, ActivityId = m.ActivityId, SourceUpdatedAt = m.SourceUpdatedAt, SourcePointCount = m.SourcePointCount,
                    FusedType = m.FusedType, Status = m.Status, Confidence = m.Confidence, Polyline = "-"
                };
                if (RouteChoice.Drawable(row, owner, m.SourceUpdatedAt, m.SourcePointCount, m.FusedType, m.Decision,
                        settings with { ShownByDefault = true }))
                {
                    result.Add(new RouteRecord(m.ActivityId, m.SourceUpdatedAt, m.SourcePointCount, m.FusedType));
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Unavailable(ex, "Matched routes unavailable; the road filter finds nothing");
            return Array.Empty<RouteRecord>();
        }
    }

    /// <summary>
    /// The Owner's matches on a road: a route number by equality, a street name by
    /// ILIKE over the row's names (the text's wildcards escaped with backslashes, ILIKE's
    /// default escape). Owner-scoped (the (OwnerId, ActivityId) index), so it
    /// reads one Owner's rows only.
    /// </summary>
    internal static string OnRoadSql(bool isRef)
    {
        var match = isRef
            ? @"m.""RoadNames"" @> ARRAY[@road]::text[]"
            : @"EXISTS (SELECT 1 FROM unnest(m.""RoadNames"") r WHERE r ILIKE @road)";
        return $@"
            SELECT m.""ActivityId"", m.""SourceUpdatedAt"", m.""SourcePointCount"", m.""FusedType"", m.""Status"", m.""Confidence"",
                   d.""Kind"" AS ""Decision"",
                   (SELECT s.""Json""::text FROM routes.settings s WHERE s.""OwnerId"" = @owner) AS ""Settings""
              FROM routes.matched_routes m
              LEFT JOIN routes.route_decisions d ON d.""OwnerId"" = @owner AND d.""SourceKey"" = m.""SourceKey""
             WHERE m.""OwnerId"" = @owner AND m.""RoadNames"" IS NOT NULL AND m.""Status"" <> '{RouteStatuses.Failed}'
               AND {match}";
    }

    private static string EscapeLike(string s) => s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    /// <summary>One row of the road filter</summary>
    internal sealed class RoadRow
    {
        public Guid ActivityId { get; set; }
        public DateTime SourceUpdatedAt { get; set; }
        public int SourcePointCount { get; set; }
        public string FusedType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public double? Confidence { get; set; }
        public string? Decision { get; set; }
        public string? Settings { get; set; }
    }

    /// <inheritdoc />
    public async Task<RouteDescription?> DescribeAsync(string owner, RouteRecord record, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return null;
        }

        try
        {
            var quality = await _db.Quality.AsNoTracking()
                .Where(q => q.OwnerId == owner && q.ActivityId == record.Id)
                .Select(q => new { q.Grade, q.Score, q.Flags, q.Matchable, q.NeedsImprovement, q.SourceKey })
                .FirstOrDefaultAsync(ct);
            var match = await (
                    from m in _db.MatchedRoutes.AsNoTracking()
                    where m.OwnerId == owner && m.ActivityId == record.Id
                    select new
                    {
                        m.Status, m.Confidence, m.MatchedDistanceM, m.Reason, m.SourceKey, m.Method,
                        Current = m.SourceUpdatedAt == record.UpdatedAt && m.SourcePointCount == record.PointCount && m.FusedType == record.FusedType
                    })
                .FirstOrDefaultAsync(ct);
            var key = match?.SourceKey ?? quality?.SourceKey;
            var decision = key == null
                ? null
                : await _db.Decisions.AsNoTracking().Where(d => d.OwnerId == owner && d.SourceKey == key).Select(d => d.Kind).FirstOrDefaultAsync(ct);
            var marks = key == null
                ? null
                : await _db.Waypoints.AsNoTracking().Where(w => w.OwnerId == owner && w.SourceKey == key).Select(w => w.Points).FirstOrDefaultAsync(ct);
            var waypoints = marks == null ? null : RouteMarks.Parse(marks).Select(p => new[] { p.Lat, p.Lon }).ToList();
            if (quality == null && match == null)
            {
                return new RouteDescription(null, null, Array.Empty<string>(), null, false, null, null, null, false, decision, null, null, waypoints);
            }

            var flags = (quality?.Flags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new RouteDescription(quality?.Grade, quality?.Score, flags, quality?.Matchable, quality?.NeedsImprovement ?? false,
                match?.Status, match?.Confidence, match?.MatchedDistanceM, match?.Current ?? false, decision, match?.Reason, match?.Method, waypoints);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Unavailable(ex, "Route quality unavailable for the panel");
            return null;
        }
    }
}
