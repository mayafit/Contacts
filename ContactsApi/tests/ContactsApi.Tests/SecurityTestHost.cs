using System.Collections.Concurrent;
using System.Net;
using ContactsApi.WebApi.Configuration;
using ContactsApi.WebApi.Controllers;
using ContactsApi.WebApi.Middleware;
using ContactsApi.WebApi.Services;
using Google.Apis.Auth;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContactsApi.Tests;

/// <summary>
/// The backend's security pipeline (CSRF, session, EchoSession, request logging) and the real
/// AuthController on a TestServer, with Google, Redis and the clock replaced.
/// </summary>
public sealed class SecurityTestHost : IAsyncDisposable
{
    public const string PublicOrigin = "https://contacts.example.com";
    public const string ContactsScope = "https://www.googleapis.com/auth/contacts";

    private readonly WebApplication _app;

    public FakeGoogle Google { get; } = new();
    public TestCache Cache { get; } = new();
    public TestClock Clock { get; } = new(DateTimeOffset.UtcNow);
    public CapturingLoggerProvider Logs { get; } = new();
    public CookieJar Cookies { get; } = new();
    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _app.Services;

    private SecurityTestHost(string? allowedEmails, bool development)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = development ? Environments.Development : Environments.Production
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AllowedEmails.ConfigKey] = allowedEmails,
            ["Frontend:BaseUrl"] = PublicOrigin
        });
        // The levels of appsettings.json (Serilog overrides Microsoft.AspNetCore to Warning)
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddProvider(Logs);

        var services = builder.Services;
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<IDistributedCache>(Cache);
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.Configure<GoogleOAuthOptions>(o =>
        {
            o.ClientId = "client-id";
            o.ClientSecret = "client-secret";
            o.RedirectUri = PublicOrigin + "/api/auth/callback";
            o.Scopes = new[] { "openid", "profile", "email", ContactsScope };
        });
        services.AddSingleton<IGoogleSignIn>(Google);
        services.AddSingleton<GoogleTokenService>();
        services.AddSingleton<ScopePreferences>();
        services.AddEchoSession(builder.Configuration);
        services.AddSingleton(new CsrfOptions
        {
            PublicOrigin = PublicOrigin,
            DevelopmentOrigins = new[] { "http://localhost:3002" },
            IsDevelopment = development
        });
        services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);

        _app = builder.Build();
        _app.UseMiddleware<RequestLoggingMiddleware>();
        _app.UseMiddleware<CsrfProtectionMiddleware>();
        _app.UseSession();
        _app.UseMiddleware<EchoSessionMiddleware>();
        _app.MapControllers();
        _app.MapGet("/api/test/whoami", (HttpContext ctx) => Results.Ok(new { owner = ctx.OwnerId() }));
        _app.MapGet("/api/test/self/{email}", (string email) => Results.Ok());
        _app.MapPost("/api/test/change", () => Results.Ok(new { changed = true }));
        _app.MapPost("/api/mcp", () => Results.Ok(new { mcp = true }));
    }

    public static async Task<SecurityTestHost> StartAsync(string? allowedEmails = "owner@example.com", bool development = false)
    {
        var host = new SecurityTestHost(allowedEmails, development);
        await host._app.StartAsync();
        host.Client = host._app.GetTestServer().CreateClient();
        return host;
    }

    /// <summary>A browser request: the jar's cookies, and the app's own headers on state-changing calls</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, bool appHeaders = true)
    {
        using var request = new HttpRequestMessage(method, url);
        var cookie = Cookies.Header();
        if (cookie.Length > 0)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (appHeaders)
        {
            request.Headers.Add(CsrfProtectionMiddleware.HeaderName, "1");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            request.Headers.Add("Origin", PublicOrigin);
        }

        var response = await Client.SendAsync(request);
        Cookies.Take(response);
        return response;
    }

    public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(HttpMethod.Get, url, appHeaders: false);

    /// <summary>The whole Google sign-in: login, the state from Google's URL, the callback</summary>
    public async Task<HttpResponseMessage> SignInAsync(string subject = "owner-1", string email = "Owner@Example.com",
        bool emailVerified = true, string loginQuery = "")
    {
        Google.NextPayload = new GoogleJsonWebSignature.Payload { Subject = subject, Email = email, EmailVerified = emailVerified, Name = "Owner" };
        var login = await GetAsync("/api/auth/login" + loginQuery);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var state = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(login.Headers.Location!.Query)["state"].ToString();
        return await GetAsync($"/api/auth/callback?code=the-code&state={Uri.EscapeDataString(state)}");
    }

    /// <summary>The ticket keys in the store</summary>
    public IReadOnlyList<string> TicketKeys() => Cache.Keys.Where(k => k.Contains(":ticket:", StringComparison.Ordinal)).ToList();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}

