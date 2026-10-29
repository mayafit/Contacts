/**
 * @fileoverview Atlas data: activities with tracks, places, visits, health samples
 * @module ContactsApi.Infrastructure.Atlas
 */
namespace ContactsApi.Infrastructure.Atlas;

/// <summary>Where a row came from; every imported row has one, plus a stable id there</summary>
public interface ISourced
{
    /// <summary>Google account id of the owner</summary>
    string OwnerId { get; set; }
    /// <summary>sports-tracker, google-timeline, google-fit, fitbit, gpx, manual…</summary>
    string Source { get; set; }
    /// <summary>The item's id in the source (re-imports update instead of duplicating)</summary>
    string? ExternalId { get; set; }
    /// <summary>The import that brought it in (deleting the import deletes it)</summary>
    Guid? ImportId { get; set; }
}

/// <summary>A movement with a track: ride, hike, run, walk, drive, flight…</summary>
public class AtlasActivity : ISourced
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = string.Empty;
    public string Source { get; set; } = "manual";
    public string? ExternalId { get; set; }
    public Guid? ImportId { get; set; }

    /// <summary>ride, hike, run, walk, drive, flight, transit, ski, swim, other</summary>
    public string Type { get; set; } = "other";
    /// <summary>The source's own type text before normalising (in_bus, in_train…); written only by import</summary>
    public string? SourceType { get; set; }
    public string? Name { get; set; }
    public string? Notes { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public double? DistanceM { get; set; }
    public double? DurationS { get; set; }
    public double? ElevationGainM { get; set; }
    public int? AvgHeartRate { get; set; }
    public int? MaxHeartRate { get; set; }

    /// <summary>Bounding box of the track (area searches without PostGIS)</summary>
    public double? MinLat { get; set; }
    public double? MinLon { get; set; }
    public double? MaxLat { get; set; }
    public double? MaxLon { get; set; }

    /// <summary>Simplified track as a Google encoded polyline (for drawing many tracks)</summary>
    public string? Polyline { get; set; }
    public int PointCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the record's track or type last changed (an import, a re-import, a new type,
    /// a cluster change). Not moved by a rename or a note: route quality compares a
    /// matched route against it (AD-34 rule 3), and its change feed follows it.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Fusion: the id of the primary record of the real-world activity this record
    /// belongs to (the primary's own id on the primary); null when it stands alone
    /// </summary>
    public Guid? ClusterId { get; set; }

    /// <summary>The user set the type by hand: it wins over every source in fusion</summary>
    public bool TypeByUser { get; set; }

    /// <summary>Fused values and provenance (on the primary of a fused activity only)</summary>
    public ActivityFusion? Fusion { get; set; }

    public List<TrackPoint> Points { get; set; } = new();
}

/// <summary>One point of an activity's track</summary>
public class TrackPoint
{
    public long Id { get; set; }
    public Guid ActivityId { get; set; }
    public DateTime Time { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double? Elevation { get; set; }
    public int? HeartRate { get; set; }
    /// <summary>m/s</summary>
    public double? Speed { get; set; }
    public int? Cadence { get; set; }
}

/// <summary>A named place</summary>
public class AtlasPlace : ISourced
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = string.Empty;
    public string Source { get; set; } = "manual";
    public string? ExternalId { get; set; }
    public Guid? ImportId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string? Category { get; set; }
    public string? GooglePlaceId { get; set; }
    public string? Notes { get; set; }
    /// <summary>The real-world location it is associated with, e.g. "osm:node/123"</summary>
    public string? LinkedRef { get; set; }
    public string? Website { get; set; }
    public string? Phone { get; set; }
    /// <summary>OSM opening_hours syntax, shown as is</summary>
    public string? OpeningHours { get; set; }
    /// <summary>JSON object: whatever else was found (cuisine, brand, wikipedia…)</summary>
    public string Details { get; set; } = "{}";
    /// <summary>The user named or associated it: imports keep its address and category</summary>
    public bool UserEdited { get; set; }
    /// <summary>pending, auto, ambiguous, none, user (see PlaceIdentity)</summary>
    public string IdentifyState { get; set; } = "pending";
    /// <summary>JSON: the possible identities of an ambiguous place</summary>
    public string IdentifyCandidates { get; set; } = "[]";
    /// <summary>How an automatic identity was found: lookup, nearby</summary>
    public string? IdentifiedFrom { get; set; }
    /// <summary>When the background identification last looked (a "none" is looked at again after 30 days)</summary>
    public DateTime? IdentifyCheckedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Being at a place for a while</summary>
public class PlaceVisit : ISourced
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = string.Empty;
    public string Source { get; set; } = "manual";
    public string? ExternalId { get; set; }
    public Guid? ImportId { get; set; }

    public Guid? PlaceId { get; set; }
    public AtlasPlace? Place { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    /// <summary>The source's confidence (0-1) that this was the place</summary>
    public double? Confidence { get; set; }
}

/// <summary>A health measurement (heart rate, steps…) at a time or over an interval</summary>
public class HealthSample
{
    public long Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    /// <summary>heart_rate, steps, resting_heart_rate, weight, sleep…</summary>
    public string Type { get; set; } = "heart_rate";
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public double Value { get; set; }
    public string? Unit { get; set; }
    public string Source { get; set; } = "manual";
    public Guid? ImportId { get; set; }
}

/// <summary>One import run (a file or a set of files)</summary>
public class ImportRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int Activities { get; set; }
    public int Places { get; set; }
    public int Visits { get; set; }
    public int HealthSamples { get; set; }
}

/// <summary>A personal access token for MCP clients (only its hash is kept)</summary>
public class AccessToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>SHA-256 of the token, hex</summary>
    public string TokenHash { get; set; } = string.Empty;
    /// <summary>First characters, to recognise it in the list</summary>
    public string Prefix { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
}

