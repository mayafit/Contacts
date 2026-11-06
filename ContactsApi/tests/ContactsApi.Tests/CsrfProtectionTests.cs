using System.Net;
using ContactsApi.WebApi.Middleware;
using ContactsApi.WebApi.Services;

namespace ContactsApi.Tests;

/// <summary>The CSRF matrix of AD-50 rule 7</summary>
public class CsrfProtectionTests
{
    private static readonly CsrfOptions Production = new() { PublicOrigin = SecurityTestHost.PublicOrigin, DevelopmentOrigins = new[] { "http://localhost:3002" } };
    private static readonly CsrfOptions Development = new() { PublicOrigin = "http://localhost:3002", DevelopmentOrigins = new[] { "http://localhost:3002" }, IsDevelopment = true };

    private static Microsoft.AspNetCore.Http.IHeaderDictionary Headers(string? echo, string? fetchSite, string? origin)
    {
        var headers = new Microsoft.AspNetCore.Http.HeaderDictionary();
        if (echo != null) headers[CsrfProtectionMiddleware.HeaderName] = echo;
        if (fetchSite != null) headers["Sec-Fetch-Site"] = fetchSite;
        if (origin != null) headers["Origin"] = origin;
        return headers;
    }

    [Theory]
    // Same origin, with the header
    [InlineData("1", "same-origin", SecurityTestHost.PublicOrigin, true)]
    [InlineData("1", "same-origin", null, true)]
    // No fetch metadata: the Origin decides
    [InlineData("1", null, SecurityTestHost.PublicOrigin, true)]
    [InlineData("1", null, "https://CONTACTS.example.com/", true)]
    [InlineData("1", null, "https://evil.example.com", false)]
    // Neither Sec-Fetch-Site nor Origin
    [InlineData("1", null, null, false)]
    // Missing or wrong header
    [InlineData(null, "same-origin", SecurityTestHost.PublicOrigin, false)]
    [InlineData("0", "same-origin", SecurityTestHost.PublicOrigin, false)]
    // A sibling subdomain (same-site) and another site
    [InlineData("1", "same-site", "https://grafana.example.com", false)]
    [InlineData("1", "cross-site", "https://evil.test", false)]
    [InlineData("1", "none", null, false)]
    // The development exception does not apply in Production
    [InlineData("1", "same-site", "http://localhost:3002", false)]
    public void Production_decisions(string? echo, string? fetchSite, string? origin, bool accepted)
    {
        Assert.Equal(accepted, CsrfProtectionMiddleware.IsAccepted(Headers(echo, fetchSite, origin), Production));
    }

    [Theory]
    [InlineData("1", "same-site", "http://localhost:3002", true)]
    [InlineData("1", "same-site", "http://localhost:4000", false)]
    [InlineData("1", "same-site", null, false)]
    [InlineData(null, "same-site", "http://localhost:3002", false)]
    [InlineData("1", "cross-site", "http://localhost:3002", false)]
    public void Development_accepts_same_site_from_a_cors_origin_only(string? echo, string? fetchSite, string? origin, bool accepted)
    {
        Assert.Equal(accepted, CsrfProtectionMiddleware.IsAccepted(Headers(echo, fetchSite, origin), Development));
    }

    [Theory]
    [InlineData("GET", false)]
    [InlineData("HEAD", false)]
    [InlineData("OPTIONS", false)]
    [InlineData("POST", true)]
    [InlineData("PUT", true)]
    [InlineData("PATCH", true)]
    [InlineData("DELETE", true)]
    public void Only_state_changing_methods_are_checked(string method, bool checkedMethod)
    {
        Assert.Equal(checkedMethod, CsrfProtectionMiddleware.IsStateChanging(method));
    }

    [Fact]
    public async Task A_post_without_the_header_gets_403_REQUEST_REJECTED()
    {
        await using var host = await SecurityTestHost.StartAsync();

        var refused = await host.SendAsync(HttpMethod.Post, "/api/test/change", appHeaders: false);
        var accepted = await host.SendAsync(HttpMethod.Post, "/api/test/change");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("REQUEST_REJECTED", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task A_cross_site_sign_out_is_refused_and_the_user_stays_signed_in()
    {
        await using var host = await SecurityTestHost.StartAsync();
        await host.SignInAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Add("Cookie", host.Cookies.Header());
        request.Headers.Add(CsrfProtectionMiddleware.HeaderName, "1");
        request.Headers.Add("Sec-Fetch-Site", "same-site");
        request.Headers.Add("Origin", "https://sibling.example.com");
        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(host.TicketKeys());
    }

    [Fact]
    public async Task Mcp_is_exempt()
    {
        await using var host = await SecurityTestHost.StartAsync();

        var response = await host.SendAsync(HttpMethod.Post, "/api/mcp", appHeaders: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_get_is_never_checked()
    {
        await using var host = await SecurityTestHost.StartAsync();

        var response = await host.GetAsync("/api/auth/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
