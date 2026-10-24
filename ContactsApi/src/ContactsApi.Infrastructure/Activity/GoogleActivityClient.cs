/**
 * @fileoverview Reads email headers from Gmail and meetings from Google Calendar
 * @module ContactsApi.Infrastructure.Activity
 */
using Google.Apis.Http;
using Google.Apis.Calendar.v3;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Requests;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using CalendarAttendee = global::Google.Apis.Calendar.v3.Data.EventAttendee;
using CalendarEvent = global::Google.Apis.Calendar.v3.Data.Event;

namespace ContactsApi.Infrastructure.Activity;

/// <summary>A scan stopped for a reason the user can act on</summary>
public class ActivityScanException : Exception
{
    /// <summary>NOT_CONNECTED, GMAIL_API_DISABLED, CALENDAR_API_DISABLED or SCAN_FAILED</summary>
    public string Code { get; }

    /// <summary>Creates the exception</summary>
    /// <param name="code">Error code</param>
    /// <param name="message">Message for the user</param>
    /// <param name="inner">Cause</param>
    public ActivityScanException(string code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;
}

/// <summary>Outcome of a full mail scan</summary>
/// <param name="Read">Messages listed</param>
/// <param name="Oldest">Oldest message date read</param>
/// <param name="Truncated">Stopped at the message cap before reaching the window start</param>
public record MailScan(int Read, DateTime? Oldest, bool Truncated);

/// <summary>
/// Sets a fresh access token on every request, so a scan that runs longer than one
/// token's life keeps working.
/// </summary>
internal sealed class FreshTokenInitializer : IConfigurableHttpClientInitializer, IHttpExecuteInterceptor
{
    private readonly Func<CancellationToken, Task<string>> _token;

    public FreshTokenInitializer(Func<CancellationToken, Task<string>> token) => _token = token;

    public void Initialize(ConfigurableHttpClient httpClient) =>
        httpClient.MessageHandler.AddExecuteInterceptor(this);

    public async Task InterceptAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _token(cancellationToken));
}

/// <summary>
/// Gmail (metadata only: From/To/Cc, date, labels) and Calendar (start, attendees,
/// title) reads for the activity snapshot. Subjects are kept only for recent
/// one-to-few threads (replies owed / awaited); message bodies are never read.
/// </summary>
public class GoogleActivityClient
{
    /// <summary>Messages per batch request</summary>
    private const int BatchSize = 20;

    /// <summary>
    /// Pause between batches. A metadata read costs 5 quota units and Gmail allows
    /// 15,000 units per user per minute: 40 x 5 per second stays under it.
    /// </summary>
    private static readonly TimeSpan BatchPause = TimeSpan.FromSeconds(1);

    /// <summary>Events with more people than this (all-hands, webinars) are not meetings with a person</summary>
    private const int MaxMeetingSize = 20;

    private readonly GmailService _gmail;
    private readonly CalendarService _calendar;
    private readonly ILogger _logger;

    /// <summary>Creates a client for one user</summary>
    /// <param name="accessToken">Returns a valid access token (asked before every request)</param>
    /// <param name="logger">Logger</param>
    public GoogleActivityClient(Func<CancellationToken, Task<string>> accessToken, ILogger logger)
    {
        var init = new BaseClientService.Initializer
        {
            HttpClientInitializer = new FreshTokenInitializer(accessToken),
            ApplicationName = "ContactsApi"
        };
        _gmail = new GmailService(init);
        _calendar = new CalendarService(init);
        _logger = logger;
    }

    /// <summary>The account's "send mail as" addresses (empty when Gmail does not tell)</summary>
    public async Task<List<string>> GetSendAsAddressesAsync(CancellationToken ct)
    {
        try
        {
            var list = await _gmail.Users.Settings.SendAs.List("me").ExecuteAsync(ct);
            return list?.SendAs?.Select(s => s.SendAsEmail).OfType<string>().ToList() ?? new List<string>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation("Gmail aliases unavailable: {Message}", ex.Message);
            return new List<string>();
        }
    }

    /// <summary>The mailbox address and its current history id</summary>
    /// <returns>Address and history id</returns>
    public async Task<(string Email, ulong? HistoryId)> GetProfileAsync(CancellationToken ct)
    {
        var profile = await Run(() => _gmail.Users.GetProfile("me").ExecuteAsync(ct), "gmail");
        return (profile.EmailAddress ?? string.Empty, profile.HistoryId);
    }

