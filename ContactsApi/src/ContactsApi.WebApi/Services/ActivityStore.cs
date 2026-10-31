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
    private readonly IDataProtector _historyProtector;
    private readonly ILogger<ActivityStore> _logger;

    /// <summary>Creates the store</summary>
    public ActivityStore(IDistributedCache cache, IDataProtectionProvider dataProtection, ILogger<ActivityStore> logger)
    {
        _cache = cache;
        _protector = dataProtection.CreateProtector("ContactsApi.Activity.v1");
        _historyProtector = dataProtection.CreateProtector("ContactsApi.Activity.History.v1");
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

    private static string SelfKey(string userId) => $"self-emails:{userId}";

    /// <summary>Addresses the user marked as their own ("This is me")</summary>
    public async Task<List<string>> GetSelfAsync(string userId)
    {
        var stored = await _cache.GetStringAsync(SelfKey(userId));
        if (stored == null)
        {
            return new List<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(_protector.Unprotect(stored), Json) ?? new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Own addresses could not be read");
            return new List<string>();
        }
    }

    /// <summary>Saves the addresses the user marked as their own (kept until changed)</summary>
    public Task SaveSelfAsync(string userId, IEnumerable<string> emails) =>
        _cache.SetStringAsync(SelfKey(userId), _protector.Protect(JsonSerializer.Serialize(emails.Distinct().Take(50).ToList(), Json)));

    /// <summary>The scan periods offered, in years (AD-47 rule 1)</summary>
    public static readonly int[] WindowYears = { 1, 2, 3, 5 };

    /// <summary>Contacts' histories kept per user; the oldest checked go first</summary>
    public const int MaxHistories = 2_000;

    private static string WindowKey(string userId) => $"activity-window:{userId}";

    private static string HistoryKey(string userId) => $"activity-history:{userId}";

    /// <summary>The scan period in years (1 when never set). A setting: Forget keeps it.</summary>
    public async Task<int> GetWindowYearsAsync(string userId)
    {
        var stored = await _cache.GetStringAsync(WindowKey(userId));
        return int.TryParse(stored, out var years) && WindowYears.Contains(years) ? years : 1;
    }

    /// <summary>Saves the scan period (kept without expiry: it says nothing about anyone)</summary>
    public Task SaveWindowYearsAsync(string userId, int years) =>
        _cache.SetStringAsync(WindowKey(userId), years.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The Contacts' whole histories by resource name</summary>
    public async Task<Dictionary<string, ContactHistory>> GetHistoriesAsync(string userId)
    {
        var stored = await _cache.GetStringAsync(HistoryKey(userId));
        if (stored == null)
        {
            return new Dictionary<string, ContactHistory>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, ContactHistory>>(_historyProtector.Unprotect(stored), Json)
                ?? new Dictionary<string, ContactHistory>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Contact histories could not be read; starting over");
            return new Dictionary<string, ContactHistory>();
        }
    }

    /// <summary>Saves one Contact's history, dropping the oldest checked beyond <see cref="MaxHistories"/></summary>
    public async Task SaveHistoryAsync(string userId, string contact, ContactHistory history)
    {
        var all = await GetHistoriesAsync(userId);
        all[contact] = history;
        var kept = all.OrderByDescending(e => e.Value.CheckedAt).Take(MaxHistories).ToDictionary(e => e.Key, e => e.Value);
        await _cache.SetStringAsync(
            HistoryKey(userId),
            _historyProtector.Protect(JsonSerializer.Serialize(kept, Json)),
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(90) });
    }

    /// <summary>Deletes the histories (with Forget)</summary>
    public Task RemoveHistoriesAsync(string userId) => _cache.RemoveAsync(HistoryKey(userId));
}
