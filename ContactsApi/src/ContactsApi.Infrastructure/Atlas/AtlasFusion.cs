/**
 * @fileoverview Fusion: records from several sources that describe the same
 * real-world activity become one activity, with provenance, match evidence and
 * conflicts. Matching uses each source's time and location accuracy.
 * @module ContactsApi.Infrastructure.Atlas
 */
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ContactsApi.Infrastructure.Atlas;

/// <summary>What fusion needs to know about one activity record</summary>
public record FusionCandidate(
    Guid Id, string Source, string Type, string? Name, DateTime Start, DateTime End,
    double? DistanceM, double? ElevationGainM, int? AvgHeartRate, int? MaxHeartRate,
    double? MinLat, double? MinLon, double? MaxLat, double? MaxLon, int PointCount, bool TypeByUser = false);

/// <summary>A position at a time, from a record's track (a sample of it)</summary>
public record TrackSample(DateTime T, double Lat, double Lon);

/// <summary>How far a source's times and positions can be off</summary>
public record Tolerance(double TimeS, double LocationM);

/// <summary>Accuracy per source and the confidence an automatic match needs</summary>
public record FusionRules(IReadOnlyDictionary<string, Tolerance> Sources, double Threshold)
{
    /// <summary>Sources in trust order, with their default accuracy</summary>
    public static readonly IReadOnlyList<(string Source, Tolerance Tolerance)> DefaultList = new List<(string, Tolerance)>
    {
        ("sports-tracker", new Tolerance(30, 25)),
        ("gpx", new Tolerance(30, 25)),
        ("tcx", new Tolerance(30, 25)),
        ("manual", new Tolerance(900, 500)),
        // Watch GPS: often off by a few hundred meters
        ("google-fit", new Tolerance(120, 300)),
        ("fitbit", new Tolerance(60, 100)),
        ("google-timeline", new Tolerance(600, 300)),
        ("other", new Tolerance(300, 200))
    };

    /// <summary>Lowest confidence for an automatic match, by default</summary>
    public const double DefaultThreshold = 0.5;

    /// <summary>The built-in rules</summary>
    public static FusionRules Default { get; } = new(DefaultList.ToDictionary(d => d.Source, d => d.Tolerance), DefaultThreshold);

    /// <summary>The accuracy of a source (unknown sources use "other")</summary>
    public Tolerance For(string source) =>
        Sources.TryGetValue(source, out var t) ? t : Sources.TryGetValue("other", out var o) ? o : new Tolerance(300, 200);
}

/// <summary>One fused activity: its primary record, members, fused values and how they matched</summary>
public record FusedGroup(
    Guid PrimaryId, List<FusionCandidate> Members, string Type, string? Name, DateTime Start, DateTime End,
    double? DistanceM, double? ElevationGainM, int? AvgHeartRate, int? MaxHeartRate,
    Dictionary<string, string> FieldSources, List<FusionConflict> Conflicts, List<MatchEvidence> Evidence)
{
    /// <summary>The weakest member match (null when the user decided every one)</summary>
    public double? MatchConfidence => Evidence.Where(e => !e.UserDecision).Select(e => (double?)e.Confidence).Min();
}

/// <summary>
/// Fusion rules, without the database: how well two records match
/// (<see cref="Evaluate"/>), which records are one activity (<see cref="Group"/>)
/// and what the fused activity looks like (<see cref="Fuse"/>).
/// </summary>
public static class Fusion
{
    private const double MetersPerDegree = 111_320;

    /// <summary>Label of a source in messages</summary>
    public static string Label(string source) => source switch
    {
        "sports-tracker" => "Sports Tracker",
        "google-timeline" => "Google Timeline",
        "google-fit" => "Google Fit",
        "fitbit" => "Fitbit",
        "gpx" => "GPX file",
        "tcx" => "TCX file",
        "manual" => "Manual",
        _ => source
    };

    /// <summary>
    /// How much a source is trusted for tracks, distance and type (lower is
    /// better): a dedicated recording beats a phone's guess
    /// </summary>
    public static int Rank(string source) => source switch
    {
        "sports-tracker" => 0,
        "gpx" or "tcx" => 1,
        "manual" => 2,
        "google-fit" => 3,
        "fitbit" => 4,
        "google-timeline" => 6,
        _ => 5
    };

