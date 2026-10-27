/**
 * @fileoverview Atlas REST API for the web app (signed-in Google session)
 * @module ContactsApi.WebApi.Controllers
 */
using ContactsApi.Infrastructure.Atlas;
using ContactsApi.WebApi.Models.Responses;
using ContactsApi.WebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace ContactsApi.WebApi.Controllers;

/// <summary>Request to create an access token</summary>
public record CreateTokenRequest(string Name);

/// <summary>Insights for a query, in a time zone</summary>
public record InsightsRequest(AtlasQuery? Query, string? Tz);

/// <summary>Timeline counts for a range, with the map's filters</summary>
public record HistogramRequest(DateTime From, DateTime To, string? Bucket, string? Tz, AtlasQuery? Filter);

/// <summary>
/// Activities, places, visits and health data of the signed-in user, plus
/// imports and the personal access tokens for MCP clients.
/// </summary>
[ApiController]
[Route("api/atlas")]
public class AtlasController : ControllerBase
{
    private readonly IServiceProvider _services;
    private readonly AtlasReadiness _readiness;
    private readonly GoogleTokenService _tokens;
    private readonly IConfiguration _config;

    /// <summary>Creates the controller</summary>
    public AtlasController(IServiceProvider services, AtlasReadiness readiness, GoogleTokenService tokens, IConfiguration config)
    {
        _services = services;
        _readiness = readiness;
        _tokens = tokens;
        _config = config;
    }

    /// <summary>Whether Atlas can serve (database configured and migrated)</summary>
    [HttpGet("status")]
    public IActionResult Status() =>
        Ok(ApiResponse<object>.SuccessResponse(new { configured = _readiness.Configured, ready = _readiness.Ready }));

    /// <summary>Adds or updates a batch of imported items</summary>
    [HttpPost("ingest")]
    [RequestSizeLimit(20_000_000)]
    public Task<IActionResult> Ingest([FromBody] IngestRequest request) =>
        Run((svc, owner) => svc.IngestAsync(owner, request, HttpContext.RequestAborted));

    /// <summary>Activities and visits by time, area, type and text</summary>
    [HttpPost("search")]
    public Task<IActionResult> Search([FromBody] AtlasQuery query) =>
        Run((svc, owner) => svc.SearchAsync(owner, query, HttpContext.RequestAborted));

    /// <summary>Counts per time bucket (timeline)</summary>
    [HttpGet("histogram")]
    public Task<IActionResult> Histogram([FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] string bucket = "day", [FromQuery] string? tz = null) =>
        Run((svc, owner) => svc.HistogramAsync(owner, from, to, bucket, HttpContext.RequestAborted, tz));

    /// <summary>Counts per time bucket of what a search finds (area, types, text)</summary>
    [HttpPost("histogram")]
    public Task<IActionResult> FilteredHistogram([FromBody] HistogramRequest request) =>
        Run((svc, owner) => svc.HistogramAsync(owner, request.From, request.To, request.Bucket ?? "day", HttpContext.RequestAborted,
            request.Tz, request.Filter));

    /// <summary>Statistics, records, places, data quality and proposals for a query</summary>
    [HttpPost("insights")]
    public Task<IActionResult> Insights([FromBody] InsightsRequest request) =>
        Run((svc, owner) => svc.InsightsAsync(owner, request.Query ?? new AtlasQuery(), request.Tz, HttpContext.RequestAborted));

    /// <summary>Totals and imports</summary>
    [HttpGet("summary")]
    public Task<IActionResult> Summary() => Run((svc, owner) => svc.SummaryAsync(owner, HttpContext.RequestAborted));

    /// <summary>An activity with its track and heart rate</summary>
    [HttpGet("activities/{id:guid}")]
    public Task<IActionResult> Activity(Guid id, [FromQuery] int maxPoints = 2000) =>
        Run((svc, owner) => svc.GetActivityAsync(owner, id, maxPoints, HttpContext.RequestAborted));

    /// <summary>Changes an activity</summary>
    [HttpPatch("activities/{id:guid}")]
    public Task<IActionResult> UpdateActivity(Guid id, [FromBody] ActivityPatch patch) =>
        Run((svc, owner) => svc.UpdateActivityAsync(owner, id, patch, HttpContext.RequestAborted));

