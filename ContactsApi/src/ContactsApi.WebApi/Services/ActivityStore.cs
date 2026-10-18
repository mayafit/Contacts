/**
 * @fileoverview Keeps each user's activity snapshot encrypted in Redis
 * @module ContactsApi.WebApi.Services
 */
using System.Text.Json;
using ContactsApi.Infrastructure.Activity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;

namespace ContactsApi.WebApi.Services;

/// <summary>
/// Activity snapshots per Google account. They say who the user corresponds and
/// meets with, so they are stored encrypted like the refresh tokens.
/// </summary>
public class ActivityStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDistributedCache _cache;
    private readonly IDataProtector _protector;
    private readonly ILogger<ActivityStore> _logger;

    /// <summary>Creates the store</summary>
    public ActivityStore(IDistributedCache cache, IDataProtectionProvider dataProtection, ILogger<ActivityStore> logger)
    {
        _cache = cache;
        _protector = dataProtection.CreateProtector("ContactsApi.Activity.v1");
        _logger = logger;
    }

    /// <summary>The user's snapshot, or an empty one</summary>
    /// <param name="userId">Google account id</param>
    /// <returns>Snapshot</returns>
    public async Task<ActivitySnapshot> GetAsync(string userId)
    {
        var stored = await _cache.GetStringAsync(Key(userId));
        if (stored == null)
        {
            return new ActivitySnapshot();
        }

        try
        {
            return JsonSerializer.Deserialize<ActivitySnapshot>(_protector.Unprotect(stored), Json) ?? new ActivitySnapshot();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Activity snapshot could not be read; starting over");
            return new ActivitySnapshot();
        }
    }

    /// <summary>Saves the user's snapshot</summary>
    /// <param name="userId">Google account id</param>
    /// <param name="snapshot">Snapshot</param>
    public Task SaveAsync(string userId, ActivitySnapshot snapshot) =>
        _cache.SetStringAsync(
            Key(userId),
            _protector.Protect(JsonSerializer.Serialize(snapshot, Json)),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(90) });

    /// <summary>Deletes the user's snapshot</summary>
    /// <param name="userId">Google account id</param>
    public Task RemoveAsync(string userId) => _cache.RemoveAsync(Key(userId));

    private static string Key(string userId) => $"activity:{userId}";
}
