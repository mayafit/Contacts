/**
 * @fileoverview Insights: statistics, records, streaks, places, data quality and
 * proposals for a query, over the fused data
 * @module ContactsApi.Infrastructure.Atlas
 */
using Microsoft.EntityFrameworkCore;

namespace ContactsApi.Infrastructure.Atlas;

/// <summary>Insights for a query (see the contract in docs/planning/epic-11-fusion.md)</summary>
public record Insights(
    DateTime? From, DateTime? To, InsightTotals Totals, InsightTotals? Previous, List<TypeStats> ByType,
    InsightSeries Series, int[] ByWeekday, int[] ByHour, List<PlaceStat> TopPlaces, List<RecordStat> Records,
    InsightStreaks Streaks, InsightTravel Travel, InsightCoverage Coverage, List<Proposal> Proposals);

/// <summary>Sums over activities and visits</summary>
public record InsightTotals(int Activities, double DistanceKm, double Hours, double ClimbM, int Visits, int Places);

/// <summary>Sums of one activity type</summary>
public record TypeStats(string Type, int Count, double DistanceKm, double Hours, double ClimbM,
    int? AvgHeartRate, double? AvgSpeedKmh, double? LongestKm);

/// <summary>Distance, time and count per bucket and type</summary>
public record InsightSeries(string Bucket, List<InsightSeriesPoint> Points);

/// <summary>One bucket of one type</summary>
public record InsightSeriesPoint(DateTime T, string Type, double DistanceKm, double Hours, int Count);

/// <summary>A place and the time spent there</summary>
public record PlaceStat(Guid? PlaceId, string Name, double Lat, double Lon, int Visits, double Hours,
    DateTime FirstVisit, DateTime LastVisit, bool IsNew, bool Unnamed);

/// <summary>A personal best within the query</summary>
public record RecordStat(string Kind, string Type, string Label, string Value, Guid ActivityId, DateTime Date);

/// <summary>Active days in a row, and the gap since the last activity</summary>
public record InsightStreaks(int LongestActiveDays, DateTime? LongestActiveFrom, int? DaysSinceLast, int ActiveDays);

/// <summary>Flights and days away from home</summary>
public record InsightTravel(int Flights, double FlightKm, int DaysAway);

/// <summary>How complete and consistent the data is</summary>
public record InsightCoverage(int Sports, int WithHeartRate, int Recorded, int InferredOnly, int Fused, int WithConflicts,
    List<SourceCount> Sources);

/// <summary>Activities that a source contributes to</summary>
public record SourceCount(string Source, int Activities);

/// <summary>Something worth doing or knowing, with what to do about it</summary>
public record Proposal(string Kind, string Severity, string Title, string Text, ProposalAction? Action);

/// <summary>What the app does when a proposal's button is used</summary>
public record ProposalAction(string Kind, Guid? ActivityId = null, double? Lat = null, double? Lon = null, string? Name = null,
    List<string>? Types = null, bool? ConflictsOnly = null);

public partial class AtlasService
{
    private static readonly HashSet<string> Sports = new() { "ride", "run", "hike", "walk", "swim", "ski" };
    private static readonly HashSet<string> RhythmSports = new() { "ride", "run", "hike", "swim" };
    private static readonly HashSet<string> RecordedSources = new() { "sports-tracker", "gpx", "tcx", "google-fit" };
    private static readonly HashSet<string> UnnamedPlaces = new(StringComparer.OrdinalIgnoreCase) { "", "Place", "Saved place", "Searched place" };

    private static readonly Dictionary<string, string> TypeNames = new()
    {
        ["ride"] = "ride", ["run"] = "run", ["hike"] = "hike", ["walk"] = "walk", ["swim"] = "swim", ["ski"] = "ski day",
        ["drive"] = "drive", ["flight"] = "flight", ["transit"] = "transit trip", ["other"] = "activity"
    };

    /// <summary>One fused activity, only what insights need</summary>
    private sealed record InsightRow(Guid Id, string Type, DateTime Start, DateTime End, double? DistanceM, double? DurationS,
        double? ClimbM, int? AvgHr, int? MaxHr, string Sources, int Members, int Conflicts, int Points);

