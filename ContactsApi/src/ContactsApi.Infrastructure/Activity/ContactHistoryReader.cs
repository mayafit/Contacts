/**
 * @fileoverview A Contact's whole history with the user, without a date limit:
 * how many emails each way and meetings, and when the first and the last were (AD-47)
 * @module ContactsApi.Infrastructure.Activity
 */
using System.Text.RegularExpressions;
using Google.Apis.Calendar.v3;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using CalendarEvent = global::Google.Apis.Calendar.v3.Data.Event;

namespace ContactsApi.Infrastructure.Activity;

/// <summary>Emails one way: how many, and the dates of the first and the last</summary>
public class MailCount
{
    /// <summary>Messages found</summary>
    public int Count { get; set; }

    /// <summary>The listing stopped at <see cref="ContactHistoryReader.MaxIds"/></summary>
    public bool Capped { get; set; }

    /// <summary>The oldest</summary>
    public DateTime? First { get; set; }

    /// <summary>The newest</summary>
    public DateTime? Last { get; set; }
}

/// <summary>Meetings with the person</summary>
public class MeetingCount
{
    /// <summary>Past meetings</summary>
    public int Count { get; set; }

    /// <summary>The first past meeting</summary>
    public DateTime? First { get; set; }

    /// <summary>The last past meeting</summary>
    public DateTime? Last { get; set; }

    /// <summary>The next one</summary>
    public DateTime? Next { get; set; }

    /// <summary>An address had more events than were read</summary>
    public bool Capped { get; set; }
}

/// <summary>What the user has with one Contact, at any date (AD-47 rule 6)</summary>
public class ContactHistory
{
    /// <summary>When it was searched</summary>
    public DateTime CheckedAt { get; set; }

    /// <summary><c>searched</c>, <c>not_allowed</c> without email content (T2), or <c>failed</c></summary>
    public string Mail { get; set; } = "not_allowed";

    /// <summary><c>searched</c>, <c>not_allowed</c> without Calendar, or <c>failed</c></summary>
    public string Calendar { get; set; } = "not_allowed";

    /// <summary>Why a part failed (busy, too slow…)</summary>
    public string? Error { get; set; }

    /// <summary>Emails from them</summary>
    public MailCount? Received { get; set; }

    /// <summary>The user's emails to them</summary>
    public MailCount? Sent { get; set; }

    /// <summary>Meetings</summary>
    public MeetingCount? Meetings { get; set; }
}

/// <summary>
/// Counts a Contact's mail by Gmail search (needs gmail.readonly: the metadata
/// scope refuses <c>q</c>) and their meetings by Calendar search. Only ids are
/// listed, and only the newest and the oldest message's date is read.
/// </summary>
public class ContactHistoryReader
{
    /// <summary>Ids listed per query at most; more is shown as "20,000+"</summary>
    public const int MaxIds = 20_000;

    /// <summary>Addresses per Contact</summary>
    public const int MaxAddresses = 10;

    /// <summary>Calendar pages per address</summary>
    private const int MaxEventPages = 4;

    /// <summary>Events with more people than this are not meetings with a person (as the scan)</summary>
    private const int MaxMeetingSize = 20;

    /// <summary>How far ahead meetings are read: bounds endless recurrences, as the scan</summary>
    public static readonly TimeSpan Ahead = TimeSpan.FromDays(60);

    private static readonly Regex Plain = new(
        @"^[a-z0-9._%+'\-]+@[a-z0-9\-]+(\.[a-z0-9\-]+)+$", RegexOptions.Compiled);

    private readonly GmailService _gmail;
    private readonly CalendarService _calendar;

    /// <summary>Creates a reader for one user</summary>
    /// <param name="accessToken">Returns a valid access token</param>
    public ContactHistoryReader(Func<CancellationToken, Task<string>> accessToken)
    {
        var init = new BaseClientService.Initializer
        {
            HttpClientInitializer = new FreshTokenInitializer(accessToken),
            ApplicationName = "ContactsApi"
        };
        _gmail = new GmailService(init);
        _calendar = new CalendarService(init);
    }

