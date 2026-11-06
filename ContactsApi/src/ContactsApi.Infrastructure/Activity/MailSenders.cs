/**
 * @fileoverview A contact's address from the mail (AD-43 rule 7): the senders Gmail finds
 * for the contact's names, read from the From header only, and the merge with the
 * activity directory
 * @module ContactsApi.Infrastructure.Activity
 */
using System.Text.RegularExpressions;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;

namespace ContactsApi.Infrastructure.Activity;

/// <summary>One From header</summary>
/// <param name="Name">The display name ('' when none)</param>
/// <param name="Email">The address, lower case</param>
/// <param name="At">When the mail arrived</param>
public record SenderSeen(string Name, string Email, DateTime At);

/// <summary>An address seen in mail for a contact, before and after the merge</summary>
public class FoundEmail
{
    /// <summary>Address</summary>
    public string Email { get; set; } = string.Empty;
    /// <summary>Display names seen for it (at most 3)</summary>
    public List<string> Names { get; set; } = new();
    /// <summary>3 the same words, 2 the address spells the name, 1 the name plus a word</summary>
    public int Score { get; set; }
    /// <summary>Mails seen with it</summary>
    public int Mails { get; set; }
    /// <summary>Last seen</summary>
    public DateTime? LastSeen { get; set; }
    /// <summary>directory, mail</summary>
    public List<string> Sources { get; set; } = new();
}

/// <summary>The senders of the mail Gmail finds for a query</summary>
public interface IMailSenders
{
    /// <summary>The From headers of the newest messages matching the query</summary>
    Task<List<SenderSeen>> SearchAsync(string query, int max, CancellationToken ct);
}

/// <summary>The pure part: the query, the header parse and the merge</summary>
public static class SenderSearch
{
    /// <summary>Messages read per search</summary>
    public const int MaxMessages = 25;

    /// <summary>Candidates returned</summary>
    public const int MaxCandidates = 3;

    private static readonly Regex Unsafe = new(@"[^\p{L}\p{N} '\-.]+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex AngleAddress = new(@"<([^<>\s""]+@[^<>\s""]+)>", RegexOptions.Compiled);

    /// <summary>
    /// <c>from:("name 1" OR "name 2") -in:chats</c>, each name stripped to letters, digits,
    /// spaces, apostrophes, hyphens and dots, so no quote, bracket or operator reaches Gmail
    /// </summary>
    /// <param name="names">The contact's names</param>
    /// <returns>The query, or null when no name is left</returns>
    public static string? Query(IEnumerable<string> names)
    {
        var safe = names
            .Select(n => Spaces.Replace(Unsafe.Replace(n, " "), " ").Trim())
            .Where(n => n.Contains(' '))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => $"\"{n}\"")
            .ToList();
        return safe.Count == 0 ? null : $"from:({string.Join(" OR ", safe)}) -in:chats";
    }

    /// <summary>The display name and address of a From header</summary>
    /// <param name="from">Header value</param>
    /// <returns>Name ('' when none) and lower-case address, or null without an address</returns>
    public static (string Name, string Email)? ParseFrom(string? from)
    {
        if (string.IsNullOrWhiteSpace(from))
        {
            return null;
        }

        // "Name <address>": the last angle-bracketed address (the name may hold one too,
        // "Dana (dana@old.com) <dana@new.com>"); otherwise the header is a bare address
        var bracket = AngleAddress.Matches(from).LastOrDefault();
        string email, name;
        if (bracket != null)
        {
            email = bracket.Groups[1].Value.Trim();
            name = from.Substring(0, bracket.Index).Trim().Trim('"', '\'').Trim();
        }
        else
        {
            email = from.Trim().Trim('"', '\'');
            name = string.Empty;
        }

        email = email.ToLowerInvariant();
        return OwnAddresses.IsAddress(email) ? (EncodedWords.Decode(name), email) : null;
    }

    /// <summary>
    /// The senders that match the names, grouped by address: Gmail's <c>from:</c> also
    /// matches parts of names, so each is scored again
    /// </summary>
    /// <param name="variants">Normalized names (two words or more)</param>
    /// <param name="senders">From headers</param>
    /// <returns>One per address with a score above 0</returns>
    public static List<FoundEmail> FromMail(IReadOnlyList<string> variants, IEnumerable<SenderSeen> senders) =>
        senders
            .GroupBy(s => s.Email)
            .Select(g =>
            {
                var names = g.Select(s => s.Name).Where(n => n.Length > 0).Distinct().Take(3).ToList();
                return new FoundEmail
                {
                    Email = g.Key,
                    Names = names,
                    Score = NameMatcher.Score(variants, g.Key, names),
                    Mails = g.Count(),
                    LastSeen = g.Max(s => s.At),
                    Sources = new() { "mail" }
                };
            })
            .Where(f => f.Score > 0)
            .ToList();