    private IQueryable<InsightRow> InsightRows(string owner, AtlasQuery q) =>
        TimeFilter(FilterActivities(owner, q).Where(_ => q.Activities), q)
            .Select(a => new InsightRow(a.Id,
                a.Fusion != null && a.Fusion.Type != null ? a.Fusion.Type : a.Type,
                a.Fusion != null ? a.Fusion.StartTime : a.StartTime,
                a.Fusion != null ? a.Fusion.EndTime : a.EndTime,
                a.DistanceM ?? (a.Fusion != null ? a.Fusion.DistanceM : null),
                a.DurationS,
                a.ElevationGainM ?? (a.Fusion != null ? a.Fusion.ElevationGainM : null),
                a.AvgHeartRate ?? (a.Fusion != null ? a.Fusion.AvgHeartRate : null),
                a.MaxHeartRate ?? (a.Fusion != null ? a.Fusion.MaxHeartRate : null),
                a.Fusion != null ? a.Fusion.Sources : a.Source,
                a.Fusion != null ? a.Fusion.MemberCount : 1,
                a.Fusion != null ? a.Fusion.ConflictCount : 0,
                a.PointCount));

    private static IQueryable<AtlasActivity> TimeFilter(IQueryable<AtlasActivity> aq, AtlasQuery q)
    {
        if (q.From.HasValue)
        {
            var from = Utc(q.From.Value);
            aq = aq.Where(a => a.EndTime >= from);
        }

        if (q.To.HasValue)
        {
            var to = Utc(q.To.Value);
            aq = aq.Where(a => a.StartTime <= to);
        }

        return aq;
    }

    private sealed record VisitRow(Guid? PlaceId, string? Name, double Lat, double Lon, DateTime Start, DateTime End);

    private async Task<List<VisitRow>> InsightVisitsAsync(string owner, AtlasQuery q, CancellationToken ct)
    {
        if (!q.Visits || q.ConflictsOnly || q.Types is { Count: > 0 })
        {
            return new List<VisitRow>();
        }

        var vq = FilterVisits(owner, q);
        if (q.From.HasValue)
        {
            var from = Utc(q.From.Value);
            vq = vq.Where(v => v.EndTime >= from);
        }

        if (q.To.HasValue)
        {
            var to = Utc(q.To.Value);
            vq = vq.Where(v => v.StartTime <= to);
        }

        return await vq.Select(v => new VisitRow(v.PlaceId, v.Place != null ? v.Place.Name : null, v.Lat, v.Lon, v.StartTime, v.EndTime))
            .ToListAsync(ct);
    }

    private static double Hours(InsightRow a) =>
        a.DurationS is > 0 ? a.DurationS.Value / 3600 : Math.Max(0, (a.End - a.Start).TotalHours);

    private static InsightTotals Totals(List<InsightRow> acts, List<VisitRow> visits) =>
        new(acts.Count,
            // Flights have their own line (travel): their thousands of km would drown the rest
            Math.Round(acts.Where(a => a.Type != "flight").Sum(a => a.DistanceM ?? 0) / 1000, 1),
            Math.Round(acts.Sum(Hours), 1),
            Math.Round(acts.Sum(a => a.ClimbM ?? 0)),
            visits.Count,
            visits.Select(v => v.PlaceId?.ToString() ?? $"{v.Lat:F3},{v.Lon:F3}").Distinct().Count());

