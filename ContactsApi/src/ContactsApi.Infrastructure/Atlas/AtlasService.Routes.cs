/**
 * @fileoverview The geometry Atlas draws (AD-34 rules 3, 5 and 6): the recorded line,
 * or a matched route chosen at read time through IRouteGeometryReader. Atlas never
 * references the Routes module; it only asks the reader.
 * @module ContactsApi.Infrastructure.Atlas
 */
using ContactsApi.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ContactsApi.Infrastructure.Atlas;

public partial class AtlasService
{
    /// <summary>How fusion marks a distance taken from the straight-line track length (FieldSources JSON)</summary>
    private const string DistanceFromTrackMark = "\"distance\":\"track\"";

    /// <summary>A line Atlas may draw matched: the record's check columns and its recorded line</summary>
    /// <param name="OutingEvidence">The fusion evidence of an outing of several recordings; null otherwise</param>
    private sealed record GeometryCandidate(Guid Id, string Type, string? Polyline, DateTime UpdatedAt, int PointCount, string? OutingEvidence);

    /// <summary>One recording of an outing</summary>
    private sealed record SegmentState(Guid Id, DateTime UpdatedAt, int PointCount, DateTime StartTime, string? Polyline);

    /// <summary>The chosen line of an activity (a record's, or an outing's recordings joined)</summary>
    /// <param name="Final">Already simplified for the line asked for: a density read draws it as it is</param>
    /// <param name="Roads">The roads it follows, in travel order (the detail view only)</param>
    private sealed record ChosenGeometry(string Polyline, double? DistanceM, List<RouteGeometryPiece>? Pieces, bool Final = true,
        List<RouteRoad>? Roads = null);

    /// <summary>
    /// Resolves the road filter (AD-37) to the activities it keeps: the records whose
    /// match follows the road and is still for the record as it is now (its UpdatedAt and
    /// point count), each as the activity Atlas lists (a fused record as its primary).
    /// Nothing when route quality is off.
    /// </summary>
    private async Task ResolveRoadAsync(string owner, AtlasQuery q, CancellationToken ct)
    {
        if (!q.HasRoad || q.RoadActivityIds != null)
        {
            return;
        }

        if (_routes is not { Enabled: true })
        {
            q.RoadActivityIds = Array.Empty<Guid>();
            return;
        }

        var hits = await _routes.OnRoadAsync(owner, q.Road!, ct);
        if (hits.Count == 0)
        {
            q.RoadActivityIds = Array.Empty<Guid>();
            return;
        }

        var byId = hits.ToDictionary(h => h.Id);
        var ids = byId.Keys.ToList();
        var records = await _db.Activities.AsNoTracking()
            .Where(a => a.OwnerId == owner && ids.Contains(a.Id))
            .Select(a => new { a.Id, a.UpdatedAt, a.PointCount, a.ClusterId })
            .ToListAsync(ct);
        q.RoadActivityIds = records
            .Where(r => byId[r.Id].UpdatedAt == r.UpdatedAt && byId[r.Id].PointCount == r.PointCount)
            .Select(r => r.ClusterId ?? r.Id)
            .ToHashSet();
    }

    /// <summary>The summaries with their chosen list lines (no pieces)</summary>
    private async Task<List<ActivitySummary>> WithRoutesAsync(string owner, List<ActivitySummary> list, CancellationToken ct)
    {
        if (_routes is not { Enabled: true } || list.Count == 0)
        {
            return list;
        }

        var candidates = list.Where(s => s.Polyline != null)
            .Select(s => new GeometryCandidate(s.Id, s.Type, s.Polyline, s.RecordUpdatedAt, s.PointCount, s.OutingEvidence))
            .ToList();
        var chosen = await ChooseGeometryAsync(owner, candidates, RouteLine.List, ct);
        return chosen.Count == 0 ? list : list.Select(s => Apply(s, chosen.GetValueOrDefault(s.Id), false)).ToList();
    }

    /// <summary>
    /// The chosen lines, by activity id. One routes query for the whole list; an outing of
    /// several recordings adds one Atlas query for its recordings' check columns.
    /// </summary>
    private async Task<Dictionary<Guid, ChosenGeometry>> ChooseGeometryAsync(string owner, IReadOnlyList<GeometryCandidate> items, RouteLine line,
        CancellationToken ct)
    {
        if (_routes is not { Enabled: true } || items.Count == 0)
        {
            return new Dictionary<Guid, ChosenGeometry>();
        }

        var outingIds = new Dictionary<Guid, List<Guid>>();
        foreach (var i in items.Where(i => i.OutingEvidence != null))
        {
            var ids = SegmentIds(i.Id, i.OutingEvidence!);
            if (ids.Count > 1)
            {
                outingIds[i.Id] = ids;
            }
        }

        var outings = new Dictionary<Guid, List<SegmentState>>();
        if (outingIds.Count > 0)
        {
            var all = outingIds.Values.SelectMany(x => x).Distinct().ToList();
            var states = await _db.Activities.AsNoTracking()
                .Where(x => x.OwnerId == owner && all.Contains(x.Id))
                .Select(x => new SegmentState(x.Id, x.UpdatedAt, x.PointCount, x.StartTime, x.Polyline))
                .ToDictionaryAsync(x => x.Id, ct);
            foreach (var (id, ids) in outingIds)
            {
                outings[id] = ids.Where(states.ContainsKey).Select(x => states[x]).ToList();
            }
        }

        return await ChooseCoreAsync(owner, items, outings, line, ct);
    }

