/**
 * @fileoverview The service's API, /internal/v1 (AD-33 rule 4): cluster-only, and only
 * the backend's token (Routes__BackendToken) is accepted
 * @module EchoRoutes.Worker.Api
 */
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContactsApi.Infrastructure.Routes;
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
                    Counts = JsonDocument.Parse(job.Counts).RootElement, job.LastError, job.CreatedAt, job.StartedAt, job.FinishedAt
                });
        });
        v1.MapGet("/status", async (string? ownerId, RoutesDbContext db, GraphInfoSource graph, RegionCoverage coverage,
            IValhallaClient valhalla, CancellationToken ct) =>
        {
            if (!ValidOwner(ownerId))
            {
                return ApiEnvelope.Fail(StatusCodes.Status400BadRequest, "INVALID_INPUT", "ownerId is required.");
            }

            var jobs = await db.Jobs.AsNoTracking()
                .Where(j => j.OwnerId == ownerId && (j.State == RouteJobStates.Queued || j.State == RouteJobStates.Running))
                .GroupBy(j => new { j.State, j.Priority })
                .Select(g => new { g.Key.State, g.Key.Priority, Count = g.Count() })
                .ToListAsync(ct);
            var cursor = await db.Cursors.AsNoTracking().FirstOrDefaultAsync(c => c.OwnerId == ownerId, ct);
            return ApiEnvelope.Ok(new
            {
                Queued = new
                {
                    Interactive = jobs.Where(j => j.State == RouteJobStates.Queued && j.Priority == RouteJobPriorities.Interactive).Sum(j => j.Count),
                    Batch = jobs.Where(j => j.State == RouteJobStates.Queued && j.Priority == RouteJobPriorities.Batch).Sum(j => j.Count)
                },
                Running = jobs.Where(j => j.State == RouteJobStates.Running).Sum(j => j.Count),
                LastRunAt = cursor?.LastRunAt,
                LastError = cursor?.LastError,
                Backfilled = cursor?.Cursor != null,
                EngineVersion = await valhalla.VersionAsync(ct),
                GraphVersion = graph.Current?.Version,
                Regions = coverage.Names
            });
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

    private static bool ValidOwner(string? owner) => !string.IsNullOrWhiteSpace(owner) && owner.Length <= 64;
}