    /// <summary>
    /// The directory's candidates and the mailbox's, merged by address, without the
    /// Owner's own addresses: the higher score, both names, the directory's count when
    /// it has the address; best first (score, mails, last seen)
    /// </summary>
    /// <param name="directory">From the activity directory</param>
    /// <param name="mail">From the mailbox</param>
    /// <param name="mine">The Owner's own addresses, normalized</param>
    /// <returns>At most 3</returns>
    public static List<FoundEmail> Merge(IEnumerable<FoundEmail> directory, IEnumerable<FoundEmail> mail, IReadOnlySet<string> mine)
    {
        var byAddress = new Dictionary<string, FoundEmail>();
        foreach (var f in directory.Concat(mail))
        {
            var key = OwnAddresses.Normalize(f.Email);
            if (key.Length == 0 || mine.Contains(key))
            {
                continue;
            }

            if (!byAddress.TryGetValue(key, out var had))
            {
                byAddress[key] = new FoundEmail
                {
                    Email = key,
                    Names = f.Names.Take(3).ToList(),
                    Score = f.Score,
                    Mails = f.Mails,
                    LastSeen = f.LastSeen,
                    Sources = f.Sources.ToList()
                };
                continue;
            }

            // The directory comes first: its count covers the scan's year, the mailbox's only 25 mails
            had.Score = Math.Max(had.Score, f.Score);
            had.Names = had.Names.Concat(f.Names).Distinct().Take(3).ToList();
            had.LastSeen = had.LastSeen == null || (f.LastSeen != null && f.LastSeen > had.LastSeen) ? f.LastSeen : had.LastSeen;
            had.Sources = had.Sources.Concat(f.Sources).Distinct().ToList();
        }

        return byAddress.Values
            .OrderByDescending(f => f.Score)
            .ThenByDescending(f => f.Mails)
            .ThenByDescending(f => f.LastSeen ?? DateTime.MinValue)
            .Take(MaxCandidates)
            .ToList();
    }
}

/// <summary>Gmail with the Owner's T2 grant, reading only the From header</summary>
public class GmailMailSenders : IMailSenders
{
    private readonly GmailService _gmail;

    /// <summary>Creates a reader for one Owner</summary>
    /// <param name="accessToken">Returns a valid access token</param>
    public GmailMailSenders(Func<CancellationToken, Task<string>> accessToken) =>
        _gmail = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = new FreshTokenInitializer(accessToken),
            ApplicationName = "ContactsApi"
        });

    /// <inheritdoc />
    /// <exception cref="ActivityScanException"><c>NOT_CONNECTED</c> when Gmail refused the sign-in, <c>SCAN_FAILED</c> when it is busy or unreachable</exception>
    public async Task<List<SenderSeen>> SearchAsync(string query, int max, CancellationToken ct)
    {
        var list = _gmail.Users.Messages.List("me");
        list.Q = query;
        list.MaxResults = max;
        list.Fields = "messages/id";
        var page = await Run(() => list.ExecuteAsync(ct));
        var ids = page?.Messages?.Select(m => m?.Id).OfType<string>().ToList() ?? new();
        var senders = new List<SenderSeen>();
        // Five at a time: Gmail's per-user rate allows it, and a click should not wait for 25 in a row
        foreach (var batch in ids.Chunk(5))
        {
            var read = await Task.WhenAll(batch.Select(async id =>
            {
                var get = _gmail.Users.Messages.Get("me", id);
                get.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
                get.MetadataHeaders = new global::Google.Apis.Util.Repeatable<string>(new[] { "From" });
                get.Fields = "internalDate,payload/headers";
                try
                {
                    return await Run(() => get.ExecuteAsync(ct));
                }
                catch (ActivityScanException ex) when (ex.Code == "SCAN_FAILED")
                {
                    // One message gone or busy does not lose the others
                    return null;
                }
            }));
            foreach (var m in read)
            {
                var from = m?.Payload?.Headers?.FirstOrDefault(h => string.Equals(h.Name, "From", StringComparison.OrdinalIgnoreCase))?.Value;
                if (SenderSearch.ParseFrom(from) is { } parsed)
                {
                    senders.Add(new SenderSeen(parsed.Name, parsed.Email,
                        DateTimeOffset.FromUnixTimeMilliseconds(m!.InternalDate ?? 0).UtcDateTime));
                }
            }
        }

        return senders;
    }

    /// <summary>A Gmail call: a quota, 429 and 5xx are passing; other 401 and 403 a refused sign-in</summary>
    private static async Task<T> Run<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (global::Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.Forbidden
            && ex.Error?.Errors?.Any(e => e.Reason is "rateLimitExceeded" or "userRateLimitExceeded" or "quotaExceeded") == true)
        {
            throw new ActivityScanException("SCAN_FAILED", "Gmail is busy", ex);
        }
        catch (global::Google.GoogleApiException ex) when (ex.HttpStatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new ActivityScanException("NOT_CONNECTED", "Gmail refused the sign-in", ex);
        }
        catch (global::Google.GoogleApiException ex)
        {
            throw new ActivityScanException("SCAN_FAILED", $"Gmail answered {(int)ex.HttpStatusCode}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ActivityScanException("SCAN_FAILED", "Gmail could not be reached", ex);
        }
    }
}