    /// <summary>
    /// The plain addresses among the input, lower case and distinct, at most
    /// <see cref="MaxAddresses"/>: a stored "address" must not widen a search
    /// </summary>
    public static List<string> Addresses(IEnumerable<string?>? emails) =>
        (emails ?? Enumerable.Empty<string?>())
            .Select(e => (e ?? string.Empty).Trim().ToLowerInvariant())
            .Where(e => Plain.IsMatch(e))
            .Distinct()
            .Take(MaxAddresses)
            .ToList();

    /// <summary>The query for mail from the addresses</summary>
    public static string ReceivedQuery(IReadOnlyList<string> addresses) =>
        $"from:({string.Join(" OR ", addresses.Select(a => $"\"{a}\""))}) -in:chats";

    /// <summary>The query for the user's mail to, cc or bcc the addresses</summary>
    public static string SentQuery(IReadOnlyList<string> addresses) =>
        $"in:sent {{{string.Join(" ", addresses.SelectMany(a => new[] { $"to:\"{a}\"", $"cc:\"{a}\"", $"bcc:\"{a}\"" }))}}} -in:chats";

    /// <summary>
    /// Counts the messages a query finds and dates the newest and the oldest.
    /// </summary>
    /// <param name="query">Gmail query</param>
    /// <param name="ct">Cancellation</param>
    /// <returns>The count</returns>
    public async Task<MailCount> CountMailAsync(string query, CancellationToken ct)
    {
        var result = new MailCount();
        string? newest = null, oldest = null, pageToken = null;
        do
        {
            var list = _gmail.Users.Messages.List("me");
            list.Q = query;
            list.MaxResults = 500;
            list.PageToken = pageToken;
            list.Fields = "messages/id,nextPageToken";
            var page = await Call(() => list.ExecuteAsync(ct), "gmail", ct);
            var ids = page?.Messages?.Select(m => m?.Id).OfType<string>().ToList() ?? new();
            if (ids.Count > 0)
            {
                // Gmail lists newest first in practice, but does not promise it: both ends are dated
                newest ??= ids[0];
                oldest = ids[^1];
                result.Count += ids.Count;
            }

            pageToken = page?.NextPageToken;
            if (result.Count >= MaxIds && pageToken != null)
            {
                result.Capped = true;
                break;
            }
        }
        while (pageToken != null);

        var a = newest == null ? null : await DateOfAsync(newest, ct);
        var b = oldest == null || oldest == newest ? a : await DateOfAsync(oldest, ct);
        var dates = new[] { a, b }.Where(d => d != null).Select(d => d!.Value).ToList();
        result.Last = dates.Count > 0 ? dates.Max() : null;
        // Capped: the oldest listed is not the first, so no first date is claimed
        result.First = dates.Count > 0 && !result.Capped ? dates.Min() : null;
        return result;
    }

    private async Task<DateTime?> DateOfAsync(string id, CancellationToken ct)
    {
        var get = _gmail.Users.Messages.Get("me", id);
        get.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Minimal;
        get.Fields = "internalDate";
        try
        {
            var m = await Call(() => get.ExecuteAsync(ct), "gmail", ct);
            return m?.InternalDate is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null;
        }
        catch (ActivityScanException ex) when (ex.Code == "SCAN_FAILED" && !ct.IsCancellationRequested)
        {
            // Deleted since it was listed: the count stands without that date
            return null;
        }
    }

    /// <summary>
    /// A Google call: a rate limit (shared with a running scan) is retried 3 times with
    /// back-off and then reported as busy, never as a refused sign-in
    /// </summary>
    private static async Task<T> Call<T>(Func<Task<T>> call, string api, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (global::Google.GoogleApiException ex) when (IsRateLimit(ex))
            {
                if (attempt == 3)
                {
                    throw new ActivityScanException(
                        "SCAN_FAILED", $"{(api == "gmail" ? "Gmail" : "Google Calendar")} is busy. Try again in a minute.", ex);
                }

                await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct);
            }
            catch (global::Google.GoogleApiException ex)
            {
                throw GoogleActivityClient.Classify((int)ex.HttpStatusCode, ex.Error?.Message ?? ex.Message, api, ex);
            }
        }
    }

    private static bool IsRateLimit(global::Google.GoogleApiException ex) =>
        (int)ex.HttpStatusCode == 429
        || (ex.HttpStatusCode == System.Net.HttpStatusCode.Forbidden
            && ex.Error?.Errors?.Any(e => e.Reason is "rateLimitExceeded" or "userRateLimitExceeded" or "quotaExceeded") == true);

    /// <summary>
    /// The meetings with any of the addresses, at any date.
    /// </summary>
    /// <param name="addresses">The Contact's addresses</param>
    /// <param name="now">Current time</param>
    /// <param name="ct">Cancellation</param>
    /// <returns>The count</returns>
    public async Task<MeetingCount> CountMeetingsAsync(IReadOnlyList<string> addresses, DateTime now, CancellationToken ct)
    {
        var events = new Dictionary<string, CalendarEvent>();
        var capped = false;
        foreach (var address in addresses)
        {
            string? pageToken = null;
            var pages = 0;
            do
            {
                var req = _calendar.Events.List("primary");
                req.Q = address;
                req.SingleEvents = true;
                req.ShowDeleted = false;
                // No lower bound; ahead only as far as the scan, so recurrences end
                req.TimeMaxDateTimeOffset = now + Ahead;
                req.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;
                req.MaxResults = 2500;
                req.PageToken = pageToken;
                req.Fields = "items(id,status,start,attendees(email,self,responseStatus,resource)),nextPageToken";
                var page = await Call(() => req.ExecuteAsync(ct), "calendar", ct);
                foreach (var ev in page?.Items ?? new List<CalendarEvent>())
                {
                    if (ev.Id != null)
                    {
                        events[ev.Id] = ev;
                    }
                }

                pageToken = page?.NextPageToken;
            }
            while (pageToken != null && ++pages < MaxEventPages);
            capped |= pageToken != null;
        }

        var result = Meetings(events.Values, addresses, now);
        result.Capped = capped;
        return result;
    }

    /// <summary>
    /// The meetings among the events (AD-47 rule 5): an attendee is one of the
    /// addresses and did not decline, not cancelled, 2 to 20 attendees, not declined by the user
    /// </summary>
    public static MeetingCount Meetings(IEnumerable<CalendarEvent> events, IReadOnlyList<string> addresses, DateTime now)
    {
        var wanted = addresses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new MeetingCount();
        foreach (var ev in events)
        {
            var attendees = ev.Attendees ?? new List<global::Google.Apis.Calendar.v3.Data.EventAttendee>();
            if (ev.Status == "cancelled" || attendees.Count < 2 || attendees.Count > MaxMeetingSize
                || attendees.Any(a => a.Self == true && a.ResponseStatus == "declined")
                || !attendees.Any(a => a.Self != true && a.Resource != true && a.ResponseStatus != "declined"
                    && wanted.Contains((a.Email ?? string.Empty).Trim())))
            {
                continue;
            }

            var start = ev.Start?.DateTimeDateTimeOffset?.UtcDateTime
                ?? (DateTime.TryParse(ev.Start?.Date, out var d) ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : (DateTime?)null);
            if (start == null)
            {
                continue;
            }

            if (start.Value <= now)
            {
                result.Count++;
                result.First = result.First == null || start < result.First ? start : result.First;
                result.Last = result.Last == null || start > result.Last ? start : result.Last;
            }
            else if (result.Next == null || start < result.Next)
            {
                result.Next = start;
            }
        }

        return result;
    }
}