    private async Task<Dictionary<Guid, ChosenGeometry>> ChooseCoreAsync(string owner, IReadOnlyList<GeometryCandidate> items,
        Dictionary<Guid, List<SegmentState>> outings, RouteLine line, CancellationToken ct)
    {
        var result = new Dictionary<Guid, ChosenGeometry>();
        var records = new List<RouteRecord>();
        foreach (var i in items)
        {
            if (outings.TryGetValue(i.Id, out var segments) && segments.Count > 1)
            {
                // Each recording is matched on its own, with the outing's fused type
                records.AddRange(segments.Select(s => new RouteRecord(s.Id, s.UpdatedAt, s.PointCount, i.Type)));
            }
            else if (i.PointCount > 0)
            {
                records.Add(new RouteRecord(i.Id, i.UpdatedAt, i.PointCount, i.Type));
            }
        }

        if (records.Count == 0)
        {
            return result;
        }

        var chosen = await _routes!.ChooseAsync(owner, records, line, ct);
        if (chosen.Count == 0)
        {
            return result;
        }

        foreach (var i in items)
        {
            if (outings.TryGetValue(i.Id, out var segments) && segments.Count > 1)
            {
                var joined = JoinOuting(segments.OrderBy(s => s.StartTime).ToList(), chosen, line);
                if (joined != null)
                {
                    result[i.Id] = joined;
                }
            }
            else if (chosen.TryGetValue(i.Id, out var c))
            {
                result[i.Id] = new ChosenGeometry(c.Polyline, c.MatchedDistanceM, c.Pieces?.ToList(), Roads: c.Roads?.ToList());
            }
        }

        return result;
    }

    /// <summary>
    /// An outing's recordings in time order, each with its chosen line (matched, or the
    /// recorded one), as fusion joins them; the jump between two recordings is a gap.
    /// It is simplified at most once: a list line here (the recordings' lines are stored
    /// simplified already), a density line by the density map itself.
    /// </summary>
    private static ChosenGeometry? JoinOuting(List<SegmentState> segments, IReadOnlyDictionary<Guid, ChosenRoute> chosen, RouteLine kind)
    {
        var withPieces = kind == RouteLine.Detail;
        if (!segments.Any(s => chosen.ContainsKey(s.Id)))
        {
            return null;
        }

        var line = new List<(double Lat, double Lon)>();
        var pieces = new List<RouteGeometryPiece>();
        // The recordings' roads, by name: metres summed, first taken where the outing first takes it
        var roads = new Dictionary<string, RouteRoad>(StringComparer.Ordinal);
        double at = 0;
        double? distance = 0;
        foreach (var s in segments)
        {
            List<(double Lat, double Lon)> points;
            List<RouteGeometryPiece> own;
            if (chosen.TryGetValue(s.Id, out var c))
            {
                points = DecodePolyline(c.Polyline);
                own = c.Pieces?.ToList() ?? new List<RouteGeometryPiece>();
                distance += c.MatchedDistanceM;
                foreach (var r in c.Roads ?? Array.Empty<RouteRoad>())
                {
                    roads[r.Name] = roads.TryGetValue(r.Name, out var seen)
                        ? seen with { M = seen.M + r.M }
                        : r with { FromM = Math.Round(at + r.FromM, 1) };
                }
            }
            else
            {
                if (s.Polyline == null)
                {
                    continue;
                }

                points = DecodePolyline(s.Polyline);
                own = new List<RouteGeometryPiece> { new(0, LineLength(points), "observed") };
                // Not matched end to end: the distance is not replaced
                distance = null;
            }

            if (points.Count == 0)
            {
                continue;
            }

            if (line.Count > 0)
            {
                var jump = Geo.Haversine(line[^1].Lat, line[^1].Lon, points[0].Lat, points[0].Lon);
                if (jump > 0)
                {
                    pieces.Add(new RouteGeometryPiece(Math.Round(at, 1), Math.Round(at + jump, 1), "gap"));
                    at += jump;
                }
            }

            pieces.AddRange(own.Select(p => new RouteGeometryPiece(Math.Round(at + p.FromM, 1), Math.Round(at + p.ToM, 1), p.Kind)));
            line.AddRange(points);
            at += LineLength(points);
        }

        if (line.Count < 2)
        {
            return null;
        }

        // A list draws it whole, so it is simplified as fusion does; the panel keeps the
        // pieces' metres exact; the density map simplifies it once, itself
        var polyline = kind == RouteLine.List ? Geo.EncodePolyline(Geo.Simplify(line)) : Geo.EncodePolyline(line);
        return new ChosenGeometry(polyline, distance, withPieces ? pieces : null, kind != RouteLine.Density,
            withPieces ? roads.Values.OrderBy(r => r.FromM).ToList() : null);
    }