    /// <summary>Statistics, records, places, data quality and proposals for a query</summary>
    /// <param name="timeZone">IANA zone for weekdays, hours and buckets (default UTC)</param>
    public async Task<Insights> InsightsAsync(string owner, AtlasQuery q, string? timeZone = null, CancellationToken ct = default)
    {
        var tz = timeZone != null && System.Text.RegularExpressions.Regex.IsMatch(timeZone, @"^[A-Za-z]+(/[A-Za-z0-9_+\-]+){0,2}$") ? timeZone : "UTC";
        var zone = FindZone(tz);
        DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

        var acts = await InsightRows(owner, q).ToListAsync(ct);
        var visits = await InsightVisitsAsync(owner, q, ct);
        var totals = Totals(acts, visits);

        // The period of the same length right before
        InsightTotals? previous = null;
        if (q.From.HasValue && q.To.HasValue)
        {
            var span = q.To.Value - q.From.Value;
            var before = CopyQuery(q, q.From.Value - span, q.From.Value);
            previous = Totals(await InsightRows(owner, before).ToListAsync(ct), await InsightVisitsAsync(owner, before, ct));
        }

        var byType = acts.GroupBy(a => a.Type)
            .Select(g =>
            {
                var withHr = g.Where(a => a.AvgHr.HasValue).ToList();
                var timed = g.Where(a => a.DistanceM is > 0 && Hours(a) > 0.05).ToList();
                return new TypeStats(g.Key, g.Count(),
                    Math.Round(g.Sum(a => a.DistanceM ?? 0) / 1000, 1),
                    Math.Round(g.Sum(Hours), 1),
                    Math.Round(g.Sum(a => a.ClimbM ?? 0)),
                    withHr.Count > 0 ? (int)Math.Round(withHr.Average(a => a.AvgHr!.Value)) : null,
                    timed.Count > 0 ? Math.Round(timed.Sum(a => a.DistanceM!.Value) / 1000 / timed.Sum(Hours), 1) : null,
                    g.Any(a => a.DistanceM is > 0) ? Math.Round(g.Max(a => a.DistanceM ?? 0) / 1000, 1) : null);
            })
            .OrderByDescending(t => t.Hours)
            .ToList();

        // Trend: days for up to ~6 weeks, weeks up to ~1.5 years, months beyond
        var first = q.From ?? (acts.Count > 0 ? acts.Min(a => a.Start) : DateTime.UtcNow);
        var last = q.To ?? (acts.Count > 0 ? acts.Max(a => a.End) : DateTime.UtcNow);
        var days = (last - first).TotalDays;
        var bucket = days <= 45 ? "day" : days <= 550 ? "week" : "month";
        var series = new InsightSeries(bucket, acts
            .Where(a => a.Type != "flight")
            .GroupBy(a => (T: BucketStart(a.Start, bucket, zone), a.Type))
            .Select(g => new InsightSeriesPoint(g.Key.T, g.Key.Type, Math.Round(g.Sum(a => a.DistanceM ?? 0) / 1000, 1),
                Math.Round(g.Sum(Hours), 2), g.Count()))
            .OrderBy(p => p.T)
            .ToList());

        var byWeekday = new int[7];
        var byHour = new int[24];
        foreach (var a in acts)
        {
            var l = Local(a.Start);
            byWeekday[((int)l.DayOfWeek + 6) % 7]++;
            byHour[l.Hour]++;
        }

        var (topPlaces, newPlaces, unnamedOften) = await TopPlacesAsync(owner, visits, q.From, ct);
        var records = Records(acts);
        var streaks = Streaks(acts.Where(a => Sports.Contains(a.Type)).ToList(), Local);
        var travel = await TravelAsync(owner, acts, visits, Local, ct);

        var sports = acts.Where(a => Sports.Contains(a.Type)).ToList();
        var sourceCounts = acts.SelectMany(a => SplitSources(a.Sources).Select(s => (a.Id, s)))
            .GroupBy(x => x.s).Select(g => new SourceCount(g.Key, g.Select(x => x.Id).Distinct().Count()))
            .OrderByDescending(s => s.Activities).ToList();
        var coverage = new InsightCoverage(
            sports.Count,
            sports.Count(a => a.AvgHr.HasValue),
            sports.Count(a => SplitSources(a.Sources).Any(RecordedSources.Contains)),
            sports.Count(a => a.Sources == "google-timeline"),
            acts.Count(a => a.Members > 1),
            acts.Count(a => a.Conflicts > 0),
            sourceCounts);

        var proposals = await ProposalsAsync(owner, q, records, unnamedOften, newPlaces, coverage, travel, ct);
        return new Insights(q.From, q.To, totals, previous, byType, series, byWeekday, byHour, topPlaces, records, streaks,
            travel, coverage, proposals);
    }

    private static AtlasQuery CopyQuery(AtlasQuery q, DateTime? from, DateTime? to) => new()
    {
        From = from, To = to, MinLat = q.MinLat, MinLon = q.MinLon, MaxLat = q.MaxLat, MaxLon = q.MaxLon,
        Types = q.Types, Text = q.Text, Activities = q.Activities, Visits = q.Visits, ConflictsOnly = q.ConflictsOnly
    };

