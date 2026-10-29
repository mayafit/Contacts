/**
 * @fileoverview Route quality rule routes.v1, stage A (AD-35 rules 1 to 5): metrics,
 * score, grade, flags, matchability and "needs improvement", from the source points
 * alone. Pure functions; no engine, no database.
 * @module ContactsApi.Infrastructure.Routes
 */
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ContactsApi.Infrastructure.Atlas;

namespace ContactsApi.Infrastructure.Routes;

/// <summary>A type's numbers in the rule (AD-35 rule 4); null where the type has none</summary>
/// <param name="Phase">R1, R2, or null when the type is never matched</param>
public record RouteProfile(
    string Type, string? Phase, string? Costing, double? ExpectedPerKm, double? StraightJumpM,
    double? MaxSpeedKmh, double? SearchRadiusM, double? BreakageM);

/// <summary>The stage A metrics of a record</summary>
/// <param name="StraightShare">Share of the track length in legs longer than the type's straight jump</param>
/// <param name="ImplausibleSpeedShare">Share of the track length in legs faster than 1.2 × the type's maximum</param>
/// <param name="StartEndpointM">First point to the Visit that ends within 30 min of it; null when there is none</param>
/// <param name="EndEndpointM">Last point to the Visit that starts within 30 min of it; null when there is none</param>
public record RouteMetrics(
    int PointCount, double TrackM, double? PointsPerKm,
    double? MaxGapS, double? MaxGapM, double? P90GapS, double? P90GapM,
    double StraightShare, int StraightCount, double ImplausibleSpeedShare,
    double? StartEndpointM, double? EndEndpointM)
{
    /// <summary>The farther of the two endpoint distances</summary>
    public double? EndpointM => StartEndpointM.HasValue || EndEndpointM.HasValue
        ? Math.Max(StartEndpointM ?? 0, EndEndpointM ?? 0)
        : null;
}

/// <summary>A record's assessment</summary>
/// <param name="Type">The type used (the fused type)</param>
/// <param name="Score">0-100; null without a track</param>
/// <param name="Grade">good, fair, poor; null without a track</param>
/// <param name="Matchable">yes, no_track, type_never, outside_coverage, rail, unknown</param>
public record RouteAssessment(string Type, RouteMetrics Metrics, int? Score, string? Grade, List<string> Flags, bool NeedsImprovement, string Matchable);

/// <summary>Matchability reasons</summary>
public static class Matchability
{
    public const string Yes = "yes";
    public const string NoTrack = "no_track";
    public const string TypeNever = "type_never";
    public const string OutsideCoverage = "outside_coverage";
    public const string Rail = "rail";
    public const string Unknown = "unknown";
}

/// <summary>Rule routes.v1, stage A</summary>
public static class RouteQuality
{
    /// <summary>
    /// The rule version stored on every row; bumping it re-assesses and re-matches
    /// (AD-34 rule 7). routes.v2: gap filling by /route and confidence v2 (AD-35 rules 6 and 7).
    /// routes.v3: a fill is rejected for a true back-track only, not a loop ramp's turn,
    /// and a rejected headed fill is retried once without headings (AD-35 rule 7).
    /// routes.v4: the roads each line follows are stored, and every matchable record of a
    /// matched type is matched, not only those that need improvement (AD-37).
    /// routes.v5: each row stores its record's start time and first and last point, for
    /// the similar-drive pool of a rebuild (AD-38)
    /// </summary>
    public const string RuleVersion = "routes.v5";

    /// <summary>Phases whose types are matched now</summary>
    public static readonly IReadOnlySet<string> EnabledPhases = new HashSet<string> { "R1" };

    /// <summary>The types matched in an enabled phase (a list, so a query can use it)</summary>
    public static List<string> MatchedTypes => Profiles.Values
        .Where(p => p.Phase != null && EnabledPhases.Contains(p.Phase)).Select(p => p.Type).ToList();

    /// <summary>A Visit counts for the endpoint check within this time of the track's end</summary>
    public static readonly TimeSpan EndpointWindow = TimeSpan.FromMinutes(30);

