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
}

/// <summary>What a batch added or updated</summary>
public record IngestResult(Guid ImportId, int Activities, int Places, int Visits, int HealthSamples);

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
}

/// <summary>An activity without its points</summary>
public record ActivitySummary(
    Guid Id, string Type, string? Name, DateTime StartTime, DateTime EndTime, double? DistanceM, double? DurationS,
    double? ElevationGainM, int? AvgHeartRate, int? MaxHeartRate, string Source, string? Polyline,
    double? MinLat, double? MinLon, double? MaxLat, double? MaxLon, int PointCount,
    List<string> Sources, int MemberCount, int ConflictCount);

/// <summary>A visit with its place</summary>
public record VisitSummary(
    Guid Id, Guid? PlaceId, string? PlaceName, string? Address, DateTime StartTime, DateTime EndTime,
    double Lat, double Lon, string Source);

/// <summary>Search results</summary>
public record SearchResult(List<ActivitySummary> Activities, List<VisitSummary> Visits, int TotalActivities, int TotalVisits);

/// <summary>A track point as returned ([time, lat, lon, elevation, heart rate, speed])</summary>
public record PointOutput(DateTime T, double Lat, double Lon, double? Ele, int? Hr, double? Speed);

/// <summary>A heart rate (or other health) value at a time</summary>
public record SeriesPoint(DateTime T, double V, double? Min = null, double? Max = null);

/// <summary>An activity with its track and heart rate</summary>
public record ActivityDetail(
    ActivitySummary Summary, string? Notes, List<PointOutput> Points,
    string? HeartRateSource, List<SeriesPoint> HeartRate,
    List<ActivityMember> Members, List<FusionConflict> Conflicts,
    Dictionary<string, string> FieldSources, List<LinkedPlace> Places);

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
public record PlacePatch(string? Name = null, string? Address = null, string? Category = null, string? Notes = null, double? Lat = null, double? Lon = null);

/// <summary>What was at or passed through a point</summary>
public record NearResult(List<VisitSummary> Visits, List<ActivitySummary> Activities);
