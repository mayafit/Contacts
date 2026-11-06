using ContactsApi.WebApi.Services;
using Microsoft.Extensions.Configuration;

namespace ContactsApi.Tests;

/// <summary>The backend authenticates to Redis with the password from Vault (AD-50 rule 1)</summary>
public class RedisSettingsTests
{
    private static IConfiguration Config(string? connection, string? password) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Redis:ConnectionString"] = connection,
            ["Redis:Password"] = password
        }).Build();

    [Fact]
    public void The_vault_password_is_added_to_the_configmap_connection_string()
    {
        var options = RedisSettings.Options(Config("contacts-redis-master:6379", "from-vault"));

        Assert.Equal("from-vault", options.Password);
        Assert.Equal("contacts-redis-master:6379", options.EndPoints.Single().ToString()!.Replace("Unspecified/", string.Empty));
        Assert.False(options.AbortOnConnectFail);
    }

    [Fact]
    public void Without_a_password_the_connection_is_as_before()
    {
        Assert.Null(RedisSettings.Options(Config("localhost:6379", null)).Password);
        Assert.Equal("inline", RedisSettings.Options(Config("localhost:6379,password=inline", "")).Password);
    }
}