/// <summary>
/// What fusion made of several records of one real-world activity: the values the
/// primary record lacks, where every value came from, and what the sources disagree on.
/// Recomputed by every fusion run; the source records themselves are never changed.
/// </summary>
public class ActivityFusion
{
    /// <summary>The primary record's id (= the fused activity's id)</summary>
    public Guid ActivityId { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    /// <summary>Type chosen by source priority (may differ from the primary's)</summary>
    public string? Type { get; set; }
    public string? Name { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public double? DistanceM { get; set; }
    /// <summary>Recorded time of an outing of several recordings (their sum); null otherwise</summary>
    public double? DurationS { get; set; }
    /// <summary>The joined track of an outing of several recordings; null otherwise</summary>
    public string? Polyline { get; set; }
    /// <summary>How many recordings the outing is made of (1: one recording)</summary>
    public int SegmentCount { get; set; } = 1;
    /// <summary>The area of an outing of several recordings; null otherwise (the primary's)</summary>
    public double? MinLat { get; set; }
    public double? MinLon { get; set; }
    public double? MaxLat { get; set; }
    public double? MaxLon { get; set; }
    public double? ElevationGainM { get; set; }
    public int? AvgHeartRate { get; set; }
    public int? MaxHeartRate { get; set; }
    /// <summary>Sources of the members, best first, comma separated</summary>
    public string Sources { get; set; } = string.Empty;
    public int MemberCount { get; set; }
    public int ConflictCount { get; set; }
    /// <summary>JSON: field → source that supplied it</summary>
    public string FieldSources { get; set; } = "{}";
    /// <summary>JSON: list of conflicts</summary>
    public string Conflicts { get; set; } = "[]";
    /// <summary>JSON: how each member matches the primary (time and track agreement, confidence)</summary>
    public string Evidence { get; set; } = "[]";
    /// <summary>The weakest automatic member match (0-1); null when the user decided every one</summary>
    public double? MatchConfidence { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A time window with new or changed data, waiting for the fusion service</summary>
public class FusionQueueItem
{
    public long Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    /// <summary>Everything (after a settings change, or on request)</summary>
    public bool Full { get; set; }
    /// <summary>Failed runs so far (each doubles the wait before the next try)</summary>
    public int Attempts { get; set; }
    /// <summary>Not before this time (after a failure)</summary>
    public DateTime? NextAttemptAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>An owner's fusion settings (tolerances per source, match threshold), as JSON</summary>
public class FusionSetting
{
    public string OwnerId { get; set; } = string.Empty;
    public string Json { get; set; } = "{}";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A user's decision about two records: always together, or never together</summary>
public class FusionDecision
{
    public long Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    /// <summary>The smaller of the two record ids (so a pair is stored once)</summary>
    public Guid ActivityA { get; set; }
    public Guid ActivityB { get; set; }
    /// <summary>"together" or "apart"</summary>
    public string Kind { get; set; } = "apart";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A connected account of a data source (Sports Tracker…); never a password</summary>
public class SourceConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = string.Empty;
    /// <summary>sports-tracker</summary>
    public string Provider { get; set; } = string.Empty;
    /// <summary>The user name as entered (shown in the app)</summary>
    public string? Account { get; set; }
    /// <summary>The source's session key, encrypted with ASP.NET Data Protection</summary>
    public string? SecretProtected { get; set; }
    /// <summary>idle, syncing, error, expired</summary>
    public string Status { get; set; } = "idle";
    public DateTime? LastSyncAt { get; set; }
    public string? LastError { get; set; }
    public int ItemsTotal { get; set; }
    public int ItemsDone { get; set; }
    public int ItemsImported { get; set; }
    public Guid? ImportId { get; set; }
    /// <summary>Where the next sync goes on from (OneDrive: the delta link); null to start over</summary>
    public string? Cursor { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A photo or video of a connected library (OneDrive). Only its details are kept;
/// the file stays in the library, thumbnails are fetched from there.
/// </summary>
public class AtlasPhoto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = string.Empty;
    /// <summary>onedrive</summary>
    public string Provider { get; set; } = "onedrive";
    /// <summary>The item's id in the library</summary>
    public string ExternalId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? MimeType { get; set; }
    public bool IsVideo { get; set; }
    public long? Size { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? CameraMake { get; set; }
    public string? CameraModel { get; set; }
    /// <summary>The time as the library gives it (cameras often write their local time, which reads as UTC)</summary>
    public DateTime TakenAtRaw { get; set; }
    /// <summary>Minutes the library's time is ahead of the real (UTC) time; 0 when it is UTC</summary>
    public int ClockOffsetMin { get; set; }
    /// <summary>When it was taken (UTC): TakenAtRaw minus the offset</summary>
    public DateTime TakenAt { get; set; }
    /// <summary>Where the camera says it was taken</summary>
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public double? Altitude { get; set; }
    /// <summary>Where it is shown: the camera's position, else a track or visit at its time</summary>
    public double? MapLat { get; set; }
    public double? MapLon { get; set; }
    /// <summary>gps, track, visit; null when nothing says where</summary>
    public string? PlacedBy { get; set; }
    /// <summary>The activity (fused one) it was taken during</summary>
    public Guid? ActivityId { get; set; }
    /// <summary>The visit it was taken during</summary>
    public Guid? VisitId { get; set; }
    /// <summary>Placement is up to date (cleared when the photo or the data at its time changes)</summary>
    public bool Placed { get; set; }
    /// <summary>Opens the photo in the library</summary>
    public string? WebUrl { get; set; }
    public string? Folder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