    /// <summary>Takes a source record out of a fused activity</summary>
    [HttpPost("activities/{id:guid}/split")]
    public Task<IActionResult> Split(Guid id, [FromBody] ActivitySplitRequest request) =>
        Run((svc, owner) => svc.SplitActivityAsync(owner, id, request.MemberId, HttpContext.RequestAborted));

    /// <summary>Joins two activities into one</summary>
    [HttpPost("activities/{id:guid}/merge")]
    public Task<IActionResult> Merge(Guid id, [FromBody] ActivityMergeRequest request) =>
        Run((svc, owner) => svc.MergeActivitiesAsync(owner, id, request.OtherId, HttpContext.RequestAborted));

    /// <summary>Queues a re-fusion of all activities (the fusion service does it)</summary>
    [HttpPost("fuse")]
    public Task<IActionResult> Fuse() => Run(async (svc, owner) =>
    {
        await svc.QueueFusionAsync(owner, null, null, HttpContext.RequestAborted);
        return await StatusOf(svc, owner);
    });

    /// <summary>What the fusion service is doing</summary>
    [HttpGet("fusion/status")]
    public Task<IActionResult> FusionStatus() => Run((svc, owner) => StatusOf(svc, owner));

    /// <summary>Tolerances per source and the match threshold</summary>
    [HttpGet("fusion/settings")]
    public Task<IActionResult> FusionSettings() => Run((svc, owner) => svc.GetFusionSettingsAsync(owner, HttpContext.RequestAborted));

    /// <summary>Saves the tolerances and threshold; everything is re-fused</summary>
    [HttpPut("fusion/settings")]
    public Task<IActionResult> SaveFusionSettings([FromBody] FusionSettingsDto settings) =>
        Run((svc, owner) => svc.SaveFusionSettingsAsync(owner, settings, HttpContext.RequestAborted));

    /// <summary>Back to the built-in tolerances; everything is re-fused</summary>
    [HttpDelete("fusion/settings")]
    public Task<IActionResult> ResetFusionSettings() => Run((svc, owner) => svc.ResetFusionSettingsAsync(owner, HttpContext.RequestAborted));

    private async Task<FusionStatus> StatusOf(AtlasService svc, string owner) =>
        HttpContext.RequestServices.GetRequiredService<FusionStatusBoard>()
            .For(owner, await svc.PendingFusionAsync(owner, HttpContext.RequestAborted));

    /// <summary>Connected sources</summary>
    [HttpGet("connections")]
    public Task<IActionResult> Connections() =>
        RunWith<AtlasConnectionService, List<SourceConnectionInfo>>((svc, owner) => svc.ListAsync(owner, HttpContext.RequestAborted));

    /// <summary>Connects Sports Tracker and starts importing (the password is not stored)</summary>
    [HttpPost("connections/sports-tracker")]
    public Task<IActionResult> ConnectSportsTracker([FromBody] ConnectSportsTrackerRequest request) =>
        RunWith<AtlasConnectionService, SourceConnectionInfo>((svc, owner) => svc.ConnectSportsTrackerAsync(owner, request, HttpContext.RequestAborted));

    /// <summary>Syncs Sports Tracker again</summary>
    [HttpPost("connections/sports-tracker/sync")]
    public Task<IActionResult> SyncSportsTracker() =>
        RunWith<AtlasConnectionService, SourceConnectionInfo>((svc, owner) => svc.SyncAsync(owner, HttpContext.RequestAborted));

    /// <summary>Disconnects Sports Tracker (imported data stays)</summary>
    [HttpDelete("connections/sports-tracker")]
    public Task<IActionResult> DisconnectSportsTracker() =>
        RunWith<AtlasConnectionService, object>(async (svc, owner) => new { removed = await svc.DisconnectAsync(owner, HttpContext.RequestAborted) });

    /// <summary>OneDrive photos: whether it can be connected, the connection, how many photos</summary>
    [HttpGet("connections/onedrive")]
    public Task<IActionResult> OneDrive() =>
        RunWith<OneDrivePhotoService, OneDriveStatus>((svc, owner) => svc.StatusAsync(owner, HttpContext.RequestAborted));

