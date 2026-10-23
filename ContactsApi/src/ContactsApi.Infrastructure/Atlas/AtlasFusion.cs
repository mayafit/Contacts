/**
 * @fileoverview Fusion: records from several sources that describe the same
 * real-world activity become one activity, with provenance and conflicts
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

/// <summary>One fused activity: its primary record, members and fused values</summary>
public record FusedGroup(
    Guid PrimaryId, List<FusionCandidate> Members, string Type, string? Name, DateTime Start, DateTime End,
    double? DistanceM, double? ElevationGainM, int? AvgHeartRate, int? MaxHeartRate,
    Dictionary<string, string> FieldSources, List<FusionConflict> Conflicts);

/// <summary>
/// Fusion rules, without the database: which records are the same activity
/// (<see cref="Group"/>) and what the fused activity looks like (<see cref="Fuse"/>).
/// </summary>
public static class Fusion
{
    /// <summary>Label of a source in conflict messages</summary>
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
    /// Whether two records describe the same activity: their times overlap by at
    /// least half of the shorter one and a quarter of the longer one (a short walk
    /// inside a long drive is not the drive), and their tracks (when both have
    /// one) are in the same area. Two records of one source are only the same when their
    /// times match (the same recording imported twice, e.g. a file and a sync).
    /// </summary>
    public static bool Same(FusionCandidate a, FusionCandidate b)
    {
        if (a.Id == b.Id)
        {
            return false;
        }

        if (a.Source == b.Source)
        {
            return Math.Abs((a.Start - b.Start).TotalSeconds) <= 120 && Math.Abs((a.End - b.End).TotalSeconds) <= 300;
        }

        var overlap = (Min(a.End, b.End) - Max(a.Start, b.Start)).TotalSeconds;
        var shortest = Math.Min((a.End - a.Start).TotalSeconds, (b.End - b.Start).TotalSeconds);
        if (shortest < 60)
        {
            // A moment (a session without a duration) belongs to what it falls within
            var (point, span) = (a.End - a.Start).TotalSeconds < 60 ? (a, b) : (b, a);
            if (point.Start < span.Start.AddMinutes(-1) || point.Start > span.End.AddMinutes(1))
            {
                return false;
            }
        }
        else if (overlap < shortest * 0.5
                 || overlap < Math.Max((a.End - a.Start).TotalSeconds, (b.End - b.Start).TotalSeconds) * 0.25)
        {
            return false;
        }

        if (a.MinLat.HasValue && b.MinLat.HasValue)
        {
            // Bounding boxes, widened by ~1 km, must touch
            const double pad = 0.01;
            return a.MinLat - pad <= b.MaxLat && b.MinLat - pad <= a.MaxLat
                   && a.MinLon - pad <= b.MaxLon && b.MinLon - pad <= a.MaxLon;
        }

        return true;
    }

    /// <summary>
    /// Groups records into real-world activities. User decisions win: pairs
    /// marked "together" are always joined, pairs marked "apart" never end up
    /// in one group (directly or through others).
    /// </summary>
    /// <param name="items">Records (any order)</param>
    /// <param name="together">Pairs the user merged</param>
    /// <param name="apart">Pairs the user split</param>
    /// <returns>Groups, each a list of record ids (singletons included)</returns>
    public static List<List<Guid>> Group(
        IReadOnlyList<FusionCandidate> items,
        IEnumerable<(Guid A, Guid B)> together,
        IEnumerable<(Guid A, Guid B)> apart)
    {
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

        bool Union(Guid x, Guid y, bool force)
        {
            var rx = Find(x);
            var ry = Find(y);
            if (rx == ry)
            {
                return true;
            }

            // Never join what the user split, nor two different recordings of one
            // source (a long Timeline segment overlapping two separate rides)
            if (!force && members[rx].Any(m => members[ry].Any(n =>
                    apartSet.Contains(Key(m, n)) || (byId[m].Source == byId[n].Source && !Same(byId[m], byId[n])))))
            {
                return false;
            }

            if (members[rx].Count < members[ry].Count)
            {
                (rx, ry) = (ry, rx);
            }

            parent[ry] = rx;
            members[rx].UnionWith(members[ry]);
            members.Remove(ry);
            return true;
        }

        foreach (var (a, b) in together)
        {
            if (parent.ContainsKey(a) && parent.ContainsKey(b))
            {
                Union(a, b, force: true);
            }
        }

        // Candidate pairs by time (sorted sweep), strongest overlap first
        var sorted = items.OrderBy(i => i.Start).ToList();
        var pairs = new List<(FusionCandidate A, FusionCandidate B, double Score)>();
        for (var i = 0; i < sorted.Count; i++)
        {
            for (var j = i + 1; j < sorted.Count && sorted[j].Start < sorted[i].End.AddMinutes(5); j++)
            {
                if (!apartSet.Contains(Key(sorted[i].Id, sorted[j].Id)) && Same(sorted[i], sorted[j]))
                {
                    var overlap = (Min(sorted[i].End, sorted[j].End) - Max(sorted[i].Start, sorted[j].Start)).TotalSeconds;
                    pairs.Add((sorted[i], sorted[j], overlap));
                }
            }
        }

        foreach (var (a, b, _) in pairs.OrderByDescending(p => p.Score))
        {
            Union(a.Id, b.Id, force: false);
        }

        return members.Values.Select(m => m.ToList()).ToList();
    }

    /// <summary>
    /// The fused activity of a group: the best record is the primary (a real
    /// track from the most trusted source); every other value comes from the most
    /// trusted source that has it; disagreements become conflicts.
    /// </summary>
    public static FusedGroup Fuse(IReadOnlyList<FusionCandidate> group)
    {
        var ordered = group.OrderBy(m => m.PointCount > 1 ? 0 : 1).ThenBy(m => Rank(m.Source)).ThenByDescending(m => m.PointCount).ToList();
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

        return new FusedGroup(primary.Id, ordered, typed.Type, named?.Name, start, end,
            distance?.DistanceM, climb?.ElevationGainM, hr?.AvgHeartRate, hr?.MaxHeartRate,
            fields, Conflicts(group, typed.Type, distance?.DistanceM, start));
    }

    private static List<FusionConflict> Conflicts(IReadOnlyList<FusionCandidate> group, string type, double? distance, DateTime start)
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

        var late = group.Where(m => Math.Abs((m.Start - start).TotalMinutes) > 10).ToList();
        if (late.Count > 0)
        {
            var values = group.Select(m => new SourceValue(m.Source, m.Start.ToString("u"))).ToList();
            conflicts.Add(new FusionConflict("startTime",
                $"Start times differ by up to {late.Max(m => Math.Abs((m.Start - start).TotalMinutes)):0} min",
                values, start.ToString("u")));
        }

        return conflicts;
    }

    private static (Guid, Guid) Key(Guid a, Guid b) => a.CompareTo(b) < 0 ? (a, b) : (b, a);

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}

public partial class AtlasService
{
    private static int SourceRank(string source) => Fusion.Rank(source);

    /// <summary>Re-fuses all of the owner's activities</summary>
    public async Task<FuseResult> FuseAsync(string owner, CancellationToken ct = default)
    {
        var gate = Gates.GetOrAdd(owner, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await FuseCoreAsync(owner, null, null, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Re-fuses the activities around a time window (and every fused activity
    /// that reaches into it). The caller holds the owner's gate.
    /// </summary>
    private async Task<FuseResult> FuseCoreAsync(string owner, DateTime? from, DateTime? to, CancellationToken ct)
    {
        // All or nothing (inside an ingest's transaction when there is one)
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
            if (own != null)
            {
                await own.DisposeAsync();
            }
        }
    }

    private async Task<FuseResult> FuseWindowAsync(string owner, DateTime? from, DateTime? to, CancellationToken ct)
    {var q = _db.Activities.AsNoTracking().Where(a => a.OwnerId == owner);
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
        var groups = Fusion.Group(items,
            decisions.Where(d => d.Kind == "together").Select(d => (d.ActivityA, d.ActivityB)),
            decisions.Where(d => d.Kind == "apart").Select(d => (d.ActivityA, d.ActivityB)));

        var byId = items.ToDictionary(i => i.Id);
        var fused = groups.Where(g => g.Count > 1).Select(g => Fusion.Fuse(g.Select(id => byId[id]).ToList())).ToList();

        // Replace the window's fusion: reset, then write the groups
        await _db.Activities.Where(a => a.OwnerId == owner && ids.Contains(a.Id) && a.ClusterId != null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ClusterId, (Guid?)null), ct);
        await _db.Fusions.Where(f => f.OwnerId == owner && ids.Contains(f.ActivityId)).ExecuteDeleteAsync(ct);
        foreach (var g in fused)
        {
            var memberIds = g.Members.Select(m => m.Id).ToList();
            await _db.Activities.Where(a => memberIds.Contains(a.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ClusterId, g.PrimaryId), ct);
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
                AvgHeartRate = g.AvgHeartRate,
                MaxHeartRate = g.MaxHeartRate,
                Sources = string.Join(",", g.Members.Select(m => m.Source).Distinct().OrderBy(Fusion.Rank)),
                MemberCount = g.Members.Count,
                ConflictCount = g.Conflicts.Count,
                FieldSources = JsonSerializer.Serialize(g.FieldSources),
                Conflicts = JsonSerializer.Serialize(g.Conflicts, JsonCamel)
            });
        }

        await _db.SaveChangesAsync(ct);
        var result = new FuseResult(items.Count, fused.Count, fused.Sum(g => g.Conflicts.Count));
        _logger.LogInformation("Fusion: {Activities} records, {Fused} fused activities, {Conflicts} conflicts",
            result.Activities, result.Fused, result.Conflicts);
        return result;
    }

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
