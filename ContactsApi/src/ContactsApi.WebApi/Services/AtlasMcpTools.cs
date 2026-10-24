/**
 * @fileoverview Atlas MCP tools: the same service as the REST API, for AI clients
 * @module ContactsApi.WebApi.Services
 */
using System.ComponentModel;
using ContactsApi.Infrastructure.Atlas;
using ModelContextProtocol.Server;

namespace ContactsApi.WebApi.Services;

/// <summary>
/// MCP tools over the owner's Atlas data. The owner comes from the personal
/// access token checked by the /api/mcp middleware (HttpContext.Items).
/// </summary>
[McpServerToolType]
public class AtlasMcpTools
{
    /// <summary>HttpContext item holding the token's owner</summary>
    public const string OwnerItem = "AtlasOwner";

    private readonly AtlasService _atlas;
    private readonly IHttpContextAccessor _http;

    /// <summary>Creates the tools</summary>
    public AtlasMcpTools(AtlasService atlas, IHttpContextAccessor http)
    {
        _atlas = atlas;
        _http = http;
    }

    private string Owner => _http.HttpContext?.Items[OwnerItem] as string
                            ?? throw new UnauthorizedAccessException("No access token");

    [McpServerTool(Name = "atlas_insights"), Description("Insights over the user's fused data for a query (all filters optional): totals with the change against the period before, per activity type (distance, time, climb, speed, heart rate), trend, weekdays and hours, personal records, active streaks, top places and new places, travel (flights, days away from home), data quality (heart-rate coverage, recorded vs inferred, conflicts), proposals (rhythm, training load, places to name, data to add), weekly training load (hours, km, minutes × intensity), time in heart-rate zones and per-sport trends of speed and heart rate (last third vs first third of the period).")]
    public Task<Insights> Insights(
        [Description("Start of the time range, ISO 8601")] DateTime? from = null,
        [Description("End of the time range, ISO 8601")] DateTime? to = null,
        [Description("Bounding box south latitude")] double? minLat = null,
        [Description("Bounding box west longitude")] double? minLon = null,
        [Description("Bounding box north latitude")] double? maxLat = null,
        [Description("Bounding box east longitude")] double? maxLon = null,
        [Description("Activity types: ride, hike, run, walk, drive, flight, transit, ski, swim, other")] string[]? types = null,
        [Description("Text in activity names/notes or place names")] string? text = null,
        [Description("IANA time zone for weekdays and hours, e.g. Asia/Jerusalem")] string? timeZone = null) =>
        _atlas.InsightsAsync(Owner, new AtlasQuery
        {
            From = from, To = to, MinLat = minLat, MinLon = minLon, MaxLat = maxLat, MaxLon = maxLon, Types = types?.ToList(), Text = text
        }, timeZone);

    [McpServerTool(Name = "atlas_merge_activities"), Description("Join two activities that are the same real-world activity (the user's decision is remembered). Returns the merged activity.")]
    public Task<ActivityDetail> Merge(
        [Description("Activity id")] Guid id,
        [Description("Id of the activity to merge with it")] Guid otherId) =>
        _atlas.MergeActivitiesAsync(Owner, id, otherId);

    [McpServerTool(Name = "atlas_split_activity"), Description("Take one source record out of a fused activity when it is not the same activity (remembered). Returns the activity without it.")]
    public Task<ActivityDetail> Split(
        [Description("Fused activity id")] Guid id,
        [Description("Id of the source record (from members) to take out")] Guid memberId) =>
        _atlas.SplitActivityAsync(Owner, id, memberId);

    [McpServerTool(Name = "atlas_fuse"), Description("Re-run fusion over all activities now (normally the fusion service does it automatically after every import). Returns what it did.")]
    public Task<FuseResult> Fuse() => _atlas.FuseAsync(Owner);

    [McpServerTool(Name = "atlas_fusion_settings"), Description("How sources are combined: each source's time and location tolerance (how far its times and positions can be off) and the confidence an automatic match needs.")]
    public Task<FusionSettingsDto> FusionSettings() => _atlas.GetFusionSettingsAsync(Owner);

    [McpServerTool(Name = "atlas_summary"), Description("Totals of the user's activities, places, visits and health samples, the date range they cover, distance per activity type, and the imports.")]
    public Task<AtlasSummary> Summary() => _atlas.SummaryAsync(Owner);

    [McpServerTool(Name = "atlas_search"), Description("Search activities (rides, hikes, runs, walks, drives, flights…) and place visits. All filters are optional and combine: time range, bounding box, activity types, text in names/places.")]
    public Task<SearchResult> Search(
        [Description("Start of the time range, ISO 8601")] DateTime? from = null,
        [Description("End of the time range, ISO 8601")] DateTime? to = null,
        [Description("Bounding box south latitude")] double? minLat = null,
        [Description("Bounding box west longitude")] double? minLon = null,
        [Description("Bounding box north latitude")] double? maxLat = null,
        [Description("Bounding box east longitude")] double? maxLon = null,
        [Description("Activity types: ride, hike, run, walk, drive, flight, transit, ski, swim, other")] string[]? types = null,
        [Description("Text in activity names/notes or place names/addresses")] string? text = null,
        [Description("Include activities")] bool activities = true,
        [Description("Include visits")] bool visits = true,
        [Description("Maximum results of each kind (default 100)")] int limit = 100) =>
        _atlas.SearchAsync(Owner, new AtlasQuery
        {
            From = from, To = to, MinLat = minLat, MinLon = minLon, MaxLat = maxLat, MaxLon = maxLon,
            Types = types?.ToList(), Text = text, Activities = activities, Visits = visits, Limit = limit
        });