    /// <summary>
    /// Reads every message newer than <paramref name="since"/> (newest first, at most
    /// <paramref name="maxMessages"/>) into the aggregator.
    /// </summary>
    /// <param name="upToHistoryId">Skip messages newer than this (read by the next incremental run)</param>
    /// <returns>Messages read, oldest date read, whether the cap cut the window short</returns>
    public async Task<MailScan> ScanMailAsync(
        ActivityAggregator aggregator, DateTime since, int maxMessages, ulong? upToHistoryId,
        Func<int, Task> progress, CancellationToken ct)
    {
        var done = 0;
        DateTime? oldest = null;
        var reachedStart = false;
        string? pageToken = null;
        do
        {
            var list = _gmail.Users.Messages.List("me");
            list.MaxResults = 500;
            list.PageToken = pageToken;
            list.Fields = "messages/id,nextPageToken";
            var page = await Run(() => list.ExecuteAsync(ct), "gmail");
            var ids = page.Messages?.Select(m => m.Id).ToList() ?? new List<string>();
            var messages = await GetMetadataAsync(ids, () => progress(done), ct, partialOk: true);
            var inWindow = 0;
            foreach (var m in messages)
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(m.InternalDate ?? 0).UtcDateTime;
                if (at < since)
                {
                    reachedStart = true;
                    continue;
                }

                inWindow++;
                oldest = oldest == null || at < oldest ? at : oldest;
                if (upToHistoryId == null || m.HistoryId == null || m.HistoryId <= upToHistoryId)
                {
                    Add(aggregator, m, at);
                }
            }

            done += ids.Count;
            await progress(done);
            pageToken = page.NextPageToken;

            // Newest first: a page with nothing in the window means we are past it
            if ((inWindow == 0 && ids.Count > 0) || GaveUp)
            {
                break;
            }
        }
        while (pageToken != null && done < maxMessages);

