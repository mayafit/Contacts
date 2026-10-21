/**
 * @fileoverview Activity snapshot: when the user was last in touch with each email address
 * @module ContactsApi.Infrastructure.Activity
 */
namespace ContactsApi.Infrastructure.Activity;

/// <summary>State of a user's activity snapshot</summary>
public static class ActivityStatus
{
    /// <summary>Never built</summary>
    public const string None = "none";
    /// <summary>A scan is in progress</summary>
    public const string Running = "running";
    /// <summary>Built and current</summary>
    public const string Ready = "ready";
    /// <summary>The last scan failed (see Error)</summary>
    public const string Error = "error";
}

/// <summary>Scan progress shown while a scan runs</summary>
public class ActivityProgress
{
    /// <summary>"contacts", "gmail" or "calendar"</summary>
    public string Phase { get; set; } = string.Empty;
    /// <summary>Items processed in this phase</summary>
    public int Done { get; set; }
}

/// <summary>Email and meeting activity with one email address</summary>
public class PersonActivity
{
    /// <summary>Last email in either direction</summary>
    public DateTime? LastEmailAt { get; set; }
    /// <summary>Last email the user sent to this address</summary>
    public DateTime? LastSentAt { get; set; }
    /// <summary>Last email received from this address</summary>
    public DateTime? LastReceivedAt { get; set; }
    /// <summary>Emails sent to this address</summary>
    public int SentCount { get; set; }
    /// <summary>Emails received from this address</summary>
    public int ReceivedCount { get; set; }
    /// <summary>Emails per month ("yyyy-MM"), both directions</summary>
    public Dictionary<string, int> EmailMonths { get; set; } = new();
    /// <summary>Start of the last past meeting together</summary>
    public DateTime? LastMeetingAt { get; set; }
    /// <summary>Title of that meeting</summary>
    public string? LastMeetingTitle { get; set; }
    /// <summary>Start of the next upcoming meeting together</summary>
    public DateTime? NextMeetingAt { get; set; }
    /// <summary>Title of that meeting</summary>
    public string? NextMeetingTitle { get; set; }
    /// <summary>Past meetings per month ("yyyy-MM")</summary>
    public Dictionary<string, int> MeetingMonths { get; set; } = new();
}

/// <summary>
/// Someone seen in mail headers or as a meeting guest, with the names they appeared
/// under. Lets the assistant find an address by name, contact or not.
/// </summary>
public class DirectoryEntry
{
    /// <summary>Display names seen for the address (at most 3)</summary>
    public List<string> Names { get; set; } = new();
    /// <summary>Emails the user sent them</summary>
    public int Sent { get; set; }
    /// <summary>Emails received from them</summary>
    public int Received { get; set; }
    /// <summary>Emails where both were recipients (e.g. cc'd together)</summary>
    public int CopiedTogether { get; set; }
    /// <summary>Past meetings together</summary>
    public int Meetings { get; set; }
    /// <summary>Last time seen in any of these</summary>
    public DateTime? LastSeen { get; set; }
}

/// <summary>
/// The state of a recent email thread with a person: who wrote last, and when.
/// Used for "you owe a reply" and "waiting for an answer".
/// </summary>
public class ThreadState
{
    /// <summary>Gmail thread id (for the link to the thread)</summary>
    public string ThreadId { get; set; } = string.Empty;
    /// <summary>When the last message was sent or received</summary>
    public DateTime LastAt { get; set; }
    /// <summary>The user wrote the last message</summary>
    public bool LastFromMe { get; set; }
    /// <summary>The people on the last message other than the user (sender, or recipients)</summary>
    public List<string> Counterparts { get; set; } = new();
    /// <summary>Subject of the last message</summary>
    public string? Subject { get; set; }
    /// <summary>The last message is in the inbox (not archived)</summary>
    public bool InInbox { get; set; }
    /// <summary>Messages seen in the thread</summary>
    public int Messages { get; set; }
}

/// <summary>Everything known about a user's activity, kept per Google account</summary>
public class ActivitySnapshot
{
    /// <summary>See <see cref="ActivityStatus"/></summary>
    public string Status { get; set; } = ActivityStatus.None;
    /// <summary>Set while running</summary>
    public ActivityProgress? Progress { get; set; }
    /// <summary>Last heartbeat of a running scan; a stale one means the scan died</summary>
    public DateTime? HeartbeatAt { get; set; }
    /// <summary>Why the last scan failed</summary>
    public string? Error { get; set; }
    /// <summary>Machine-readable failure (e.g. GMAIL_API_DISABLED, NOT_CONNECTED)</summary>
    public string? ErrorCode { get; set; }
    /// <summary>When the data was last brought up to date</summary>
    public DateTime? UpdatedAt { get; set; }
    /// <summary>When the last full Gmail scan ran</summary>
    public DateTime? FullScanAt { get; set; }
    /// <summary>Oldest date the email data covers</summary>
    public DateTime? Since { get; set; }
    /// <summary>Gmail history id to continue from, when the data is complete up to it</summary>
    public ulong? GmailHistoryId { get; set; }
    /// <summary>Messages read in the last scan</summary>
    public int MessagesScanned { get; set; }
    /// <summary>Activity per lower-case email address</summary>
    public Dictionary<string, PersonActivity> People { get; set; } = new();

    /// <summary>
    /// Names and addresses from mail headers and meeting guests. Null in snapshots
    /// made before it existed (a full scan builds it).
    /// </summary>
    public Dictionary<string, DirectoryEntry>? Directory { get; set; }

    /// <summary>
    /// Recent one-to-few threads with people (last 45 days). Null in snapshots
    /// made before it existed (a full scan builds it).
    /// </summary>
    public Dictionary<string, ThreadState>? Threads { get; set; }
}
