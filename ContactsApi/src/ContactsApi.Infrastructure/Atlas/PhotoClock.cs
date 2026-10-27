/**
 * @fileoverview Whether a photo library's times are UTC or the camera's local clock
 * @module ContactsApi.Infrastructure.Atlas
 */
namespace ContactsApi.Infrastructure.Atlas;

/// <summary>
/// Whether the library's photo times are UTC or the camera's clock (local time
/// written as if it were UTC). A photo with a position has two readings: as UTC,
/// or as the local time of the time zone it was taken in. The user's own history
/// (visits and activities) usually puts them where the photo was taken at only one
/// of the two; when both or neither fit (stayed put, nothing known) it says nothing.
/// </summary>
public static class PhotoClock
{
    /// <summary>Within this distance the history agrees with the photo</summary>
    public const double AgreeM = 1000;

    /// <summary>Beyond this distance the history contradicts the photo</summary>
    public const double FarM = 5000;

    /// <summary>Where the user was over a time (a visit's point, or an activity's area)</summary>
    public record Fix(DateTime Start, DateTime End, double MinLat, double MinLon, double MaxLat, double MaxLon);

    /// <summary>A photo as the clock check needs it</summary>
    public record Sample(DateTime Raw, double? Lat, double? Lon, bool IsVideo);

    /// <summary>How the times were read, and what said so</summary>
    public record Result(int[] Offsets, int LocalVotes, int UtcVotes, bool LocalClock, bool VideoLocalClock);

    /// <summary>The user's whereabouts, by time</summary>
    public class History
    {
        private readonly List<Fix> _fixes;
        private readonly TimeSpan _longest;

        /// <summary>Creates it (fixes longer than a day say little and are left out)</summary>
        public History(IEnumerable<Fix> fixes)
        {
            _fixes = fixes.Where(f => f.End >= f.Start && f.End - f.Start <= TimeSpan.FromDays(1)).OrderBy(f => f.Start).ToList();
            _longest = _fixes.Count == 0 ? TimeSpan.Zero : _fixes.Max(f => f.End - f.Start);
        }

        /// <summary>The distance from the point to where the user was at the time; null when unknown</summary>
        public double? DistanceAt(DateTime t, double lat, double lon)
        {
            // Last fix starting at or before t, then back while one could still cover t
            int lo = 0, hi = _fixes.Count - 1, last = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (_fixes[mid].Start <= t)
                {
                    last = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            double? best = null;
            for (var i = last; i >= 0 && _fixes[i].Start >= t - _longest; i--)
            {
                var f = _fixes[i];
                if (f.End < t)
                {
                    continue;
                }

                var d = Geo.Haversine(lat, lon, Math.Clamp(lat, f.MinLat, f.MaxLat), Math.Clamp(lon, f.MinLon, f.MaxLon));
                best = best.HasValue ? Math.Min(best.Value, d) : d;
            }

            return best;
        }
    }

    /// <summary>
    /// Minutes the local time at a place is ahead of UTC, for a local time there
    /// (daylight saving included); null when the zone is not known
    /// </summary>
    public static int? LocalOffset(double lat, double lon, DateTime localTime)
    {
        try
        {
            var id = GeoTimeZone.TimeZoneLookup.GetTimeZone(lat, lon).Result;
            var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return (int)Math.Round(zone.GetUtcOffset(DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified)).TotalMinutes);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The offset of each photo (minutes to subtract from its time). Photos and
    /// videos are judged apart (phones write their times differently). A photo
    /// votes for UTC when the history agrees with its time as it is and is far off
    /// at the local reading; for the camera's clock the other way round. The camera's
    /// clock needs a clear majority; then each photo with a position takes its place's
    /// offset, one without that of the nearest photo in time that has one.
    /// </summary>
    /// <param name="photos">The photos</param>
    /// <param name="history">Where the user was</param>
    /// <param name="zoneOffset">The local offset at a place and local time (default: <see cref="LocalOffset"/>)</param>
    public static Result Decide(IReadOnlyList<Sample> photos, History history, Func<double, double, DateTime, int?>? zoneOffset = null)
    {
        zoneOffset ??= LocalOffset;
        var offsets = new int[photos.Count];
        int localVotes = 0, utcVotes = 0;
        var local = new bool[2];
        foreach (var video in new[] { false, true })
        {
            var group = Enumerable.Range(0, photos.Count).Where(i => photos[i].IsVideo == video).ToList();
            // The local offset of every photo with a position
            var own = new Dictionary<int, int>();
            int l = 0, u = 0;
            foreach (var i in group)
            {
                var p = photos[i];
                if (p.Lat is not { } lat || p.Lon is not { } lon || zoneOffset(lat, lon, p.Raw) is not { } k)
                {
                    continue;
                }

                own[i] = k;
                if (k == 0)
                {
                    // Both readings are the same
                    continue;
                }

                if (history.DistanceAt(p.Raw, lat, lon) is not { } asUtc
                    || history.DistanceAt(p.Raw.AddMinutes(-k), lat, lon) is not { } asLocal)
                {
                    continue;
                }

                if (asUtc <= AgreeM && asLocal > FarM)
                {
                    u++;
                }
                else if (asLocal <= AgreeM && asUtc > FarM)
                {
                    l++;
                }
            }

            localVotes += l;
            utcVotes += u;
            var isLocal = l >= 5 && l > 3 * u;
            local[video ? 1 : 0] = isLocal;
            if (!isLocal || own.Count == 0)
            {
                continue;
            }

            // Without a position: the nearest photo in time that has one (within 3 days), else the usual offset
            var usual = own.Values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
            var known = own.Keys.OrderBy(i => photos[i].Raw).ToList();
            var times = known.Select(i => photos[i].Raw).ToList();
            foreach (var i in group)
            {
                if (own.TryGetValue(i, out var k))
                {
                    offsets[i] = k;
                    continue;
                }

                var at = times.BinarySearch(photos[i].Raw);
                if (at < 0)
                {
                    at = ~at;
                }

                var best = usual;
                var bestGap = TimeSpan.FromDays(3);
                foreach (var j in new[] { at - 1, at })
                {
                    if (j >= 0 && j < times.Count && (times[j] - photos[i].Raw).Duration() <= bestGap)
                    {
                        bestGap = (times[j] - photos[i].Raw).Duration();
                        best = own[known[j]];
                    }
                }

                offsets[i] = best;
            }
        }

        return new Result(offsets, localVotes, utcVotes, local[0], local[1]);
    }
}