    /// <summary>An endpoint farther than this from its Visit is flagged</summary>
    public const double EndpointFarM = 500;

    private static readonly Dictionary<string, RouteProfile> Profiles = new()
    {
        ["drive"] = new("drive", "R1", "auto", 2, 500, 200, 100, 10_000),
        ["transit"] = new("transit", "R1", "bus", 2, 500, 130, 100, 10_000),
        ["ride"] = new("ride", "R2", "bicycle", 5, 300, 70, 50, 5_000),
        ["run"] = new("run", "R2", "pedestrian", 10, 150, 25, 50, 3_000),
        ["walk"] = new("walk", "R2", "pedestrian", 10, 150, 12, 50, 3_000),
        ["hike"] = new("hike", "R2", "pedestrian", 10, 150, 12, 50, 3_000),
        ["flight"] = new("flight", null, null, null, null, 1_100, null, null),
    };

    private static readonly RouteProfile RailProfile = new("transit", null, null, null, null, 300, null, null);

    /// <summary>The type's profile; types without one (ski, swim, other…) are never matched</summary>
    public static RouteProfile ProfileFor(string type) =>
        Profiles.TryGetValue(type, out var p) ? p : new RouteProfile(type, null, null, null, null, null, null, null);

    /// <summary>bus, taxi or rail from the source's own transit type; null when it says nothing</summary>
    public static string? TransitKind(string? sourceType)
    {
        var t = sourceType?.Trim().ToLowerInvariant().Replace(' ', '_');
        return t switch
        {
            null or "" => null,
            "in_bus" or "bus" => "bus",
            "in_taxi" or "taxi" => "taxi",
            "in_train" or "train" or "in_subway" or "subway" or "in_tram" or "tram" or "in_ferry" or "ferry"
                or "in_vehicle_ferry" or "in_gondola_lift" or "in_cable_car" or "cable_car" or "in_rail" => "rail",
            _ => null
        };
    }

    /// <summary>
    /// Assesses one record. <paramref name="covered"/> says whether the track lies inside
    /// the map's region outlines (asked only when everything else allows matching).
    /// </summary>
    public static RouteAssessment Assess(string type, string? sourceType, IReadOnlyList<RoutePoint> points,
        RouteVisit? before, RouteVisit? after, Func<IReadOnlyList<RoutePoint>, bool> covered)
    {
        var kind = type == "transit" ? TransitKind(sourceType) : null;
        var profile = type == "transit" && kind == "rail" ? RailProfile : ProfileFor(type);
        if (type == "transit" && kind == "taxi")
        {
            profile = profile with { Costing = "auto" };
        }

        var metrics = Measure(profile, points, before, after);
        if (points.Count < 2)
        {
            return new RouteAssessment(type, metrics, null, null, new List<string> { Matchability.NoTrack }, false, Matchability.NoTrack);
        }

        var score = Score(metrics, profile);
        var flags = Flags(metrics, profile);
        string matchable;
        if (profile.Phase == null)
        {
            matchable = type == "transit" ? Matchability.Rail : Matchability.TypeNever;
        }
        else if (type == "transit" && kind == null)
        {
            // Imported before the source type was kept: no guessing (AD-35 rule 5)
            matchable = Matchability.Unknown;
        }
        else
        {
            matchable = covered(points) ? Matchability.Yes : Matchability.OutsideCoverage;
        }

        if (matchable != Matchability.Yes)
        {
            flags.Add(matchable);
        }

        var needs = matchable == Matchability.Yes && EnabledPhases.Contains(profile.Phase!)
                    && (score < 80 || metrics.StraightShare > 0.2);
        return new RouteAssessment(type, metrics, score, Grade(score), flags, needs, matchable);
    }

