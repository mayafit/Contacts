/**
 * @fileoverview Stage B vocabulary shared by the service and the backend: piece kinds,
 * route statuses, the stored piece shape, the Owner's settings and the read-time
 * choice of AD-34 rule 3 (the seam story 12.6's IRouteGeometryReader builds on)
 * @module ContactsApi.Infrastructure.Routes
 */
using System.Text.Json;
using System.Text.RegularExpressions;
using ContactsApi.Core.Interfaces;

namespace ContactsApi.Infrastructure.Routes;

/// <summary>Kinds of a matched line's pieces (AD-35 rule 7)</summary>
public static class PieceKinds
{
    /// <summary>Recorded points lie along it</summary>
    public const string Observed = "observed";

    /// <summary>The road was chosen by routing between sparse points</summary>
    public const string Inferred = "inferred";

    /// <summary>Nothing matched: the original straight leg stays</summary>
    public const string Gap = "gap";
}

/// <summary>Statuses of a matched route (AD-35 rule 6)</summary>
public static class RouteStatuses
{
    public const string Matched = "matched";
    public const string LowConfidence = "low_confidence";
    public const string Failed = "failed";

    /// <summary>Confidence at or above which a route is matched</summary>
    public const double MatchedAt = 0.7;

    /// <summary>Confidence at or above which a route is low_confidence (below it: failed)</summary>
    public const double LowConfidenceAt = 0.3;

    /// <summary>The status of a confidence</summary>
    public static string Of(double confidence) =>
        confidence >= MatchedAt ? Matched : confidence >= LowConfidenceAt ? LowConfidence : Failed;
}

/// <summary>One piece of a matched line, in metres along the stored (precision 5) line</summary>
public record RoutePiece(double FromM, double ToM, string Kind);

/// <summary>The roads of a matched line (AD-37): what counts, how they are stored and searched</summary>
public static class RouteRoads
{
    /// <summary>A road the line follows for less than this (metres) is left out: a junction crossed, a snapping slip</summary>
    public const double MinM = 100;

    /// <summary>Most roads stored per line (the longest are kept)</summary>
    public const int Max = 40;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex RefPattern = new(@"^[0-9]{1,4}[A-Za-z]?$", RegexOptions.Compiled);
    private static readonly Regex Prefix = new(@"^(road|route|rd|hwy|highway|כביש|דרך)\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A route number ("431", "4"), not a street name</summary>
    public static bool IsRef(string name) => RefPattern.IsMatch(name);

    /// <summary>
    /// The roads to store from metres per name: at least <see cref="MinM"/>, at most
    /// <see cref="Max"/> (the longest), in travel order
    /// </summary>
    public static List<RouteRoad> Keep(IEnumerable<(string Name, double M, double FromM)> roads) =>
        roads.Where(r => r.M >= MinM && !string.IsNullOrWhiteSpace(r.Name))
            .OrderByDescending(r => r.M).Take(Max)
            .OrderBy(r => r.FromM)
            .Select(r => new RouteRoad(r.Name.Trim(), IsRef(r.Name.Trim()), Math.Round(r.M), Math.Round(r.FromM, 1)))
            .ToList();

    public static string Serialize(IReadOnlyList<RouteRoad> roads) => JsonSerializer.Serialize(roads, Json);

    /// <summary>The stored roads of a row; empty when it has none or they cannot be read</summary>
    public static List<RouteRoad> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<RouteRoad>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<RouteRoad>>(json, Json) ?? new List<RouteRoad>();
        }
        catch (JsonException)
        {
            return new List<RouteRoad>();
        }
    }

    /// <summary>
    /// What the Owner typed, as searched: trimmed, without a "road" / "כביש" prefix; null
    /// when nothing is left. "Road 431" and "כביש 431" search for 431.
    /// </summary>
    public static string? Normalize(string? road)
    {
        var s = road?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        s = Prefix.Replace(s, string.Empty).Trim();
        return s.Length == 0 ? null : s.Length > 100 ? s[..100] : s;
    }
}

/// <summary>Decision kinds (route_decisions.Kind)</summary>
public static class RouteDecisionKinds
{
    public const string Reject = "reject";
    public const string Accept = "accept";
}

/// <summary>The Owner's route settings (routes.settings JSON); defaults when absent</summary>
public record RouteSettingsDoc(double Threshold = RouteStatuses.MatchedAt, bool ShownByDefault = true, List<string>? MatchedTypes = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Parses the stored JSON leniently</summary>
    public static RouteSettingsDoc Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new RouteSettingsDoc();
        }

        try
        {
            return JsonSerializer.Deserialize<RouteSettingsDoc>(json, Json) ?? new RouteSettingsDoc();
        }
        catch (JsonException)
        {
            return new RouteSettingsDoc();
        }
    }
}

/// <summary>
/// AD-34 rule 3 as a pure function: whether a stored match is drawn for a record. The
/// record's current UpdatedAt, point count and fused type are columns Atlas already
/// reads, so the check reads no points. Story 12.6 calls this from IRouteGeometryReader.
/// </summary>
public static class RouteChoice
{
    /// <summary>Whether <paramref name="row"/> may be drawn instead of the recorded line</summary>
    public static bool Drawable(MatchedRouteRow? row, string owner, DateTime recordUpdatedAt, int recordPointCount, string recordFusedType,
        string? decision, RouteSettingsDoc settings)
    {
        if (row == null || row.OwnerId != owner || string.IsNullOrEmpty(row.Polyline) || decision == RouteDecisionKinds.Reject)
        {
            return false;
        }

        if (row.SourceUpdatedAt != recordUpdatedAt || row.SourcePointCount != recordPointCount || row.FusedType != recordFusedType)
        {
            return false;
        }

        var confidence = row.Confidence ?? 0;
        return (row.Status == RouteStatuses.Matched && confidence >= settings.Threshold && settings.ShownByDefault)
               || (decision == RouteDecisionKinds.Accept && confidence >= RouteStatuses.LowConfidenceAt
                   && row.Status != RouteStatuses.Failed);
    }

    /// <summary>The stored pieces of a row</summary>
    public static List<RoutePiece> Pieces(MatchedRouteRow row) =>
        JsonSerializer.Deserialize<List<RoutePiece>>(row.Pieces, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new List<RoutePiece>();
}

/// <summary>
/// The pre-simplified lines stored beside a matched route, so no read decodes or
/// simplifies a full line: the list line has the recorded lines' budget, the density
/// line what the density map draws
/// </summary>
public static class RouteLines
{
    /// <summary>Most vertices of a list line (as the recorded lines, Geo.Simplify's default)</summary>
    public const int ListVertices = 400;

    /// <summary>Most vertices of a density line</summary>
    public const int DensityVertices = 80;

    /// <summary>The density line's first tolerance in degrees (~30 m)</summary>
    public const double DensityTolerance = 0.0003;

    /// <summary>The list and density lines of a full line (Google encoded at precision 5)</summary>
    public static (string List, string Density) Derive(string polyline)
    {
        var points = Atlas.AtlasService.DecodePolyline(polyline);
        return (Atlas.Geo.EncodePolyline(Atlas.Geo.Simplify(points, ListVertices)), Density(points));
    }

    /// <summary>A line simplified for the density map</summary>
    public static string Density(IReadOnlyList<(double Lat, double Lon)> points) =>
        Atlas.Geo.EncodePolyline(Atlas.Geo.Simplify(points, DensityVertices, DensityTolerance));
}
