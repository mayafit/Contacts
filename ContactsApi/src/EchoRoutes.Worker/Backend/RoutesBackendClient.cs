/**
 * @fileoverview The service's only way to Atlas data: the backend's /internal/routes/*
 * (AD-33 rule 5), with Routes__ServiceToken
 * @module EchoRoutes.Worker.Backend
 */
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ContactsApi.Infrastructure.Routes;

namespace EchoRoutes.Worker.Backend;

/// <summary>The backend answered 429: interactive reads are in flight, wait and try again</summary>
public class BackendBusyException : Exception
{
    /// <summary>Creates the exception</summary>
    public BackendBusyException(TimeSpan retryAfter) : base("The backend is busy with interactive reads.")
    {
        RetryAfter = retryAfter;
    }

    /// <summary>How long to wait</summary>
    public TimeSpan RetryAfter { get; }
}

/// <summary>The backend answered 409 DERIVATION_NOT_READY: the Owner's fusion is still running</summary>
public class DerivationPendingException : Exception
{
    /// <summary>Creates the exception</summary>
    public DerivationPendingException() : base("Fusion is not settled for this owner yet.")
    {
    }
}

/// <summary>The backend's internal API, as the service uses it</summary>
public interface IRoutesBackend
{
    /// <summary>Owners whose routes the service keeps</summary>
    Task<List<string>> OwnersAsync(CancellationToken ct);

    /// <summary>A page of the Owner's change feed after the cursor</summary>
    Task<RouteChangesPage> ChangesAsync(string owner, string? after, int limit, bool interactive, CancellationToken ct);

    /// <summary>Tracks of at most 25 of the Owner's records</summary>
    Task<List<RouteTrack>> TracksAsync(string owner, IReadOnlyCollection<Guid> ids, bool interactive, CancellationToken ct);

    /// <summary>A page of the ids of the Owner's records that start in [from, to)</summary>
    Task<RouteIdsPage> RangeAsync(string owner, DateTime from, DateTime to, string? after, int limit, bool interactive, CancellationToken ct);

    /// <summary>Which of the Owner's records still exist</summary>
    Task<List<Guid>> ExistsAsync(string owner, IReadOnlyCollection<Guid> ids, CancellationToken ct);
}

/// <summary>The backend over HTTP</summary>
public class RoutesBackendClient : IRoutesBackend
{
    /// <summary>The named HttpClient</summary>
    public const string HttpClientName = "backend";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _http;

    /// <summary>Creates the client</summary>
    public RoutesBackendClient(IHttpClientFactory http)
    {
        _http = http;
    }

    /// <inheritdoc />
    public Task<List<string>> OwnersAsync(CancellationToken ct) =>
        SendAsync<List<string>>(new HttpRequestMessage(HttpMethod.Get, "internal/routes/owners"), false, ct);

    /// <inheritdoc />
    public Task<RouteChangesPage> ChangesAsync(string owner, string? after, int limit, bool interactive, CancellationToken ct)
    {
        var url = $"internal/routes/changes?ownerId={Uri.EscapeDataString(owner)}&limit={limit}"
                  + (after != null ? $"&after={Uri.EscapeDataString(after)}" : string.Empty);
        return SendAsync<RouteChangesPage>(new HttpRequestMessage(HttpMethod.Get, url), interactive, ct);
    }

    /// <inheritdoc />
    public Task<List<RouteTrack>> TracksAsync(string owner, IReadOnlyCollection<Guid> ids, bool interactive, CancellationToken ct)
    {
        var url = $"internal/routes/tracks?ownerId={Uri.EscapeDataString(owner)}" + string.Concat(ids.Select(id => $"&ids={id}"));
        return SendAsync<List<RouteTrack>>(new HttpRequestMessage(HttpMethod.Get, url), interactive, ct);
    }

    /// <inheritdoc />
    public Task<RouteIdsPage> RangeAsync(string owner, DateTime from, DateTime to, string? after, int limit, bool interactive, CancellationToken ct)
    {
        var url = $"internal/routes/range?ownerId={Uri.EscapeDataString(owner)}&from={Uri.EscapeDataString(from.ToUniversalTime().ToString("O"))}"
                  + $"&to={Uri.EscapeDataString(to.ToUniversalTime().ToString("O"))}&limit={limit}"
                  + (after != null ? $"&after={Uri.EscapeDataString(after)}" : string.Empty);
        return SendAsync<RouteIdsPage>(new HttpRequestMessage(HttpMethod.Get, url), interactive, ct);
    }

    /// <inheritdoc />
    public Task<List<Guid>> ExistsAsync(string owner, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        SendAsync<List<Guid>>(new HttpRequestMessage(HttpMethod.Post, "internal/routes/exists")
        {
            Content = JsonContent.Create(new { ownerId = owner, ids }, options: Json)
        }, false, ct);

    private async Task<T> SendAsync<T>(HttpRequestMessage request, bool interactive, CancellationToken ct)
    {
        using (request)
        {
            if (interactive)
            {
                request.Headers.Add(EchoPriority.Header, EchoPriority.Interactive);
            }

            using var response = await _http.CreateClient(HttpClientName).SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new BackendBusyException(RetryAfter(response.Headers.RetryAfter));
            }

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                throw new DerivationPendingException();
            }

            response.EnsureSuccessStatusCode();
            var envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(Json, ct);
            return envelope is { Success: true, Data: not null }
                ? envelope.Data
                : throw new HttpRequestException($"The backend answered without data ({envelope?.Error?.Code}).");
        }
    }

    /// <summary>Retry-After in seconds or as a date; 2 s when absent</summary>
    internal static TimeSpan RetryAfter(RetryConditionHeaderValue? header)
    {
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(2);
    }

    private sealed record Envelope<T>(bool Success, T? Data, EnvelopeError? Error);

    private sealed record EnvelopeError(string Code, string Message);
}
