using System.Net;
using ContactsApi.WebApi.Middleware;
using ContactsApi.WebApi.Services;

namespace ContactsApi.Tests;

/// <summary>Logs carry no codes, states, emails or coordinates from URLs (AD-50 rule 8)</summary>
public class RequestLogRedactionTests
{
    [Fact]
    public async Task The_callback_code_and_state_appear_in_no_log_event()
    {
        await using var host = await SecurityTestHost.StartAsync();

        await host.GetAsync("/api/auth/callback?code=XCODE-4711&state=YSTATE-0815");

        Assert.NotEmpty(host.Logs.Events);
        Assert.DoesNotContain(host.Logs.Events, e => e.Contains("XCODE-4711") || e.Contains("YSTATE-0815"));
        Assert.Contains(host.Logs.Events, e => e.Contains("/api/Auth/callback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_full_sign_in_logs_no_code_state_hint_or_token()
    {
        await using var host = await SecurityTestHost.StartAsync();

        await host.SignInAsync();
        await host.GetAsync("/api/auth/login?hint=owner%40example.com&again=true");

        Assert.DoesNotContain(host.Logs.Events, e => e.Contains("the-code") || e.Contains("access-the-code")
                                                    || e.Contains("owner@example.com", StringComparison.OrdinalIgnoreCase)
                                                    || e.Contains("owner%40example.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_route_template_is_logged_not_the_raw_path()
    {
        await using var host = await SecurityTestHost.StartAsync();

        await host.GetAsync("/api/test/self/someone@example.com?lat=32.1&lon=34.8&token=atl_secret&page=2");

        Assert.DoesNotContain(host.Logs.Events, e => e.Contains("someone@example.com") || e.Contains("32.1") || e.Contains("34.8") || e.Contains("atl_secret"));
        Assert.Contains(host.Logs.Events, e => e.Contains("/api/test/self/{email}") && e.Contains("?page=2"));
    }

    [Theory]
    [InlineData("page=3&pageSize=50", "?page=3&pageSize=50")]
    [InlineData("code=abc&state=def", "")]
    [InlineData("limit=abc@x.com", "?limit=…")]
    [InlineData("", "")]
    public void Only_numbers_of_harmless_keys_are_kept(string query, string expected)
    {
        var parsed = new Microsoft.AspNetCore.Http.QueryCollection(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query));

        Assert.Equal(expected, RequestLoggingMiddleware.SafeQuery(parsed));
    }
}
