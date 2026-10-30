/**
 * @fileoverview Which optional Google permissions each user opted into, and the
 * exact scope list a sign-in asks for
 * @module ContactsApi.WebApi.Services
 */
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace ContactsApi.WebApi.Services;

/// <summary>A user's opt-ins</summary>
public class OptIns
{
    /// <summary>Gmail & Calendar activity</summary>
    public bool Activity { get; set; }

    /// <summary>Email content (signatures)</summary>
    public bool Signatures { get; set; }

    /// <summary>Reading Takeout exports from Google Drive (AD-40)</summary>
    public bool Takeout { get; set; }

    /// <summary>Both sets of opt-ins combined</summary>
    public OptIns With(OptIns other) =>
        new() { Activity = Activity || other.Activity, Signatures = Signatures || other.Signatures, Takeout = Takeout || other.Takeout };
}

/// <summary>The Drive scope of the Takeout opt-in (AD-40 rule 2)</summary>
public static class TakeoutScopes
{
    /// <summary>Drive files, read only (restricted: Takeout, not Echo, writes the exports, so drive.file does not cover them)</summary>
    public const string Drive = "https://www.googleapis.com/auth/drive.readonly";
}

/// <summary>
/// Opt-ins per Google account. Sign-ins ask Google for exactly these scopes
/// instead of everything ever granted (include_granted_scopes), because Gmail
/// refuses content reads when a token also carries gmail.metadata.
/// </summary>
public class ScopePreferences
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDistributedCache _cache;

    /// <summary>Creates the store</summary>
    public ScopePreferences(IDistributedCache cache) => _cache = cache;

    /// <summary>The user's opt-ins (none when unknown)</summary>
    /// <param name="userId">Google account id</param>
    /// <returns>Opt-ins</returns>
    public async Task<OptIns> GetAsync(string userId)
    {
        var stored = await _cache.GetStringAsync(Key(userId));
        return stored == null ? new OptIns() : JsonSerializer.Deserialize<OptIns>(stored, Json) ?? new OptIns();
    }

    /// <summary>The user's opt-ins, or null when never saved</summary>
    /// <param name="userId">Google account id</param>
    /// <returns>Opt-ins or null</returns>
    public async Task<OptIns?> FindAsync(string userId)
    {
        var stored = await _cache.GetStringAsync(Key(userId));
        return stored == null ? null : JsonSerializer.Deserialize<OptIns>(stored, Json);
    }

    /// <summary>Opt-ins implied by a set of granted scopes</summary>
    /// <param name="scopes">Scopes</param>
    /// <returns>Opt-ins</returns>
    public static OptIns FromScopes(IReadOnlyCollection<string> scopes) => new()
    {
        Signatures = scopes.Contains(ActivityScopes.GmailRead),
        Activity = scopes.Contains(ActivityScopes.Gmail) || scopes.Contains(ActivityScopes.Calendar),
        Takeout = scopes.Contains(TakeoutScopes.Drive)
    };

    /// <summary>Saves the user's opt-ins</summary>
    /// <param name="userId">Google account id</param>
    /// <param name="optIns">Opt-ins</param>
    public Task SaveAsync(string userId, OptIns optIns) =>
        _cache.SetStringAsync(Key(userId), JsonSerializer.Serialize(optIns, Json),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(365) });

    /// <summary>
    /// Scopes for a set of opt-ins. With email content, gmail.readonly replaces
    /// gmail.metadata (it allows everything the activity scan does).
    /// </summary>
    /// <param name="baseScopes">Scopes every sign-in needs</param>
    /// <param name="optIns">Opt-ins</param>
    /// <returns>Scopes, without duplicates</returns>
    public static List<string> ScopesFor(IEnumerable<string> baseScopes, OptIns optIns)
    {
        var scopes = baseScopes.ToList();
        if (optIns.Signatures)
        {
            scopes.Add(ActivityScopes.GmailRead);
        }
        else if (optIns.Activity)
        {
            scopes.Add(ActivityScopes.Gmail);
        }

        if (optIns.Activity)
        {
            scopes.Add(ActivityScopes.Calendar);
        }

        if (optIns.Takeout)
        {
            scopes.Add(TakeoutScopes.Drive);
        }

        return scopes.Distinct().ToList();
    }

    private static string Key(string userId) => $"optins:{userId}";
}
