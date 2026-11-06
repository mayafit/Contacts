/**
 * @fileoverview The EchoSession scheme (AD-3 rule 3, AD-50 rule 4): cookie authentication
 * with a server-side ticket store in Redis, re-keyed at every sign-in, deleted at sign-out,
 * 7 days sliding and 30 days absolute, under __Host- cookies
 * @module ContactsApi.WebApi.Services
 */
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace ContactsApi.WebApi.Services;

/// <summary>Names and limits of the EchoSession scheme</summary>
public static class EchoSessionDefaults
{
    /// <summary>The scheme</summary>
    public const string Scheme = "EchoSession";

    /// <summary>The authentication cookie (the ticket's key, protected)</summary>
    public const string AuthCookie = "__Host-ContactsApi.Auth";

    /// <summary>The ASP.NET session cookie (non-identity state: the pre-sign-in OAuth flow, the OneDrive state)</summary>
    public const string SessionCookie = "__Host-ContactsApi.Session";

    /// <summary>Claims of the signed-in user</summary>
    public const string SubClaim = "sub";
    public const string EmailClaim = "email";
    public const string NameClaim = "name";
    public const string PictureClaim = "picture";
    /// <summary>The scopes Google granted at this sign-in, space separated</summary>
    public const string ScopesClaim = "echo:scopes";

    /// <summary>Ticket properties</summary>
    public const string TicketKeyItem = "echo.ticket";
    public const string SignedInItem = "echo.signed_in";
    public const string AccessTokenItem = "echo.gat";
    public const string AccessTokenExpiresItem = "echo.gat_exp";

    /// <summary>Data Protection purposes</summary>
    public const string TicketPurpose = "ContactsApi.Session.Ticket.v1";
    public const string AccessTokenPurpose = "ContactsApi.Session.GoogleAccessToken.v1";

    /// <summary>Idle lifetime (renewed while used)</summary>
    public static readonly TimeSpan SlidingLifetime = TimeSpan.FromDays(7);

    /// <summary>Lifetime from sign-in, however active the session</summary>
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// Registers the scheme, the ticket store and the ASP.NET session (under the
    /// <c>__Host-</c> cookie). The scheme is never a default: the <see cref="EchoSessionMiddleware"/>
    /// authenticates /api requests explicitly. The OAuth callback is not authenticated, and
    /// <see cref="BeginNewSession"/> hides the incoming ticket cookie from the handler, so its
    /// <c>SignInAsync</c> always issues a new ticket key (the handler would otherwise renew the
    /// ticket under the key the browser presented).
    /// </summary>
    public static IServiceCollection AddEchoSession(this IServiceCollection services, IConfiguration configuration)
    {
        AppContext.SetSwitch("Microsoft.AspNetCore.Authentication.SuppressAutoDefaultScheme", true);
        services.AddSingleton(AllowedEmails.FromConfiguration(configuration));
        services.AddSingleton<RedisTicketStore>();
        services.AddSingleton<ITicketStore>(sp => sp.GetRequiredService<RedisTicketStore>());
        services.AddAuthentication().AddCookie(Scheme, o =>
        {
            o.Cookie.Name = AuthCookie;
            ApplyHostCookie(o.Cookie);
            o.ExpireTimeSpan = SlidingLifetime;
            o.SlidingExpiration = true;
            o.Events.OnRedirectToLogin = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
            o.Events.OnValidatePrincipal = ValidatePrincipalAsync;
            o.CookieManager = new EchoCookieManager();
        });
        services.AddOptions<CookieAuthenticationOptions>(Scheme)
            .Configure<ITicketStore>((o, store) => o.SessionStore = store);

        services.AddSession(o =>
        {
            o.IdleTimeout = SlidingLifetime;
            o.Cookie.Name = SessionCookie;
            ApplyHostCookie(o.Cookie);
            o.Cookie.IsEssential = true;
        });
        return services;
    }