    /// <param name="from">The query's start: places first visited after it are new (none without a start)</param>
    /// <returns>The top places by time, how many places are new, and unnamed places visited at least 5 times (most visits first)</returns>
    private async Task<(List<PlaceStat> Top, int NewPlaces, List<PlaceStat> UnnamedOften)> TopPlacesAsync(
        string owner, List<VisitRow> visits, DateTime? from, CancellationToken ct)
    {
        var groups = visits
            .GroupBy(v => v.PlaceId?.ToString() ?? $"{v.Lat:F3},{v.Lon:F3}")
            .Select(g => new
            {
                g.First().PlaceId,
                Name = g.First().Name,
                Lat = g.Average(v => v.Lat),
                Lon = g.Average(v => v.Lon),
                Visits = g.Count(),
                Hours = g.Sum(v => Math.Max(0, (v.End - v.Start).TotalHours)),
                First = g.Min(v => v.Start),
                Last = g.Max(v => v.End)
            })
            .ToList();

        // First visit ever, to tell new places
        var ids = groups.Where(g => g.PlaceId.HasValue).Select(g => g.PlaceId!.Value).ToList();
        var firstEver = ids.Count == 0
            ? new Dictionary<Guid, DateTime>()
            : await _db.Visits.AsNoTracking().Where(v => v.OwnerId == owner && v.PlaceId != null && ids.Contains(v.PlaceId.Value))
                .GroupBy(v => v.PlaceId!.Value).Select(g => new { Id = g.Key, First = g.Min(v => v.StartTime) })
                .ToDictionaryAsync(x => x.Id, x => x.First, ct);
        bool IsNew(Guid? id) => from.HasValue && id.HasValue && firstEver.TryGetValue(id.Value, out var f) && f >= Utc(from.Value);
        var newPlaces = groups.Count(g => IsNew(g.PlaceId));

        PlaceStat Stat(Guid? placeId, string? name, double lat, double lon, int count, double hours, DateTime first, DateTime last)
        {
            var ever = placeId.HasValue && firstEver.TryGetValue(placeId.Value, out var f) ? f : first;
            var unnamed = UnnamedPlaces.Contains(name ?? string.Empty);
            return new PlaceStat(placeId, unnamed ? "Unnamed place" : name!, Math.Round(lat, 5), Math.Round(lon, 5),
                count, Math.Round(hours, 1), ever, last, IsNew(placeId), unnamed);
        }

        var unnamedOften = groups
            .Where(g => UnnamedPlaces.Contains(g.Name ?? string.Empty) && g.Visits >= 5)
            .OrderByDescending(g => g.Visits)
            .Take(2)
            .Select(g => Stat(g.PlaceId, g.Name, g.Lat, g.Lon, g.Visits, g.Hours, g.First, g.Last))
            .ToList();
        var top = groups
            .OrderByDescending(g => g.Hours)
            .Take(10)
            .Select(g => Stat(g.PlaceId, g.Name, g.Lat, g.Lon, g.Visits, g.Hours, g.First, g.Last))
            .ToList();
        return (top, newPlaces, unnamedOften);
    }

    private static List<RecordStat> Records(List<InsightRow> acts)
    {
        var records = new List<RecordStat>();
        foreach (var g in acts.Where(a => Sports.Contains(a.Type)).GroupBy(a => a.Type))
        {
            var name = TypeNames.GetValueOrDefault(g.Key, g.Key);
            var longest = g.Where(a => a.DistanceM is > 0).MaxBy(a => a.DistanceM);
            if (longest != null)
            {
                records.Add(new RecordStat("longest", g.Key, $"Longest {name}", $"{longest.DistanceM!.Value / 1000:0.0} km", longest.Id, longest.Start));
            }

            var longestTime = g.MaxBy(Hours);
            if (longestTime != null && Hours(longestTime) > 0)
            {
                records.Add(new RecordStat("longestTime", g.Key, $"Longest {name} by time", Duration(Hours(longestTime)), longestTime.Id, longestTime.Start));
            }

            var climb = g.Where(a => a.ClimbM is > 0).MaxBy(a => a.ClimbM);
            if (climb != null)
            {
                records.Add(new RecordStat("climb", g.Key, $"Biggest climb ({name})", $"{climb.ClimbM!.Value:0} m", climb.Id, climb.Start));
            }

            // Speed only from real recordings of at least 1 km and 5 minutes
            var fastest = g.Where(a => a.Points > 1 && a.DistanceM >= 1000 && Hours(a) >= 5.0 / 60)
                .MaxBy(a => a.DistanceM!.Value / Hours(a));
            if (fastest != null && g.Key is "ride" or "run" or "swim" or "ski")
            {
                var kmh = fastest.DistanceM!.Value / 1000 / Hours(fastest);
                records.Add(new RecordStat("fastest", g.Key, $"Fastest {name}",
                    g.Key == "run" ? $"{Pace(kmh)} /km" : $"{kmh:0.0} km/h", fastest.Id, fastest.Start));
            }

            var hr = g.Where(a => a.MaxHr.HasValue).MaxBy(a => a.MaxHr);
            if (hr != null)
            {
                records.Add(new RecordStat("maxHeartRate", g.Key, $"Highest heart rate ({name})", $"{hr.MaxHr} bpm", hr.Id, hr.Start));
            }
        }

        return records;
    }

