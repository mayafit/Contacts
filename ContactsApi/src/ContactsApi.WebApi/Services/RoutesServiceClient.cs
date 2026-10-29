/**
 * @fileoverview The backend's client of the route-quality service's /internal/v1
 * (AD-33 rule 4), with Routes__BackendToken. The ownerId it sends always comes from
 * the session or the PAT, never from a request body (AD-33 rule 3).
 * @module ContactsApi.WebApi.Services
 */
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ContactsApi.WebApi.Services;

/// <summary>The route-quality service cannot be reached (ROUTES_UNAVAILABLE, AD-9)</summary>
public class RoutesUnavailableException : Exception
{
    /// <summary>Creates the exception</summary>
    public RoutesUnavailableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>What the service answered: its status code and its JSON envelope, passed through</summary>
public record RoutesServiceAnswer(int Status, string Body);

/// <summary>Calls the route-quality service</summary>
public class RoutesServiceClient : ContactsApi.Core.Interfaces.IRouteChangeSignal
{
    /// <summary>The named HttpClient</summary>
    public const string HttpClientName = "routes-service";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _http;
    private readonly ILogger<RoutesServiceClient> _logger;

    /// <summary>Creates the client</summary>
    public RoutesServiceClient(IHttpClientFactory http, IConfiguration config, ILogger<RoutesServiceClient> logger)
    {
        _http = http;
        _logger = logger;
        Configured = !string.IsNullOrWhiteSpace(config["Routes:ServiceUrl"]) && !string.IsNullOrWhiteSpace(config["Routes:BackendToken"]);
    }

    /// <summary>The service's URL and token are set (routes.enabled)</summary>
    public bool Configured { get; }

    /// <summary>Configures the named HttpClient from Routes:ServiceUrl and Routes:BackendToken</summary>
    public static void Configure(IServiceCollection services, IConfiguration config) =>
        services.AddHttpClient(HttpClientName, c =>
        {
            var url = config["Routes:ServiceUrl"];
            c.BaseAddress = new Uri(string.IsNullOrWhiteSpace(url) ? "http://routes.invalid/" : url.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(10);
            var token = config["Routes:BackendToken"];
            if (!string.IsNullOrWhiteSpace(token))
            {
                c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        });

    /// <summary>Queues assess or improve for the Owner; the answer is the service's 202 {jobId}</summary>
    /// <param name="kind">assess or improve</param>
    /// <param name="scope">{activityIds} or {from, to} or {all: true}</param>
    public Task<RoutesServiceAnswer> QueueAsync(string kind, string owner, object scope, bool interactive, bool force, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"internal/v1/{kind}", new { ownerId = owner, scope, interactive, force }, ct);

    /// <summary>Queues a rebuild of one record (AD-38); the answer is the service's 202 {jobId}</summary>
    /// <param name="method">similar or waypoints</param>
    /// <param name="waypoints">waypoints only: [[lat, lon]…] in the order marked</param>
    public Task<RoutesServiceAnswer> RebuildAsync(string owner, Guid activityId, string method, List<double[]>? waypoints, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "internal/v1/rebuild", new { ownerId = owner, activityId, method, waypoints }, ct);

    /// <summary>Uses a rebuild's suggestion as the record's match; 409 when the record changed or there is none</summary>
    public Task<RoutesServiceAnswer> ApplyAsync(string owner, Guid jobId, Guid activityId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "internal/v1/apply", new { ownerId = owner, jobId, activityId }, ct);

    /// <summary>One of the Owner's jobs (another Owner's answers 404)</summary>
    public Task<RoutesServiceAnswer> JobAsync(string owner, Guid id, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"internal/v1/jobs/{id}?ownerId={Uri.EscapeDataString(owner)}", null, ct);

    /// <summary>The Owner's route work: queues, engine, breaker, ETA</summary>
    public Task<RoutesServiceAnswer> StatusAsync(string owner, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"internal/v1/status?ownerId={Uri.EscapeDataString(owner)}", null, ct);

    /// <summary>
    /// Tells the service the Owner has newly fused data (AD-33 rule 6). Best effort and
    /// never blocks fusion: a missed wake costs at most one poll interval.
    /// </summary>
    public void Wake(string owner)
    {
        if (!Configured)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var answer = await SendAsync(HttpMethod.Post, "internal/v1/wake", new { ownerId = owner }, cts.Token);
                if (answer.Status >= 300)
                {
                    _logger.LogDebug("Route service wake answered {Status}", answer.Status);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Route service wake failed; the service polls anyway");
            }
        });
    }

    /// <inheritdoc />
    public void Changed(string owner) => Wake(owner);

    private async Task<RoutesServiceAnswer> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (!Configured)
        {
            throw new RoutesUnavailableException("The route-quality service is not enabled.");
        }

        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body != null)
            {
                request.Content = JsonContent.Create(body, options: Json);
            }

            using var response = await _http.CreateClient(HttpClientName).SendAsync(request, ct);
            if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new RoutesUnavailableException($"The route-quality service answered {(int)response.StatusCode}.");
            }

            return new RoutesServiceAnswer((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new RoutesUnavailableException("The route-quality service cannot be reached.", ex);
        }
    }
}
