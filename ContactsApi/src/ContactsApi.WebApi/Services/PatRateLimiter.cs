/**
 * @fileoverview A rate limit per MCP access token (AD-50 rule 9): per minute and per day
 * @module ContactsApi.WebApi.Services
 */
using System.Collections.Concurrent;

namespace ContactsApi.WebApi.Services;

/// <summary>The limits (configuration <c>Mcp:RateLimit:PerMinute</c> and <c>Mcp:RateLimit:PerDay</c>)</summary>
public class PatRateLimitOptions
{
    /// <summary>Calls per token per minute</summary>
    public int PerMinute { get; set; } = 60;

    /// <summary>Calls per token per UTC day</summary>
    public int PerDay { get; set; } = 5000;
}

/// <summary>
/// Fixed-window counters per token id, in process memory. The backend runs one replica on
/// the Pi, so a restart resetting the counters is the only gap (accepted; a Redis counter
/// under the Owner prefix replaces this when a second replica exists).
/// </summary>
public class PatRateLimiter
{
    private readonly PatRateLimitOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, Counters> _counters = new();

    /// <summary>Creates the limiter</summary>
    public PatRateLimiter(PatRateLimitOptions options, TimeProvider? time = null)
    {
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Counts a call; false when the token is over a limit (the call is refused, not counted)</summary>
    /// <param name="tokenId">The token</param>
    /// <param name="retryAfter">When over a limit, how long until the window resets</param>
    public bool TryAcquire(Guid tokenId, out TimeSpan retryAfter)
    {
        var now = _time.GetUtcNow();
        var minute = now.ToUnixTimeSeconds() / 60;
        var day = now.ToUnixTimeSeconds() / 86_400;
        var counters = _counters.GetOrAdd(tokenId, _ => new Counters());
        lock (counters)
        {
            if (counters.Minute != minute)
            {
                counters.Minute = minute;
                counters.InMinute = 0;
            }

            if (counters.Day != day)
            {
                counters.Day = day;
                counters.InDay = 0;
            }

            if (counters.InDay >= _options.PerDay)
            {
                retryAfter = TimeSpan.FromSeconds((day + 1) * 86_400 - now.ToUnixTimeSeconds());
                return false;
            }

            if (counters.InMinute >= _options.PerMinute)
            {
                retryAfter = TimeSpan.FromSeconds((minute + 1) * 60 - now.ToUnixTimeSeconds());
                return false;
            }

            counters.InMinute++;
            counters.InDay++;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private sealed class Counters
    {
        public long Minute;
        public long Day;
        public int InMinute;
        public int InDay;
    }
}
