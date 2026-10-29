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

/// <summary>Whether the backend reads matched routes (the backend's routes.enabled)</summary>
public class RouteGeometryOptions
{
    /// <summary>On when the route-quality service is configured</summary>
    public bool Enabled { get; set; }
}

/// <summary>The Routes module's reader of matched geometry for Atlas</summary>
public class RouteGeometryReader : IRouteGeometryReader
{
    private readonly RoutesDbContext _db;
    private readonly ILogger<RouteGeometryReader> _logger;

    /// <summary>Creates the reader</summary>
    public RouteGeometryReader(RoutesDbContext db, RouteGeometryOptions options, ILogger<RouteGeometryReader> logger)
    {
        _db = db;
        _logger = logger;
        Enabled = options.Enabled;
    }

    /// <inheritdoc />
    public bool Enabled { get; }

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
                // A row stored before its simplified lines existed (until the service fills them):
                // simplified here, so a read never sends more than its budget
                var polyline = m.Coarse ? Coarsen(m.Line!, line) : m.Line!;
                chosen[m.ActivityId] = new ChosenRoute(m.ActivityId, polyline, m.MatchedDistanceM, m.Confidence, pieces);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missing schema (the service never ran), a revoked grant or a lost
            // connection: draw what was recorded, as without route quality
            _logger.LogWarning(ex, "Matched routes unavailable; drawing the recorded lines");
            chosen.Clear();
        }

        return chosen;
    }

    /// <summary>
    /// The read-time choice as one query: the rows by (OwnerId, ActivityId), the decision
    /// by (OwnerId, SourceKey) and the Owner's settings as a scalar subquery (evaluated
    /// once). Only the stored line the read needs is selected (COALESCE is lazy, so the
    /// full line is read only for a row stored before its simplified lines), and the
    /// pieces only for the detail view.
    /// </summary>
    internal static string ChoiceSql(RouteLine line)
    {
        var (lineSql, coarseSql, piecesSql) = line switch
        {
            RouteLine.Detail => (@"m.""Polyline""", "false", @"m.""Pieces""::text"),
            RouteLine.List => (@"COALESCE(m.""ListPolyline"", m.""Polyline"")", @"m.""ListPolyline"" IS NULL", "NULL::text"),
            _ => (@"COALESCE(m.""DensityPolyline"", m.""ListPolyline"", m.""Polyline"")", @"m.""DensityPolyline"" IS NULL", "NULL::text")
        };
        return $@"
            SELECT m.""ActivityId"", m.""SourceUpdatedAt"", m.""SourcePointCount"", m.""FusedType"", m.""Status"", m.""Confidence"",
                   {lineSql} AS ""Line"", {coarseSql} AS ""Coarse"", {piecesSql} AS ""Pieces"", m.""MatchedDistanceM"",
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
        public double? MatchedDistanceM { get; set; }
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
                        m.Status, m.Confidence, m.MatchedDistanceM, m.Reason, m.SourceKey,
                        Current = m.SourceUpdatedAt == record.UpdatedAt && m.SourcePointCount == record.PointCount && m.FusedType == record.FusedType
                    })
                .FirstOrDefaultAsync(ct);
            var key = match?.SourceKey ?? quality?.SourceKey;
            var decision = key == null
                ? null
                : await _db.Decisions.AsNoTracking().Where(d => d.OwnerId == owner && d.SourceKey == key).Select(d => d.Kind).FirstOrDefaultAsync(ct);
            if (quality == null && match == null)
            {
                return new RouteDescription(null, null, Array.Empty<string>(), null, false, null, null, null, false, decision, null);
            }

            var flags = (quality?.Flags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new RouteDescription(quality?.Grade, quality?.Score, flags, quality?.Matchable, quality?.NeedsImprovement ?? false,
                match?.Status, match?.Confidence, match?.MatchedDistanceM, match?.Current ?? false, decision, match?.Reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Route quality unavailable for the panel");
            return null;
        }
    }
}
