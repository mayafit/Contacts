/**
 * @fileoverview The CSRF stance of AD-50 rule 7: state-changing /api requests need the
 * X-Echo-Request header and same-origin fetch metadata (or, without fetch metadata, the
 * public Origin)
 * @module ContactsApi.WebApi.Middleware
 */
using ContactsApi.WebApi.Models.Responses;

namespace ContactsApi.WebApi.Middleware;

/// <summary>Settings of the CSRF check</summary>
public class CsrfOptions
{
    /// <summary>The public origin (<c>Frontend:BaseUrl</c>, which the chart sets to <c>global.publicUrl</c>)</summary>
    public string? PublicOrigin { get; set; }

    /// <summary>The CORS origins (<c>Cors:AllowedOrigins</c>), accepted as <c>same-site</c> in Development only</summary>
    public IReadOnlyCollection<string> DevelopmentOrigins { get; set; } = Array.Empty<string>();

    /// <summary>Whether the app runs in Development</summary>
    public bool IsDevelopment { get; set; }
}

/// <summary>
/// <c>SameSite=Lax</c> stops cross-site requests but not same-site ones, and every
/// <c>*.mayaffit.com</c> host is same-site. So every <c>POST</c>, <c>PUT</c>, <c>PATCH</c> and
/// <c>DELETE</c> on <c>/api/**</c>, except <c>/api/mcp</c> (bearer token, no cookie), must carry
/// <c>X-Echo-Request: 1</c> <b>and</b> <c>Sec-Fetch-Site: same-origin</c> (or, when the browser sends
/// no fetch metadata, an <c>Origin</c> equal to the public origin). In Development only,
/// <c>same-site</c> from a configured CORS origin is accepted too (the SPA on :3002 calls the API
/// on :5000). Anything else gets 403 <c>REQUEST_REJECTED</c>. The custom header also forces a
/// CORS preflight, which CORS limited to the public origin refuses.
/// </summary>
public class CsrfProtectionMiddleware
{
    /// <summary>The header the shared API client adds to every request</summary>
    public const string HeaderName = "X-Echo-Request";

    private readonly RequestDelegate _next;
    private readonly CsrfOptions _options;
    private readonly ILogger<CsrfProtectionMiddleware> _logger;

    /// <summary>Creates the middleware</summary>
    public CsrfProtectionMiddleware(RequestDelegate next, CsrfOptions options, ILogger<CsrfProtectionMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    /// <summary>Runs the check</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (IsStateChanging(context.Request.Method)
            && context.Request.Path.StartsWithSegments("/api")
            && !context.Request.Path.StartsWithSegments("/api/mcp")
            && !IsAccepted(context.Request.Headers, _options))
        {
            _logger.LogWarning("State-changing request refused by the CSRF check");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(ApiResponse<object>.ErrorResponse(
                "REQUEST_REJECTED", "The request was refused because it did not come from the app."));
            return;
        }

        await _next(context);
    }

    /// <summary>Whether a method changes state (GET, HEAD, OPTIONS and TRACE do not)</summary>
    public static bool IsStateChanging(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    /// <summary>The decision, on the request headers alone</summary>
    public static bool IsAccepted(IHeaderDictionary headers, CsrfOptions options)
    {
        if (headers[HeaderName].ToString() != "1")
        {
            return false;
        }

        var fetchSite = headers["Sec-Fetch-Site"].ToString();
        var origin = headers.Origin.ToString();
        if (fetchSite.Length > 0)
        {
            if (fetchSite == "same-origin")
            {
                return true;
            }

            return options.IsDevelopment && fetchSite == "same-site" && origin.Length > 0
                   && options.DevelopmentOrigins.Any(o => SameOrigin(o, origin));
        }

        // No fetch metadata (an older browser): the Origin must be the public one
        return origin.Length > 0 && options.PublicOrigin != null && SameOrigin(options.PublicOrigin, origin);
    }

    /// <summary>Scheme, host and port equal (case-insensitive; a trailing slash ignored)</summary>
    public static bool SameOrigin(string configured, string origin) =>
        Uri.TryCreate(configured.TrimEnd('/'), UriKind.Absolute, out var a)
        && Uri.TryCreate(origin.TrimEnd('/'), UriKind.Absolute, out var b)
        && string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;
}
