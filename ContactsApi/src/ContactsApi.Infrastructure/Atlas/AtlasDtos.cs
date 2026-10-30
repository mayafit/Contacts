/**
 * @fileoverview Atlas request and response shapes (REST and MCP share them)
 * @module ContactsApi.Infrastructure.Atlas
 */
namespace ContactsApi.Infrastructure.Atlas;

/// <summary>A track point in an ingest batch</summary>
public record PointInput(DateTime T, double Lat, double Lon, double? Ele = null, int? Hr = null, double? Speed = null, int? Cad = null);

/// <summary>An activity in an ingest batch</summary>
public record ActivityInput(
    string? ExternalId, string Type, string? Name, DateTime? StartTime, DateTime? EndTime,
    double? DistanceM = null, double? DurationS = null, double? ElevationGainM = null, string? Notes = null,
    List<PointInput>? Points = null, int? AvgHeartRate = null, int? MaxHeartRate = null);

/// <summary>A place in an ingest batch</summary>
public record PlaceInput(
    string? ExternalId, string Name, double Lat, double Lon, string? Address = null,
    string? Category = null, string? GooglePlaceId = null);

/// <summary>A visit in an ingest batch (place by external id, or by name/coordinates)</summary>
public record VisitInput(
    string? ExternalId, DateTime StartTime, DateTime EndTime, double Lat, double Lon,
    string? PlaceExternalId = null, string? PlaceName = null, double? Confidence = null);

/// <summary>A health sample in an ingest batch</summary>
public record HealthInput(string Type, DateTime StartTime, double Value, DateTime? EndTime = null, string? Unit = null);

/// <summary>One batch of imported items</summary>
public class IngestRequest
{
    /// <summary>Continue this import (later batches of the same files), or null to start one</summary>
    public Guid? ImportId { get; set; }
    /// <summary>Source name (sports-tracker, google-timeline, google-fit, fitbit, gpx…)</summary>
    public string Source { get; set; } = "manual";
    public string? FileName { get; set; }
    public List<ActivityInput> Activities { get; set; } = new();
    public List<PlaceInput> Places { get; set; } = new();
    public List<VisitInput> Visits { get; set; } = new();
    public List<HealthInput> Health { get; set; } = new();
    /// <summary>The Owner's Google Maps reviews (Takeout Reviews.json, AD-39)</summary>
    public List<ReviewInput> Reviews { get; set; } = new();
}

/// <summary>What a batch added or updated</summary>
public record IngestResult(Guid ImportId, int Activities, int Places, int Visits, int HealthSamples, int Reviews = 0);

/// <summary>One of Google's questions about a place, and the Owner's answer</summary>
public record ReviewAnswer(string Question, string Answer);

/// <summary>A review as parsed from Reviews.json (published values, else the draft's)</summary>
public record ReviewInput(
    string? ExternalId, string Name, string? Address, string? CountryCode, double Lat, double Lon, int? Rating, string? Text,
    bool Published, DateTime ReviewedAt, string? MapsUrl, List<ReviewAnswer>? Answers);

/// <summary>A review as shown</summary>
/// <param name="PlaceId">The Atlas place it is about, when linked</param>
public record PlaceReviewView(
    Guid Id, string Name, string? Address, int? Rating, string? Text, bool Published, DateTime ReviewedAt, string? MapsUrl,
    List<ReviewAnswer> Answers, Guid? PlaceId, string? LinkedBy, double Lat, double Lon);

/// <summary>
/// Where a place stands on reviews (AD-39 rule 4): reviewed, to_review, later (Until),
/// never (Category: set aside by its category), done, not_reviewable (Reason: no_identity,
/// category, no_recent_visit, routine)
/// </summary>
public record ReviewState(string State, DateTime? Until = null, bool Category = false, string? Reason = null, string? WriteUrl = null);

/// <summary>A place to review</summary>
public record ReviewDue(Guid PlaceId, string Name, string? Category, DateTime LastVisit, int Visits, string WriteUrl, double Lat, double Lon);

