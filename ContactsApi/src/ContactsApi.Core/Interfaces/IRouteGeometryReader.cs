/**
 * @fileoverview The read-time choice of geometry (AD-34 rule 3): Atlas asks which of
 * its source records may be drawn with a matched route. Declared here, implemented by
 * the Routes module, so Atlas never references Routes (AD-2).
 * @module ContactsApi.Core.Interfaces
 */
namespace ContactsApi.Core.Interfaces;

/// <summary>A source record as Atlas reads it: the columns the read-time check compares (no points)</summary>
/// <param name="Id">The record's id (activities.Id)</param>
/// <param name="UpdatedAt">activities.UpdatedAt</param>
/// <param name="PointCount">activities.PointCount</param>
/// <param name="FusedType">The fused type (the cluster's), else the record's own</param>
public record RouteRecord(Guid Id, DateTime UpdatedAt, int PointCount, string FusedType);

/// <summary>
/// Which stored matched line a read needs. Each is stored pre-simplified by the
/// route-quality service, so a read decodes and simplifies nothing.
/// </summary>
public enum RouteLine
{
    /// <summary>The full line (at most 1,000 vertices) with its pieces: the detail view</summary>
    Detail,

    /// <summary>At most 400 vertices, the recorded lines' budget: lists, search, the map</summary>
    List,

    /// <summary>At most 80 vertices at ~30 m: the density map</summary>
    Density
}

/// <summary>One piece of a matched line, in metres along it: observed, inferred or gap</summary>
public record RouteGeometryPiece(double FromM, double ToM, string Kind);

/// <summary>A road a matched line follows (AD-37)</summary>
/// <param name="Name">A route number ("431") or a street name, as the map data spells it</param>
/// <param name="Ref">A route number</param>
/// <param name="M">Metres of the line on it (observed and inferred pieces only)</param>
/// <param name="FromM">Where the line first takes it, in metres along the line (travel order)</param>
public record RouteRoad(string Name, bool Ref, double M, double FromM);

/// <summary>The matched line chosen for a record</summary>
/// <param name="Polyline">Google encoded at precision 5</param>
/// <param name="Pieces">Only when asked for (the detail view)</param>
/// <param name="Roads">The roads it follows, in travel order; only with the pieces (the detail view)</param>
public record ChosenRoute(Guid RecordId, string Polyline, double? MatchedDistanceM, double? Confidence,
    IReadOnlyList<RouteGeometryPiece>? Pieces, IReadOnlyList<RouteRoad>? Roads = null);

/// <summary>What the panel shows about one record's route (the detail view only)</summary>
/// <param name="Grade">good, fair, poor; null when not assessed or without a track</param>
/// <param name="Flags">sparse, straight, implausible_speed, endpoint_far…</param>
/// <param name="Matchable">yes, no_track, type_never, outside_coverage, rail, unknown; null when not assessed</param>
/// <param name="Status">The stored match's status (matched, low_confidence, failed); null without one</param>
/// <param name="Current">The stored match is for the record as it is now (rule 3's check)</param>
/// <param name="Decision">reject or accept; null without one</param>
/// <param name="Method">How the stored match was made (AD-38): auto, similar or waypoints; null without one</param>
/// <param name="Waypoints">The points the Owner marked for the record, [[lat, lon]…]; null without them</param>
public record RouteDescription(string? Grade, int? Score, IReadOnlyList<string> Flags, string? Matchable, bool NeedsImprovement,
    string? Status, double? Confidence, double? MatchedDistanceM, bool Current, string? Decision, string? Reason,
    string? Method = null, IReadOnlyList<double[]>? Waypoints = null);

/// <summary>
/// Tells the route-quality service that an Owner's tracks or types changed outside an
/// import (an edited type, a merge or a split), so it reads its change feed now
/// rather than at its next poll. Best effort, never blocks the edit.
/// </summary>
public interface IRouteChangeSignal
{
    /// <summary>The Owner's tracks or fused types changed</summary>
    void Changed(string owner);
}

/// <summary>Chooses, at read time, the geometry Atlas draws (AD-34 rule 3)</summary>
public interface IRouteGeometryReader
{
    /// <summary>Route quality is switched on (when off, every record is drawn as recorded)</summary>
    bool Enabled { get; }

    /// <summary>
    /// The matched lines that may be drawn for these records, by record id. One
    /// set-based query for the whole list, whatever its size, reading only the stored
    /// line <paramref name="line"/> asks for; no points are read. Records without a
    /// drawable match are left out. Never throws for a missing or unreachable routes
    /// schema: the records are then drawn as recorded.
    /// </summary>
    /// <param name="line">The line to read; only <see cref="RouteLine.Detail"/> carries the pieces</param>
    Task<IReadOnlyDictionary<Guid, ChosenRoute>> ChooseAsync(string owner, IReadOnlyCollection<RouteRecord> records, RouteLine line,
        CancellationToken ct = default);

    /// <summary>The quality, the stored match and the Owner's decision of one record; null when unknown</summary>
    Task<RouteDescription?> DescribeAsync(string owner, RouteRecord record, CancellationToken ct = default);

    /// <summary>
    /// The records whose match follows a road (AD-37): a route number exactly ("431"), or
    /// a street name containing the text. Only matches the Owner sees as right: shown by
    /// default or accepted, never rejected. Each comes with the values it was matched for
    /// (UpdatedAt, point count), so the caller keeps only the records still as they were.
    /// Empty when route quality is off or unreadable.
    /// </summary>
    Task<IReadOnlyList<RouteRecord>> OnRoadAsync(string owner, string road, CancellationToken ct = default);
}
