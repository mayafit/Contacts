/**
 * @fileoverview The service's API, /internal/v1 (AD-33 rule 4): cluster-only, and only
 * the backend's token (Routes__BackendToken) is accepted
 * @module EchoRoutes.Worker.Api
 */
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker.Matching;
using EchoRoutes.Worker.Scheduling;
using EchoRoutes.Worker.Valhalla;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EchoRoutes.Worker.Api;

/// <summary>The response envelope (the backend's ApiResponse shape)</summary>
public record ApiEnvelope(bool Success, object? Data, ApiError? Error)
{
    /// <summary>A success</summary>
    public static IResult Ok(object data, int status = StatusCodes.Status200OK) =>
        Results.Json(new ApiEnvelope(true, data, null), statusCode: status);

    /// <summary>An error</summary>
    public static IResult Fail(int status, string code, string message) =>
        Results.Json(new ApiEnvelope(false, null, new ApiError(code, message, DateTime.UtcNow)), statusCode: status);
}

/// <summary>An error of the envelope</summary>
public record ApiError(string Code, string Message, DateTime Timestamp);

/// <summary>Queue an assessment</summary>
/// <param name="Interactive">Started by the Owner from the UI or MCP (≤ 50 ids makes it interactive)</param>
/// <param name="Force">improve only: skip the "needs improvement" gate; never overrides a reject decision</param>
public record AssessRequest(string? OwnerId, JobScope? Scope, bool? Interactive, bool? Force = null);

/// <summary>New fused data for an Owner</summary>
public record WakeRequest(string? OwnerId);

/// <summary>Rebuild one record on demand (AD-38)</summary>
/// <param name="Method">similar or waypoints</param>
/// <param name="Waypoints">waypoints only: [[lat, lon]…] in the order marked</param>
public record RebuildRequest(string? OwnerId, Guid? ActivityId, string? Method, List<double[]>? Waypoints);

/// <summary>Use a rebuild's suggestion</summary>
public record ApplyRequest(string? OwnerId, Guid? JobId, Guid? ActivityId);

/// <summary>The /internal/v1 endpoints and their token check</summary>
public static class InternalApi
{
    /// <summary>Most ids of one job (the backend resolves a query into at most this many)</summary>
    public const int JobIdsMax = 2000;

