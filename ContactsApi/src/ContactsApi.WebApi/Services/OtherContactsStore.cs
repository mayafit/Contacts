/**
 * @fileoverview The suggestions from Other contacts the Owner dismissed (AD-48 rule 7)
 * @module ContactsApi.WebApi.Services
 */
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;

namespace ContactsApi.WebApi.Services;

/// <summary>
/// Dismissed suggestion keys per user, encrypted (they hold addresses). Nothing of
/// Other contacts itself is stored.
/// </summary>
public class OtherContactsStore
{
    /// <summary>Keys kept at most; the oldest are dropped</summary>
    public const int MaxKeys = 5_000;

    /// <summary>The longest key accepted</summary>
    public const int MaxKeyLength = 600;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDistributedCache _cache;
    private readonly IDataProtector _protector;
    private readonly ILogger<OtherContactsStore> _logger;

    /// <summary>Creates the store</summary>
    public OtherContactsStore(IDistributedCache cache, IDataProtectionProvider dataProtection, ILogger<OtherContactsStore> logger)
    {
        _cache = cache;
        _protector = dataProtection.CreateProtector("ContactsApi.OtherContacts.Dismissed.v1");
        _logger = logger;
    }

    /// <summary>The dismissed keys, oldest first</summary>
    /// <param name="userId">Google account id</param>
    /// <returns>Keys</returns>
    public async Task<List<string>> GetDismissedAsync(string userId)
    {
        var stored = await _cache.GetStringAsync(Key(userId));
        if (stored == null)
        {
            return new List<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(_protector.Unprotect(stored), Json) ?? new List<string>();
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            // A rotated key ring: the dismissals are lost, the suggestions come back
            _logger.LogWarning(ex, "Dismissed Other contacts could not be read; starting over");
            return new List<string>();
        }
    }

    /// <summary>Adds keys (too long or empty ones are ignored), keeping the newest <see cref="MaxKeys"/></summary>
    /// <param name="userId">Google account id</param>
    /// <param name="keys">Keys to add</param>
    /// <returns>All keys</returns>
    public async Task<List<string>> AddDismissedAsync(string userId, IEnumerable<string> keys)
    {
        var all = await GetDismissedAsync(userId);
        var known = all.ToHashSet(StringComparer.Ordinal);
        foreach (var key in keys.Where(k => !string.IsNullOrWhiteSpace(k) && k.Length <= MaxKeyLength))
        {
            if (known.Add(key))
            {
                all.Add(key);
            }
        }

        if (all.Count > MaxKeys)
        {
            all = all.Skip(all.Count - MaxKeys).ToList();
        }

        await _cache.SetStringAsync(Key(userId), _protector.Protect(JsonSerializer.Serialize(all, Json)),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(365) });
        return all;
    }

    /// <summary>Forgets every dismissal</summary>
    /// <param name="userId">Google account id</param>
    public Task ClearDismissedAsync(string userId) => _cache.RemoveAsync(Key(userId));

    private static string Key(string userId) => $"other-contacts-dismissed:{userId}";
}
