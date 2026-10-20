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

    /// <summary>Everyone seen in headers or as a guest, with their display names</summary>
    public Dictionary<string, DirectoryEntry> Directory { get; }

    /// <summary>Messages with neither To nor Cc (diagnostics: Bcc, mailing lists)</summary>
    public int MessagesWithoutRecipients { get; set; }

    /// <summary>Directory names that are still encoded after decoding (diagnostics)</summary>
    public int UndecodedNames => Directory.Values.Sum(e => e.Names.Count(n => n.Contains("=?", StringComparison.Ordinal)));

    /// <summary>Most entries kept in the directory (the most frequent ones)</summary>
    public const int MaxDirectory = 5000;

    /// <summary>
    /// Creates an aggregator.
    /// </summary>
    /// <param name="contactEmails">The user's contacts' addresses</param>
    /// <param name="myEmails">The user's own addresses</param>
    /// <param name="existing">Activity to add to (incremental scans), or null</param>
    /// <param name="directory">Directory to add to (incremental scans), or null</param>
    public ActivityAggregator(
        IEnumerable<string> contactEmails,
        IEnumerable<string> myEmails,
        Dictionary<string, PersonActivity>? existing = null,
        Dictionary<string, DirectoryEntry>? directory = null)
    {
        Directory = directory ?? new Dictionary<string, DirectoryEntry>();
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
    /// Name and address of each mailbox in a header such as
    /// <c>"Cohen, Dana" &lt;dana@x.com&gt;, b@y.com</c> (commas inside quotes or
    /// angle brackets do not split).
    /// </summary>
    /// <param name="header">Header value</param>
    /// <returns>Display name ('' when none) and normalized address</returns>
    public static IEnumerable<(string Name, string Address)> ParseMailboxes(string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            yield break;
        }

        header = EncodedWords.Decode(header);
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false, angled = false;
        foreach (var ch in header)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (ch == '<' && !quoted)
            {
                angled = true;
            }
            else if (ch == '>' && !quoted)
            {
                angled = false;
            }

            if (ch == ',' && !quoted && !angled)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        parts.Add(current.ToString());
        foreach (var part in parts)
        {
            var match = EmailPattern.Match(part);
            if (!match.Success)
            {
                continue;
            }

            var name = Regex.Replace(part, @"<[^>]*>", string.Empty).Replace(match.Value, string.Empty)
                .Trim().Trim('"', '\'', ' ').Trim();
            yield return (name, Normalize(match.Value));
        }
    }

    /// <summary>Records an address (and its display name) in the directory</summary>
    private void Note(string address, string? name, DateTime at, Action<DirectoryEntry> count)
    {
        if (address.Length == 0 || _myEmails.Contains(address))
        {
            return;
        }

        if (!Directory.TryGetValue(address, out var entry))
        {
            Directory[address] = entry = new DirectoryEntry();
        }

        count(entry);
        entry.LastSeen = Max(entry.LastSeen, at);
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length > 0 && !string.Equals(clean, address, StringComparison.OrdinalIgnoreCase)
            && entry.Names.Count < 3
            && !entry.Names.Contains(clean, StringComparer.OrdinalIgnoreCase))
        {
            entry.Names.Add(clean);
        }
    }

    /// <summary>Keeps the most frequent directory entries</summary>
    public void PruneDirectory()
    {
        if (Directory.Count <= MaxDirectory)
        {
            return;
        }

        var keep = Directory
            .OrderByDescending(kv => kv.Value.Sent * 3 + kv.Value.Meetings * 3 + kv.Value.Received + kv.Value.CopiedTogether)
            .ThenByDescending(kv => kv.Value.LastSeen)
            .Take(MaxDirectory)
            .Select(kv => kv.Key)
            .ToHashSet();
        foreach (var key in Directory.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            Directory.Remove(key);
        }
    }

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

        // Directory: everyone in the headers, under the names they were given
        var recipients = ParseMailboxes(to).Concat(ParseMailboxes(cc)).ToList();
        if (sent)
        {
            foreach (var (name, address) in recipients.DistinctBy(r => r.Address))
            {
                Note(address, name, at, e => e.Sent++);
            }
        }
        else
        {
            foreach (var (name, address) in ParseMailboxes(from).Take(1))
            {
                Note(address, name, at, e => e.Received++);
            }

            foreach (var (name, address) in recipients.DistinctBy(r => r.Address))
            {
                Note(address, name, at, e => e.CopiedTogether++);
            }
        }
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
    /// <param name="attendees">Other attendees' addresses and display names</param>
    /// <param name="now">Current time; later events count as upcoming</param>
    public void AddMeeting(DateTime start, string? title, IEnumerable<(string Email, string? Name)> attendees, DateTime now)
    {
        var guests = attendees.Select(a => (Address: Normalize(a.Email), a.Name)).DistinctBy(a => a.Address).ToList();
        foreach (var (address, name) in guests)
        {
            Note(address, name, start, e =>
            {
                if (start <= now)
                {
                    e.Meetings++;
                }
            });
        }

        foreach (var address in guests.Select(g => g.Address))
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
