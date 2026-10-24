/**
 * @fileoverview The user's own email addresses: mail between them is not a
 * conversation with someone else
 * @module ContactsApi.Infrastructure.Activity
 */
namespace ContactsApi.Infrastructure.Activity;

/// <summary>Collects the user's own addresses</summary>
public static class OwnAddresses
{
    /// <summary>
    /// The user's addresses plus every address on a contact card that holds one of
    /// them (the user's own card lists their other addresses). Cards with many
    /// addresses (a shared "family" card) are not taken as the user's.
    /// </summary>
    /// <param name="known">Addresses known to be the user's (account, mailbox, Gmail aliases, marked by the user)</param>
    /// <param name="cards">Each contact's addresses</param>
    /// <returns>Lower-case addresses</returns>
    public static HashSet<string> Expand(IEnumerable<string?> known, IEnumerable<IReadOnlyCollection<string?>> cards)
    {
        var mine = known.Select(Normalize).Where(e => e.Length > 0).ToHashSet();
        var own = cards
            .Select(c => c.Select(Normalize).Where(e => e.Length > 0).Distinct().ToList())
            .Where(c => c.Count is > 1 and <= 6 && c.Any(mine.Contains))
            .SelectMany(c => c)
            .ToList();
        mine.UnionWith(own);
        return mine;
    }

    /// <summary>Lower-case, trimmed</summary>
    public static string Normalize(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>A plain email address (no names, no spaces)</summary>
    public static bool IsAddress(string email) =>
        System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s<>""]+@[^@\s<>""]+\.[^@\s<>""]+$");
}
