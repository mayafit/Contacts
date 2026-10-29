/**
 * @fileoverview The routing engine (Valhalla, 127.0.0.1:8002 in the same pod): a hard
 * per-call timeout, a bounded number of calls in flight, and failures split into
 * "the engine is unwell" (back off, count toward the breaker) and "this trace cannot
 * be matched" (a final answer for that call)
 * @module EchoRoutes.Worker.Valhalla
 */
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace EchoRoutes.Worker.Valhalla;

/// <summary>
/// The engine timed out, could not be reached or failed (5xx): back off. A timeout or an
/// error is the record's attempt; an engine that cannot be reached at all is not
/// </summary>
public class EngineUnavailableException : Exception
{
    /// <summary>Creates the exception</summary>
    /// <param name="unreachable">No connection at all (refused, reset, DNS): the whole engine is down, not this record</param>
    public EngineUnavailableException(string reason, bool timeout, Exception? inner = null, bool unreachable = false) : base(reason, inner)
    {
        Timeout = timeout;
        Unreachable = unreachable;
    }

    /// <summary>The call hit the hard timeout</summary>
    public bool Timeout { get; }

    /// <summary>The engine could not be reached at all (the pod's engine is down or restarting)</summary>
    public bool Unreachable { get; }
}

/// <summary>The engine answered that it cannot match this trace (4xx, e.g. no road near a point)</summary>
public class EngineRejectedException : Exception
{
    /// <summary>Creates the exception</summary>
    public EngineRejectedException(int? code, string message) : base(message)
    {
        Code = code;
    }

    /// <summary>Valhalla's error_code</summary>
    public int? Code { get; }
}

/// <summary>The routing engine, behind an interface so tests run without it (and OSRM could stand in, AD-32)</summary>
public interface IValhallaClient
{
    /// <summary>Whether the engine answers (GET /status)</summary>
    Task<bool> PingAsync(CancellationToken ct);

    /// <summary>The engine's version, from /status; null when it does not say</summary>
    Task<string?> VersionAsync(CancellationToken ct);

    /// <summary>One trace_attributes call, within the configured timeout and concurrency</summary>
    /// <exception cref="EngineUnavailableException">Timeout, connection failure or 5xx</exception>
    /// <exception cref="EngineRejectedException">The engine cannot match this trace</exception>
    Task<JsonDocument> TraceAttributesAsync(JsonObject request, CancellationToken ct);
}

/// <summary>Valhalla over HTTP</summary>
public class ValhallaClient : IValhallaClient
{
    /// <summary>The named HttpClient (its own timeout is only a backstop)</summary>
    public const string HttpClientName = "valhalla";

    private readonly IHttpClientFactory _http;
    private readonly SemaphoreSlim _inFlight;
    private readonly TimeSpan _timeout;
    private string? _version;

    /// <summary>Creates the client</summary>
    public ValhallaClient(IHttpClientFactory http, IOptions<RoutesOptions> options)
    {
        _http = http;
        _inFlight = new SemaphoreSlim(Math.Max(1, options.Value.EngineConcurrency));
        _timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.EngineTimeoutSeconds));
    }

    /// <inheritdoc />
    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _http.CreateClient(HttpClientName).GetAsync("status", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<string?> VersionAsync(CancellationToken ct)
    {
        if (_version != null)
        {
            return _version;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var status = await _http.CreateClient(HttpClientName).GetFromJsonAsync<JsonElement>("status", cts.Token);
            return _version = status.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<JsonDocument> TraceAttributesAsync(JsonObject request, CancellationToken ct)
    {
        await _inFlight.WaitAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            using var response = await _http.CreateClient(HttpClientName).PostAsJsonAsync("trace_attributes", request, cts.Token);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new EngineUnavailableException($"the engine answered {(int)response.StatusCode}", false);
            }

            if (!response.IsSuccessStatusCode)
            {
                var (code, message) = await ErrorOf(response, cts.Token);
                throw new EngineRejectedException(code, message);
            }

            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cts.Token), cancellationToken: cts.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new EngineUnavailableException($"the engine did not answer within {_timeout.TotalSeconds:0} s", true, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new EngineUnavailableException("the engine cannot be reached: " + ex.Message, false, ex, unreachable: true);
        }
        catch (JsonException ex)
        {
            throw new EngineUnavailableException("the engine's answer is not JSON", false, ex);
        }
        finally
        {
            _inFlight.Release();
        }
    }

    private static async Task<(int? Code, string Message)> ErrorOf(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var code = body.TryGetProperty("error_code", out var c) && c.TryGetInt32(out var n) ? n : (int?)null;
            var message = body.TryGetProperty("error", out var e) ? e.GetString() : null;
            return (code, $"engine {(int)response.StatusCode}{(code != null ? $" ({code})" : string.Empty)}: {message ?? "no match"}");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return (null, $"engine {(int)response.StatusCode}");
        }
    }
}

