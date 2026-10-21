/**
 * @fileoverview Finds the part of an email body that holds the sender's signature
 * @module ContactsApi.Infrastructure.Activity
 */
using System.Net;
using System.Text.RegularExpressions;

namespace ContactsApi.Infrastructure.Activity;

/// <summary>
/// The sender's own text ends where the quoted earlier messages begin; the
/// signature is the last lines of that text. Only those lines are passed on, so
/// the quoted messages of other people (and most of the body) never leave.
/// </summary>
public static class SignatureText
{
    /// <summary>Lines that start quoted history, in English and Hebrew mail clients</summary>
    private static readonly Regex[] QuoteStarts =
    {
        new(@"^On .{4,200}wrote:\s*$", RegexOptions.IgnoreCase),
        new(@"^בתאריך .{4,200}(כתב|כתבה|נכתב)[^:]*:\s*$"),
        new(@"^-{2,}\s*(Original Message|Forwarded message|הודעה מקורית|הודעה שהועברה)\s*-{2,}", RegexOptions.IgnoreCase),
        new(@"^(From|מאת)\s*:\s*.+", RegexOptions.IgnoreCase),
        new(@"^_{10,}\s*$"),
        new(@"^>"),
    };

    private static readonly Regex Tags = new(@"<(script|style)[^>]*>.*?</\1>|<[^>]+>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex Breaks = new(@"<\s*(br|/p|/div|/tr|/li)\s*/?>", RegexOptions.IgnoreCase);

    /// <summary>HTML mail as plain text with line breaks kept</summary>
    /// <param name="html">HTML</param>
    /// <returns>Text</returns>
    public static string HtmlToText(string html) =>
        WebUtility.HtmlDecode(Tags.Replace(Breaks.Replace(html, "\n"), string.Empty));

    /// <summary>
    /// The last lines the sender wrote (above any quoted history), where a
    /// signature is.
    /// </summary>
    /// <param name="body">Plain-text body</param>
    /// <param name="maxLines">Lines to keep</param>
    /// <param name="maxChars">Characters to keep</param>
    /// <returns>The tail, '' when the body is empty</returns>
    public static string AuthorTail(string? body, int maxLines = 18, int maxChars = 1200)
    {
        var lines = (body ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var own = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Replace('\u00a0', ' ').TrimEnd();
            var trimmed = line.TrimStart();
            // Gmail wraps long quote headers: "On ... Dana <" / "dana@x> wrote:"
            var joined = i + 1 < lines.Length ? $"{trimmed} {lines[i + 1].Trim()}" : trimmed;
            if (QuoteStarts.Any(q => q.IsMatch(trimmed))
                || (trimmed.StartsWith("On ", StringComparison.OrdinalIgnoreCase) && QuoteStarts[0].IsMatch(joined))
                || (trimmed.StartsWith("בתאריך ", StringComparison.Ordinal) && QuoteStarts[1].IsMatch(joined)))
            {
                break;
            }

            own.Add(line);
        }

        var tail = own.Where(l => l.Trim().Length > 0).TakeLast(maxLines).Select(l => l.Trim()).ToList();
        var text = string.Join("\n", tail);
        return text.Length <= maxChars ? text : text[^maxChars..];
    }
}
