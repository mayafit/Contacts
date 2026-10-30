/**
 * @fileoverview The links in a person's own email signature (AD-46 rule 4), by pattern
 * @module ContactsApi.Infrastructure.Activity
 */
using System.Text.RegularExpressions;

namespace ContactsApi.Infrastructure.Activity;

/// <summary>Profile and website links from signature tails, without a model</summary>
public static class SignatureLinks
{
    /// <summary>Most links returned</summary>
    public const int MaxLinks = 20;

    private static readonly Regex Url = new(@"(?:https?://|www\.)[^\s<>""'()\[\]]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BareLinkedIn = new(@"(?<![\w./])(?:[a-z]{2}\.)?linkedin\.com/in/[^\s<>""'()\[\]]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Every URL (and bare linkedin.com/in/… link) in the tails, cleaned (trailing
    /// punctuation and tracking parameters dropped), with https added when missing,
    /// distinct ignoring case, at most 20
    /// </summary>
    /// <param name="tails">The author tails of the person's emails</param>
    /// <returns>Links</returns>
    public static List<string> Extract(IEnumerable<string> tails)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<string>();
        foreach (var tail in tails)
        {
            var found = Url.Matches(tail).Select(m => m.Value)
                .Concat(BareLinkedIn.Matches(tail).Select(m => m.Value));
            foreach (var raw in found)
            {
                var url = SignatureText.CleanUrl(raw.TrimEnd('.', ',', ';', ':', '!', '?', '>', '|'));
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://" + url;
                }

                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Contains('.') && seen.Add(url))
                {
                    links.Add(url);
                    if (links.Count == MaxLinks)
                    {
                        return links;
                    }
                }
            }
        }

        return links;
    }
}