/// <summary>Google, replaced: a fixed token response, the next ID-token payload, and the revocations</summary>
public sealed class FakeGoogle : IGoogleSignIn
{
    public GoogleJsonWebSignature.Payload NextPayload { get; set; } = new();
    public List<string> Revoked { get; } = new();
    public string Scope { get; set; } = "openid profile email " + SecurityTestHost.ContactsScope;

    public Task<TokenResponse> ExchangeCodeAsync(string code, CancellationToken ct) => Task.FromResult(new TokenResponse
    {
        AccessToken = "access-" + code, RefreshToken = "refresh-" + code, IdToken = "id-token", Scope = Scope, ExpiresInSeconds = 3600
    });

    public Task<GoogleJsonWebSignature.Payload> ValidateIdTokenAsync(string idToken) => Task.FromResult(NextPayload);

    public Task<(string AccessToken, DateTime ExpiresAt)?> RefreshAsync(string refreshToken) =>
        Task.FromResult<(string, DateTime)?>(null);

    public Task RevokeAsync(string token, CancellationToken ct)
    {
        Revoked.Add(token);
        return Task.CompletedTask;
    }
}

/// <summary>An IDistributedCache whose keys can be listed</summary>
public sealed class TestCache : IDistributedCache
{
    private readonly ConcurrentDictionary<string, byte[]> _items = new();

    public IEnumerable<string> Keys => _items.Keys;

    public byte[]? Get(string key) => _items.TryGetValue(key, out var v) ? v : null;
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _items[key] = value;

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        Set(key, value, options);
        return Task.CompletedTask;
    }

    public void Refresh(string key)
    {
    }

    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    public void Remove(string key) => _items.TryRemove(key, out _);

    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        Remove(key);
        return Task.CompletedTask;
    }
}

/// <summary>A clock the test moves</summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;

    public TestClock(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Cookies as a browser keeps them (by name; a deletion removes)</summary>
public sealed class CookieJar
{
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    /// <summary>Every Set-Cookie header seen, by cookie name (the last one)</summary>
    public Dictionary<string, string> LastSetCookie { get; } = new(StringComparer.Ordinal);

    public string? this[string name] => _cookies.TryGetValue(name, out var v) ? v : null;

    public void Take(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers))
        {
            return;
        }

        foreach (var header in headers)
        {
            var first = header.Split(';')[0];
            var eq = first.IndexOf('=');
            var name = first[..eq].Trim();
            var value = first[(eq + 1)..].Trim();
            LastSetCookie[name] = header;
            if (value.Length == 0 || header.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
            {
                _cookies.Remove(name);
            }
            else
            {
                _cookies[name] = value;
            }
        }
    }

    public void Set(string name, string value) => _cookies[name] = value;

    public void Clear() => _cookies.Clear();

    public string Header() => string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}"));
}

/// <summary>Captures every log event: its formatted message, its properties and its exception</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Events { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger : ILogger
    {
        private readonly CapturingLoggerProvider _owner;
        private readonly string _category;

        public Logger(CapturingLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;
            _owner.Events.Enqueue($"{_category} {logLevel}: {formatter(state, exception)} | {properties} | {exception}");
        }
    }
}