    /// <summary>Requires the backend's token on every /internal path</summary>
    public static IApplicationBuilder UseBackendToken(this IApplicationBuilder app) =>
        app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/internal"), inner => inner.Use(async (ctx, next) =>
        {
            var expected = ctx.RequestServices.GetRequiredService<IOptions<RoutesOptions>>().Value.BackendToken;
            if (!Accepts(ctx.Request.Headers.Authorization.ToString(), expected))
            {
                await ApiEnvelope.Fail(StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "The backend's service token is required.")
                    .ExecuteAsync(ctx);
                return;
            }

            await next();
        }));

    /// <summary>Whether an Authorization header carries the backend's token (constant time)</summary>
    public static bool Accepts(string? authorization, string? expected)
    {
        if (string.IsNullOrEmpty(expected) || authorization == null
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..].Trim())),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }

    /// <summary>Maps /internal/v1/*</summary>
    public static void MapInternalApi(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/internal/v1");
        v1.MapPost("/assess", (AssessRequest request, RoutesDbContext db, WakeSignal wake, CancellationToken ct) =>
            QueueAsync(RouteJobKinds.Assess, request, db, wake, ct));
        // Assessment and matching; force skips the "needs improvement" gate, never a reject
        v1.MapPost("/improve", (AssessRequest request, RoutesDbContext db, WakeSignal wake, CancellationToken ct) =>
            QueueAsync(RouteJobKinds.Improve, request, db, wake, ct));
        v1.MapPost("/rebuild", async (RebuildRequest request, RoutesDbContext db, WakeSignal wake, CancellationToken ct) =>
        {
            if (!ValidOwner(request.OwnerId) || request.ActivityId is not { } id || id == Guid.Empty)
            {
                return ApiEnvelope.Fail(StatusCodes.Status400BadRequest, "INVALID_INPUT", "ownerId and activityId are required.");
            }

            var method = request.Method?.Trim().ToLowerInvariant();
            if (method is not (RebuildMethods.Similar or RebuildMethods.Waypoints))
            {
                return ApiEnvelope.Fail(StatusCodes.Status400BadRequest, "INVALID_INPUT", "method is similar or waypoints.");
            }

            var marks = request.Waypoints ?? new List<double[]>();
            if (method == RebuildMethods.Waypoints && (marks.Count == 0 || marks.Count > Waypoints.Max
                    || marks.Any(p => p.Length != 2 || !double.IsFinite(p[0]) || !double.IsFinite(p[1]) || Math.Abs(p[0]) > 90 || Math.Abs(p[1]) > 180)))
            {
                return ApiEnvelope.Fail(StatusCodes.Status400BadRequest, "INVALID_INPUT", $"Mark 1 to {Waypoints.Max} points as [lat, lon].");
            }

            var job = new RouteJob
            {
                OwnerId = request.OwnerId!,
                Kind = RouteJobKinds.Rebuild,
                Priority = RouteJobPriorities.Interactive,
                Scope = JsonSerializer.Serialize(new JobScope(new List<Guid> { id }, null, Method: method,
                    Waypoints: method == RebuildMethods.Waypoints ? marks : null), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Total = 1
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync(ct);
            wake.Interactive();
            return ApiEnvelope.Ok(new { jobId = job.Id, job.Priority }, StatusCodes.Status202Accepted);
        });
        v1.MapPost("/apply", async (ApplyRequest request, RebuildRunner rebuild, CancellationToken ct) =>
        {
            if (!ValidOwner(request.OwnerId) || request.JobId is not { } jobId || request.ActivityId is not { } activityId)
            {
                return ApiEnvelope.Fail(StatusCodes.Status400BadRequest, "INVALID_INPUT", "ownerId, jobId and activityId are required.");
            }

            var (suggestion, conflict) = await rebuild.ApplyAsync(request.OwnerId!, jobId, activityId, ct);
            return conflict switch
            {
                null => ApiEnvelope.Ok(new
                {
                    suggestion!.ActivityId, suggestion.SourceKey, suggestion.Method, suggestion.Status, suggestion.Confidence
                }),
                "not_found" => ApiEnvelope.Fail(StatusCodes.Status404NotFound, "NOT_FOUND", "Rebuild not found."),
                _ => ApiEnvelope.Fail(StatusCodes.Status409Conflict, "CONFLICT", conflict)
            };
        });
        v1.MapPost("/wake", (WakeRequest request, WakeSignal wake) =>
        {
            if (!ValidOwner(request.OwnerId))
            {
                return ApiEnvelope.Fail(StatusCodes.Status400BadRequest, "INVALID_INPUT", "ownerId is required.");
            }

            wake.Wake(request.OwnerId!);
            return Results.NoContent();
        });
        v1.MapGet("/jobs/{id:guid}", async (Guid id, string? ownerId, RoutesDbContext db, CancellationToken ct) =>
        {
            if (!ValidOwner(ownerId))
            {
                return ApiEnvelope.Fail(StatusCodes.Status400BadRequest, "INVALID_INPUT", "ownerId is required.");
            }

            // Another Owner's job is simply not found
            var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.OwnerId == ownerId && j.Id == id, ct);
            return job == null
                ? ApiEnvelope.Fail(StatusCodes.Status404NotFound, "NOT_FOUND", "Job not found.")
                : ApiEnvelope.Ok(new
                {
                    job.Id, job.Kind, job.Priority, job.State, job.Total, job.Done,
                    Counts = JsonDocument.Parse(job.Counts).RootElement, job.LastError, job.CreatedAt, job.StartedAt, job.FinishedAt,
                    // A rebuild's suggestion (AD-38)
                    Result = job.Result == null ? (JsonElement?)null : JsonDocument.Parse(job.Result).RootElement
                });
        });
        v1.MapGet("/status", async (string? ownerId, RoutesDbContext db, MatchRunner matcher, GraphInfoSource graph, RegionCoverage coverage,
            IValhallaClient valhalla, EngineBreaker breaker, IOptions<RoutesOptions> options, CancellationToken ct) =>
        {
            if (!ValidOwner(ownerId))
            {
                return ApiEnvelope.Fail(StatusCodes.Status400BadRequest, "INVALID_INPUT", "ownerId is required.");
            }

            var status = await StatusAsync(ownerId!, db, matcher, graph, coverage, valhalla, breaker, options.Value, ct);
            return ApiEnvelope.Ok(status);
        });
    }

    private static async Task<IResult> QueueAsync(string kind, AssessRequest request, RoutesDbContext db, WakeSignal wake, CancellationToken ct)
    {
        if (!ValidOwner(request.OwnerId))
        {
            return ApiEnvelope.Fail(StatusCodes.Status400BadRequest, "INVALID_INPUT", "ownerId is required.");
        }

        var ids = request.Scope?.ActivityIds?.Distinct().ToList();
        var all = request.Scope?.All == true;
        var range = request.Scope?.From != null || request.Scope?.To != null;
        var forms = (ids is { Count: > 0 } ? 1 : 0) + (all ? 1 : 0) + (range ? 1 : 0);
        if (forms != 1 || ids?.Count > JobIdsMax
            || (range && (request.Scope!.From == null || request.Scope.To == null || request.Scope.From >= request.Scope.To)))
        {
            return ApiEnvelope.Fail(StatusCodes.Status400BadRequest, "INVALID_INPUT",
                $"The scope is one of activityIds (1 to {JobIdsMax}), from and to (from before to), or all.");
        }

        var interactive = request.Interactive == true && ids is { Count: <= RoutesScheduler.InteractiveBatch };
        var scope = new JobScope(ids, all ? true : null,
            range ? DateTime.SpecifyKind(request.Scope!.From!.Value, DateTimeKind.Utc) : null,
            range ? DateTime.SpecifyKind(request.Scope!.To!.Value, DateTimeKind.Utc) : null,
            kind == RouteJobKinds.Improve && request.Force == true ? true : null);
        var job = new RouteJob
        {
            OwnerId = request.OwnerId!,
            Kind = kind,
            Priority = interactive ? RouteJobPriorities.Interactive : RouteJobPriorities.Batch,
            Scope = JsonSerializer.Serialize(scope, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Total = ids?.Count ?? 0
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);
        if (interactive)
        {
            wake.Interactive();
        }
        else
        {
            wake.Poke();
        }

        return ApiEnvelope.Ok(new { jobId = job.Id, job.Priority }, StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// The Owner's route work (AD-33 rule 4): jobs queued per priority and running, the
    /// records waiting for a match, an ETA, the engine's health and the breaker's state,
    /// the graph and the regions. Polling it is never interactive load.
    /// </summary>
    public static async Task<object> StatusAsync(string ownerId, RoutesDbContext db, MatchRunner matcher, GraphInfoSource graph,
        RegionCoverage coverage, IValhallaClient valhalla, EngineBreaker breaker, RoutesOptions options, CancellationToken ct)
    {
        var jobs = await db.Jobs.AsNoTracking()
            .Where(j => j.OwnerId == ownerId && (j.State == RouteJobStates.Queued || j.State == RouteJobStates.Running))
            .Select(j => new { j.State, j.Priority, j.Kind, j.Total, j.Done, j.NextAttemptAt })
            .ToListAsync(ct);
        var cursor = await db.Cursors.AsNoTracking().FirstOrDefaultAsync(c => c.OwnerId == ownerId, ct);
        var waiting = await matcher.PendingCountAsync(ownerId, ct);
        var rematching = await matcher.RematchCountAsync(ownerId, ct);
        var jobRecords = jobs.Where(j => j.Kind == RouteJobKinds.Improve).Sum(j => Math.Max(0, j.Total - j.Done));
        // Per record: the measured match time, plus the batch pause spread over the batch
        var perRecordS = (breaker.AvgRecordMs > 0 ? breaker.AvgRecordMs / 1000 : 1.0)
                         + options.MatchPauseMs / 1000.0 / Math.Max(1, options.MatchBatch);
        var engineUp = await valhalla.PingAsync(ct);
        return new
        {
            Queued = new
            {
                Interactive = jobs.Count(j => j.State == RouteJobStates.Queued && j.Priority == RouteJobPriorities.Interactive),
                Batch = jobs.Count(j => j.State == RouteJobStates.Queued && j.Priority == RouteJobPriorities.Batch)
            },
            Running = jobs.Count(j => j.State == RouteJobStates.Running),
            Retrying = jobs.Count(j => j.NextAttemptAt != null),
            WaitingForMatch = waiting,
            // Of those, re-matches of rows from an older rule version (AD-34 rule 7)
            RematchRemaining = rematching,
            RuleVersion = RouteQuality.RuleVersion,
            EtaSeconds = (int)Math.Ceiling((waiting + jobRecords) * perRecordS),
            LastRunAt = cursor?.LastRunAt,
            LastError = cursor?.LastError,
            Backfilled = cursor?.Cursor != null && waiting == 0,
            SweptAt = cursor?.SweptAt,
            Engine = new
            {
                Up = engineUp,
                Version = await valhalla.VersionAsync(ct),
                AvgRecordMs = Math.Round(breaker.AvgRecordMs),
                Calls = breaker.Successes + breaker.Failures,
                breaker.Failures,
                Breaker = new
                {
                    Open = breaker.IsOpen,
                    breaker.OpenUntil,
                    breaker.ConsecutiveFailures,
                    breaker.Trips,
                    breaker.LastFailure,
                    breaker.LastFailureAt
                }
            },
            GraphVersion = graph.Current?.Version,
            Regions = coverage.Names
        };
    }

    private static bool ValidOwner(string? owner) => !string.IsNullOrWhiteSpace(owner) && owner.Length <= 64;
}
