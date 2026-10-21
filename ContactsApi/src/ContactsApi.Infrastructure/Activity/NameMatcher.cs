/**
 * @fileoverview Matches a contact's names against names seen in mail headers
 * @module ContactsApi.Infrastructure.Activity
 */
using System.Text.RegularExpressions;

namespace ContactsApi.Infrastructure.Activity;

/// <summary>
/// Decides whether an address seen in mail belongs to a contact, by name:
/// "Dana Cohen", "Cohen, Dana" and "dana.cohen@..." all match the contact
/// Dana Cohen. Single words never match (too many Danas).
/// </summary>
public static class NameMatcher
{
    private static readonly Regex NonLetters = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>Lower-case words separated by single spaces</summary>
    /// <param name="name">Name</param>
    /// <returns>Normalized name</returns>
    public static string Normalize(string? name) =>
        NonLetters.Replace((name ?? string.Empty).ToLowerInvariant(), " ").Trim();

    /// <summary>
    /// How well an address matches: 3 = a display name with exactly the same
    /// words, 2 = the address's local part spells the name (dana.cohen@),
    /// 1 = a display name containing all the words plus at most one more
    /// (a middle name), 0 = no match.
    /// </summary>
    /// <param name="variants">Normalized names of the contact (two or more words)</param>
    /// <param name="address">Email address</param>
    /// <param name="names">Display names seen for the address</param>
    /// <returns>Score</returns>
    public static int Score(IReadOnlyList<string> variants, string address, IEnumerable<string> names)
    {
        var seen = names.Select(Normalize).Where(n => n.Length > 0).ToList();
        var local = Normalize(address.Split('@')[0]);
        var best = 0;
        foreach (var v in variants)
        {
            var words = v.Split(' ');
            if (words.Length < 2)
            {
                continue;
            }

            var set = words.ToHashSet();
            foreach (var n in seen)
            {
                var nw = n.Split(' ');
                if (nw.Length == set.Count && set.SetEquals(nw))
                {
                    return 3;
                }

                if (nw.Length <= set.Count + 1 && set.All(nw.Contains))
                {
                    best = Math.Max(best, 1);
                }
            }

            var lw = local.Split(' ');
            if (lw.Length == set.Count && set.SetEquals(lw))
            {
                best = Math.Max(best, 2);
            }
        }

        return best;
    }
}
