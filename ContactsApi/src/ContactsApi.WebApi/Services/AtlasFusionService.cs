/**
 * @fileoverview The fusion service: notices new data (every import queues the
 * time window it touched) and fuses it with what already exists, in the background
 * @module ContactsApi.WebApi.Services
 */
using System.Collections.Concurrent;
using ContactsApi.Infrastructure.Atlas;

namespace ContactsApi.WebApi.Services;

/// <summary>Fusion progress of an owner, as the app shows it</summary>
public record FusionStatus(int Pending, bool Running, DateTime? LastRunAt, FuseResult? LastResult, string? LastError = null);

/// <summary>Wakes the fusion service (registered as <see cref="IFusionSignal"/>)</summary>
public class FusionSignal : IFusionSignal
{
    private readonly SemaphoreSlim _wake = new(0);

    /// <inheritdoc />
    public void Notify()
    {
        // One pending wake is enough: the service drains everything queued
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    /// <summary>A wake is waiting (new data to fuse)</summary>
    public bool Pending => _wake.CurrentCount > 0;

    /// <summary>Waits for a wake or the timeout</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _wake.WaitAsync(timeout, ct);
}

/// <summary>What the fusion service is doing, per owner</summary>
public class FusionStatusBoard
{
    private readonly ConcurrentDictionary<string, (bool Running, DateTime? LastRunAt, FuseResult? LastResult, string? LastError)> _owners = new();

    /// <summary>Marks a run as started</summary>
    public void Started(string owner) =>
        _owners.AddOrUpdate(owner, _ => (true, null, null, null), (_, s) => (true, s.LastRunAt, s.LastResult, s.LastError));

    /// <summary>Marks a run as finished (with the error when it failed)</summary>
    public void Finished(string owner, FuseResult? result, string? error = null) =>
        _owners.AddOrUpdate(owner, _ => (false, DateTime.UtcNow, result, error),
            (_, s) => (false, DateTime.UtcNow, result ?? s.LastResult, error));

    /// <summary>The owner's status (pending comes from the queue)</summary>
    public FusionStatus For(string owner, int pending) =>
        _owners.TryGetValue(owner, out var s)
            ? new FusionStatus(pending, s.Running, s.LastRunAt, s.LastResult, s.LastError)
            : new FusionStatus(pending, false, null, null);
}

/// <summary>
/// The fusion service. Imports (files, zips, Sports Tracker syncs) queue the time
/// window they touched; within seconds this service fuses each window with the
/// data already there: new records join an existing activity or form a new one,
/// health data enhances activities without heart rate. It also checks the queue
/// every few minutes, so work queued before a restart is not lost.
/// </summary>
public class AtlasFusionWorker : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);
    private readonly IServiceScopeFactory _scopes;
    private readonly FusionSignal _signal;
    private readonly FusionStatusBoard _board;
    private readonly AtlasReadiness _readiness;
    private readonly ILogger<AtlasFusionWorker> _logger;

    /// <summary>Creates the service</summary>
    public AtlasFusionWorker(IServiceScopeFactory scopes, FusionSignal signal, FusionStatusBoard board, AtlasReadiness readiness,
        ILogger<AtlasFusionWorker> logger)
    {
        _scopes = scopes;
        _signal = signal;
        _board = board;
        _readiness = readiness;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_readiness.Ready)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                // Work queued before a restart is done right away; then on each wake
                if (!first)
                {
                    await _signal.WaitAsync(Idle, stoppingToken);
                }

                first = false;

                // Let an import's next batches arrive, then fuse them together
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                await DrainAsync(stoppingToken);
                await AnalyzeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fusion service run failed");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    /// <summary>
    /// Best efforts of activities that have none yet (after a release, an import or a
    /// fusion change), in small batches; new data to fuse goes first
    /// </summary>
    private async Task AnalyzeAsync(CancellationToken ct)
    {
        try
        {
            // Photos waiting for placement (database only, quick)
            while (!ct.IsCancellationRequested && !_signal.Pending)
            {
                using var scope = _scopes.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<AtlasService>().PlacePendingPhotosAsync(500, ct) == 0)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
            }

            Guid? after = null;
            while (!ct.IsCancellationRequested && !_signal.Pending)
            {
                using (var scope = _scopes.CreateScope())
                {
                    after = await scope.ServiceProvider.GetRequiredService<AtlasService>().AnalyzePendingEffortsAsync(20, after, ct);
                }

                if (after == null)
                {
                    break;
                }

                // Leave the Pi room for the app between batches
                await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
            }

            // Then places waiting for an identity (about a second of lookup each)
            while (!ct.IsCancellationRequested && !_signal.Pending)
            {
                using var scope = _scopes.CreateScope();
                var done = await scope.ServiceProvider.GetRequiredService<AtlasService>()
                    .IdentifyPendingPlacesAsync(scope.ServiceProvider.GetRequiredService<GeoLookupClient>(), 10, ct);
                if (done == 0)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Best-effort analysis failed; it is retried on the next run");
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        List<string> owners;
        using (var scope = _scopes.CreateScope())
        {
            owners = await scope.ServiceProvider.GetRequiredService<AtlasService>().OwnersWithQueuedFusionAsync(ct);
        }

        foreach (var owner in owners)
        {
            _board.Started(owner);
            FuseResult? result = null;
            string? error = null;
            try
            {
                using var scope = _scopes.CreateScope();
                result = await scope.ServiceProvider.GetRequiredService<AtlasService>().ProcessFusionQueueAsync(owner, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                error = "The last fusion run failed; it is retried automatically.";
                _logger.LogError(ex, "Fusion failed for an owner; their queue is retried later");
            }
            finally
            {
                _board.Finished(owner, result, error);
            }
        }
    }
}