    /// <summary>A summary drawn with its chosen line; the distance replaced only when fusion took it from the straight legs (rule 5)</summary>
    private static ActivitySummary Apply(ActivitySummary s, ChosenGeometry? c, bool detail)
    {
        if (c == null)
        {
            return s;
        }

        return s with
        {
            Polyline = c.Polyline,
            Geometry = RouteGeometryKinds.Matched,
            DistanceM = s.DistanceFromTrack && c.DistanceM.HasValue ? Math.Round(c.DistanceM.Value) : s.DistanceM,
            Pieces = detail ? c.Pieces : null,
            OnRoadsM = detail && c.DistanceM.HasValue ? Math.Round(c.DistanceM.Value) : null
        };
    }

    /// <summary>The detail view's chosen geometry (with pieces) and the panel's route information</summary>
    private async Task<(ActivitySummary Summary, ActivityRouteInfo? Route)> DetailRouteAsync(string owner, ActivitySummary summary,
        List<AtlasActivity> trackRecords, CancellationToken ct)
    {
        if (_routes is not { Enabled: true })
        {
            return (summary, null);
        }

        var segments = trackRecords.Select(m => new SegmentState(m.Id, m.UpdatedAt, m.PointCount, m.StartTime, m.Polyline)).ToList();
        var item = new GeometryCandidate(summary.Id, summary.Type, summary.Polyline, summary.RecordUpdatedAt, summary.PointCount, null);
        var outings = segments.Count > 1
            ? new Dictionary<Guid, List<SegmentState>> { [summary.Id] = segments }
            : new Dictionary<Guid, List<SegmentState>>();
        var chosen = (await ChooseCoreAsync(owner, new[] { item }, outings, RouteLine.Detail, ct)).GetValueOrDefault(summary.Id);
        var description = await _routes.DescribeAsync(owner,
            new RouteRecord(summary.Id, summary.RecordUpdatedAt, summary.PointCount, summary.Type), ct);
        double? observed = null, inferred = null;
        if (chosen?.Pieces is { Count: > 0 } pieces)
        {
            var total = pieces.Sum(p => p.ToM - p.FromM);
            if (total > 0)
            {
                observed = Math.Round(pieces.Where(p => p.Kind == "observed").Sum(p => p.ToM - p.FromM) / total, 3);
                inferred = Math.Round(pieces.Where(p => p.Kind == "inferred").Sum(p => p.ToM - p.FromM) / total, 3);
            }
        }

        return (Apply(summary, chosen, true),
            new ActivityRouteInfo(segments.Select(s => s.Id).ToList(), description, observed, inferred, chosen?.Roads));
    }

    /// <summary>
    /// The records whose track an activity draws, with their natural keys: the primary,
    /// or each recording of an outing. Decisions are written for these keys.
    /// </summary>
    /// <exception cref="AtlasNotFoundException">Not the owner's activity</exception>
    public async Task<List<(Guid Id, string SourceKey)>> RouteRecordsAsync(string owner, Guid id, CancellationToken ct = default)
    {
        var a = await _db.Activities.AsNoTracking().Include(x => x.Fusion).FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == owner, ct)
                ?? throw new AtlasNotFoundException("Activity");
        if (a.ClusterId.HasValue && a.ClusterId != a.Id)
        {
            a = await _db.Activities.AsNoTracking().Include(x => x.Fusion).FirstOrDefaultAsync(x => x.Id == a.ClusterId && x.OwnerId == owner, ct) ?? a;
        }

        var ids = a.Fusion is { SegmentCount: > 1 } ? SegmentIds(a.Id, a.Fusion.Evidence) : new List<Guid> { a.Id };
        var rows = await _db.Activities.AsNoTracking()
            .Where(x => x.OwnerId == owner && ids.Contains(x.Id))
            .Select(x => new { x.Id, x.Source, x.ExternalId })
            .ToListAsync(ct);
        // The same natural key the route-quality service stores (AtlasRoutesFeed)
        return rows.Select(x => (x.Id, Routes.RouteKeys.SourceKey(x.Source, x.ExternalId, x.Id))).ToList();
    }

    /// <summary>The primary and the recordings joined with it (fusion's evidence)</summary>
    private static List<Guid> SegmentIds(Guid primary, string evidence)
    {
        var ids = new List<Guid> { primary };
        try
        {
            ids.AddRange((System.Text.Json.JsonSerializer.Deserialize<List<MatchEvidence>>(evidence, JsonCamel) ?? new())
                .Where(e => e.Segment && e.MemberId != primary).Select(e => e.MemberId));
        }
        catch (System.Text.Json.JsonException)
        {
            // Unreadable evidence: the primary alone
        }

        return ids.Distinct().ToList();
    }

    private static double LineLength(IReadOnlyList<(double Lat, double Lon)> points)
    {
        double length = 0;
        for (var i = 1; i < points.Count; i++)
        {
            length += Geo.Haversine(points[i - 1].Lat, points[i - 1].Lon, points[i].Lat, points[i].Lon);
        }

        return length;
    }
}
