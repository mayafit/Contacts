/**
 * @fileoverview Interactive load (AD-8, AD-33 rule 5): while a map or timeline read is
 * in flight, the route-quality service's batch calls are answered 429
 * @module ContactsApi.WebApi.Services
 */
using System.Text.RegularExpressions;

namespace ContactsApi.WebApi.Services;

/// <summary>
/// Counts the map and timeline reads in flight, over every Owner: the Pi is shared,
/// so one Owner's map read makes every batch call wait.
/// </summary>
public class InteractiveLoad
{
    private int _inFlight;

    /// <summary>Seconds a refused batch call should wait</summary>
    public const int RetryAfterSeconds = 2;

    /// <summary>Reads in flight now</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>A map or timeline read is in flight</summary>
    public bool Busy => InFlight > 0;

    /// <summary>Marks a read as started; dispose to mark it finished</summary>
    public IDisposable Begin()
    {
        Interlocked.Increment(ref _inFlight);
        return new Lease(this);
    }

    private sealed class Lease : IDisposable
    {
        private InteractiveLoad? _load;

        public Lease(InteractiveLoad load) => _load = load;

        public void Dispose()
        {
            var load = Interlocked.Exchange(ref _load, null);
            if (load != null)
            {
                Interlocked.Decrement(ref load._inFlight);
            }
        }
    }
}

/// <summary>Marks the map and timeline reads as interactive load</summary>
public class InteractiveLoadMiddleware
{
    private static readonly Regex ActivityDetail = new(@"^/api/atlas/activities/[0-9a-fA-F-]{36}/?$", RegexOptions.Compiled);
    private readonly RequestDelegate _next;

    /// <summary>Creates the middleware</summary>
    public InteractiveLoadMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>
    /// The reads that count: the map's search, histogram and density, and an Activity's
    /// detail. Route job status polling and everything else never count.
    /// </summary>
    public static bool IsInteractiveRead(string method, string path) =>
        (HttpMethods.IsPost(method) && (path.Equals("/api/atlas/search", StringComparison.OrdinalIgnoreCase)
                                        || path.Equals("/api/atlas/histogram", StringComparison.OrdinalIgnoreCase)
                                        || path.Equals("/api/atlas/density", StringComparison.OrdinalIgnoreCase)))
        || (HttpMethods.IsGet(method) && (path.Equals("/api/atlas/histogram", StringComparison.OrdinalIgnoreCase)
                                          || ActivityDetail.IsMatch(path)));

    /// <summary>Counts the request while it runs, when it is an interactive read</summary>
    public async Task InvokeAsync(HttpContext context, InteractiveLoad load)
    {
        if (!IsInteractiveRead(context.Request.Method, context.Request.Path.Value ?? string.Empty))
        {
            await _next(context);
            return;
        }

        using (load.Begin())
        {
            await _next(context);
        }
    }
}
