/**
 * @fileoverview The trace_attributes request (AD-32 rule 2, AD-35 rule 4) and the
 * parts of its response that stage B reads: the polyline6 shape, the edges' shape
 * ranges and each input point's label (AD-35 rule 7)
 * @module EchoRoutes.Worker.Matching
 */
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContactsApi.Infrastructure.Routes;

namespace EchoRoutes.Worker.Matching;

/// <summary>An edge of the matched path: its range of shape vertices</summary>
public record TraceEdge(int BeginShape, int EndShape);

/// <summary>Valhalla's label for one input point</summary>
/// <param name="Type">matched, interpolated or unmatched</param>
/// <param name="EdgeIndex">Index into the edges; null when the point has no edge</param>
/// <param name="AlongEdge">distance_along_edge: a fraction of the edge</param>
/// <param name="FromTrace">distance_from_trace_point, in metres</param>
/// <param name="BeginDiscontinuity">The path breaks after this point</param>
/// <param name="EndDiscontinuity">The path resumes at this point</param>
public record TraceLabel(string Type, int? EdgeIndex, double? AlongEdge, double? FromTrace, double? Lat, double? Lon,
    bool BeginDiscontinuity, bool EndDiscontinuity)
{
    /// <summary>An observed anchor: matched or interpolated, on an edge</summary>
    public bool IsAnchor => Type is "matched" or "interpolated" && EdgeIndex.HasValue;
}

/// <summary>What stage B reads of one trace_attributes answer</summary>
public record TraceResponse(List<(double Lat, double Lon)> Shape, List<TraceEdge> Edges, List<TraceLabel> Labels);

/// <summary>Builds the request and reads the response</summary>
public static class ValhallaTrace
{
    /// <summary>The attributes asked for (everything else is left out of the answer)</summary>
    public static readonly string[] Attributes =
    {
        "edge.id", "edge.length", "edge.begin_shape_index", "edge.end_shape_index",
        "matched.point", "matched.type", "matched.edge_index", "matched.distance_along_edge",
        "matched.distance_from_trace_point", "matched.begin_route_discontinuity", "matched.end_route_discontinuity",
        "shape", "confidence_score"
    };

    /// <summary>
    /// The costing (AD-35 rule 4): drive → auto, transit → bus, or auto for a taxi; null
    /// when the record is not matched by road in R1 (rail, unknown transit, other types)
    /// </summary>
    public static string? CostingFor(string type, string? sourceType)
    {
        if (type == "transit")
        {
            return RouteQuality.TransitKind(sourceType) switch
            {
                "bus" => "bus",
                "taxi" => "auto",
                _ => null
            };
        }

        var profile = RouteQuality.ProfileFor(type);
        return profile.Phase != null && RouteQuality.EnabledPhases.Contains(profile.Phase) ? profile.Costing : null;
    }

    /// <summary>One trace_attributes request over points [call.Start, call.End)</summary>
    public static JsonObject BuildRequest(IReadOnlyList<RoutePoint> points, TraceCall call, string costing, string source, MatchSettings s)
    {
        var shape = new JsonArray();
        for (var i = call.Start; i < call.End; i++)
        {
            var p = points[i];
            shape.Add(new JsonObject
            {
                ["lat"] = Math.Round(p.Lat, 6),
                ["lon"] = Math.Round(p.Lon, 6),
                ["time"] = new DateTimeOffset(DateTime.SpecifyKind(p.T, DateTimeKind.Utc)).ToUnixTimeSeconds()
            });
        }

        return new JsonObject
        {
            ["shape"] = shape,
            ["costing"] = costing,
            ["shape_match"] = "map_snap",
            ["use_timestamps"] = true,
            ["trace_options"] = new JsonObject
            {
                ["search_radius"] = s.SearchRadiusFor(source),
                ["gps_accuracy"] = s.GpsAccuracyFor(source),
                ["breakage_distance"] = s.BreakageDistanceM
            },
            ["filters"] = new JsonObject
            {
                ["attributes"] = new JsonArray(Attributes.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                ["action"] = "include"
            }
        };
    }

    /// <summary>Reads an answer; a point whose edge index is out of range (Valhalla's sentinel) has no edge</summary>
    public static TraceResponse Parse(JsonElement root)
    {
        var shape = root.TryGetProperty("shape", out var s) && s.ValueKind == JsonValueKind.String
            ? DecodePolyline(s.GetString()!, 6)
            : new List<(double, double)>();
        var edges = new List<TraceEdge>();
        if (root.TryGetProperty("edges", out var es) && es.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in es.EnumerateArray())
            {
                edges.Add(new TraceEdge(Int(e, "begin_shape_index") ?? 0, Int(e, "end_shape_index") ?? 0));
            }
        }

        var labels = new List<TraceLabel>();
        if (root.TryGetProperty("matched_points", out var mp) && mp.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in mp.EnumerateArray())
            {
                var type = m.TryGetProperty("type", out var t) ? t.GetString() ?? "unmatched" : "unmatched";
                var edge = m.TryGetProperty("edge_index", out var ei) && ei.TryGetInt64(out var idx) && idx >= 0 && idx < edges.Count
                    ? (int)idx
                    : (int?)null;
                labels.Add(new TraceLabel(type, edge, Double(m, "distance_along_edge"), Double(m, "distance_from_trace_point"),
                    Double(m, "lat"), Double(m, "lon"), Bool(m, "begin_route_discontinuity"), Bool(m, "end_route_discontinuity")));
            }
        }

        return new TraceResponse(shape, edges, labels);
    }

    /// <summary>Decodes a Google encoded polyline of the given precision (Valhalla: 6)</summary>
    public static List<(double Lat, double Lon)> DecodePolyline(string encoded, int precision)
    {
        var factor = Math.Pow(10, precision);
        var result = new List<(double, double)>();
        int index = 0;
        long lat = 0, lon = 0;
        while (index < encoded.Length)
        {
            lat += Next(encoded, ref index);
            if (index >= encoded.Length)
            {
                break;
            }

            lon += Next(encoded, ref index);
            result.Add((lat / factor, lon / factor));
        }

        return result;
    }

    private static long Next(string s, ref int index)
    {
        long result = 0;
        var shift = 0;
        int b;
        do
        {
            if (index >= s.Length)
            {
                break;
            }

            b = s[index++] - 63;
            result |= (long)(b & 0x1f) << shift;
            shift += 5;
        }
        while (b >= 0x20);

        return (result & 1) != 0 ? ~(result >> 1) : result >> 1;
    }

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;

    private static double? Double(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble()
        : e.TryGetProperty(name, out var str) && str.ValueKind == JsonValueKind.String
          && double.TryParse(str.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
