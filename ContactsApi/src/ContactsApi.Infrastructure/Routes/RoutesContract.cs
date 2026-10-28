/**
 * @fileoverview The backend's internal API for the route-quality service (AD-33 rule 5):
 * what the service pulls, shared by both processes so the two sides cannot drift
 * @module ContactsApi.Infrastructure.Routes
 */
namespace ContactsApi.Infrastructure.Routes;

/// <summary>The header a call made for an interactive job carries (never refused with a 429)</summary>
public static class EchoPriority
{
    /// <summary>Header name</summary>
    public const string Header = "X-Echo-Priority";

    /// <summary>The value for an interactive job</summary>
    public const string Interactive = "interactive";
}

/// <summary>Limits of the internal API</summary>
public static class RoutesFeedLimits
{
    /// <summary>Most items of one changes page</summary>
    public const int ChangesMax = 500;

    /// <summary>Most records of one tracks call</summary>
    public const int TracksMax = 25;

    /// <summary>Most points of one track (longer tracks are thinned)</summary>
    public const int PointsMax = 5000;

    /// <summary>Most ids of one exists call</summary>
    public const int ExistsMax = 2000;
}

/// <summary>A source record that may have changed: enough to tell whether it really did</summary>
/// <param name="Id">The source record's id</param>
/// <param name="UpdatedAt">The record's activities.UpdatedAt</param>
/// <param name="PointCount">The record's point count</param>
/// <param name="Type">The fused type (the fused activity's, else the record's own)</param>
/// <param name="SourceKey">The natural key: source|externalId, or activity|id</param>
/// <param name="SourceType">The source's own type text (in_bus…); null for records imported before it was kept</param>
public record RouteChange(Guid Id, DateTime UpdatedAt, int PointCount, string Type, string SourceKey, string? SourceType);

/// <summary>A page of the change feed</summary>
/// <param name="Items">Changed records, oldest change first</param>
/// <param name="Next">The cursor to ask from next (the same cursor when nothing changed)</param>
/// <param name="More">More changes wait after this page</param>
public record RouteChangesPage(List<RouteChange> Items, string? Next, bool More);

/// <summary>A track point: time and position</summary>
public record RoutePoint(DateTime T, double Lat, double Lon);

/// <summary>Where a Visit was, and when</summary>
public record RouteVisit(DateTime Start, DateTime End, double Lat, double Lon);

/// <summary>A source record with its points, for assessment and matching</summary>
/// <param name="Points">Points in time order (thinned to at most 5,000)</param>
/// <param name="Before">The Visit just before the record (by start time), if any</param>
/// <param name="After">The Visit just after the record, if any</param>
public record RouteTrack(
    Guid Id, string Source, string? ExternalId, string Type, string? SourceType,
    DateTime Start, DateTime End, DateTime UpdatedAt, int PointCount,
    List<RoutePoint> Points, RouteVisit? Before, RouteVisit? After)
{
    /// <summary>The natural key: source|externalId, or activity|id</summary>
    public string SourceKey => RouteKeys.SourceKey(Source, ExternalId, Id);
}

/// <summary>Natural keys of source records</summary>
public static class RouteKeys
{
    /// <summary>source|externalId, or activity|id for a record without an external id (AD-34 rule 1)</summary>
    public static string SourceKey(string source, string? externalId, Guid id) =>
        externalId != null ? $"{source}|{externalId}" : $"activity|{id}";
}