    /// <summary>
    /// Makes the next <c>SignInAsync</c> of this request ignore the ticket cookie the browser
    /// sent, so it stores a new ticket under a new key (re-keying at sign-in, AD-50 rule 4).
    /// </summary>
    public static void BeginNewSession(HttpContext context) => context.Items[EchoCookieManager.IgnoreIncomingItem] = true;

    /// <summary>
    /// The <c>__Host-</c> attributes: Secure in every environment (browsers drop a
    /// <c>__Host-</c> cookie without it; Chromium and Firefox accept Secure cookies on
    /// http://localhost), HttpOnly, Path=/, no Domain, SameSite=Lax, persistent.
    /// </summary>
    public static void ApplyHostCookie(CookieBuilder cookie)
    {
        cookie.HttpOnly = true;
        cookie.SecurePolicy = CookieSecurePolicy.Always;
        cookie.SameSite = SameSiteMode.Lax;
        cookie.Path = "/";
        cookie.Domain = null;
        cookie.MaxAge = SlidingLifetime;
    }

    /// <summary>
    /// Every request: the ticket is refused (and deleted from the store) once its sign-in is
    /// 30 days old (sliding renewal moves IssuedUtc, so the sign-in time is kept separately),
    /// or when the account is no longer on the allowlist.
    /// </summary>
    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var allowed = context.HttpContext.RequestServices.GetRequiredService<AllowedEmails>();
        var time = context.HttpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        var email = context.Principal?.FindFirst(EmailClaim)?.Value;
        var reason = !IsWithinAbsoluteLifetime(context.Properties, time.GetUtcNow())
            ? "absolute lifetime reached"
            : !allowed.IsAllowed(email, emailVerified: true)
                ? "account not on the allowlist"
                : null;
        if (reason == null)
        {
            return;
        }

        context.HttpContext.RequestServices.GetRequiredService<ILogger<RedisTicketStore>>()
            .LogInformation("Session ended: {Reason}", reason);
        context.RejectPrincipal();
        context.ShouldRenew = false;
        if (context.Properties.Items.TryGetValue(TicketKeyItem, out var key) && key != null)
        {
            await context.HttpContext.RequestServices.GetRequiredService<RedisTicketStore>().RemoveAsync(key);
        }

        context.Options.CookieManager.DeleteCookie(context.HttpContext, context.Options.Cookie.Name!,
            context.Options.Cookie.Build(context.HttpContext));
    }

    /// <summary>Whether the ticket's sign-in is less than 30 days old (a ticket without a sign-in time is not)</summary>
    public static bool IsWithinAbsoluteLifetime(AuthenticationProperties properties, DateTimeOffset now) =>
        properties.Items.TryGetValue(SignedInItem, out var value)
        && DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var signedIn)
        && now - signedIn < AbsoluteLifetime;
}

/// <summary>
/// The default chunking cookie manager, except that after <see cref="EchoSessionDefaults.BeginNewSession"/>
/// the incoming ticket cookie is not seen.
/// </summary>
public class EchoCookieManager : ICookieManager
{
    /// <summary>HttpContext item set by <see cref="EchoSessionDefaults.BeginNewSession"/></summary>
    public const string IgnoreIncomingItem = "echo.new_session";

    private readonly ChunkingCookieManager _inner = new();

    /// <inheritdoc />
    public string? GetRequestCookie(HttpContext context, string key) =>
        context.Items.ContainsKey(IgnoreIncomingItem) ? null : _inner.GetRequestCookie(context, key);

    /// <inheritdoc />
    public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options) =>
        _inner.AppendResponseCookie(context, key, value, options);

    /// <inheritdoc />
    public void DeleteCookie(HttpContext context, string key, CookieOptions options) =>
        _inner.DeleteCookie(context, key, options);
}

