/**
 * @fileoverview The Redis connection options (AD-50 rule 1: Redis requires a password)
 * @module ContactsApi.WebApi.Services
 */
using StackExchange.Redis;

namespace ContactsApi.WebApi.Services;

/// <summary>How the backend connects to Redis</summary>
public static class RedisSettings
{
    /// <summary>
    /// The options from <c>Redis:ConnectionString</c> (the ConfigMap, without a password) and
    /// <c>Redis:Password</c> (Vault: <c>Redis__Password</c>). A password inside the connection
    /// string is kept when <c>Redis:Password</c> is not set. AbortOnConnectFail is off, so a
    /// Redis that is still starting costs seconds of degraded sessions, not a crash loop.
    /// </summary>
    public static ConfigurationOptions Options(IConfiguration configuration)
    {
        var options = ConfigurationOptions.Parse(configuration["Redis:ConnectionString"] ?? "localhost:6379");
        options.AbortOnConnectFail = false;
        options.ConnectRetry = 5;
        options.ConnectTimeout = 5000;
        var password = configuration["Redis:Password"];
        if (!string.IsNullOrEmpty(password))
        {
            options.Password = password;
        }

        return options;
    }
}