    private static InsightStreaks Streaks(List<InsightRow> sports, Func<DateTime, DateTime> local)
    {
        var dates = sports.Select(a => local(a.Start).Date).Distinct().OrderBy(d => d).ToList();
        int best = 0, run = 0;
        DateTime? bestFrom = null, runFrom = null;
        for (var i = 0; i < dates.Count; i++)
        {
            if (i > 0 && (dates[i] - dates[i - 1]).TotalDays == 1)
            {
                run++;
            }
            else
            {
                run = 1;
                runFrom = dates[i];
            }

            if (run > best)
            {
                best = run;
                bestFrom = runFrom;
            }
        }

        int? since = dates.Count > 0 ? (int)(local(DateTime.UtcNow).Date - dates[^1]).TotalDays : null;
        return new InsightStreaks(best, bestFrom, since, dates.Count);
    }

    private async Task<InsightTravel> TravelAsync(string owner, List<InsightRow> acts, List<VisitRow> visits,
        Func<DateTime, DateTime> local, CancellationToken ct)
    {
        var flights = acts.Where(a => a.Type == "flight").ToList();
        var home = await _db.Places.AsNoTracking()
            .Where(p => p.OwnerId == owner && (p.Name == "Home" || p.Category == "home" || p.Category == "inferred_home"))
            .Select(p => new { p.Lat, p.Lon, Visits = _db.Visits.Count(v => v.PlaceId == p.Id) })
            .OrderByDescending(p => p.Visits)
            .FirstOrDefaultAsync(ct);
        var away = home == null
            ? 0
            : visits.Where(v => Geo.Haversine(home.Lat, home.Lon, v.Lat, v.Lon) > 100_000)
                .Select(v => local(v.Start).Date).Distinct().Count();
        return new InsightTravel(flights.Count, Math.Round(flights.Sum(f => f.DistanceM ?? 0) / 1000), away);
    }