    /// <summary>
    /// How well record <paramref name="b"/> matches record <paramref name="a"/>.
    /// Times must agree within the two sources' combined time tolerance, or overlap
    /// by at least half of the shorter and a quarter of the longer record (they
    /// must overlap in any case: consecutive items are different activities); the
    /// areas must touch (widened by the combined location tolerance); and when both
    /// have tracks, they are compared at the same moments. Two records of one source
    /// only match when their times agree within tolerance (the same recording twice).
    /// </summary>
    /// <param name="optimistic">Treat a missing track comparison as perfect (to find candidates before tracks are loaded)</param>
    /// <returns>The evidence, or null when the two cannot be the same activity</returns>
    public static MatchEvidence? Evaluate(FusionCandidate a, FusionCandidate b, FusionRules rules,
        IReadOnlyDictionary<Guid, List<TrackSample>>? samples = null, bool optimistic = false)
    {
        if (a.Id == b.Id)
        {
            return null;
        }

        var (ta, tb) = (rules.For(a.Source), rules.For(b.Source));
        var tt = ta.TimeS + tb.TimeS;
        var lt = ta.LocationM + tb.LocationM;
        if (a.Source == b.Source)
        {
            // The same ride recorded by two devices into one source (phone and watch
            // into Sports Tracker): times decide; a watch's GPS may be far off
            lt = Math.Max(lt, 500);
        }

        var dStart = (b.Start - a.Start).TotalSeconds;
        var dEnd = (b.End - a.End).TotalSeconds;
        var withinTime = Math.Abs(dStart) <= tt && Math.Abs(dEnd) <= tt;
        var durA = (a.End - a.Start).TotalSeconds;
        var durB = (b.End - b.Start).TotalSeconds;
        var longer = Math.Max(durA, durB);
        var shorter = Math.Min(durA, durB);
        var overlap = (Min(a.End, b.End) - Max(a.Start, b.Start)).TotalSeconds;

        double timeScore;
        if (a.Source == b.Source)
        {
            // The same recording twice: times agree and they almost fully overlap
            // (consecutive segments of one source are never one activity)
            if (!withinTime || overlap < longer * 0.8)
            {
                return null;
            }

            timeScore = 1;
        }
        else if (shorter < 60)
        {
            // A moment (a session without a duration) belongs to what it falls within
            var (point, span) = durA < 60 ? (a, b) : (b, a);
            if (point.Start < span.Start.AddSeconds(-tt) || point.Start > span.End.AddSeconds(tt))
            {
                return null;
            }

            timeScore = 1;
        }
        else if (overlap <= 0)
        {
            // One right after the other is not the same activity, however close
            return null;
        }
        else if (withinTime)
        {
            timeScore = 1;
        }
        else if (overlap < shorter * 0.5 || overlap < longer * 0.25)
        {
            // A short walk inside a long drive is not the drive
            return null;
        }
        else
        {
            // Covered: most of the shorter record, and a good part of the longer one
            timeScore = Math.Min(1, (overlap + tt) / shorter) * Math.Min(1, (overlap + tt) / (longer * 0.5));
        }

        var boxes = a.MinLat.HasValue && b.MinLat.HasValue;
        if (boxes)
        {
            var padLat = lt / MetersPerDegree;
            var padLon = lt / (MetersPerDegree * Math.Max(0.1, Math.Cos(a.MinLat!.Value * Math.PI / 180)));
            if (!(a.MinLat - padLat <= b.MaxLat && b.MinLat - padLat <= a.MaxLat
                  && a.MinLon - padLon <= b.MaxLon && b.MinLon - padLon <= a.MaxLon))
            {
                return null;
            }
        }

        double? deviation = null;
        if (samples != null && samples.TryGetValue(a.Id, out var sa) && samples.TryGetValue(b.Id, out var sb))
        {
            deviation = Deviation(sa, sb);
        }

        var locationScore = deviation switch
        {
            null when optimistic => 1,
            null => boxes ? 0.85 : 0.75,
            var d when d <= lt => 1,
            var d when d >= lt * 4 => 0,
            var d => 1 - (d.Value - lt) / (lt * 3)
        };
        return new MatchEvidence(b.Id, b.Source, Math.Round(dStart), Math.Round(dEnd), tt,
            deviation.HasValue ? Math.Round(deviation.Value) : null, lt, Math.Round(timeScore * locationScore, 3), false);
    }

    /// <summary>
    /// Median distance between two tracks at the same moments (the second track's
    /// position interpolated at each sample time of the first); null when they
    /// share fewer than three moments
    /// </summary>
    public static double? Deviation(IReadOnlyList<TrackSample> a, IReadOnlyList<TrackSample> b)
    {
        if (a.Count == 0 || b.Count < 2)
        {
            return null;
        }

        var distances = new List<double>();
        var j = 0;
        foreach (var s in a)
        {
            if (s.T < b[0].T || s.T > b[^1].T)
            {
                continue;
            }

            while (j < b.Count - 2 && b[j + 1].T < s.T)
            {
                j++;
            }

            var (p, q) = (b[j], b[j + 1]);
            var span = (q.T - p.T).TotalSeconds;
            var f = span > 0 ? Math.Clamp((s.T - p.T).TotalSeconds / span, 0, 1) : 0;
            distances.Add(Geo.Haversine(s.Lat, s.Lon, p.Lat + (q.Lat - p.Lat) * f, p.Lon + (q.Lon - p.Lon) * f));
        }

        if (distances.Count < 3)
        {
            return null;
        }

        distances.Sort();
        return distances[distances.Count / 2];
    }