    [McpServerTool(Name = "atlas_get_activity"), Description("One activity with its track (time, position, elevation, heart rate, speed) and its heart rate series (from the track, or from health samples recorded at the same time). Activities are fused from several sources (Sports Tracker, Google Timeline, Google Fit, Fitbit…): the result lists the source records (members), which source supplied each value (fieldSources), what they disagree on (conflicts), and the places visited at the start, end or on the way. Points carry distance from the start (d), speed and cadence. The analysis has moving vs stopped time, splits (per km for runs/walks/hikes, per 5 km for rides) with pace, heart rate and climb, time in heart-rate zones (from the user's own max), aerobic decoupling (drift), best efforts with their all-time rank, and the comparison with the same route and the same type over the last year.")]
    public Task<ActivityDetail> GetActivity(
        [Description("Activity id")] Guid id,
        [Description("Maximum track points returned (default 500)")] int maxPoints = 500) =>
        _atlas.GetActivityAsync(Owner, id, maxPoints);

    [McpServerTool(Name = "atlas_health"), Description("Health values of one type over a time range (heart_rate, steps, resting_heart_rate…), averaged into buckets with min/max when the range is long.")]
    public Task<List<SeriesPoint>> Health(
        [Description("Type, e.g. heart_rate")] string type,
        [Description("Start, ISO 8601")] DateTime from,
        [Description("End, ISO 8601")] DateTime to,
        [Description("Maximum points (default 300)")] int maxPoints = 300) =>
        _atlas.HealthAsync(Owner, type, from, to, maxPoints);

    [McpServerTool(Name = "atlas_near"), Description("Visits within a radius of a point, and activities whose track passed within it, optionally in a time range.")]
    public Task<NearResult> Near(
        [Description("Latitude")] double lat,
        [Description("Longitude")] double lon,
        [Description("Radius in metres (default 500)")] double radiusM = 500,
        [Description("Start, ISO 8601")] DateTime? from = null,
        [Description("End, ISO 8601")] DateTime? to = null) =>
        _atlas.NearAsync(Owner, lat, lon, radiusM, from, to);

    [McpServerTool(Name = "atlas_update_activity"), Description("Change an activity's name, type or notes.")]
    public Task<ActivitySummary> UpdateActivity(
        [Description("Activity id")] Guid id,
        [Description("New name")] string? name = null,
        [Description("New type")] string? type = null,
        [Description("New notes")] string? notes = null) =>
        _atlas.UpdateActivityAsync(Owner, id, new ActivityPatch(name, type, notes));

    [McpServerTool(Name = "atlas_delete_activity"), Description("Delete an activity and its track. Ask the user before deleting.")]
    public async Task<string> DeleteActivity([Description("Activity id")] Guid id)
    {
        await _atlas.DeleteActivityAsync(Owner, id);
        return $"Deleted activity {id}";
    }

    [McpServerTool(Name = "atlas_create_activity"), Description("Create an activity, optionally with a track.")]
    public Task<IngestResult> CreateActivity(
        [Description("Type: ride, hike, run, walk, drive, flight, transit, ski, swim, other")] string type,
        [Description("Start, ISO 8601")] DateTime start,
        [Description("End, ISO 8601")] DateTime end,
        [Description("Name")] string? name = null,
        [Description("Notes")] string? notes = null,
        [Description("Distance in metres")] double? distanceM = null,
        [Description("Track points")] List<PointInput>? points = null) =>
        _atlas.IngestAsync(Owner, new IngestRequest
        {
            Source = "manual",
            Activities = { new ActivityInput($"manual:{Guid.NewGuid()}", type, name, start, end, distanceM, null, null, notes, points) }
        });

    [McpServerTool(Name = "atlas_create_place"), Description("Create a named place.")]
    public Task<AtlasPlace> CreatePlace(
        [Description("Name")] string name,
        [Description("Latitude")] double lat,
        [Description("Longitude")] double lon,
        [Description("Address")] string? address = null,
        [Description("Category")] string? category = null) =>
        _atlas.CreatePlaceAsync(Owner, new PlaceInput(null, name, lat, lon, address, category));

    [McpServerTool(Name = "atlas_update_place"), Description("Change a place's name, address, category, notes or position.")]
    public Task<AtlasPlace> UpdatePlace(
        [Description("Place id")] Guid id,
        string? name = null, string? address = null, string? category = null, string? notes = null, double? lat = null, double? lon = null) =>
        _atlas.UpdatePlaceAsync(Owner, id, new PlacePatch(name, address, category, notes, lat, lon));

    [McpServerTool(Name = "atlas_delete_place"), Description("Delete a place (its visits stay, without the place). Ask the user first.")]
    public async Task<string> DeletePlace([Description("Place id")] Guid id)
    {
        await _atlas.DeletePlaceAsync(Owner, id);
        return $"Deleted place {id}";
    }

    [McpServerTool(Name = "atlas_delete_visit"), Description("Delete a visit. Ask the user first.")]
    public async Task<string> DeleteVisit([Description("Visit id")] Guid id)
    {
        await _atlas.DeleteVisitAsync(Owner, id);
        return $"Deleted visit {id}";
    }

    [McpServerTool(Name = "atlas_add_health"), Description("Add health samples (e.g. heart_rate in bpm, weight in kg).")]
    public Task<IngestResult> AddHealth([Description("Samples")] List<HealthInput> samples) =>
        _atlas.IngestAsync(Owner, new IngestRequest { Source = "manual", Health = samples });

    [McpServerTool(Name = "atlas_delete_import"), Description("Delete an import and everything it brought in. Ask the user first.")]
    public async Task<string> DeleteImport([Description("Import id (from atlas_summary)")] Guid id) =>
        $"Removed {await _atlas.DeleteImportAsync(Owner, id)} items";
}
