/**
 * @fileoverview Stage B vocabulary shared by the service and the backend: piece kinds,
 * route statuses, the stored piece shape, the Owner's settings and the read-time
 * choice of AD-34 rule 3 (the seam story 12.6's IRouteGeometryReader builds on)
 * @module ContactsApi.Infrastructure.Routes
 */
using System.Text.Json;

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