/// <summary>The Owner's reviews and the places to review (AD-39 rule 7)</summary>
public record ReviewsOverview(int Written, double? Average, int Linked, int Unlinked, List<PlaceReviewView> Recent, List<ReviewDue> Due,
    int DueTotal);

/// <summary>A choice about a review reminder: later, never, done, never_kind, clear (Kind: also the category's)</summary>
public record ReviewReminderRequest(Guid PlaceId, string Action, bool Kind = false);

/// <summary>Search filters; every one is optional and they combine</summary>
public class AtlasQuery
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public double? MinLat { get; set; }
    public double? MinLon { get; set; }
    public double? MaxLat { get; set; }
    public double? MaxLon { get; set; }
    /// <summary>Activity types (ride, hike…)</summary>
    public List<string>? Types { get; set; }
    /// <summary>Text in activity names / notes and place names / addresses</summary>
    public string? Text { get; set; }
    public bool Activities { get; set; } = true;
    public bool Visits { get; set; } = true;
    public int Limit { get; set; } = 300;
    /// <summary>Only fused activities whose sources disagree</summary>
    public bool ConflictsOnly { get; set; }

    /// <summary>
    /// Only activities whose matched route follows this road (AD-37): a route number
    /// ("431", "road 431") or part of a street name. Visits are left out, as with ConflictsOnly.
    /// </summary>
    public string? Road { get; set; }

    /// <summary>The road filter resolved to activity ids (set by the service; never read from a request)</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<Guid>? RoadActivityIds { get; set; }

    /// <summary>The query filters by road</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasRoad => !string.IsNullOrWhiteSpace(Road);

    /// <summary>Only activities: the conflicts or road filter is on (they say nothing about visits)</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ActivitiesOnly => ConflictsOnly || HasRoad;
}

/// <summary>An activity without its points</summary>
/// <param name="PhotoCount">Photos taken during it (set in search results)</param>
public record ActivitySummary(
    Guid Id, string Type, string? Name, DateTime StartTime, DateTime EndTime, double? DistanceM, double? DurationS,
    double? ElevationGainM, int? AvgHeartRate, int? MaxHeartRate, string Source, string? Polyline,
    double? MinLat, double? MinLon, double? MaxLat, double? MaxLon, int PointCount,
    List<string> Sources, int MemberCount, int ConflictCount, int PhotoCount = 0)
{
    /// <summary>
    /// "matched" when Polyline is a matched route chosen at read time (AD-34 rule 3),
    /// else "recorded" (the fused, recorded line)
    /// </summary>
    public string Geometry { get; init; } = RouteGeometryKinds.Recorded;

    /// <summary>The matched line's pieces in metres along it (the detail view only)</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<ContactsApi.Core.Interfaces.RouteGeometryPiece>? Pieces { get; init; }

    /// <summary>The matched line's length (the detail view only): "on roads" beside the source's distance</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? OnRoadsM { get; init; }

    // Read with the summary for the read-time choice; never sent

    /// <summary>The primary record's UpdatedAt</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime RecordUpdatedAt { get; init; }

    /// <summary>Recordings of an outing (1: one recording)</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int SegmentCount { get; init; } = 1;

    /// <summary>The fusion evidence, read only for an outing of several recordings</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? OutingEvidence { get; init; }

    /// <summary>Fusion took the distance from the straight-line track length (FieldSources.distance = "track")</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DistanceFromTrack { get; init; }
}

/// <summary>ActivitySummary.Geometry values</summary>
public static class RouteGeometryKinds
{
    public const string Matched = "matched";
    public const string Recorded = "recorded";
}

/// <summary>A visit with its place</summary>
/// <param name="PhotoCount">Photos taken during it (set in search results)</param>
public record VisitSummary(
    Guid Id, Guid? PlaceId, string? PlaceName, string? Address, DateTime StartTime, DateTime EndTime,
    double Lat, double Lon, string Source, string? IdentifyState = null, int PhotoCount = 0);