    /// <summary>
    /// Pairs worth comparing in detail: the records whose tracks should be loaded.
    /// Optimistic about the tracks, so no pair that could match is missed.
    /// </summary>
    public static HashSet<Guid> Candidates(IReadOnlyList<FusionCandidate> items, FusionRules rules)
    {
        var ids = new HashSet<Guid>();
        foreach (var (a, b) in Sweep(items, rules))
        {
            if (a.PointCount > 1 && b.PointCount > 1 && Evaluate(a, b, rules, optimistic: true) is { } e && e.Confidence >= rules.Threshold)
            {
                ids.Add(a.Id);
                ids.Add(b.Id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Groups records into real-world activities, best matches first. User
    /// decisions win: pairs marked "together" are always joined, pairs marked
    /// "apart" never end up in one group (directly or through others), and two
    /// records of one source that do not match never share a group.
    /// </summary>
    /// <returns>Groups, each a list of record ids (singletons included)</returns>
    public static List<List<Guid>> Group(
        IReadOnlyList<FusionCandidate> items,
        IEnumerable<(Guid A, Guid B)> together,
        IEnumerable<(Guid A, Guid B)> apart,
        FusionRules? rules = null,
        IReadOnlyDictionary<Guid, List<TrackSample>>? samples = null)
    {
        rules ??= FusionRules.Default;
        var togetherList = together.ToList();
        var userJoined = togetherList.SelectMany(p => new[] { p.A, p.B }).ToHashSet();
        var byId = items.ToDictionary(i => i.Id);
        var parent = items.ToDictionary(i => i.Id, i => i.Id);
        var members = items.ToDictionary(i => i.Id, i => new HashSet<Guid> { i.Id });
        var apartSet = apart.Select(p => Key(p.A, p.B)).ToHashSet();

        Guid Find(Guid x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        void Union(Guid x, Guid y, bool force)
        {
            var rx = Find(x);
            var ry = Find(y);
            if (rx == ry)
            {
                return;
            }

            if (!force)
            {
                if (members[rx].Any(m => members[ry].Any(n =>
                        apartSet.Contains(Key(m, n))
                        || (byId[m].Source == byId[n].Source && Evaluate(byId[m], byId[n], rules) == null))))
                {
                    return;
                }

                // Not through a chain: every member must match the joined group's
                // best record itself (except what the user merged)
                var joined = members[rx].Concat(members[ry]).Select(id => byId[id]).ToList();
                var best = Best(joined);
                if (joined.Any(m => m.Id != best.Id && !userJoined.Contains(m.Id)
                                    && !(Evaluate(best, m, rules, samples)?.Confidence >= rules.Threshold)))
                {
                    return;
                }
            }

            if (members[rx].Count < members[ry].Count)
            {
                (rx, ry) = (ry, rx);
            }

            parent[ry] = rx;
            members[rx].UnionWith(members[ry]);
            members.Remove(ry);
        }

        foreach (var (a, b) in togetherList)
        {
            if (parent.ContainsKey(a) && parent.ContainsKey(b))
            {
                Union(a, b, force: true);
            }
        }

        var pairs = Sweep(items, rules)
            .Where(p => !apartSet.Contains(Key(p.A.Id, p.B.Id)))
            .Select(p => (p.A, p.B, Evidence: Evaluate(p.A, p.B, rules, samples)))
            .Where(p => p.Evidence != null && p.Evidence.Confidence >= rules.Threshold)
            .OrderByDescending(p => p.Evidence!.Confidence)
            .ToList();
        foreach (var (a, b, _) in pairs)
        {
            Union(a.Id, b.Id, force: false);
        }

        return members.Values.Select(m => m.ToList()).ToList();
    }

    /// <summary>
    /// The fused activity of a group: the best record is the primary (a real
    /// track from the most trusted source); every other value comes from the most
    /// trusted source that has it; each member's match is measured against the
    /// primary; what disagrees beyond the sources' tolerance becomes a conflict.
    /// </summary>
    /// <param name="userJoined">Records the user merged in</param>
    public static FusedGroup Fuse(IReadOnlyList<FusionCandidate> group, FusionRules? rules = null,
        IReadOnlyDictionary<Guid, List<TrackSample>>? samples = null, IReadOnlySet<Guid>? userJoined = null)
    {
        rules ??= FusionRules.Default;
        var ordered = Order(group);
        var primary = ordered[0];
        var byRank = group.OrderBy(m => Rank(m.Source)).ThenByDescending(m => m.PointCount).ToList();
        var fields = new Dictionary<string, string> { ["track"] = primary.Source };

        var byUser = group.FirstOrDefault(m => m.TypeByUser);
        var typed = byUser ?? byRank.FirstOrDefault(m => m.Type != "other") ?? primary;
        fields["type"] = byUser != null ? "user" : typed.Source;
        var named = primary.Name != null ? primary : byRank.FirstOrDefault(m => m.Name != null);
        if (named != null)
        {
            fields["name"] = named.Source;
        }

        // A recording's own times are exact; otherwise the span of all
        DateTime start, end;
        if (primary.PointCount > 1 && Rank(primary.Source) < Rank("google-timeline"))
        {
            (start, end) = (primary.Start, primary.End);
            fields["time"] = primary.Source;
        }
        else
        {
            (start, end) = (group.Min(m => m.Start), group.Max(m => m.End));
            fields["time"] = byRank[0].Source;
        }

        var distance = primary.DistanceM.HasValue ? primary : byRank.FirstOrDefault(m => m.DistanceM.HasValue);
        if (distance != null)
        {
            fields["distanceM"] = distance.Source;
        }

        var climb = primary.ElevationGainM.HasValue ? primary : byRank.FirstOrDefault(m => m.ElevationGainM.HasValue);
        var hr = primary.AvgHeartRate.HasValue ? primary : byRank.FirstOrDefault(m => m.AvgHeartRate.HasValue);
        if (hr != null)
        {
            fields["heartRate"] = hr.Source;
        }

        // How each member matches the primary (measured, even when joined through another member)
        var evidence = ordered.Skip(1)
            .Select(m => Evaluate(primary, m, rules, samples) is { } e
                ? e with { UserDecision = userJoined?.Contains(m.Id) == true }
                : Measure(primary, m, rules, samples, userJoined?.Contains(m.Id) == true))
            .ToList();

        return new FusedGroup(primary.Id, ordered, typed.Type, named?.Name, start, end,
            distance?.DistanceM, climb?.ElevationGainM, hr?.AvgHeartRate, hr?.MaxHeartRate,
            fields, Conflicts(group, primary, typed.Type, distance?.DistanceM, evidence), evidence);
    }

    /// <summary>Records best first: a real track, then the most trusted source, then the most points</summary>
    private static List<FusionCandidate> Order(IEnumerable<FusionCandidate> group) =>
        group.OrderBy(m => m.PointCount > 1 ? 0 : 1).ThenBy(m => Rank(m.Source)).ThenByDescending(m => m.PointCount).ToList();

    private static FusionCandidate Best(IEnumerable<FusionCandidate> group) => Order(group)[0];

    /// <summary>Deltas without the matching rules (a member joined through another, or by the user)</summary>
    private static MatchEvidence Measure(FusionCandidate a, FusionCandidate b, FusionRules rules,
        IReadOnlyDictionary<Guid, List<TrackSample>>? samples, bool user)
    {
        var (ta, tb) = (rules.For(a.Source), rules.For(b.Source));
        double? deviation = samples != null && samples.TryGetValue(a.Id, out var sa) && samples.TryGetValue(b.Id, out var sb)
            ? Deviation(sa, sb)
            : null;
        return new MatchEvidence(b.Id, b.Source, Math.Round((b.Start - a.Start).TotalSeconds), Math.Round((b.End - a.End).TotalSeconds),
            ta.TimeS + tb.TimeS, deviation.HasValue ? Math.Round(deviation.Value) : null, ta.LocationM + tb.LocationM, 0, user);
    }

    private static List<FusionConflict> Conflicts(IReadOnlyList<FusionCandidate> group, FusionCandidate primary, string type,
        double? distance, List<MatchEvidence> evidence)
    {
        var conflicts = new List<FusionConflict>();
        var types = group.Where(m => m.Type != "other").GroupBy(m => m.Type).ToList();
        if (types.Count > 1)
        {
            var values = group.Where(m => m.Type != "other").Select(m => new SourceValue(m.Source, m.Type)).ToList();
            conflicts.Add(new FusionConflict("type",
                string.Join(", ", values.Select(v => $"{Label(v.Source)} says {v.Value}")),
                values, type));
        }

        var distances = group.Where(m => m.DistanceM is > 0).ToList();
        if (distances.Count > 1)
        {
            var max = distances.Max(m => m.DistanceM!.Value);
            var min = distances.Min(m => m.DistanceM!.Value);
            if (max - min > 500 && max - min > max * 0.1)
            {
                var values = distances.Select(m => new SourceValue(m.Source, $"{m.DistanceM!.Value / 1000:0.0} km")).ToList();
                conflicts.Add(new FusionConflict("distanceM",
                    string.Join(", ", values.Select(v => $"{Label(v.Source)}: {v.Value}")),
                    values, distance.HasValue ? $"{distance.Value / 1000:0.0} km" : ""));
            }
        }

        // Times and tracks beyond the combined tolerance of the two sources
        foreach (var e in evidence)
        {
            var member = group.First(m => m.Id == e.MemberId);
            if (Math.Abs(e.StartDeltaS) > e.TimeToleranceS)
            {
                conflicts.Add(new FusionConflict("startTime",
                    $"{Label(e.Source)} starts {Span(e.StartDeltaS)} {(e.StartDeltaS < 0 ? "earlier" : "later")} than {Label(primary.Source)} (tolerance ±{Span(e.TimeToleranceS)})",
                    new List<SourceValue> { new(primary.Source, primary.Start.ToString("u")), new(e.Source, member.Start.ToString("u")) },
                    primary.Start.ToString("u")));
            }

            if (Math.Abs(e.EndDeltaS) > e.TimeToleranceS)
            {
                conflicts.Add(new FusionConflict("endTime",
                    $"{Label(e.Source)} ends {Span(e.EndDeltaS)} {(e.EndDeltaS < 0 ? "earlier" : "later")} than {Label(primary.Source)} (tolerance ±{Span(e.TimeToleranceS)})",
                    new List<SourceValue> { new(primary.Source, primary.End.ToString("u")), new(e.Source, member.End.ToString("u")) },
                    primary.End.ToString("u")));
            }

            if (e.TrackDeviationM > e.LocationToleranceM)
            {
                conflicts.Add(new FusionConflict("location",
                    $"{Label(e.Source)}'s track runs {e.TrackDeviationM:0} m from {Label(primary.Source)}'s (tolerance ±{e.LocationToleranceM:0} m)",
                    new List<SourceValue> { new(e.Source, $"{e.TrackDeviationM:0} m") },
                    primary.Source));
            }
        }

        return conflicts;
    }

    /// <summary>Pairs close enough in time to compare (sorted sweep)</summary>
    private static IEnumerable<(FusionCandidate A, FusionCandidate B)> Sweep(IReadOnlyList<FusionCandidate> items, FusionRules rules)
    {
        var slack = rules.Sources.Values.Select(t => t.TimeS).DefaultIfEmpty(600).Max() * 2 + 60;
        var sorted = items.OrderBy(i => i.Start).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            for (var j = i + 1; j < sorted.Count && sorted[j].Start <= sorted[i].End.AddSeconds(slack); j++)
            {
                yield return (sorted[i], sorted[j]);
            }
        }
    }

    private static string Span(double seconds)
    {
        var s = Math.Abs(seconds);
        return s < 90 ? $"{s:0} s" : s < 5400 ? $"{s / 60:0.#} min" : $"{s / 3600:0.#} h";
    }

    private static (Guid, Guid) Key(Guid a, Guid b) => a.CompareTo(b) < 0 ? (a, b) : (b, a);

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}

/// <summary>Tells the fusion service that new work is queued</summary>
public interface IFusionSignal
{
    /// <summary>Wakes the service</summary>
    void Notify();
}

public partial class AtlasService
{
    private static int SourceRank(string source) => Fusion.Rank(source);

    private static readonly HashSet<string> Vehicles = new() { "drive", "flight", "transit" };

    // ---------------------------------------------------------------- queue

    /// <summary>
    /// Records that a time window changed (new or updated data) so the fusion
    /// service fuses it. Without a running service (tests, tools) it is fused now.
    /// </summary>
    public async Task QueueFusionAsync(string owner, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        if (_signal == null)
        {
            await RefuseAsync(owner, from, to, ct);
            return;
        }

        _db.FusionQueue.Add(new FusionQueueItem { OwnerId = owner, From = from, To = to, Full = !from.HasValue || !to.HasValue });
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        _signal.Notify();
    }

    /// <summary>Windows waiting for the fusion service</summary>
    public Task<int> PendingFusionAsync(string owner, CancellationToken ct = default) =>
        _db.FusionQueue.CountAsync(q => q.OwnerId == owner, ct);

    /// <summary>Owners with queued work</summary>
    public Task<List<string>> OwnersWithQueuedFusionAsync(CancellationToken ct = default) =>
        _db.FusionQueue.Select(q => q.OwnerId).Distinct().ToListAsync(ct);

    /// <summary>
    /// Works off the owner's queue: overlapping or nearby windows are merged, a
    /// full request covers everything; processed entries are removed
    /// </summary>
    public async Task<FuseResult> ProcessFusionQueueAsync(string owner, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var queued = await _db.FusionQueue.AsNoTracking()
            .Where(q => q.OwnerId == owner && (q.NextAttemptAt == null || q.NextAttemptAt <= now))
            .OrderBy(q => q.Id).ToListAsync(ct);
        if (queued.Count == 0)
        {
            return new FuseResult(0, 0, 0);
        }

        var readIds = queued.Select(q => q.Id).ToList();

        var windows = new List<(DateTime? From, DateTime? To)>();
        if (queued.Any(q => q.Full))
        {
            windows.Add((null, null));
        }
        else
        {
            foreach (var q in queued.OrderBy(q => q.From))
            {
                if (windows.Count > 0 && q.From <= windows[^1].To!.Value.AddHours(12))
                {
                    var last = windows[^1];
                    windows[^1] = (last.From, q.To > last.To ? q.To : last.To);
                }
                else
                {
                    windows.Add((q.From, q.To));
                }
            }
        }

        var total = new FuseResult(0, 0, 0);
        try
        {
            foreach (var (from, to) in windows)
            {
                var r = await RefuseAsync(owner, from, to, ct);
                total = new FuseResult(total.Activities + r.Activities, total.Fused + r.Fused, total.Conflicts + r.Conflicts);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Try again later: 2, 4, 8… minutes (at most an hour)
            var attempts = queued.Max(q => q.Attempts) + 1;
            var next = DateTime.UtcNow.AddMinutes(Math.Min(60, Math.Pow(2, attempts)));
            await _db.FusionQueue.Where(q => readIds.Contains(q.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(q => q.Attempts, attempts).SetProperty(q => q.NextAttemptAt, next), CancellationToken.None);
            throw;
        }

        await _db.FusionQueue.Where(q => readIds.Contains(q.Id)).ExecuteDeleteAsync(ct);
        return total;
    }

    /// <summary>Re-fuses all of the owner's activities now</summary>
    public Task<FuseResult> FuseAsync(string owner, CancellationToken ct = default) => RefuseAsync(owner, null, null, ct);

    /// <summary>Re-fuses a time window (or everything) now, under the owner's gate</summary>
    public async Task<FuseResult> RefuseAsync(string owner, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var gate = Gates.GetOrAdd(owner, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await FuseCoreAsync(owner, from, to, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    // ---------------------------------------------------------------- settings

    /// <summary>The owner's fusion rules (or the defaults)</summary>
    public async Task<FusionRules> RulesAsync(string owner, CancellationToken ct = default)
    {
        var row = await _db.FusionSettings.AsNoTracking().FirstOrDefaultAsync(s => s.OwnerId == owner, ct);
        if (row == null)
        {
            return FusionRules.Default;
        }

        var saved = JsonSerializer.Deserialize<FusionSettingsDto>(row.Json, JsonCamel);
        var sources = FusionRules.Default.Sources.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var s in saved?.Sources ?? new List<SourceToleranceDto>())
        {
            if (sources.ContainsKey(s.Source))
            {
                sources[s.Source] = new Tolerance(s.TimeToleranceS, s.LocationToleranceM);
            }
        }

        return new FusionRules(sources, saved?.MatchThreshold ?? FusionRules.DefaultThreshold);
    }

    /// <summary>The owner's settings as the app shows them</summary>
    public async Task<FusionSettingsDto> GetFusionSettingsAsync(string owner, CancellationToken ct = default) =>
        ToDto(await RulesAsync(owner, ct));

    /// <summary>Saves the owner's tolerances and threshold (clamped) and queues a full re-fusion</summary>
    public async Task<FusionSettingsDto> SaveFusionSettingsAsync(string owner, FusionSettingsDto input, CancellationToken ct = default)
    {
        var sources = FusionRules.Default.Sources.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var s in input.Sources ?? new List<SourceToleranceDto>())
        {
            if (sources.ContainsKey(s.Source) && double.IsFinite(s.TimeToleranceS) && double.IsFinite(s.LocationToleranceM))
            {
                sources[s.Source] = new Tolerance(Math.Clamp(s.TimeToleranceS, 1, 21_600), Math.Clamp(s.LocationToleranceM, 1, 20_000));
            }
        }

        var threshold = double.IsFinite(input.MatchThreshold) && input.MatchThreshold > 0 ? input.MatchThreshold : FusionRules.DefaultThreshold;
        var dto = ToDto(new FusionRules(sources, Math.Clamp(threshold, 0.05, 0.99)));
        var row = await _db.FusionSettings.FirstOrDefaultAsync(s => s.OwnerId == owner, ct);
        if (row == null)
        {
            row = new FusionSetting { OwnerId = owner };
            _db.FusionSettings.Add(row);
        }

        row.Json = JsonSerializer.Serialize(dto, JsonCamel);
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        await QueueFusionAsync(owner, null, null, ct);
        return dto;
    }

    /// <summary>Back to the built-in rules; queues a full re-fusion</summary>
    public async Task<FusionSettingsDto> ResetFusionSettingsAsync(string owner, CancellationToken ct = default)
    {
        await _db.FusionSettings.Where(s => s.OwnerId == owner).ExecuteDeleteAsync(ct);
        await QueueFusionAsync(owner, null, null, ct);
        return ToDto(FusionRules.Default);
    }

    private static FusionSettingsDto ToDto(FusionRules rules) =>
        new(rules.Threshold, FusionRules.DefaultList.Select(d =>
        {
            var t = rules.For(d.Source);
            return new SourceToleranceDto(d.Source, t.TimeS, t.LocationM, t == d.Tolerance);
        }).ToList());

    // ---------------------------------------------------------------- the run

    /// <summary>
    /// Re-fuses the activities around a time window (and every fused activity
    /// that reaches into it). The caller holds the owner's gate.
    /// </summary>
    private async Task<FuseResult> FuseCoreAsync(string owner, DateTime? from, DateTime? to, CancellationToken ct)
    {
        // All or nothing (inside an ingest's transaction when there is one); a full
        // re-fuse on a small server needs more than the default 30 s per command
        var timeout = _db.Database.GetCommandTimeout();
        _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
        var own = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            var result = await FuseWindowAsync(owner, from, to, ct);
            if (own != null)
            {
                await own.CommitAsync(ct);
            }

            return result;
        }
        finally
        {
            _db.ChangeTracker.Clear();
            _db.Database.SetCommandTimeout(timeout);
            if (own != null)
            {
                await own.DisposeAsync();
            }
        }
    }

    private async Task<FuseResult> FuseWindowAsync(string owner, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var rules = await RulesAsync(owner, ct);
        var q = _db.Activities.AsNoTracking().Where(a => a.OwnerId == owner);
        if (from.HasValue && to.HasValue)
        {
            var f = from.Value.AddHours(-12);
            var t = to.Value.AddHours(12);
            var clusters = await q.Where(a => a.EndTime >= f && a.StartTime <= t && a.ClusterId != null)
                .Select(a => a.ClusterId).Distinct().ToListAsync(ct);
            q = q.Where(a => (a.EndTime >= f && a.StartTime <= t) || clusters.Contains(a.ClusterId));
        }

        var items = await q
            .Select(a => new FusionCandidate(a.Id, a.Source, a.Type, a.Name, a.StartTime, a.EndTime, a.DistanceM, a.ElevationGainM,
                a.AvgHeartRate, a.MaxHeartRate, a.MinLat, a.MinLon, a.MaxLat, a.MaxLon, a.PointCount, a.TypeByUser))
            .ToListAsync(ct);
        if (items.Count == 0)
        {
            return new FuseResult(0, 0, 0);
        }

        var ids = items.Select(i => i.Id).ToList();
        var decisions = await _db.FusionDecisions.AsNoTracking()
            .Where(d => d.OwnerId == owner && (ids.Contains(d.ActivityA) || ids.Contains(d.ActivityB)))
            .ToListAsync(ct);
        var together = decisions.Where(d => d.Kind == "together").Select(d => (d.ActivityA, d.ActivityB)).ToList();

        // Tracks only of the records that could match: compared at the same moments
        var samples = await SamplesAsync(Fusion.Candidates(items, rules).Concat(together.SelectMany(p => new[] { p.ActivityA, p.ActivityB })).Distinct().ToArray(), ct);
        var groups = Fusion.Group(items, together, decisions.Where(d => d.Kind == "apart").Select(d => (d.ActivityA, d.ActivityB)), rules, samples);

        var byId = items.ToDictionary(i => i.Id);
        var userJoined = together.SelectMany(p => new[] { p.ActivityA, p.ActivityB }).ToHashSet();
        var fused = groups.Select(g => Fusion.Fuse(g.Select(id => byId[id]).ToList(), rules, samples, userJoined)).ToList();

        // Health enhances activities without heart rate (not vehicles): average and max in their window
        var needHr = fused.Where(g => !g.AvgHeartRate.HasValue && !Vehicles.Contains(g.Type)).ToList();
        var hrById = await HeartRateAsync(owner, needHr, ct);

        // Replace the window's fusion: reset, then write fused and enhanced activities
        await _db.Activities.Where(a => a.OwnerId == owner && ids.Contains(a.Id) && a.ClusterId != null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ClusterId, (Guid?)null), ct);
        await _db.Fusions.Where(f => f.OwnerId == owner && ids.Contains(f.ActivityId)).ExecuteDeleteAsync(ct);
        int multi = 0, enhanced = 0, conflicts = 0;
        var memberIdList = new List<Guid>();
        var clusterIdList = new List<Guid>();
        foreach (var g in fused)
        {
            var health = hrById.GetValueOrDefault(g.PrimaryId);
            if (g.Members.Count == 1 && health == null)
            {
                continue;
            }

            var sources = g.Members.Select(m => m.Source).Distinct().OrderBy(Fusion.Rank).ToList();
            var fields = new Dictionary<string, string>(g.FieldSources);
            if (health != null)
            {
                fields["heartRate"] = health.Source;
                if (!sources.Contains(health.Source))
                {
                    sources.Add(health.Source);
                }

                enhanced++;
            }

            multi += g.Members.Count > 1 ? 1 : 0;
            conflicts += g.Conflicts.Count;
            foreach (var m in g.Members)
            {
                memberIdList.Add(m.Id);
                clusterIdList.Add(g.PrimaryId);
            }

            _db.Fusions.Add(new ActivityFusion
            {
                ActivityId = g.PrimaryId,
                OwnerId = owner,
                Type = g.Type,
                Name = g.Name,
                StartTime = g.Start,
                EndTime = g.End,
                DistanceM = g.DistanceM,
                ElevationGainM = g.ElevationGainM,
                AvgHeartRate = g.AvgHeartRate ?? health?.Avg,
                MaxHeartRate = g.MaxHeartRate ?? health?.Max,
                Sources = string.Join(",", sources),
                MemberCount = g.Members.Count,
                ConflictCount = g.Conflicts.Count,
                FieldSources = JsonSerializer.Serialize(fields),
                Conflicts = JsonSerializer.Serialize(g.Conflicts, JsonCamel),
                Evidence = JsonSerializer.Serialize(g.Evidence, JsonCamel),
                MatchConfidence = g.MatchConfidence
            });
        }

        if (memberIdList.Count > 0)
        {
            var memberArray = memberIdList.ToArray();
            var clusterArray = clusterIdList.ToArray();
            await _db.Database.ExecuteSqlAsync($@"
                UPDATE atlas.activities a SET ""ClusterId"" = v.c
                  FROM unnest({memberArray}, {clusterArray}) AS v(m, c)
                 WHERE a.""Id"" = v.m", ct);
        }

        await _db.SaveChangesAsync(ct);
        var result = new FuseResult(items.Count, multi, conflicts);
        _logger.LogInformation("Fusion: {Activities} records, {Fused} fused activities, {Enhanced} enhanced by health data, {Conflicts} conflicts",
            result.Activities, result.Fused, enhanced, result.Conflicts);
        return result;
    }

    private sealed class SampleRow
    {
        public Guid ActivityId { get; set; }
        public DateTime Time { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
    }

    private sealed class HeartRateRow
    {
        public Guid ActivityId { get; set; }
        public double Avg { get; set; }
        public double Max { get; set; }
        public string Source { get; set; } = string.Empty;
        public int Samples { get; set; }
        public double Minutes { get; set; }
    }

    private sealed record HeartRateStats(int Avg, int Max, string Source);

    /// <summary>About 60 evenly spaced points of each record's track</summary>
    private async Task<Dictionary<Guid, List<TrackSample>>> SamplesAsync(Guid[] ids, CancellationToken ct)
    {
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, List<TrackSample>>();
        }

        var rows = await _db.Database.SqlQuery<SampleRow>($@"
            SELECT x.""ActivityId"", x.""Time"", x.""Lat"", x.""Lon""
              FROM (SELECT p.""ActivityId"", p.""Time"", p.""Lat"", p.""Lon"",
                           row_number() OVER (PARTITION BY p.""ActivityId"" ORDER BY p.""Time"") AS rn
                      FROM atlas.track_points p WHERE p.""ActivityId"" = ANY({ids})) x
              JOIN atlas.activities a ON a.""Id"" = x.""ActivityId""
             WHERE (x.rn - 1) % GREATEST(1, a.""PointCount"" / 60) = 0").ToListAsync(ct);
        return rows.GroupBy(r => r.ActivityId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Time).Select(r => new TrackSample(r.Time, r.Lat, r.Lon)).ToList());
    }

    /// <summary>Average and maximum heart rate from health samples in each fused activity's time window</summary>
    private async Task<Dictionary<Guid, HeartRateStats>> HeartRateAsync(string owner, List<FusedGroup> groups, CancellationToken ct)
    {
        if (groups.Count == 0)
        {
            return new Dictionary<Guid, HeartRateStats>();
        }

        var ids = groups.Select(g => g.PrimaryId).ToArray();
        var starts = groups.Select(g => DateTime.SpecifyKind(g.Start, DateTimeKind.Utc)).ToArray();
        var ends = groups.Select(g => DateTime.SpecifyKind(g.End, DateTimeKind.Utc)).ToArray();
        var rows = await _db.Database.SqlQuery<HeartRateRow>($@"
            SELECT g.id AS ""ActivityId"", avg(h.""Value"") AS ""Avg"", max(h.""Value"") AS ""Max"", min(h.""Source"") AS ""Source"",
                   count(*)::int AS ""Samples"", max(extract(epoch FROM g.e - g.s) / 60)::float8 AS ""Minutes""
              FROM unnest({ids}, {starts}, {ends}) AS g(id, s, e)
              JOIN atlas.health_samples h ON h.""OwnerId"" = {owner} AND h.""Type"" = 'heart_rate'
                   AND h.""StartTime"" >= g.s AND h.""StartTime"" <= g.e
             GROUP BY g.id").ToListAsync(ct);
        // Only from a real series (at least one value per 5 minutes): a few scattered
        // values, often resting heart rate, would give a misleading average
        return rows.Where(r => r.Samples >= 3 && r.Samples >= r.Minutes / 5)
            .ToDictionary(r => r.ActivityId, r => new HeartRateStats((int)Math.Round(r.Avg), (int)Math.Round(r.Max), r.Source));
    }

    // ---------------------------------------------------------------- user decisions

    /// <summary>Joins two activities into one (remembered: re-fusion keeps them together)</summary>
    public async Task<ActivityDetail> MergeActivitiesAsync(string owner, Guid id, Guid otherId, CancellationToken ct = default)
    {
        var a = await MembersOfAsync(owner, id, ct);
        var b = await MembersOfAsync(owner, otherId, ct);
        if (a.Overlaps(b))
        {
            return await GetActivityAsync(owner, id, ct: ct);
        }

        var gate = Gates.GetOrAdd(owner, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Forget earlier splits between the two, then join them
            await _db.FusionDecisions.Where(d => d.OwnerId == owner && d.Kind == "apart"
                                                 && ((a.Contains(d.ActivityA) && b.Contains(d.ActivityB)) || (b.Contains(d.ActivityA) && a.Contains(d.ActivityB))))
                .ExecuteDeleteAsync(ct);
            await DecideAsync(owner, id, otherId, "together", ct);
            await FuseAroundAsync(owner, a.Concat(b).ToList(), ct);
        }
        finally
        {
            gate.Release();
        }

        return await GetActivityAsync(owner, id, ct: ct);
    }

    /// <summary>
    /// Takes one record out of a fused activity (remembered: re-fusion keeps it
    /// apart from the rest)
    /// </summary>
    /// <returns>The fused activity without it</returns>
    public async Task<ActivityDetail> SplitActivityAsync(string owner, Guid id, Guid memberId, CancellationToken ct = default)
    {
        var members = await MembersOfAsync(owner, id, ct);
        if (!members.Contains(memberId))
        {
            throw new AtlasNotFoundException("Source record");
        }

        var rest = members.Where(m => m != memberId).ToList();
        var gate = Gates.GetOrAdd(owner, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await _db.FusionDecisions.Where(d => d.OwnerId == owner && d.Kind == "together"
                                                 && (d.ActivityA == memberId || d.ActivityB == memberId))
                .ExecuteDeleteAsync(ct);
            foreach (var other in rest)
            {
                await DecideAsync(owner, memberId, other, "apart", ct);
            }

            await FuseAroundAsync(owner, members.ToList(), ct);
        }
        finally
        {
            gate.Release();
        }

        return await GetActivityAsync(owner, rest.Count > 0 ? rest[0] : memberId, ct: ct);
    }

    /// <summary>The record ids of the fused activity a record belongs to</summary>
    private async Task<HashSet<Guid>> MembersOfAsync(string owner, Guid id, CancellationToken ct)
    {
        var cluster = await _db.Activities.AsNoTracking().Where(a => a.Id == id && a.OwnerId == owner)
            .Select(a => new { a.Id, a.ClusterId }).FirstOrDefaultAsync(ct) ?? throw new AtlasNotFoundException("Activity");
        if (!cluster.ClusterId.HasValue)
        {
            return new HashSet<Guid> { id };
        }

        return (await _db.Activities.AsNoTracking().Where(a => a.OwnerId == owner && a.ClusterId == cluster.ClusterId)
            .Select(a => a.Id).ToListAsync(ct)).ToHashSet();
    }

    private async Task DecideAsync(string owner, Guid x, Guid y, string kind, CancellationToken ct)
    {
        var (a, b) = x.CompareTo(y) < 0 ? (x, y) : (y, x);
        await _db.FusionDecisions.Where(d => d.OwnerId == owner && d.ActivityA == a && d.ActivityB == b).ExecuteDeleteAsync(ct);
        _db.FusionDecisions.Add(new FusionDecision { OwnerId = owner, ActivityA = a, ActivityB = b, Kind = kind });
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    private async Task FuseAroundAsync(string owner, List<Guid> ids, CancellationToken ct)
    {
        var span = await _db.Activities.AsNoTracking().Where(a => a.OwnerId == owner && ids.Contains(a.Id))
            .GroupBy(_ => 1).Select(g => new { From = g.Min(a => a.StartTime), To = g.Max(a => a.EndTime) })
            .FirstOrDefaultAsync(ct);
        if (span != null)
        {
            await FuseCoreAsync(owner, span.From, span.To, ct);
        }
    }
}