/// <summary>Short, non-reversible Owner prefix for Redis keys (AD-28: <c>echo:{OwnerId-hash}:</c>)</summary>
public static class OwnerKeys
{
    /// <summary>The first 16 bytes of SHA-256 of the Owner id, lower-case hex</summary>
    public static string Hash(string ownerId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerId)), 0, 16).ToLowerInvariant();

    /// <summary>The prefix of every key holding this Owner's data</summary>
    public static string Prefix(string ownerId) => $"echo:{Hash(ownerId)}:";
}

/// <summary>The session store (Redis) did not answer; the request gets 503, not a sign-out</summary>
public class SessionStoreUnavailableException : Exception
{
    /// <summary>Creates the exception</summary>
    public SessionStoreUnavailableException(Exception inner) : base("The session store is unavailable", inner)
    {
    }
}

/// <summary>
/// Authentication tickets in Redis (through <see cref="IDistributedCache"/>), under
/// <c>echo:{OwnerId-hash}:ticket:{random}</c>. The ticket bytes are Data-Protection-protected,
/// so a Redis copy alone yields nothing usable once the key ring is wrapped (AD-50 rule 2).
/// Every <see cref="StoreAsync(AuthenticationTicket)"/> makes a new random key: that is the
/// re-keying at sign-in. The key is also written into the ticket's properties so the
/// access-token refresh can update the ticket in place.
/// </summary>
public class RedisTicketStore : ITicketStore
{
    private readonly IDistributedCache _cache;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly ILogger<RedisTicketStore> _logger;

    /// <summary>Creates the store</summary>
    public RedisTicketStore(IDistributedCache cache, IDataProtectionProvider protection, ILogger<RedisTicketStore> logger,
        TimeProvider? time = null)
    {
        _cache = cache;
        _protector = protection.CreateProtector(EchoSessionDefaults.TicketPurpose);
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The key of a new ticket for this Owner</summary>
    public static string NewKey(string ownerId) =>
        OwnerKeys.Prefix(ownerId) + "ticket:" + Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <inheritdoc />
    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var owner = ticket.Principal.FindFirst(EchoSessionDefaults.SubClaim)?.Value
                    ?? throw new InvalidOperationException("A session ticket needs the sub claim");
        var key = NewKey(owner);
        ticket.Properties.Items[EchoSessionDefaults.TicketKeyItem] = key;
        await WriteAsync(key, ticket);
        return key;
    }

    /// <inheritdoc />
    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        ticket.Properties.Items[EchoSessionDefaults.TicketKeyItem] = key;
        return WriteAsync(key, ticket);
    }

    /// <inheritdoc />
    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var bytes = await WithRetryAsync(() => _cache.GetAsync(key));
        if (bytes == null)
        {
            return null;
        }

        try
        {
            var ticket = TicketSerializer.Default.Deserialize(_protector.Unprotect(bytes));
            if (ticket != null)
            {
                // A ticket read under another key (copied in Redis) is not that key's ticket
                if (!ticket.Properties.Items.TryGetValue(EchoSessionDefaults.TicketKeyItem, out var stored) || stored != key)
                {
                    _logger.LogWarning("A session ticket was found under a key that is not its own; refused");
                    return null;
                }
            }

            return ticket;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Protected with a key that was retired: the session has ended
            return null;
        }
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key) => WithRetryAsync(async () =>
    {
        await _cache.RemoveAsync(key);
        return true;
    });

    private Task WriteAsync(string key, AuthenticationTicket ticket)
    {
        var now = _time.GetUtcNow();
        var expires = ticket.Properties.ExpiresUtc ?? now + EchoSessionDefaults.SlidingLifetime;
        if (DateTimeOffset.TryParse((ticket.Properties.Items.TryGetValue(EchoSessionDefaults.SignedInItem, out var at) ? at : null), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var signedIn)
            && signedIn + EchoSessionDefaults.AbsoluteLifetime < expires)
        {
            expires = signedIn + EchoSessionDefaults.AbsoluteLifetime;
        }

        if (expires <= now)
        {
            return RemoveAsync(key);
        }

        var bytes = _protector.Protect(TicketSerializer.Default.Serialize(ticket));
        return WithRetryAsync(async () =>
        {
            await _cache.SetAsync(key, bytes, new DistributedCacheEntryOptions { AbsoluteExpiration = expires });
            return true;
        });
    }

    /// <summary>One retry after 300 ms (a busy Redis on the Pi), then <see cref="SessionStoreUnavailableException"/></summary>
    private static async Task<T> WithRetryAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Task.Delay(300);
            try
            {
                return await action();
            }
            catch (Exception again) when (again is not OperationCanceledException)
            {
                throw new SessionStoreUnavailableException(again);
            }
        }
    }
}

