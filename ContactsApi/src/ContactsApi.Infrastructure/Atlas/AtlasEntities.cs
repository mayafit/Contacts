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
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

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
