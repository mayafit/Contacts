/**
 * @fileoverview Stage B for one list of points (AD-35 rules 6 and 7): the trace calls,
 * the gap fills and the assembly. Shared by the automatic match (MatchRunner) and the
 * rebuild on demand (AD-38), which matches a drive's points with synthetic ones added.
 * @module EchoRoutes.Worker.Matching
 */
using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker.Valhalla;

namespace EchoRoutes.Worker.Matching;

/// <summary>The engine failed a match (not a refusal of part of the trace)</summary>
/// <param name="Down">The whole engine is down (no connection, or the breaker opened): not an attempt of the record</param>
public class StageBEngineException : Exception
{
    /// <summary>Creates the exception</summary>
    public StageBEngineException(string message, bool down, Exception inner) : base(message, inner)
    {
        Down = down;
    }

    /// <summary>The whole engine is down</summary>
    public bool Down { get; }
}

/// <summary>A match of a list of points, and the engine's first refusal of a part of it (that part is a gap)</summary>
public record StageBResult(MatchResult Result, string? Refusal);

/// <summary>Stage B over a list of points</summary>
public static class StageB
{
    /// <summary>
    /// Plans, calls trace_attributes per call, fills the unbridged stretches by /route,
    /// and assembles the line. A refusal leaves that call a gap; any other engine failure
    /// throws <see cref="StageBEngineException"/>. <paramref name="anchorToleranceM"/>
    /// replaces the source's off-road tolerance (a rebuild uses the fit's, AD-38).
    /// </summary>
    public static async Task<StageBResult> MatchAsync(IReadOnlyList<RoutePoint> points, string type, string costing, string source,
        MatchSettings settings, IValhallaClient engine, EngineBreaker breaker, Func<bool>? yield, CancellationToken ct,
        double? anchorToleranceM = null)
    {
        var plan = TracePlanner.Plan(points, settings);
        var answers = new Dictionary<TraceCall, TraceResponse?>();
        string? refusal = null;
        foreach (var call in plan.SelectMany(s => s.Calls))
        {
            try
            {
                using var doc = await engine.TraceAttributesAsync(ValhallaTrace.BuildRequest(points, call, costing, source, settings), ct);
                answers[call] = ValhallaTrace.Parse(doc.RootElement);
                breaker.Success();
            }
            catch (EngineRejectedException ex)
            {
                // The engine is alive; this part of the trace has no match (a gap)
                breaker.Success();
                answers[call] = null;
                refusal ??= ex.Message;
            }
            catch (EngineUnavailableException ex)
            {
                // The whole engine down (no connection, or the breaker opened or its probe
                // failed) is no attempt of this record; a timeout or an error is
                throw new StageBEngineException(ex.Message, breaker.Failure(ex.Message) | ex.Unreachable, ex);
            }
        }

        var profile = RouteQuality.ProfileFor(type);
        var skeleton = RouteAssembler.Build(points, plan, answers, profile);
        List<FillOutcome>? fills = null;
        var stretches = RouteAssembler.Stretches(skeleton, settings.GapFill);
        if (stretches.Count > 0)
        {
            try
            {
                // One /route call per stretch, longest first, at most N (AD-35 rule 7). A
                // timeout leaves that gap; timeouts in a row count for the breaker, and only
                // an engine that is down makes the record wait. Interactive work stops it.
                fills = await GapFiller.FillAsync(stretches, costing, settings.GapFill, profile.MaxSpeedKmh, engine,
                    ex => breaker.Failure(ex.Message) | ex.Unreachable, breaker.Success, ct, yield);
            }
            catch (EngineUnavailableException ex)
            {
                throw new StageBEngineException(ex.Message, true, ex);
            }
        }

        var result = RouteAssembler.Assemble(points, skeleton, fills, profile, costing, settings.MaxStoredVertices,
            anchorToleranceM ?? settings.AnchorToleranceFor(source, costing), settings.GapFill.RoutedOnlyWeight);
        return new StageBResult(result, refusal);
    }
}