/// <summary>Who is signed in, from the EchoSession principal (never from the request)</summary>
public static class EchoUser
{
    /// <summary>The signed-in Owner's id (Google <c>sub</c>), or null</summary>
    public static string? OwnerId(this HttpContext context) =>
        context.User.Identity is { IsAuthenticated: true, AuthenticationType: EchoSessionDefaults.Scheme }
            ? context.User.FindFirst(EchoSessionDefaults.SubClaim)?.Value
            : null;

    /// <summary>The signed-in user's email, or empty</summary>
    public static string UserEmail(this HttpContext context) =>
        context.OwnerId() == null ? string.Empty : context.User.FindFirst(EchoSessionDefaults.EmailClaim)?.Value ?? string.Empty;

    /// <summary>The scopes Google granted at this sign-in, space separated (empty when signed out)</summary>
    public static string GrantedScopes(this HttpContext context) =>
        context.OwnerId() == null ? string.Empty : context.User.FindFirst(EchoSessionDefaults.ScopesClaim)?.Value ?? string.Empty;
}

/// <summary>
/// Loads the ASP.NET session and authenticates the EchoSession ticket for /api requests,
/// except <c>/api/mcp</c> (bearer tokens only) and the OAuth callback (which must not read
/// the old ticket, so that its sign-in issues a new key). A store that does not answer gives
/// 503 instead of signing the user out.
/// </summary>
public class EchoSessionMiddleware
{
    /// <summary>The Google OAuth callback</summary>
    public const string CallbackPath = "/api/auth/callback";

    private readonly RequestDelegate _next;
    private readonly ILogger<EchoSessionMiddleware> _logger;

    /// <summary>Creates the middleware</summary>
    public EchoSessionMiddleware(RequestDelegate next, ILogger<EchoSessionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>Runs the middleware</summary>
    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        if (!path.StartsWithSegments("/api") || path.StartsWithSegments("/api/mcp"))
        {
            await _next(ctx);
            return;
        }

        // Load the session up front. Lazily, a Redis timeout is swallowed and the session
        // looks empty, so a signed-in user got 401 and was sent to sign in (2026-10-24, a
        // merge on a busy Pi). One retry, then 503: the app keeps the user signed in.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ctx.Session.LoadAsync(ctx.RequestAborted);
                break;
            }
            catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                if (attempt < 2)
                {
                    await Task.Delay(300, ctx.RequestAborted);
                    continue;
                }

                await UnavailableAsync(ctx, ex);
                return;
            }
        }

        if (!path.StartsWithSegments(CallbackPath))
        {
            try
            {
                var result = await ctx.AuthenticateAsync(EchoSessionDefaults.Scheme);
                if (result.Succeeded)
                {
                    ctx.User = result.Principal;
                }
            }
            catch (SessionStoreUnavailableException ex)
            {
                await UnavailableAsync(ctx, ex);
                return;
            }
        }

        await _next(ctx);
    }

    private async Task UnavailableAsync(HttpContext ctx, Exception ex)
    {
        _logger.LogWarning(ex, "Session store unavailable; answering 503 instead of signing the user out");
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Response.Headers.RetryAfter = "2";
        await ctx.Response.WriteAsJsonAsync(new
        {
            success = false,
            error = new { code = "SESSION_STORE_UNAVAILABLE", message = "The server is busy. Try again in a moment.", timestamp = DateTime.UtcNow }
        });
    }
}