    private async Task<List<Proposal>> ProposalsAsync(string owner, AtlasQuery q, List<RecordStat> records,
        List<PlaceStat> unnamedOften, int newPlaces, InsightCoverage coverage, InsightTravel travel, CancellationToken ct)
    {
        var proposals = new List<Proposal>();
        var now = DateTime.UtcNow;
        var reachesToday = !q.To.HasValue || q.To.Value >= now.AddDays(-1);

        // Rhythm and load: from the last 180 days, whatever the query (only when it reaches today)
        if (reachesToday)
        {
            var recent = await InsightRows(owner, CopyQuery(new AtlasQuery(), now.AddDays(-180), now)).ToListAsync(ct);
            foreach (var g in recent.Where(a => RhythmSports.Contains(a.Type)).GroupBy(a => a.Type))
            {
                var starts = g.Select(a => a.Start).OrderBy(t => t).ToList();
                if (starts.Count < 4)
                {
                    continue;
                }

                var gaps = starts.Zip(starts.Skip(1), (a, b) => (b - a).TotalDays).OrderBy(d => d).ToList();
                var median = gaps[gaps.Count / 2];
                var since = (now - starts[^1]).TotalDays;
                if (since >= 7 && since > median * 2)
                {
                    var name = TypeNames.GetValueOrDefault(g.Key, g.Key);
                    proposals.Add(new Proposal("rhythm", "tip", $"{since:0} days since your last {name}",
                        $"You usually go every {Math.Max(1, median):0} days ({starts.Count} times in the last six months).",
                        new ProposalAction("filter", Types: new List<string> { g.Key })));
                }
            }

            var moving = recent.Where(a => Sports.Contains(a.Type)).ToList();
            var thisWeek = moving.Where(a => a.Start >= now.AddDays(-7)).Sum(a => a.DistanceM ?? 0) / 1000;
            var before = moving.Where(a => a.Start >= now.AddDays(-35) && a.Start < now.AddDays(-7)).Sum(a => a.DistanceM ?? 0) / 1000 / 4;
            if (before > 0 && thisWeek > before * 1.5)
            {
                proposals.Add(new Proposal("load-high", "warning", "A big week",
                    $"{thisWeek:0} km in the last 7 days, {thisWeek / before:0.0}× your weekly average of the 4 weeks before ({before:0} km). Leave room to recover.",
                    null));
            }
            else if (before >= 10 && thisWeek < before * 0.5)
            {
                proposals.Add(new Proposal("load-low", "tip", "A quiet week",
                    $"{thisWeek:0} km in the last 7 days against a weekly average of {before:0} km in the 4 weeks before.", null));
            }
        }

        if (coverage.WithConflicts > 0)
        {
            proposals.Add(new Proposal("conflicts", "warning", $"{coverage.WithConflicts} activities where the sources disagree",
                "Type, distance, times or tracks differ between sources. Review them: split what is not the same activity.",
                new ProposalAction("filter", ConflictsOnly: true)));
        }

        var noHr = coverage.Sports - coverage.WithHeartRate;
        if (noHr >= 5)
        {
            proposals.Add(new Proposal("heart-rate", "tip", $"{noHr} activities without heart rate",
                "Import heart rate from Fitbit or Google Fit, or sync Sports Tracker workouts recorded with a heart-rate strap: fusion adds it to the matching activities.",
                new ProposalAction("import")));
        }

        foreach (var p in unnamedOften)
        {
            proposals.Add(new Proposal("unnamed-place", "tip", $"An unnamed place you visited {p.Visits} times",
                $"{p.Hours:0} hours there in this period. Give it a name so searches and insights can use it.",
                new ProposalAction("place", Lat: p.Lat, Lon: p.Lon)));
        }

        if (newPlaces >= 3)
        {
            proposals.Add(new Proposal("new-places", "info", $"{newPlaces} new places", "Places you visited for the first time in this period.", null));
        }

        // Bests of the query that are also all-time bests
        if (q.From.HasValue || q.To.HasValue)
        {
            foreach (var r in records.Where(r => r.Kind == "longest"))
            {
                var best = await FilterActivities(owner, new AtlasQuery())
                    .Where(a => (a.Fusion != null && a.Fusion.Type != null ? a.Fusion.Type : a.Type) == r.Type)
                    .Where(a => (a.DistanceM ?? (a.Fusion != null ? a.Fusion.DistanceM : null)) != null)
                    .OrderByDescending(a => a.DistanceM ?? (a.Fusion != null ? a.Fusion.DistanceM : null))
                    .ThenBy(a => a.StartTime)
                    .Select(a => a.Id).FirstOrDefaultAsync(ct);
                if (best == r.ActivityId)
                {
                    proposals.Add(new Proposal("record", "info", $"All-time best: {r.Label.ToLowerInvariant()}",
                        $"{r.Value} on {r.Date:d MMM yyyy}, your longest ever.", new ProposalAction("activity", ActivityId: r.ActivityId)));
                }
            }
        }

        if (travel.DaysAway >= 3)
        {
            proposals.Add(new Proposal("away", "info", $"{travel.DaysAway} days away from home",
                travel.Flights > 0 ? $"With {travel.Flights} flights ({travel.FlightKm:0} km)." : "More than 100 km from home.", null));
        }

        var order = new Dictionary<string, int> { ["warning"] = 0, ["tip"] = 1, ["info"] = 2 };
        return proposals.OrderBy(p => order[p.Severity]).ToList();
    }

    internal static string Duration(double hours)
    {
        var minutes = (int)Math.Round(hours * 60);
        return minutes >= 60 ? $"{minutes / 60}:{minutes % 60:00} h" : $"{minutes} min";
    }

    internal static string Pace(double kmh)
    {
        var seconds = (int)Math.Round(3600 / kmh);
        return $"{seconds / 60}:{seconds % 60:00}";
    }
}