/// <summary>
/// The circuit breaker: after N consecutive engine failures (errors or timeouts) all
/// matching pauses for M minutes, so a dying engine does not burn the queue. After the
/// pause one call is let through; a success closes it, a failure opens it again.
/// </summary>
public class EngineBreaker
{
    private readonly object _lock = new();
    private readonly TimeProvider _clock;
    private readonly int _threshold;
    private readonly TimeSpan _pause;
    private double _avgMs;

    /// <summary>Creates the breaker</summary>
    public EngineBreaker(IOptions<RoutesOptions> options, TimeProvider clock)
    {
        _clock = clock;
        _threshold = Math.Max(1, options.Value.BreakerFailures);
        _pause = TimeSpan.FromMinutes(Math.Max(0, options.Value.BreakerPauseMinutes));
    }

    /// <summary>Failures in a row</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>Matching is paused until then (UTC); null when closed</summary>
    public DateTime? OpenUntil { get; private set; }

    /// <summary>The last failure's reason</summary>
    public string? LastFailure { get; private set; }

    /// <summary>When it failed last (UTC)</summary>
    public DateTime? LastFailureAt { get; private set; }

    /// <summary>Calls that answered, and calls that failed, since start</summary>
    public long Successes { get; private set; }

    /// <summary>Failed calls since start</summary>
    public long Failures { get; private set; }

    /// <summary>Times the breaker opened since start</summary>
    public int Trips { get; private set; }

    /// <summary>Average wall time per matched record, in ms (moving), for the ETA</summary>
    public double AvgRecordMs
    {
        get
        {
            lock (_lock)
            {
                return _avgMs;
            }
        }
    }

    /// <summary>Whether matching is paused now</summary>
    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                return OpenUntil.HasValue && OpenUntil.Value > _clock.GetUtcNow().UtcDateTime;
            }
        }
    }

    /// <summary>The engine answered (with a match or a refusal: it is alive)</summary>
    public void Success()
    {
        lock (_lock)
        {
            ConsecutiveFailures = 0;
            OpenUntil = null;
            Successes++;
        }
    }

    /// <summary>
    /// The engine failed or timed out. Returns whether the breaker is open now: this
    /// failure opened it, or it was the one call let through after a pause (the probe)
    /// and the engine is still unwell. Such a failure is the engine's, not the record's.
    /// </summary>
    public bool Failure(string reason)
    {
        lock (_lock)
        {
            ConsecutiveFailures++;
            Failures++;
            LastFailure = reason;
            LastFailureAt = _clock.GetUtcNow().UtcDateTime;
            if (ConsecutiveFailures >= _threshold)
            {
                OpenUntil = LastFailureAt + _pause;
                Trips++;
                return true;
            }

            return false;
        }
    }

    /// <summary>Records how long one record took</summary>
    public void RecordTime(TimeSpan time)
    {
        lock (_lock)
        {
            _avgMs = _avgMs <= 0 ? time.TotalMilliseconds : 0.9 * _avgMs + 0.1 * time.TotalMilliseconds;
        }
    }
}
