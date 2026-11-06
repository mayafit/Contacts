using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ContactsApi.WebApi.Services;
using ContactsApi.WebApi.Services.KeyRing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace ContactsApi.Tests;

/// <summary>
/// The rotation against a real Redis (the cache's hash format, expiry kept, keys deleted from
/// the list). Opt-in: set ECHO_TEST_REDIS to a connection string (with password) of a
/// throwaway Redis; the test flushes its database.
/// </summary>
public class KeyRingRedisIntegrationTests
{
    private static string? Connection => Environment.GetEnvironmentVariable("ECHO_TEST_REDIS");

    private static ServiceProvider App(IConnectionMultiplexer redis, X509Certificate2? cert)
    {
        var settings = new Dictionary<string, string?>();
        if (cert != null)
        {
            settings[DataProtectionConfig.Certificate] = cert.ExportCertificatePem() + "\n" + cert.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddEchoDataProtection(configuration, redis);
        services.AddStackExchangeRedisCache(o =>
        {
            o.ConfigurationOptions = ConfigurationOptions.Parse(Connection!);
            o.InstanceName = RedisProtectedValues.InstanceName;
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Rotation_in_redis_keeps_values_and_expiry_and_deletes_the_old_keys()
    {
        if (string.IsNullOrEmpty(Connection))
        {
            return;
        }

        var options = ConfigurationOptions.Parse(Connection);
        options.AllowAdmin = true;
        using var redis = await ConnectionMultiplexer.ConnectAsync(options);
        await redis.GetServers().Single().FlushDatabaseAsync();

        // Before wrapping: the app writes a refresh token through IDistributedCache
        await using (var before = App(redis, null))
        {
            var protector = before.GetRequiredService<IDataProtectionProvider>().CreateProtector(GoogleTokenService.RefreshTokenPurpose);
            await before.GetRequiredService<IDistributedCache>().SetStringAsync("refresh-token:owner-1", protector.Protect("rt-1"),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(30) });
        }

        using var rsa = RSA.Create(2048);
        var cert = new CertificateRequest("CN=dp", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        await using var app = App(redis, cert);
        var ttlBefore = await redis.GetDatabase().KeyTimeToLiveAsync(RedisProtectedValues.InstanceName + "refresh-token:owner-1");
        var source = new RedisProtectedValues(redis, "google-refresh-tokens", "refresh-token:", GoogleTokenService.RefreshTokenPurpose);
        var rotator = new KeyRingRotator(app.GetRequiredService<IDataProtectionProvider>(), app.GetRequiredService<IKeyManager>(),
            KeyRingInspector.Repository(app), app.GetRequiredService<DataProtectionCertificates>(), new[] { source }, NullLogger.Instance);

        var report = await rotator.RunAsync(RotationMode.Retire, CancellationToken.None);

        Assert.Null(report.Refused);
        Assert.Null(report.RetireBlocked);
        Assert.Equal(1, report.Kinds["google-refresh-tokens"].Reprotected);
        Assert.True(report.KeysRetired >= 1);
        var keys = KeyRingInspector.Keys(KeyRingInspector.Repository(app));
        Assert.Equal(new[] { report.CurrentKey }, keys.Keys);
        Assert.True(keys[report.CurrentKey]);
        var ttlAfter = await redis.GetDatabase().KeyTimeToLiveAsync(RedisProtectedValues.InstanceName + "refresh-token:owner-1");
        Assert.NotNull(ttlAfter);
        Assert.True(ttlAfter <= ttlBefore && ttlAfter > ttlBefore - TimeSpan.FromMinutes(1));
        var stored = await app.GetRequiredService<IDistributedCache>().GetStringAsync("refresh-token:owner-1");
        Assert.Equal("rt-1", app.GetRequiredService<IDataProtectionProvider>().CreateProtector(GoogleTokenService.RefreshTokenPurpose).Unprotect(stored!));
        Assert.DoesNotContain(await redis.GetDatabase().ListRangeAsync(DataProtectionConfig.RedisKey),
            v => v.ToString().StartsWith("<key ", StringComparison.Ordinal) && !v.ToString().Contains("encryptedSecret"));
    }
}