    /// <summary>Where the browser signs in to Microsoft to connect OneDrive</summary>
    [HttpPost("connections/onedrive/start")]
    public Task<IActionResult> StartOneDrive() =>
        RunWith<OneDrivePhotoService, object>((svc, _) => Task.FromResult<object>(new { url = svc.StartUrl(HttpContext.Session) }));

    /// <summary>
    /// Microsoft comes back here after the sign-in; the browser then goes back to
    /// Atlas with ?onedrive=connected, or ?onedrive=error&amp;reason=…
    /// </summary>
    [HttpGet("connections/onedrive/callback")]
    public async Task<IActionResult> OneDriveCallback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error)
    {
        IActionResult Back(string result, string? reason = null) =>
            Redirect((_config["Frontend:BaseUrl"] ?? "http://localhost:3002").TrimEnd('/') + "/atlas?onedrive=" + result
                     + (reason == null ? "" : "&reason=" + Uri.EscapeDataString(reason)));

        if (await _tokens.GetAccessTokenAsync(HttpContext) == null || !_readiness.Ready)
        {
            return Back("error", "session");
        }

        var expected = HttpContext.Session.GetString(OneDrivePhotoService.StateKey);
        HttpContext.Session.Remove(OneDrivePhotoService.StateKey);
        if (error != null)
        {
            return Back("error", error == "access_denied" ? "denied" : "microsoft");
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(expected) || state != expected)
        {
            return Back("error", "state");
        }

        try
        {
            var owner = HttpContext.Session.GetString(GoogleTokenService.UserIdKey)!;
            await HttpContext.RequestServices.GetRequiredService<OneDrivePhotoService>().CompleteAsync(owner, code, HttpContext.RequestAborted);
            return Back("connected");
        }
        catch (OneDriveException)
        {
            return Back("error", "microsoft");
        }
    }

    /// <summary>Syncs OneDrive again (what changed since the last sync)</summary>
    [HttpPost("connections/onedrive/sync")]
    public Task<IActionResult> SyncOneDrive() =>
        RunWith<OneDrivePhotoService, OneDriveStatus>((svc, owner) => svc.SyncAsync(owner, HttpContext.RequestAborted));

    /// <summary>Disconnects OneDrive and forgets its photos' details (the photos stay in OneDrive)</summary>
    [HttpDelete("connections/onedrive")]
    public Task<IActionResult> DisconnectOneDrive() =>
        RunWith<OneDrivePhotoService, object>(async (svc, owner) => new { removed = await svc.DisconnectAsync(owner, HttpContext.RequestAborted) });

    /// <summary>Photos taken during an activity</summary>
    [HttpGet("activities/{id:guid}/photos")]
    public Task<IActionResult> ActivityPhotos(Guid id) =>
        Run((svc, owner) => svc.ActivityPhotosAsync(owner, id, HttpContext.RequestAborted));

    /// <summary>Photos taken during visits to a place</summary>
    [HttpGet("places/{id:guid}/photos")]
    public Task<IActionResult> PlacePhotos(Guid id) =>
        Run((svc, owner) => svc.PlacePhotosAsync(owner, id, HttpContext.RequestAborted));

    /// <summary>Photos for the map (time, area, activity types)</summary>
    [HttpPost("photos/map")]
    public Task<IActionResult> MapPhotos([FromBody] AtlasQuery query) =>
        Run((svc, owner) => svc.MapPhotosAsync(owner, query ?? new AtlasQuery(), HttpContext.RequestAborted));

    /// <summary>A photo's thumbnail (small, medium, large), fetched from OneDrive</summary>
    [HttpGet("photos/{id:guid}/thumbnail")]
    public async Task<IActionResult> PhotoThumbnail(Guid id, [FromQuery] string size = "medium")
    {
        if (size is not ("small" or "medium" or "large"))
        {
            return BadRequest(ApiResponse<object>.ErrorResponse("INVALID_INPUT", "size is small, medium or large"));
        }

        if (await _tokens.GetAccessTokenAsync(HttpContext) == null)
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
        }

        if (!_readiness.Ready)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ApiResponse<object>.ErrorResponse("ATLAS_NOT_READY", "Atlas is starting."));
        }

        var owner = HttpContext.Session.GetString(GoogleTokenService.UserIdKey)!;
        try
        {
            var thumb = await HttpContext.RequestServices.GetRequiredService<OneDrivePhotoService>()
                .ThumbnailAsync(owner, id, size, HttpContext.RequestAborted);
            if (thumb == null)
            {
                return NotFound(ApiResponse<object>.ErrorResponse("NOT_FOUND", "OneDrive has no thumbnail for it."));
            }

            // The photo does not change under its id; the browser keeps it a day
            Response.Headers.CacheControl = "private, max-age=86400";
            return File(thumb.Value.Bytes, thumb.Value.ContentType);
        }
        catch (AtlasNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.ErrorResponse("NOT_FOUND", ex.Message));
        }
        catch (OneDriveException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, ApiResponse<object>.ErrorResponse(ex.Code, ex.Message));
        }
    }

    /// <summary>Deletes an activity</summary>
    [HttpDelete("activities/{id:guid}")]
    public Task<IActionResult> DeleteActivity(Guid id) =>
        Run(async (svc, owner) => { await svc.DeleteActivityAsync(owner, id, HttpContext.RequestAborted); return new { deleted = id }; });

    /// <summary>Health values in a range</summary>
    [HttpGet("health")]
    public Task<IActionResult> Health([FromQuery] string type, [FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] int maxPoints = 1500) =>
        Run((svc, owner) => svc.HealthAsync(owner, type, from, to, maxPoints, HttpContext.RequestAborted));

    /// <summary>Visits and activities near a point</summary>
    [HttpGet("near")]
    public Task<IActionResult> Near([FromQuery] double lat, [FromQuery] double lon, [FromQuery] double radiusM = 500,
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null) =>
        Run((svc, owner) => svc.NearAsync(owner, lat, lon, radiusM, from, to, HttpContext.RequestAborted));

    /// <summary>Creates a place</summary>
    [HttpPost("places")]
    public Task<IActionResult> CreatePlace([FromBody] PlaceInput place) =>
        Run((svc, owner) => svc.CreatePlaceAsync(owner, place, HttpContext.RequestAborted));

    /// <summary>A place with everything known about it and its visits</summary>
    [HttpGet("places/{id:guid}")]
    public Task<IActionResult> GetPlace(Guid id) =>
        Run((svc, owner) => svc.GetPlaceAsync(owner, id, HttpContext.RequestAborted));

    /// <summary>What is at a point: the address and named places around it (OpenStreetMap)</summary>
    [HttpGet("lookup")]
    public Task<IActionResult> Lookup([FromQuery] double? lat, [FromQuery] double? lon, [FromQuery] double radius = 80) =>
        RunWith<GeoLookupClient, LocationLookup>((geo, _) => lat.HasValue && lon.HasValue
            ? geo.LookupAsync(lat.Value, lon.Value, radius, HttpContext.RequestAborted)
            : throw new ArgumentException("lat and lon are required"));

    /// <summary>Says what a visit's place is (a found location, or typed)</summary>
    [HttpPost("visits/{id:guid}/place")]
    public Task<IActionResult> AssociateVisitPlace(Guid id, [FromBody] PlaceAssociation association) =>
        Run((svc, owner) => svc.AssociateVisitPlaceAsync(owner, id, association, HttpContext.RequestAborted,
            HttpContext.RequestServices.GetRequiredService<GeoLookupClient>()));

    /// <summary>The user accepts a place's automatic identity</summary>
    [HttpPost("places/{id:guid}/confirm")]
    public Task<IActionResult> ConfirmPlace(Guid id) =>
        Run((svc, owner) => svc.ConfirmPlaceAsync(owner, id, HttpContext.RequestAborted));

    /// <summary>Gives other places the identity of this one</summary>
    [HttpPost("places/{id:guid}/apply-to")]
    public Task<IActionResult> ApplyIdentity(Guid id, [FromBody] ApplyIdentityRequest request) =>
        Run(async (svc, owner) => new { applied = await svc.ApplyIdentityAsync(owner, id, request.PlaceIds ?? new List<Guid>(), HttpContext.RequestAborted) });

    /// <summary>Tracks and visits for the density map</summary>
    [HttpPost("density")]
    public Task<IActionResult> Density([FromBody] AtlasQuery query) =>
        Run((svc, owner) => svc.DensityAsync(owner, query ?? new AtlasQuery(), HttpContext.RequestAborted));

    /// <summary>Changes a place</summary>
    [HttpPatch("places/{id:guid}")]
    public Task<IActionResult> UpdatePlace(Guid id, [FromBody] PlacePatch patch) =>
        Run((svc, owner) => svc.UpdatePlaceAsync(owner, id, patch, HttpContext.RequestAborted));

    /// <summary>Deletes a place</summary>
    [HttpDelete("places/{id:guid}")]
    public Task<IActionResult> DeletePlace(Guid id) =>
        Run(async (svc, owner) => { await svc.DeletePlaceAsync(owner, id, HttpContext.RequestAborted); return new { deleted = id }; });

    /// <summary>Deletes a visit</summary>
    [HttpDelete("visits/{id:guid}")]
    public Task<IActionResult> DeleteVisit(Guid id) =>
        Run(async (svc, owner) => { await svc.DeleteVisitAsync(owner, id, HttpContext.RequestAborted); return new { deleted = id }; });

    /// <summary>Deletes an import and what it brought in</summary>
    [HttpDelete("imports/{id:guid}")]
    public Task<IActionResult> DeleteImport(Guid id) =>
        Run(async (svc, owner) => new { removed = await svc.DeleteImportAsync(owner, id, HttpContext.RequestAborted) });

    /// <summary>The user's MCP access tokens</summary>
    [HttpGet("tokens")]
    public Task<IActionResult> Tokens() =>
        RunWith<AccessTokenService, List<AccessTokenInfo>>((svc, owner) => svc.ListAsync(owner));

    /// <summary>Creates an MCP access token (the token is returned only now)</summary>
    [HttpPost("tokens")]
    public Task<IActionResult> CreateToken([FromBody] CreateTokenRequest request) =>
        RunWith<AccessTokenService, object>(async (svc, owner) =>
        {
            var (token, info) = await svc.CreateAsync(owner, request.Name);
            return new { token, info };
        });

    /// <summary>Revokes an MCP access token</summary>
    [HttpDelete("tokens/{id:guid}")]
    public Task<IActionResult> RevokeToken(Guid id) =>
        RunWith<AccessTokenService, object>(async (svc, owner) => new { revoked = await svc.RevokeAsync(owner, id) });

    private Task<IActionResult> Run<T>(Func<AtlasService, string, Task<T>> action) => RunWith(action);

    /// <summary>Signed in, database ready, then the action; not found → 404</summary>
    private async Task<IActionResult> RunWith<TService, T>(Func<TService, string, Task<T>> action) where TService : notnull
    {
        if (await _tokens.GetAccessTokenAsync(HttpContext) == null)
        {
            return Unauthorized(ApiResponse<object>.ErrorResponse("SESSION_EXPIRED", "Your Google session expired. Please sign in again."));
        }

        if (!_readiness.Ready)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ApiResponse<object>.ErrorResponse(
                "ATLAS_NOT_READY", _readiness.Configured ? "The Atlas database is starting. Try again in a minute." : "Atlas is not configured."));
        }

        var owner = HttpContext.Session.GetString(GoogleTokenService.UserIdKey)!;
        try
        {
            var service = HttpContext.RequestServices.GetRequiredService<TService>();
            return Ok(ApiResponse<T>.SuccessResponse(await action(service, owner)));
        }
        catch (AtlasNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.ErrorResponse("NOT_FOUND", ex.Message));
        }
        catch (SportsTrackerException ex)
        {
            return BadRequest(ApiResponse<object>.ErrorResponse(ex.Code, ex.Message));
        }
        catch (OneDriveException ex)
        {
            return BadRequest(ApiResponse<object>.ErrorResponse(ex.Code, ex.Message));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<object>.ErrorResponse("INVALID_INPUT", ex.Message));
        }
    }
}
