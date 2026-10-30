/**
 * @fileoverview A rebuild on demand (AD-38 rules 3 to 5): the drive's points enriched
 * from similar drives (the best few bases, each at every spacing, until one is
 * confident) or with the points the Owner marked, then stage B as usual.
 * @module EchoRoutes.Worker.Matching
 */
using ContactsApi.Infrastructure.Routes;
using EchoRoutes.Worker.Valhalla;

namespace EchoRoutes.Worker.Matching;

/// <summary>Rebuild methods</summary>
public static class RebuildMethods
{
    /// <summary>Points borrowed from similar drives</summary>
    public const string Similar = "similar";

    /// <summary>Points the Owner marked on the map</summary>
    public const string Waypoints = "waypoints";

    /// <summary>The matcher's own result (matched_routes.Method)</summary>
    public const string Auto = "auto";
}

/// <summary>What a rebuild produced</summary>
/// <param name="Result">The match of the enriched points; its confidence already capped by the basis's fit</param>
/// <param name="Basis">The similar drive used (similar only)</param>
/// <param name="Added">Synthetic points added</param>
/// <param name="Tried">Matches tried (bases × spacings)</param>
public record RebuildOutcome(MatchResult Result, SimilarBasis? Basis, int Added, int Tried);

/// <summary>Runs a rebuild</summary>
public static class Rebuilder
{
    /// <summary>A rebuild this confident stops trying further bases and spacings</summary>
    public const double GoodEnough = 0.95;

    /// <summary>
    /// The best rebuild from similar drives: for each of the first <see cref="SimilarRoutes.Tries"/>
    /// bases of the rank (the best of up to three ways), at each spacing, borrow and match, and keep the most confident (stopping
    /// early only at <see cref="GoodEnough"/>). The confidence is min(the match's, the
    /// basis's fit). Null when no candidate fits.
    /// </summary>
    public static async Task<RebuildOutcome?> SimilarAsync(IReadOnlyList<RoutePoint> points, IReadOnlyList<SimilarCandidate> candidates,
        string type, string costing, string source, MatchSettings settings, IValhallaClient engine, EngineBreaker breaker, CancellationToken ct)
    {
        var maxSpeed = RouteQuality.ProfileFor(type).MaxSpeedKmh;
        var options = SimilarRoutes.Rank(points, candidates, SimilarRoutes.Tries, maxSpeed);
        if (options.Count == 0)
        {
            return null;
        }

        RebuildOutcome? best = null;
        var tried = 0;
        foreach (var (basis, line) in options)
        {
            foreach (var step in SimilarRoutes.Steps)
            {
                var enriched = SimilarRoutes.Borrow(points, line, maxSpeed, step);
                tried++;
                var match = (await StageB.MatchAsync(enriched, type, costing, source, settings, engine, breaker, null, ct, SimilarRoutes.NearM)).Result;
                var confidence = Math.Round(Math.Min(match.Confidence, basis.Fit), 3);
                var capped = match with { Confidence = confidence, Status = match.Polyline == null ? RouteStatuses.Failed : RouteStatuses.Of(confidence) };
                if (best == null || capped.Confidence > best.Result.Confidence)
                {
                    best = new RebuildOutcome(capped, basis, enriched.Count - points.Count, tried);
                }

                if (capped.Confidence >= GoodEnough)
                {
                    return best with { Tried = tried };
                }
            }
        }

        return best! with { Tried = tried };
    }

    /// <summary>The drive matched through the points the Owner marked (no cap: they are the Owner's word)</summary>
    public static async Task<RebuildOutcome> WaypointsAsync(IReadOnlyList<RoutePoint> points, IReadOnlyList<(double Lat, double Lon)> marks,
        string type, string costing, string source, MatchSettings settings, IValhallaClient engine, EngineBreaker breaker, CancellationToken ct)
    {
        var enriched = Waypoints.Place(points, marks);
        var match = (await StageB.MatchAsync(enriched, type, costing, source, settings, engine, breaker, null, ct, SimilarRoutes.NearM)).Result;
        return new RebuildOutcome(match, null, enriched.Count - points.Count, 1);
    }
}