        aggregator.Complete();
        return new MailScan(done, oldest, GaveUp || (!reachedStart && pageToken != null));
    }

    /// <summary>
    /// Reads the messages added since <paramref name="historyId"/>.
    /// </summary>
    /// <returns>Messages read, or null when the history id is too old (a full scan is needed)</returns>
    /// <param name="upToHistoryId">Skip messages newer than this (read by the next run)</param>
    public async Task<int?> ScanMailChangesAsync(
        ActivityAggregator aggregator, ulong historyId, ulong? upToHistoryId, Func<int, Task> progress, CancellationToken ct)
    {
        var ids = new List<string>();
        var inboxChanges = new Dictionary<string, bool>();
        string? pageToken = null;
        try
        {
            do
            {
                var req = _gmail.Users.History.List("me");
                req.StartHistoryId = historyId;
                // No type filter: new messages, and inbox labels added / removed
                // (archiving clears "you owe a reply"); the library takes only one type
                req.MaxResults = 500;
                req.PageToken = pageToken;
                var page = await req.ExecuteAsync(ct);
                ids.AddRange(page.History?
                    .SelectMany(h => h.MessagesAdded ?? new List<HistoryMessageAdded>())
                    .Select(a => a.Message.Id) ?? Enumerable.Empty<string>());
                foreach (var h in page.History ?? new List<History>())
                {
                    foreach (var r in h.LabelsRemoved ?? new List<HistoryLabelRemoved>())
                    {
                        if (r.LabelIds?.Contains("INBOX") == true)
                        {
                            inboxChanges[r.Message.Id] = false;
                        }
                    }

                    foreach (var a in h.LabelsAdded ?? new List<HistoryLabelAdded>())
                    {
                        if (a.LabelIds?.Contains("INBOX") == true)
                        {
                            inboxChanges[a.Message.Id] = true;
                        }
                    }
                }
                pageToken = page.NextPageToken;
            }
            while (pageToken != null);
        }
        catch (global::Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (global::Google.GoogleApiException ex)
        {
            throw Translate(ex, "gmail");
        }

        var messages = await GetMetadataAsync(ids.Distinct().ToList(), () => progress(0), ct);
        foreach (var m in messages.Where(m => upToHistoryId == null || m.HistoryId == null || m.HistoryId <= upToHistoryId))
        {
            Add(aggregator, m, DateTimeOffset.FromUnixTimeMilliseconds(m.InternalDate ?? 0).UtcDateTime);
        }

        foreach (var (id, inInbox) in inboxChanges)
        {
            aggregator.SetInbox(id, inInbox);
        }

        await progress(messages.Count);
        aggregator.Complete();
        return messages.Count;
    }

    /// <summary>
    /// Reads meetings on the primary calendar between the two dates into the aggregator.
    /// </summary>
    /// <returns>Events read</returns>
    public async Task<int> ScanCalendarAsync(
        ActivityAggregator aggregator, DateTime from, DateTime to, Func<int, Task> progress, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var count = 0;
        string? pageToken = null;
        do
        {
            var req = _calendar.Events.List("primary");
            req.TimeMinDateTimeOffset = from;
            req.TimeMaxDateTimeOffset = to;
            req.SingleEvents = true;
            req.ShowDeleted = false;
            req.MaxResults = 2500;
            req.PageToken = pageToken;
            req.Fields = "items(status,summary,start,attendees(email,displayName,self,responseStatus,resource)),nextPageToken";
            var page = await Run(() => req.ExecuteAsync(ct), "calendar");
            foreach (var ev in page.Items ?? new List<CalendarEvent>())
            {
                count++;
                var attendees = ev.Attendees ?? new List<CalendarAttendee>();
                if (ev.Status == "cancelled" || attendees.Count < 2 || attendees.Count > MaxMeetingSize
                    || attendees.Any(a => a.Self == true && a.ResponseStatus == "declined"))
                {
                    continue;
                }

                var start = ev.Start?.DateTimeDateTimeOffset?.UtcDateTime
                    ?? (DateTime.TryParse(ev.Start?.Date, out var d) ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : (DateTime?)null);
                if (start == null)
                {
                    continue;
                }

                aggregator.AddMeeting(
                    start.Value,
                    ev.Summary,
                    attendees.Where(a => a.Self != true && a.Resource != true && a.ResponseStatus != "declined")
                        .Select(a => (a.Email ?? string.Empty, (string?)a.DisplayName)),
                    now);
            }

            await progress(count);
            pageToken = page.NextPageToken;
        }
        while (pageToken != null);

        return count;
    }

    private static void Add(ActivityAggregator aggregator, Message m, DateTime at)
    {
        var headers = m.Payload?.Headers ?? new List<MessagePartHeader>();
        string? Header(string name) =>
            string.Join(", ", headers.Where(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value));
        var labels = m.LabelIds ?? new List<string>();
        if (labels.Contains("SPAM") || labels.Contains("TRASH") || labels.Contains("DRAFT"))
        {
            return;
        }

        aggregator.AddEmail(at, Header("From"), Header("To"), Header("Cc"), labels.Contains("SENT"));
        var bulk = !string.IsNullOrEmpty(Header("List-Id")) || !string.IsNullOrEmpty(Header("List-Unsubscribe"))
            || (Header("Auto-Submitted") ?? string.Empty).StartsWith("auto", StringComparison.OrdinalIgnoreCase)
            || (Header("Precedence") ?? string.Empty) is "bulk" or "list" or "junk"
            || labels.Any(l => l is "CATEGORY_PROMOTIONS" or "CATEGORY_SOCIAL" or "CATEGORY_UPDATES" or "CATEGORY_FORUMS");
        aggregator.AddThreadMessage(
            m.ThreadId, at, Header("From"), Header("To"), Header("Cc"), Header("Subject"),
            labels.Contains("SENT"), labels.Contains("INBOX"), bulk, DateTime.UtcNow, m.Id);
        aggregator.MessagesWithoutRecipients += string.IsNullOrEmpty(Header("To")) && string.IsNullOrEmpty(Header("Cc")) ? 1 : 0;
    }

    /// <summary>Message headers for the ids, in paced batches, retrying rate-limited items</summary>
    /// <summary>A full scan stopped early because Gmail kept rate limiting</summary>
    public bool GaveUp { get; private set; }

    private async Task<List<Message>> GetMetadataAsync(
        IReadOnlyList<string> ids, Func<Task> heartbeat, CancellationToken ct, bool partialOk = false)
    {
        var result = new List<Message>();
        var queue = new Queue<string>(ids);
        var retries = 0;
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = new List<string>();
            while (chunk.Count < BatchSize && queue.Count > 0)
            {
                chunk.Add(queue.Dequeue());
            }

            var limited = new List<string>();
            RequestError? fatal = null;
            var batch = new BatchRequest(_gmail);
            foreach (var id in chunk)
            {
                var get = _gmail.Users.Messages.Get("me", id);
                get.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
                get.MetadataHeaders = new global::Google.Apis.Util.Repeatable<string>(new[]
                {
                    "From", "To", "Cc", "Subject", "List-Id", "List-Unsubscribe", "Auto-Submitted", "Precedence"
                });
                get.Fields = "id,threadId,historyId,internalDate,labelIds,payload/headers";
                batch.Queue<Message>(get, (message, error, _, _) =>
                {
                    if (error == null)
                    {
                        lock (result)
                        {
                            result.Add(message);
                        }
                    }
                    else if (IsRetryable(error))
                    {
                        lock (limited)
                        {
                            limited.Add(id);
                        }
                    }
                    else if (error.Code != 404)
                    {
                        // 404: deleted since it was listed
                        fatal ??= error;
                    }
                });
            }

            await Run(() => batch.ExecuteAsync(ct), "gmail");
            if (fatal != null)
            {
                throw Classify(fatal.Code, fatal.Message ?? string.Empty, "gmail", null);
            }

            if (limited.Count > 0)
            {
                if (++retries > 12)
                {
                    if (!partialOk)
                    {
                        throw new ActivityScanException("SCAN_FAILED", "Gmail kept refusing requests (rate limit). Try again later.");
                    }

                    // Keep what was read: the scan ends here and records how far back it got
                    _logger.LogWarning("Gmail rate limit persisted; ending the scan with what was read");
                    GaveUp = true;
                    break;
                }

                limited.ForEach(queue.Enqueue);
                _logger.LogInformation("Gmail rate limit; backing off ({Count} messages)", limited.Count);
                await heartbeat();
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 2 << retries)), ct);
            }
            else
            {
                retries = 0;
                await heartbeat();
                await Task.Delay(BatchPause, ct);
            }
        }

        return result;
    }

    /// <summary>Rate limits and transient server errors: retried with backoff</summary>
    internal static bool IsRetryable(RequestError error) =>
        error.Code == 429
        || error.Code >= 500
        || (error.Code == 403 && (
            (error.Errors ?? new List<SingleError>()).Any(e =>
                string.Equals(e.Reason, "rateLimitExceeded", StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.Reason, "userRateLimitExceeded", StringComparison.OrdinalIgnoreCase))
            || (error.Message ?? string.Empty).Contains("rate limit", StringComparison.OrdinalIgnoreCase)));

    private static async Task<T> Run<T>(Func<Task<T>> call, string api)
    {
        try
        {
            return await call();
        }
        catch (global::Google.GoogleApiException ex)
        {
            throw Translate(ex, api);
        }
    }

    private static async Task Run(Func<Task> call, string api)
    {
        try
        {
            await call();
        }
        catch (global::Google.GoogleApiException ex)
        {
            throw Translate(ex, api);
        }
    }

    private static ActivityScanException Translate(global::Google.GoogleApiException ex, string api) =>
        Classify((int)ex.HttpStatusCode, ex.Error?.Message ?? ex.Message, api, ex);

    /// <summary>Turns a Google error into one the user can act on</summary>
    internal static ActivityScanException Classify(int status, string message, string api, Exception? inner)
    {
        var name = api == "gmail" ? "Gmail" : "Google Calendar";
        if (message.Contains("has not been used in project") || message.Contains("is disabled")
            || message.Contains("SERVICE_DISABLED") || message.Contains("accessNotConfigured"))
        {
            var url = System.Text.RegularExpressions.Regex.Match(message, @"https://console\.developers\.google\.com/\S+").Value;
            return new ActivityScanException(
                api == "gmail" ? "GMAIL_API_DISABLED" : "CALENDAR_API_DISABLED",
                $"The {name} API is not enabled for this app's Google Cloud project. Enable it" +
                (url.Length > 0 ? $" at {url.TrimEnd('.')}" : " in the Google Cloud console") + ", then refresh.",
                inner);
        }

        if (status == 401 || status == 403)
        {
            return new ActivityScanException(
                "NOT_CONNECTED", $"{name} access was not granted. Connect Gmail & Calendar again.", inner);
        }

        return new ActivityScanException("SCAN_FAILED", $"{name} failed: {message}", inner);
    }
}