/// <summary>Search results</summary>
public record SearchResult(List<ActivitySummary> Activities, List<VisitSummary> Visits, int TotalActivities, int TotalVisits);

/// <summary>A track point as returned</summary>
/// <param name="Speed">m/s: recorded, or the average over ±10 s</param>
/// <param name="Cad">Cadence (rpm or steps per minute) when recorded</param>
/// <param name="D">Distance from the start along the track (m)</param>
public record PointOutput(DateTime T, double Lat, double Lon, double? Ele, int? Hr, double? Speed, int? Cad = null, double D = 0);

/// <summary>A heart rate (or other health) value at a time</summary>
public record SeriesPoint(DateTime T, double V, double? Min = null, double? Max = null);

/// <summary>An activity with its track and heart rate</summary>
public record ActivityDetail(
    ActivitySummary Summary, string? Notes, List<PointOutput> Points,
    string? HeartRateSource, List<SeriesPoint> HeartRate,
    List<ActivityMember> Members, List<FusionConflict> Conflicts,
    Dictionary<string, string> FieldSources, List<LinkedPlace> Places,
    List<MatchEvidence> Evidence, double? MatchConfidence, ActivityAnalysis? Analysis = null)
{
    /// <summary>Route quality for the panel; null when route quality is off or unavailable</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ActivityRouteInfo? Route { get; init; }
}

/// <summary>What the Activity panel shows about the route (stories 12.6, 12.7)</summary>
/// <param name="RecordIds">The records whose track is drawn (the primary, or each recording of an outing): what "Fix this route" queues</param>
/// <param name="Description">The primary record's quality, stored match and decision; null before the service assessed it</param>
/// <param name="ObservedShare">Share of the matched line along recorded points (0-1); null when not drawn</param>
/// <param name="InferredShare">Share chosen by routing between sparse points (0-1); null when not drawn</param>
/// <param name="Roads">The roads the drawn matched line follows, in travel order (AD-37); null when not drawn</param>
public record ActivityRouteInfo(List<Guid> RecordIds, ContactsApi.Core.Interfaces.RouteDescription? Description,
    double? ObservedShare, double? InferredShare, List<ContactsApi.Core.Interfaces.RouteRoad>? Roads = null);

/// <summary>How a source record matches the fused activity's primary record</summary>
/// <param name="StartDeltaS">Its start minus the primary's (seconds)</param>
/// <param name="EndDeltaS">Its end minus the primary's (seconds)</param>
/// <param name="TimeToleranceS">The two sources' combined time tolerance</param>
/// <param name="TrackDeviationM">Median distance between the tracks at the same moments (null without two tracks)</param>
/// <param name="LocationToleranceM">The two sources' combined location tolerance</param>
/// <param name="Confidence">0-1</param>
/// <param name="UserDecision">The user merged it</param>
/// <param name="Segment">A piece of the same recording (stopped and started again), joined by continuation</param>
/// <param name="Contained">A piece that lies within one of the activity's recordings (a phone's segment of it)</param>
public record MatchEvidence(
    Guid MemberId, string Source, double StartDeltaS, double EndDeltaS, double TimeToleranceS,
    double? TrackDeviationM, double LocationToleranceM, double Confidence, bool UserDecision, bool Segment = false, bool Contained = false);

/// <summary>A source's accuracy as the app shows and edits it</summary>
public record SourceToleranceDto(string Source, double TimeToleranceS, double LocationToleranceM, bool IsDefault = false);

/// <summary>Fusion settings: the match threshold and every source's accuracy</summary>
public record FusionSettingsDto(double MatchThreshold, List<SourceToleranceDto> Sources);

/// <summary>One source record of a fused activity</summary>
public record ActivityMember(
    Guid Id, string Source, string Type, string? Name, DateTime StartTime, DateTime EndTime,
    double? DistanceM, double? DurationS, int? AvgHeartRate, int? MaxHeartRate, int PointCount,
    bool Primary, Guid? ImportId);

