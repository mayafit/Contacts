/**
 * @fileoverview Folds email headers and calendar events into per-address activity
 * @module ContactsApi.Infrastructure.Activity
 */
using System.Globalization;
using System.Text.RegularExpressions;

namespace ContactsApi.Infrastructure.Activity;

/// <summary>
/// Turns individual emails and meetings into <see cref="PersonActivity"/> records.
/// Only addresses that are contacts, or that the user has written to, are kept, so
/// newsletters and other one-way senders that are not contacts leave no trace.
/// </summary>
public class ActivityAggregator
{
    private static readonly Regex EmailPattern = new(
        @"[A-Za-z0-9._%+'\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)+",
        RegexOptions.Compiled);

    private readonly HashSet<string> _contactEmails;
    private readonly HashSet<string> _myEmails;

    // Received from someone not (yet) known: the scan reads newest first, so a
    // reply can be seen before the older email the user sent them. Kept aside
    // and folded in by Complete() when the user did write to them.
    private readonly Dictionary<string, PersonActivity> _pending = new();

    /// <summary>Activity per lower-case address</summary>
    public Dictionary<string, PersonActivity> People { get; }

    /// <summary>
    /// Creates an aggregator.
    /// </summary>
    /// <param name="contactEmails">The user's contacts' addresses</param>
    /// <param name="myEmails">The user's own addresses</param>
    /// <param name="existing">Activity to add to (incremental scans), or null</param>
    public ActivityAggregator(
        IEnumerable<string> contactEmails,
        IEnumerable<string> myEmails,
        Dictionary<string, PersonActivity>? existing = null)
    {
        _contactEmails = new HashSet<string>(contactEmails.Select(Normalize).Where(e => e.Length > 0));
        _myEmails = new HashSet<string>(myEmails.Select(Normalize).Where(e => e.Length > 0));
        People = existing ?? new Dictionary<string, PersonActivity>();
    }

    /// <summary>Lower-case, trimmed address</summary>
    /// <param name="email">Address</param>
    /// <returns>Normalized address</returns>
    public static string Normalize(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>All addresses in a header value such as <c>"A" &lt;a@x.com&gt;, b@y.com</c></summary>
    /// <param name="header">Header value</param>
    /// <returns>Normalized addresses</returns>
    public static IEnumerable<string> ParseAddresses(string? header) =>
        string.IsNullOrEmpty(header)
            ? Enumerable.Empty<string>()
            : EmailPattern.Matches(header).Select(m => Normalize(m.Value)).Distinct();

    /// <summary>
    /// Adds one email.
    /// </summary>
    /// <param name="at">When it was sent or received</param>
    /// <param name="from">From header</param>
    /// <param name="to">To header</param>
    /// <param name="cc">Cc header</param>
    /// <param name="isSent">Whether Gmail filed it as sent</param>
    public void AddEmail(DateTime at, string? from, string? to, string? cc, bool isSent)
    {
        var sender = ParseAddresses(from).FirstOrDefault();
        var sent = isSent || (sender != null && _myEmails.Contains(sender));
        if (sent)
        {
            foreach (var address in ParseAddresses(to).Concat(ParseAddresses(cc)).Distinct())
            {
                if (_myEmails.Contains(address))
                {
                    continue;
                }

                var person = Get(address);
                person.SentCount++;
                person.LastSentAt = Max(person.LastSentAt, at);
                Touch(person, at);
            }

            return;
        }

        // Received: kept for contacts, and for people the user has written to
        if (sender == null || _myEmails.Contains(sender))
        {
            return;
        }

        PersonActivity p;
        if (_contactEmails.Contains(sender) || People.ContainsKey(sender))
        {
            p = Get(sender);
        }
        else if (!_pending.TryGetValue(sender, out p!))
        {
            _pending[sender] = p = new PersonActivity();
        }

        p.ReceivedCount++;
        p.LastReceivedAt = Max(p.LastReceivedAt, at);
        Touch(p, at);
    }

    /// <summary>
    /// Ends a batch of emails: keeps what was received from people the user turned
    /// out to have written to, and forgets every other non-contact sender.
    /// </summary>
    public void Complete()
    {
        foreach (var (address, received) in _pending)
        {
            if (!People.TryGetValue(address, out var p))
            {
                continue;
            }

            p.ReceivedCount += received.ReceivedCount;
            p.LastReceivedAt = Max(p.LastReceivedAt, received.LastReceivedAt);
            p.LastEmailAt = Max(p.LastEmailAt, received.LastEmailAt);
            foreach (var (month, count) in received.EmailMonths)
            {
                p.EmailMonths[month] = p.EmailMonths.GetValueOrDefault(month) + count;
            }
        }

        _pending.Clear();
    }

    /// <summary>
    /// Adds one calendar event shared with others.
    /// </summary>
    /// <param name="start">Start time</param>
    /// <param name="title">Event title</param>
    /// <param name="attendees">Other attendees' addresses</param>
    /// <param name="now">Current time; later events count as upcoming</param>
    public void AddMeeting(DateTime start, string? title, IEnumerable<string> attendees, DateTime now)
    {
        foreach (var address in attendees.Select(Normalize).Distinct())
        {
            if (address.Length == 0 || _myEmails.Contains(address)
                || !(_contactEmails.Contains(address) || People.ContainsKey(address)))
            {
                continue;
            }

            var p = Get(address);
            if (start <= now)
            {
                if (p.LastMeetingAt == null || start > p.LastMeetingAt)
                {
                    p.LastMeetingAt = start;
                    p.LastMeetingTitle = title;
                }

                var month = MonthKey(start);
                p.MeetingMonths[month] = p.MeetingMonths.GetValueOrDefault(month) + 1;
            }
            else if (p.NextMeetingAt == null || start < p.NextMeetingAt)
            {
                p.NextMeetingAt = start;
                p.NextMeetingTitle = title;
            }
        }
    }

    /// <summary>Removes all meeting data, before the calendar is read again</summary>
    public void ClearMeetings()
    {
        foreach (var p in People.Values)
        {
            p.LastMeetingAt = null;
            p.LastMeetingTitle = null;
            p.NextMeetingAt = null;
            p.NextMeetingTitle = null;
            p.MeetingMonths.Clear();
        }
    }

    /// <summary>"yyyy-MM" of a date</summary>
    /// <param name="at">Date</param>
    /// <returns>Month key</returns>
    public static string MonthKey(DateTime at) => at.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private PersonActivity Get(string address)
    {
        if (!People.TryGetValue(address, out var p))
        {
            People[address] = p = new PersonActivity();
        }

        return p;
    }

    private static void Touch(PersonActivity p, DateTime at)
    {
        p.LastEmailAt = Max(p.LastEmailAt, at);
        var month = MonthKey(at);
        p.EmailMonths[month] = p.EmailMonths.GetValueOrDefault(month) + 1;
    }

    private static DateTime Max(DateTime? a, DateTime b) => a == null || b > a ? b : a.Value;

    private static DateTime? Max(DateTime? a, DateTime? b) => b == null ? a : Max(a, b.Value);
}
