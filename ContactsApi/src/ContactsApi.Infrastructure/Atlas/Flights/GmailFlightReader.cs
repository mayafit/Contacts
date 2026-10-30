/**
 * @fileoverview The Owner's mails about a flight (AD-42 rule 4): searched by the airports'
 * codes, read in memory, never stored
 * @module ContactsApi.Infrastructure.Atlas.Flights
 */
using System.Globalization;
using ContactsApi.Infrastructure.Activity;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;

namespace ContactsApi.Infrastructure.Atlas.Flights;

/// <summary>Gmail refused the Owner's sign-in (401, 403): sign in again</summary>
public class MailRefusedException : Exception
{
    /// <summary>Creates the exception</summary>
    public MailRefusedException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The Owner's mailbox, as searched for flights</summary>
public interface IFlightMailbox
{
    /// <summary>The ids of the newest messages matching a Gmail query</summary>
    Task<List<string>> SearchAsync(string query, int max, CancellationToken ct);

    /// <summary>A message's subject, sender, date and bodies; null when gone</summary>
    Task<FlightMailMessage?> GetAsync(string id, CancellationToken ct);
}

/// <summary>Gmail with the Owner's T2 grant (gmail.readonly)</summary>
public class GmailFlightReader : IFlightMailbox
{
    /// <summary>Mails read per flight</summary>
    public const int MaxMessages = 20;

    private readonly GmailService _gmail;

    /// <summary>Creates a reader for one Owner</summary>
    /// <param name="accessToken">Returns a valid access token</param>
    public GmailFlightReader(Func<CancellationToken, Task<string>> accessToken) =>
        _gmail = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = new FreshTokenInitializer(accessToken),
            ApplicationName = "ContactsApi"
        });

    /// <summary>
    /// The Gmail query for a flight: a departure code and an arrival code, from a year
    /// before the day to two days after it
    /// </summary>
    public static string Query(IEnumerable<string> departures, IEnumerable<string> arrivals, DateTime day) =>
        $"({string.Join(" OR ", departures)}) ({string.Join(" OR ", arrivals)}) " +
        $"after:{day.AddDays(-365).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)} " +
        $"before:{day.AddDays(2).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture)} -in:chats";

    /// <inheritdoc />
    public async Task<List<string>> SearchAsync(string query, int max, CancellationToken ct)
    {
        var list = _gmail.Users.Messages.List("me");
        list.Q = query;
        list.MaxResults = max;
        list.Fields = "messages/id";
        var page = await Run(() => list.ExecuteAsync(ct));
        return page?.Messages?.Select(m => m?.Id).OfType<string>().ToList() ?? new();
    }

    /// <inheritdoc />
    public async Task<FlightMailMessage?> GetAsync(string id, CancellationToken ct)
    {
        var get = _gmail.Users.Messages.Get("me", id);
        get.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
        get.Fields = "id,internalDate,payload(mimeType,headers,body/data,parts(mimeType,headers,body/data,parts(mimeType,headers,body/data,parts(mimeType,headers,body/data))))";
        Message? m;
        try
        {
            m = await Run(() => get.ExecuteAsync(ct));
        }
        catch (global::Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (m?.Payload == null)
        {
            return null;
        }

        string? Header(string name) => m.Payload.Headers?.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
        var (plain, html) = Bodies(m.Payload);
        var from = Header("From") ?? string.Empty;
        var address = System.Text.RegularExpressions.Regex.Match(from, @"[^<>\s""]+@[^<>\s""]+");
        return new FlightMailMessage(
            m.Id ?? id,
            EncodedWords.Decode(Header("Subject")) ?? string.Empty,
            address.Success ? address.Value : from,
            DateTimeOffset.FromUnixTimeMilliseconds(m.InternalDate ?? 0).UtcDateTime,
            html,
            plain);
    }

    /// <summary>The first text/plain and text/html parts</summary>
    private static (string? plain, string? html) Bodies(MessagePart part)
    {
        string? plain = null, html = null;
        void Walk(MessagePart? p)
        {
            if (p == null)
            {
                return;
            }

            var data = p.Body?.Data;
            if (data != null && plain == null && p.MimeType == "text/plain")
            {
                plain = SignatureReader.Decode(data, SignatureReader.CharsetOf(p));
            }
            else if (data != null && html == null && p.MimeType == "text/html")
            {
                html = SignatureReader.Decode(data, SignatureReader.CharsetOf(p));
            }

            foreach (var child in p.Parts ?? new List<MessagePart>())
            {
                Walk(child);
            }
        }

        Walk(part);
        return (plain, html);
    }

    /// <summary>A Gmail call: 401 and 403 are a refused sign-in, 429 and 5xx passing</summary>
    private static async Task<T> Run<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (global::Google.GoogleApiException ex) when (ex.HttpStatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new MailRefusedException("Gmail refused the sign-in", ex);
        }
        catch (global::Google.GoogleApiException ex) when (ex.HttpStatusCode != System.Net.HttpStatusCode.NotFound)
        {
            throw new FlightSourceException($"Gmail answered {(int)ex.HttpStatusCode}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new FlightSourceException("Gmail could not be reached", ex);
        }
    }
}