    /// <summary>The stage A metrics (AD-35 rule 1)</summary>
    public static RouteMetrics Measure(RouteProfile profile, IReadOnlyList<RoutePoint> points, RouteVisit? before, RouteVisit? after)
    {
        if (points.Count < 2)
        {
            return new RouteMetrics(points.Count, 0, null, null, null, null, null, 0, 0, 0, null, null);
        }

        var legM = new double[points.Count - 1];
        var legS = new double[points.Count - 1];
        double length = 0, straight = 0, implausible = 0;
        var straightCount = 0;
        var maxMs = profile.MaxSpeedKmh.HasValue ? profile.MaxSpeedKmh.Value * 1.2 / 3.6 : (double?)null;
        for (var i = 1; i < points.Count; i++)
        {
            var m = Geo.Haversine(points[i - 1].Lat, points[i - 1].Lon, points[i].Lat, points[i].Lon);
            var s = (points[i].T - points[i - 1].T).TotalSeconds;
            legM[i - 1] = m;
            legS[i - 1] = s;
            length += m;
            if (profile.StraightJumpM.HasValue && m > profile.StraightJumpM.Value)
            {
                straight += m;
                straightCount++;
            }

            // A jump with no time is implausible too (beyond GPS noise)
            if (maxMs.HasValue && (s > 0 ? m / s > maxMs.Value : m > 50))
            {
                implausible += m;
            }
        }

        double? perKm = length > 0 ? points.Count / (length / 1000) : null;
        double? startEnd = before != null && Math.Abs((points[0].T - before.End).TotalMinutes) <= EndpointWindow.TotalMinutes
            ? Geo.Haversine(points[0].Lat, points[0].Lon, before.Lat, before.Lon)
            : null;
        double? endEnd = after != null && Math.Abs((after.Start - points[^1].T).TotalMinutes) <= EndpointWindow.TotalMinutes
            ? Geo.Haversine(points[^1].Lat, points[^1].Lon, after.Lat, after.Lon)
            : null;
        return new RouteMetrics(points.Count, length, perKm,
            legS.Max(), legM.Max(), Percentile(legS, 0.9), Percentile(legM, 0.9),
            length > 0 ? straight / length : 0, straightCount, length > 0 ? implausible / length : 0,
            startEnd, endEnd);
    }

    /// <summary>
    /// 100 − 35 × densityDeficit − 35 × straightShare − 15 × implausibleSpeedShare − 15 × (endpoint &gt; 500 m),
    /// rounded (AD-35 rule 2)
    /// </summary>
    public static int Score(RouteMetrics m, RouteProfile profile)
    {
        var deficit = profile.ExpectedPerKm.HasValue && m.PointsPerKm.HasValue
            ? Math.Clamp(1 - m.PointsPerKm.Value / profile.ExpectedPerKm.Value, 0, 1)
            : 0;
        var score = 100 - 35 * deficit - 35 * m.StraightShare - 15 * m.ImplausibleSpeedShare
                    - 15 * (m.EndpointM > EndpointFarM ? 1 : 0);
        return (int)Math.Round(Math.Clamp(score, 0, 100), MidpointRounding.AwayFromZero);
    }

    /// <summary>good at 80 or above, fair at 60-79, poor below 60</summary>
    public static string Grade(int score) => score >= 80 ? "good" : score >= 60 ? "fair" : "poor";

    /// <summary>What lowered the score</summary>
    public static List<string> Flags(RouteMetrics m, RouteProfile profile)
    {
        var flags = new List<string>();
        if (profile.ExpectedPerKm.HasValue && m.PointsPerKm < profile.ExpectedPerKm)
        {
            flags.Add("sparse");
        }

        if (m.StraightShare > 0.2)
        {
            flags.Add("straight");
        }

        if (m.ImplausibleSpeedShare > 0)
        {
            flags.Add("implausible_speed");
        }

        if (m.EndpointM > EndpointFarM)
        {
            flags.Add("endpoint_far");
        }

        return flags;
    }

    /// <summary>SHA-256 of the points' times (µs) and coordinates rounded to 1e-6, hex</summary>
    public static string InputHash(IReadOnlyList<RoutePoint> points)
    {
        var sb = new StringBuilder(points.Count * 40);
        foreach (var p in points)
        {
            sb.Append((p.T.Ticks / 10).ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(Math.Round(p.Lat, 6).ToString("F6", CultureInfo.InvariantCulture)).Append('|')
                .Append(Math.Round(p.Lon, 6).ToString("F6", CultureInfo.InvariantCulture)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static double Percentile(double[] values, double p)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var rank = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }
}
