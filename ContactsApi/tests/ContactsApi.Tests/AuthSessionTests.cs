using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContactsApi.WebApi.Middleware;
using ContactsApi.WebApi.Services;

namespace ContactsApi.Tests;

/// <summary>Invite-only sign-in (AD-50 rule 6) and the EchoSession scheme (AD-50 rule 4) end to end</summary>
public class AuthSessionTests
{
    private const string Auth = EchoSessionDefaults.AuthCookie;
    private const string Session = EchoSessionDefaults.SessionCookie;

    private static async Task<bool> SignedInAsync(SecurityTestHost host)
    {
        var status = await host.GetAsync("/api/auth/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("data").GetProperty("isAuthenticated").GetBoolean();
    }

    [Fact]
    public async Task An_invited_account_signs_in_and_gets_a_ticket_under_its_owner_prefix()
    {
        await using var host = await SecurityTestHost.StartAsync();

        var callback = await host.SignInAsync();

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(SecurityTestHost.PublicOrigin + "/", callback.Headers.Location!.ToString());
        Assert.True(await SignedInAsync(host));
        var key = Assert.Single(host.TicketKeys());
        Assert.StartsWith(OwnerKeys.Prefix("owner-1") + "ticket:", key);
        Assert.Contains("refresh-token:owner-1", host.Cache.Keys);
        Assert.Empty(host.Google.Revoked);
    }

    [Theory]
    [InlineData("OWNER@example.COM")]
    [InlineData("  owner@example.com ")]
    public async Task The_allowlist_matches_case_insensitively(string email)
    {
        await using var host = await SecurityTestHost.StartAsync("Owner@Example.com; family@example.com");

        await host.SignInAsync(email: email);

        Assert.True(await SignedInAsync(host));
    }

    [Theory]
    [InlineData("stranger@example.com", true)]
    [InlineData("owner@example.com", false)]
    public async Task A_refused_account_gets_NOT_INVITED_its_grant_revoked_and_nothing_stored(string email, bool verified)
    {
        await using var host = await SecurityTestHost.StartAsync();

        var callback = await host.SignInAsync(subject: "stranger-1", email: email, emailVerified: verified);

        Assert.Equal(HttpStatusCode.Forbidden, callback.StatusCode);
        var page = await callback.Content.ReadAsStringAsync();
        Assert.Contains("NOT_INVITED", page);
        Assert.Contains("Invitation needed", page);
        Assert.Equal(new[] { "refresh-the-code" }, host.Google.Revoked);
        // Only the anonymous pre-sign-in session record: no grant, opt-ins, ticket or owner key
        Assert.DoesNotContain(host.Cache.Keys, k => k.Contains("stranger-1") || k.Contains(":ticket:")
                                                   || k.StartsWith(OwnerKeys.Prefix("stranger-1"), StringComparison.Ordinal));
        Assert.Null(host.Cookies[Auth]);
        Assert.False(await SignedInAsync(host));
    }

    [Fact]
    public async Task With_no_allowlist_nobody_signs_in()
    {
        await using var host = await SecurityTestHost.StartAsync(allowedEmails: null);

        var callback = await host.SignInAsync();

        Assert.Equal(HttpStatusCode.Forbidden, callback.StatusCode);
        Assert.Empty(host.TicketKeys());
    }

    [Fact]
    public async Task The_ticket_key_changes_at_every_sign_in_and_the_old_ticket_is_deleted()
    {
        await using var host = await SecurityTestHost.StartAsync();
        await host.SignInAsync();
        var first = Assert.Single(host.TicketKeys());
        var firstCookie = host.Cookies[Auth]!;

        // A signed-in user starts an opt-in: a new sign-in, a new key, the old ticket gone
        await host.SignInAsync(loginQuery: "?activity=true");

        var second = Assert.Single(host.TicketKeys());
        Assert.NotEqual(first, second);
        Assert.NotEqual(firstCookie, host.Cookies[Auth]);
        Assert.True(await SignedInAsync(host));

        // The first cookie, replayed, is dead
        host.Cookies.Set(Auth, firstCookie);
        Assert.False(await SignedInAsync(host));
    }

    [Fact]
    public async Task The_callback_clears_the_pre_sign_in_session_and_deletes_its_cookie()
    {
        await using var host = await SecurityTestHost.StartAsync();

        await host.SignInAsync();

        Assert.Null(host.Cookies[Session]);
        Assert.Contains("expires=Thu, 01 Jan 1970", host.Cookies.LastSetCookie[Session], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_session_cookie_planted_before_sign_in_carries_no_identity_after_it()
    {
        await using var host = await SecurityTestHost.StartAsync();
        // An attacker's own pre-sign-in session cookie, tossed into the victim's browser
        await host.GetAsync("/api/auth/login");
        var planted = host.Cookies[Session]!;

        await host.SignInAsync();

        // Presenting the planted session cookie alone (no ticket) is not signed in
        host.Cookies.Clear();
        host.Cookies.Set(Session, planted);
        Assert.False(await SignedInAsync(host));
    }

    [Fact]
    public async Task Sign_out_deletes_the_ticket_so_a_replayed_cookie_is_refused()
    {
        await using var host = await SecurityTestHost.StartAsync();
        await host.SignInAsync();
        var cookie = host.Cookies[Auth]!;

        var logout = await host.SendAsync(HttpMethod.Post, "/api/auth/logout");

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Empty(host.TicketKeys());
        Assert.Null(host.Cookies[Auth]);
        host.Cookies.Set(Auth, cookie);
        Assert.False(await SignedInAsync(host));
    }

    [Fact]
    public async Task The_session_slides_for_7_days_but_ends_30_days_after_sign_in()
    {
        await using var host = await SecurityTestHost.StartAsync();
        await host.SignInAsync();

        // Used every 6 days, the sliding window renews: still signed in on day 24
        for (var day = 6; day <= 24; day += 6)
        {
            host.Clock.Advance(TimeSpan.FromDays(6));
            Assert.True(await SignedInAsync(host), $"day {day}");
        }

        host.Clock.Advance(TimeSpan.FromDays(6));

        Assert.False(await SignedInAsync(host));
        Assert.Empty(host.TicketKeys());
    }

    [Fact]
    public async Task An_unused_session_ends_after_7_days()
    {
        await using var host = await SecurityTestHost.StartAsync();
        await host.SignInAsync();

        host.Clock.Advance(TimeSpan.FromDays(7.5));

        Assert.False(await SignedInAsync(host));
    }

    [Fact]
    public async Task Both_cookies_carry_the_Host_prefix_attributes()
    {
        await using var host = await SecurityTestHost.StartAsync(development: true);

        await host.GetAsync("/api/auth/login");
        var session = host.Cookies.LastSetCookie[Session];
        await host.SignInAsync();
        var auth = host.Cookies.LastSetCookie[Auth];

        foreach (var header in new[] { session, auth })
        {
            var lower = header.ToLowerInvariant();
            Assert.StartsWith("__host-contactsapi.", lower);
            Assert.Contains("; secure", lower);
            Assert.Contains("; httponly", lower);
            Assert.Contains("; path=/", lower);
            Assert.Contains("; samesite=lax", lower);
            Assert.DoesNotContain("domain=", lower);
        }
    }

    [Fact]
    public async Task The_google_access_token_is_never_in_the_store_in_clear()
    {
        await using var host = await SecurityTestHost.StartAsync();

        await host.SignInAsync();

        foreach (var key in host.Cache.Keys)
        {
            var text = System.Text.Encoding.UTF8.GetString(host.Cache.Get(key)!);
            Assert.DoesNotContain("access-the-code", text);
            Assert.DoesNotContain("refresh-the-code", text);
        }
    }

    [Fact]
    public async Task Removing_an_account_from_the_allowlist_ends_its_sessions()
    {
        // A ticket of owner@example.com, presented to a backend whose list no longer has it
        await using var restarted = await SecurityTestHost.StartAsync("family@example.com");
        var store = (RedisTicketStore)restarted.Services.GetService(typeof(RedisTicketStore))!;
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim(EchoSessionDefaults.SubClaim, "owner-1"),
            new System.Security.Claims.Claim(EchoSessionDefaults.EmailClaim, "owner@example.com")
        }, EchoSessionDefaults.Scheme));
        var props = new Microsoft.AspNetCore.Authentication.AuthenticationProperties { ExpiresUtc = restarted.Clock.GetUtcNow().AddDays(7) };
        props.Items[EchoSessionDefaults.SignedInItem] = restarted.Clock.GetUtcNow().ToString("o");
        var key = await store.StoreAsync(new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal, props, EchoSessionDefaults.Scheme));

        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = restarted.Services };
        var options = new Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions { CookieManager = new EchoCookieManager() };
        options.Cookie.Name = EchoSessionDefaults.AuthCookie;
        var validate = new Microsoft.AspNetCore.Authentication.Cookies.CookieValidatePrincipalContext(ctx,
            new Microsoft.AspNetCore.Authentication.AuthenticationScheme(EchoSessionDefaults.Scheme, null,
                typeof(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationHandler)),
            options, new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal, props, EchoSessionDefaults.Scheme));

        await EchoSessionDefaults.ValidatePrincipalAsync(validate);

        Assert.Null(validate.Principal);
        Assert.DoesNotContain(key, restarted.TicketKeys());
    }

    [Fact]
    public async Task Declining_an_opt_in_keeps_the_existing_sign_in()
    {
        await using var host = await SecurityTestHost.StartAsync();
        await host.SignInAsync();
        var key = Assert.Single(host.TicketKeys());

        await host.GetAsync("/api/auth/login?activity=true");
        var declined = await host.GetAsync("/api/auth/callback?error=access_denied");

        Assert.Equal(SecurityTestHost.PublicOrigin + "/contacts?connected=activity&declined=1", declined.Headers.Location!.ToString());
        Assert.Equal(key, Assert.Single(host.TicketKeys()));
        Assert.True(await SignedInAsync(host));
    }

    [Fact]
    public async Task The_owner_comes_from_the_ticket_not_from_the_session()
    {
        await using var host = await SecurityTestHost.StartAsync();
        await host.SignInAsync();

        var who = await host.GetAsync("/api/test/whoami");

        Assert.Equal("owner-1", (await who.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("owner").GetString());
    }

    [Fact]
    public async Task A_ticket_store_that_does_not_answer_reports_unavailable_instead_of_signing_out()
    {
        await using var host = await SecurityTestHost.StartAsync();
        await host.SignInAsync();
        var failing = new FailingTicketCache();
        var store = new RedisTicketStore(failing,
            Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create("t"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RedisTicketStore>.Instance);

        await Assert.ThrowsAsync<SessionStoreUnavailableException>(() => store.RetrieveAsync("echo:x:ticket:y"));
    }

    private sealed class FailingTicketCache : Microsoft.Extensions.Caching.Distributed.IDistributedCache
    {
        public byte[]? Get(string key) => throw new TimeoutException();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw new TimeoutException();
        public void Set(string key, byte[] value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options) => throw new TimeoutException();
        public Task SetAsync(string key, byte[] value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options, CancellationToken token = default) => throw new TimeoutException();
        public void Refresh(string key) => throw new TimeoutException();
        public Task RefreshAsync(string key, CancellationToken token = default) => throw new TimeoutException();
        public void Remove(string key) => throw new TimeoutException();
        public Task RemoveAsync(string key, CancellationToken token = default) => throw new TimeoutException();
    }
}

/// <summary>The allowlist's parsing and matching</summary>
public class AllowedEmailsTests
{
    [Fact]
    public void Entries_are_split_on_commas_semicolons_and_spaces_and_a_rendered_vault_list_works()
    {
        var list = new AllowedEmails("[A@x.com b@y.com],c@z.com;  D@W.org ");

        Assert.Equal(4, list.Count);
        Assert.True(list.IsAllowed("a@X.COM", true));
        Assert.True(list.IsAllowed("d@w.org", true));
    }

    [Fact]
    public void An_unverified_email_never_matches()
    {
        Assert.False(new AllowedEmails("a@x.com").IsAllowed("a@x.com", emailVerified: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ;, ")]
    [InlineData("not-an-email")]
    public void An_empty_list_admits_nobody(string? configured)
    {
        var list = new AllowedEmails(configured);

        Assert.False(list.IsConfigured);
        Assert.False(list.IsAllowed("a@x.com", true));
    }

    [Fact]
    public void A_missing_email_never_matches()
    {
        Assert.False(new AllowedEmails("a@x.com").IsAllowed(null, true));
    }
}
