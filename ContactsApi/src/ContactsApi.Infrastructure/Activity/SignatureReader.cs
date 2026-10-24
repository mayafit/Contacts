/**
 * @fileoverview Reads the latest emails from a person (Gmail read scope, opt-in)
 * @module ContactsApi.Infrastructure.Activity
 */
using System.Text;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;

namespace ContactsApi.Infrastructure.Activity;

/// <summary>The end of one email a person sent, where their signature is</summary>
/// <param name="At">When it was received</param>
/// <param name="Tail">The sender's last lines (see <see cref="SignatureText.AuthorTail"/>)</param>
public record SignatureSample(DateTime At, string Tail);

/// <summary>
/// Fetches a person's most recent emails and keeps only the tail of the text
/// they wrote. Needs the gmail.readonly scope; bodies are never stored.
/// </summary>
public class SignatureReader
{
    private readonly GmailService _gmail;

    /// <summary>Creates a reader for one user</summary>
    /// <param name="accessToken">Returns a valid access token</param>
    public SignatureReader(Func<CancellationToken, Task<string>> accessToken) =>
        _gmail = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = new FreshTokenInitializer(accessToken),
            ApplicationName = "ContactsApi"
        });

    /// <summary>
    /// The tails of the latest emails sent by any of the addresses.
    /// </summary>
    /// <param name="emails">The person's addresses</param>
    /// <param name="max">Emails to read</param>
    /// <param name="ct">Cancellation</param>
    /// <returns>Newest first</returns>
    public async Task<List<SignatureSample>> RecentFromAsync(IReadOnlyList<string> emails, int max, CancellationToken ct)
    {
        // Only plain addresses, quoted: a stored "address" must not widen the search
        var from = string.Join(" OR ", emails
            .Select(e => e.Trim())
            .Where(e => System.Text.RegularExpressions.Regex.IsMatch(e, @"^[A-Za-z0-9._%+'\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)+$"))
            .Distinct()
            .Take(5)
            .Select(e => $"\"{e}\""));
        if (from.Length == 0)
        {
            return new();
        }

        try
        {
            var list = _gmail.Users.Messages.List("me");
            list.Q = $"from:({from}) newer_than:3y -in:chats";
            list.MaxResults = max;
            list.Fields = "messages/id";
            // No match can come back as no response at all
            var page = await list.ExecuteAsync(ct);
            var samples = new List<SignatureSample>();
            foreach (var id in page?.Messages?.Select(m => m?.Id).OfType<string>() ?? Enumerable.Empty<string>())
            {
                var get = _gmail.Users.Messages.Get("me", id);
                get.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
                get.Fields = "internalDate,payload(mimeType,headers,body/data,parts(mimeType,headers,body/data,parts(mimeType,headers,body/data,parts(mimeType,headers,body/data))))";
                var m = await get.ExecuteAsync(ct);
                if (m == null)
                {
                    continue;
                }

                var tail = SignatureText.AuthorTail(BodyText(m.Payload));
                if (tail.Length > 0)
                {
                    samples.Add(new SignatureSample(DateTimeOffset.FromUnixTimeMilliseconds(m.InternalDate ?? 0).UtcDateTime, tail));
                }
            }

            return samples;
        }
        catch (global::Google.GoogleApiException ex)
            when ((ex.Error?.Message ?? ex.Message).Contains("Metadata scope", StringComparison.OrdinalIgnoreCase))
        {
            // The account still carries the old metadata-only grant next to the content one
            throw new ActivityScanException(
                "SCOPE_CONFLICT",
                "Google still applies the app's older Gmail permission. Remove the app at https://myaccount.google.com/permissions, sign in again, and allow email content.",
                ex);
        }
        catch (global::Google.GoogleApiException ex)
        {
            throw GoogleActivityClient.Classify((int)ex.HttpStatusCode, ex.Error?.Message ?? ex.Message, "gmail", ex);
        }
    }

    /// <summary>The plain text of a message (the HTML part converted when there is none)</summary>
    internal static string BodyText(MessagePart? part)
    {
        string? plain = null, html = null;
        void Walk(MessagePart? p)
        {
            if (p == null)
            {
                return;
            }

            var data = p.Body?.Data;
            var charset = CharsetOf(p);
            if (data != null && plain == null && p.MimeType == "text/plain")
            {
                plain = Decode(data, charset);
            }
            else if (data != null && html == null && p.MimeType == "text/html")
            {
                html = Decode(data, charset);
            }

            foreach (var child in p.Parts ?? new List<MessagePart>())
            {
                Walk(child);
            }
        }

        Walk(part);
        return plain ?? (html == null ? string.Empty : SignatureText.HtmlToText(html));
    }

    /// <summary>The charset of a part (Content-Type), e.g. windows-1255 for older Hebrew mail</summary>
    private static string? CharsetOf(MessagePart p)
    {
        var type = p.Headers?.FirstOrDefault(h => string.Equals(h.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
        var m = type == null ? null : System.Text.RegularExpressions.Regex.Match(type, @"charset=""?([^"";\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m is { Success: true } ? m.Groups[1].Value : null;
    }

    /// <summary>Gmail's base64url body data as text ('' when it cannot be decoded)</summary>
    private static string Decode(string data, string? charset)
    {
        try
        {
            var s = data.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            var bytes = Convert.FromBase64String(s);
            Encoding encoding;
            try
            {
                EncodedWords.Decode(null); // registers the code-page encodings
                encoding = charset == null ? Encoding.UTF8 : Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                encoding = Encoding.UTF8;
            }

            return encoding.GetString(bytes);
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }
}
