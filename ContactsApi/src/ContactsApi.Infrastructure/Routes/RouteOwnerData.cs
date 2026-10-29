/**
 * @fileoverview The backend's writes to the routes schema (decisions only; AD-34
 * rule 1) and the Owner's routes export (AD-34 rule 8): decisions, settings, quality
 * metrics and matched routes as GeoJSON with their piece kinds.
 * @module ContactsApi.Infrastructure.Routes
 */
using System.Text.Json;
using ContactsApi.Infrastructure.Atlas;
using Microsoft.EntityFrameworkCore;

namespace ContactsApi.Infrastructure.Routes;

/// <summary>The Owner's route decisions and the routes export</summary>
public class RouteOwnerData
{
    /// <summary>Matched routes read per page while exporting</summary>
    public const int ExportPage = 500;

    private readonly RoutesDbContext _db;

    /// <summary>Creates the store</summary>
    public RouteOwnerData(RoutesDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Records "Keep the original" (reject) or "Use this route" (accept) for records, by their
    /// natural keys, so the decision survives a re-match, re-fusion and re-import (AD-4)
    /// </summary>
    public async Task SetDecisionAsync(string owner, IReadOnlyCollection<string> sourceKeys, string kind, CancellationToken ct = default)
    {
        if (kind != RouteDecisionKinds.Reject && kind != RouteDecisionKinds.Accept)
        {
            throw new ArgumentException("kind is reject or accept", nameof(kind));
        }

        var keys = sourceKeys.Distinct().ToList();
        var existing = await _db.Decisions.Where(d => d.OwnerId == owner && keys.Contains(d.SourceKey)).ToListAsync(ct);
        foreach (var key in keys)
        {
            var row = existing.FirstOrDefault(d => d.SourceKey == key);
            if (row == null)
            {
                _db.Decisions.Add(new RouteDecision { OwnerId = owner, SourceKey = key, Kind = kind });
            }
            else
            {
                row.Kind = kind;
                row.CreatedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Removes the records' decisions ("Try again"); returns how many were removed</summary>
    public Task<int> ClearDecisionAsync(string owner, IReadOnlyCollection<string> sourceKeys, CancellationToken ct = default)
    {
        var keys = sourceKeys.Distinct().ToList();
        return _db.Decisions.Where(d => d.OwnerId == owner && keys.Contains(d.SourceKey)).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Writes the Owner's routes data as one JSON document: decisions, settings, the
    /// quality of every record, and the matched routes as a GeoJSON FeatureCollection
    /// whose features carry their pieces. Matched routes are read and written a page at a
    /// time, so a large export never sits in memory.
    /// </summary>
    public async Task ExportAsync(string owner, Utf8JsonWriter w, CancellationToken ct = default)
    {
        w.WriteStartObject();
        w.WriteString("owner", owner);
        w.WriteString("exportedAt", DateTime.UtcNow);

        var settings = await _db.Settings.AsNoTracking().Where(s => s.OwnerId == owner).Select(s => s.Json).FirstOrDefaultAsync(ct);
        w.WritePropertyName("settings");
        if (settings == null)
        {
            w.WriteNullValue();
        }
        else
        {
            using var doc = JsonDocument.Parse(settings);
            doc.RootElement.WriteTo(w);
        }

        w.WriteStartArray("decisions");
        foreach (var d in await _db.Decisions.AsNoTracking().Where(d => d.OwnerId == owner).OrderBy(d => d.SourceKey).ToListAsync(ct))
        {
            w.WriteStartObject();
            w.WriteString("sourceKey", d.SourceKey);
            w.WriteString("kind", d.Kind);
            w.WriteString("createdAt", d.CreatedAt);
            w.WriteEndObject();
        }

        w.WriteEndArray();

        // The points the Owner marked (AD-38): decisions too
        w.WriteStartArray("waypoints");
        foreach (var m in await _db.Waypoints.AsNoTracking().Where(x => x.OwnerId == owner).OrderBy(x => x.SourceKey).ToListAsync(ct))
        {
            w.WriteStartObject();
            w.WriteString("sourceKey", m.SourceKey);
            w.WritePropertyName("points");
            using (var points = JsonDocument.Parse(string.IsNullOrWhiteSpace(m.Points) ? "[]" : m.Points))
            {
                points.RootElement.WriteTo(w);
            }

            w.WriteString("createdAt", m.CreatedAt);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        await w.FlushAsync(ct);

        w.WriteStartArray("quality");
        Guid? after = null;
        while (true)
        {
            var q = _db.Quality.AsNoTracking().Where(x => x.OwnerId == owner);
            if (after.HasValue)
            {
                q = q.Where(x => x.Id.CompareTo(after.Value) > 0);
            }

            var page = await q.OrderBy(x => x.Id).Take(ExportPage).ToListAsync(ct);
            foreach (var x in page)
            {
                w.WriteStartObject();
                w.WriteString("activityId", x.ActivityId);
                w.WriteString("sourceKey", x.SourceKey);
                w.WriteString("type", x.Type);
                w.WriteString("grade", x.Grade);
                if (x.Score.HasValue)
                {
                    w.WriteNumber("score", x.Score.Value);
                }

                w.WriteString("flags", x.Flags);
                w.WriteString("matchable", x.Matchable);
                w.WriteBoolean("needsImprovement", x.NeedsImprovement);
                w.WritePropertyName("metrics");
                using (var metrics = JsonDocument.Parse(string.IsNullOrWhiteSpace(x.Metrics) ? "{}" : x.Metrics))
                {
                    metrics.RootElement.WriteTo(w);
                }

                w.WriteString("ruleVersion", x.RuleVersion);
                w.WriteString("computedAt", x.ComputedAt);
                w.WriteEndObject();
            }

            await w.FlushAsync(ct);
            if (page.Count < ExportPage)
            {
                break;
            }

            after = page[^1].Id;
        }

        w.WriteEndArray();

        w.WritePropertyName("matchedRoutes");
        w.WriteStartObject();
        w.WriteString("type", "FeatureCollection");
        w.WriteStartArray("features");
        after = null;
        while (true)
        {
            var q = _db.MatchedRoutes.AsNoTracking().Where(x => x.OwnerId == owner);
            if (after.HasValue)
            {
                q = q.Where(x => x.Id.CompareTo(after.Value) > 0);
            }

            var page = await q.OrderBy(x => x.Id).Take(ExportPage).ToListAsync(ct);
            foreach (var m in page)
            {
                WriteFeature(w, m);
            }

            await w.FlushAsync(ct);
            if (page.Count < ExportPage)
            {
                break;
            }

            after = page[^1].Id;
        }

        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
        await w.FlushAsync(ct);
    }

    /// <summary>A matched route as a GeoJSON Feature (a LineString, or no geometry when it failed)</summary>
    private static void WriteFeature(Utf8JsonWriter w, MatchedRouteRow m)
    {
        w.WriteStartObject();
        w.WriteString("type", "Feature");
        w.WritePropertyName("geometry");
        if (string.IsNullOrEmpty(m.Polyline))
        {
            w.WriteNullValue();
        }
        else
        {
            w.WriteStartObject();
            w.WriteString("type", "LineString");
            w.WriteStartArray("coordinates");
            foreach (var (lat, lon) in AtlasService.DecodePolyline(m.Polyline))
            {
                // GeoJSON is longitude first
                w.WriteStartArray();
                w.WriteNumberValue(Math.Round(lon, 5));
                w.WriteNumberValue(Math.Round(lat, 5));
                w.WriteEndArray();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteStartObject("properties");
        w.WriteString("activityId", m.ActivityId);
        w.WriteString("sourceKey", m.SourceKey);
        w.WriteString("status", m.Status);
        if (m.Confidence.HasValue)
        {
            w.WriteNumber("confidence", m.Confidence.Value);
        }

        if (m.MatchedDistanceM.HasValue)
        {
            w.WriteNumber("matchedDistanceM", m.MatchedDistanceM.Value);
        }

        w.WriteString("fusedType", m.FusedType);
        w.WriteString("costing", m.Costing);
        w.WriteString("engine", m.Engine);
        w.WriteString("engineVersion", m.EngineVersion);
        w.WriteString("graphVersion", m.GraphVersion);
        w.WriteString("ruleVersion", m.RuleVersion);
        w.WriteString("method", m.Method);
        w.WriteString("reason", m.Reason);
        w.WriteString("computedAt", m.ComputedAt);
        // Piece kinds in metres along the line: observed, inferred, gap
        w.WritePropertyName("pieces");
        using (var pieces = JsonDocument.Parse(string.IsNullOrWhiteSpace(m.Pieces) ? "[]" : m.Pieces))
        {
            pieces.RootElement.WriteTo(w);
        }

        // The roads the line follows, in travel order (AD-37)
        w.WritePropertyName("roads");
        using (var roads = JsonDocument.Parse(string.IsNullOrWhiteSpace(m.Roads) ? "[]" : m.Roads))
        {
            roads.RootElement.WriteTo(w);
        }

        w.WriteEndObject();
        w.WriteEndObject();
    }
}