/// <summary>One source's value in a conflict</summary>
public record SourceValue(string Source, string Value);

/// <summary>Something the sources of a fused activity disagree on</summary>
public record FusionConflict(string Field, string Message, List<SourceValue> Values, string Chosen);

/// <summary>A visit linked to an activity: where it started, ended, or stopped on the way</summary>
public record LinkedPlace(
    Guid VisitId, string? PlaceName, string? Address, string Role, DateTime StartTime, DateTime EndTime,
    double Lat, double Lon);

/// <summary>What a fusion run did</summary>
public record FuseResult(int Activities, int Fused, int Conflicts);

/// <summary>Body of a split: the record to take out of the fused activity</summary>
public record ActivitySplitRequest(Guid MemberId);

/// <summary>Body of a merge: the activity to merge with</summary>
public record ActivityMergeRequest(Guid OtherId);

/// <summary>Counts per time bucket and kind (timeline histogram)</summary>
public record HistogramRow(DateTime Bucket, string Kind, int Count);

/// <summary>Count and distance of one activity type</summary>
public record TypeTotal(int Count, double DistanceKm);

/// <summary>Totals for the owner</summary>
public record AtlasSummary(
    int Activities, int Places, int Visits, long HealthSamples, DateTime? First, DateTime? Last,
    Dictionary<string, TypeTotal> ByType, List<ImportRun> Imports);

/// <summary>Editable activity fields</summary>
public record ActivityPatch(string? Name = null, string? Type = null, string? Notes = null);

/// <summary>Editable place fields</summary>
public record PlacePatch(string? Name = null, string? Address = null, string? Category = null, string? Notes = null, double? Lat = null, double? Lon = null,
    string? LinkedRef = null, string? Website = null, string? Phone = null, string? OpeningHours = null);

/// <summary>A place with everything known about it and how often it was visited</summary>
public record PlaceDetail(
    Guid Id, string Name, string? Address, string? Category, double Lat, double Lon, string? Notes, string Source,
    string? LinkedRef, string? Website, string? Phone, string? OpeningHours, Dictionary<string, string> Details,
    int Visits, DateTime? FirstVisit, DateTime? LastVisit, double HoursSpent,
    string IdentifyState = "pending", List<LocationCandidate>? Candidates = null, string? IdentifiedFrom = null,
    PlaceReviewView? Review = null, ReviewState? ReviewState = null);

/// <summary>What a visit's place is: a location found by a lookup, or typed by the user</summary>
public record PlaceAssociation(
    string Name, string? Address = null, string? Category = null, string? LinkedRef = null, string? Website = null,
    string? Phone = null, string? OpeningHours = null, Dictionary<string, string>? Details = null);

/// <summary>A visit and its place after an association</summary>
public record VisitPlaceResult(VisitSummary Visit, PlaceDetail Place, List<NearbyPlace>? Applied = null, List<NearbyPlace>? Suggested = null);

/// <summary>Tracks and visits for the density map</summary>
public record DensityData(List<DensityTrack> Tracks, List<double[]> Visits, bool Truncated);

/// <summary>One track for the density map (simplified)</summary>
public record DensityTrack(string Type, string Polyline);

/// <summary>Places to give an identity to</summary>
public record ApplyIdentityRequest(List<Guid> PlaceIds);

/// <summary>What was at or passed through a point</summary>
public record NearResult(List<VisitSummary> Visits, List<ActivitySummary> Activities);

/// <summary>
/// A photo or video of a connected library. Lat/Lon is where it is shown (null when
/// unknown); PlacedBy says how that was found: gps, track, visit.
/// </summary>
public record PhotoSummary(
    Guid Id, string Name, DateTime TakenAt, bool IsVideo, double? Lat, double? Lon, string? PlacedBy,
    int? Width, int? Height, string? WebUrl, Guid? ActivityId, Guid? VisitId, string? Camera);

/// <summary>Photos for the map (Truncated: there were more, the newest are given)</summary>
public record PhotoMapData(List<PhotoSummary> Photos, bool Truncated);
