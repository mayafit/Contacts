using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace ContactsApi.WebApi.Middleware;

/// <summary>
/// Request logging without secrets or personal data from URLs (AD-50 rule 8): the method,
/// the <b>route template</b> (never the raw path, which can hold an email such as
/// <c>/api/activity/self/{email}</c>), the status and the duration. The query string is never
/// logged, except the values of a short allowlist of harmless keys (paging and counts), so
/// <c>code</c>, <c>state</c>, <c>hint</c>, <c>token</c>, <c>lat</c>, <c>lon</c> and emails never appear.
/// </summary>
public class RequestLoggingMiddleware
{
    /// <summary>Query keys whose values may be logged (paging, counts)</summary>
    public static readonly IReadOnlySet<string> HarmlessQueryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "page", "pageSize", "limit", "offset", "skip", "take", "count", "size"
    };

    /// <summary>What is logged for a request no endpoint matched</summary>
    public const string UnmatchedRoute = "(unmatched)";

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(
        RequestDelegate next,
        ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var requestId = context.TraceIdentifier;

        _logger.LogInformation(
            "HTTP Request: {Method} {Route} {Query} - TraceId: {TraceId}",
            context.Request.Method,
            RouteOf(context),
            SafeQuery(context.Request.Query),
            requestId);

        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();

            var logLevel = context.Response.StatusCode >= 500
                ? LogLevel.Error
                : context.Response.StatusCode >= 400
                    ? LogLevel.Warning
                    : LogLevel.Information;

            _logger.Log(
                logLevel,
                "HTTP Response: {Method} {Route} - Status: {StatusCode} - Duration: {Duration}ms - TraceId: {TraceId}",
                context.Request.Method,
                RouteOf(context),
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                requestId);
        }
    }

    /// <summary>The matched endpoint's route template, or a fixed marker; never the raw path</summary>
    public static string RouteOf(HttpContext context) =>
        context.GetEndpoint() switch
        {
            RouteEndpoint route => "/" + (route.RoutePattern.RawText ?? string.Empty).TrimStart('/'),
            { DisplayName: { } name } when !name.Contains('/') => name,
            _ => context.Request.Path.StartsWithSegments("/health") ? "/health" : UnmatchedRoute
        };

    /// <summary>The allowlisted query values only (empty when there are none)</summary>
    public static string SafeQuery(IQueryCollection query)
    {
        var kept = query
            .Where(q => HarmlessQueryKeys.Contains(q.Key))
            // Numbers only: anything else in a harmless key is not logged either
            .Select(q => $"{q.Key}={string.Join(",", q.Value.Select(v => IsSmallNumber(v) ? v : "…"))}")
            .ToList();
        return kept.Count == 0 ? string.Empty : "?" + string.Join("&", kept);
    }

    private static bool IsSmallNumber(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 9 && value.All(char.IsAsciiDigit);
}
