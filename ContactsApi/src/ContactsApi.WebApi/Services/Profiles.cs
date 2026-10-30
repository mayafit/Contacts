/**
 * @fileoverview Public profiles by email hash (AD-46 rule 3): each Owner's store in Redis,
 * encrypted; the queue of Owners with hashes to look up; the worker that asks Gravatar,
 * one request at a time for the whole server, within its hourly limit
 * @module ContactsApi.WebApi.Services
 */
using System.Collections.Concurrent;
using System.Text.Json;
using ContactsApi.Infrastructure.Activity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace ContactsApi.WebApi.Services;

/// <summary>Gravatar settings (Gravatar__ApiKey comes from Vault; optional)</summary>
public class GravatarOptions
{
    /// <summary>API key: 1,000 look-ups an hour instead of 100</summary>
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>One hash looked up</summary>
public class ProfileEntry
{
    /// <summary>When Gravatar answered</summary>
    public DateTime CheckedAt { get; set; }
    /// <summary>A profile exists</summary>
    public bool Found { get; set; }
    /// <summary>The profile, when found</summary>
    public GravatarProfile? Profile { get; set; }
}

/// <summary>An Owner's profiles document</summary>
public class ProfileDoc
{
    /// <summary>The Owner turned the look-up on</summary>
    public bool Enabled { get; set; }
    /// <summary>Hashes to look up, oldest first</summary>
    public List<string> Queue { get; set; } = new();
    /// <summary>Hashes looked up</summary>
    public Dictionary<string, ProfileEntry> Entries { get; set; } = new();
    /// <summary>Failed look-ups per queued hash: at the third, it counts as no profile</summary>
    public Dictionary<string, int> Failures { get; set; } = new();
}

/// <summary>Each Owner's profiles, encrypted in Redis (AD-11)</summary>
public class ProfileStore
{
    /// <summary>A hash is looked up again after this long</summary>
    public static readonly TimeSpan Recheck = TimeSpan.FromDays(30);

    /// <summary>Most hashes accepted at once</summary>
    public const int MaxHashes = 20000;

    /// <summary>Failed look-ups of one hash before it counts as no profile</summary>
    public const int MaxFailures = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDistributedCache _cache;
    private readonly IDataProtector _protector;
    private readonly ILogger<ProfileStore> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>Creates the store</summary>
    public ProfileStore(IDistributedCache cache, IDataProtectionProvider dataProtection, ILogger<ProfileStore> logger)
    {
        _cache = cache;
        _protector = dataProtection.CreateProtector("Echo.Contacts.Profiles.v1");
        _logger = logger;
    }

    /// <summary>The Owner's document, or an empty one</summary>
    public async Task<ProfileDoc> GetAsync(string owner)
    {
        var stored = await _cache.GetStringAsync(Key(owner));
        if (stored == null)
        {
            return new ProfileDoc();
        }

        try
        {
            return JsonSerializer.Deserialize<ProfileDoc>(_protector.Unprotect(stored), Json) ?? new ProfileDoc();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Profiles could not be read; starting over");
            return new ProfileDoc();
        }
    }

    /// <summary>
    /// Changes the Owner's document under a lock (the API and the worker both write it)
    /// </summary>
    /// <param name="owner">Google account id</param>
    /// <param name="change">The change; false leaves the document unsaved</param>
    /// <returns>The document after the change</returns>
    public async Task<ProfileDoc> UpdateAsync(string owner, Func<ProfileDoc, bool> change)
    {
        var gate = _locks.GetOrAdd(owner, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var doc = await GetAsync(owner);
            if (change(doc))
            {
                await _cache.SetStringAsync(Key(owner), _protector.Protect(JsonSerializer.Serialize(doc, Json)));
            }

            return doc;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Deletes everything of the Owner's (turn off and forget)</summary>
    public async Task RemoveAsync(string owner)
    {
        var gate = _locks.GetOrAdd(owner, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await _cache.RemoveAsync(Key(owner));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Queues the hashes not looked up in the last 30 days and not queued (rule 3)
    /// </summary>
    /// <param name="doc">The document</param>
    /// <param name="hashes">Valid hashes</param>
    /// <param name="now">Now</param>
    /// <returns>How many were queued</returns>
    public static int Enqueue(ProfileDoc doc, IEnumerable<string> hashes, DateTime now)
    {
        var queued = doc.Queue.ToHashSet();
        var added = 0;
        foreach (var h in hashes.Distinct())
        {
            if (queued.Contains(h)
                || (doc.Entries.TryGetValue(h, out var e) && now - e.CheckedAt < Recheck))
            {
                continue;
            }

            doc.Queue.Add(h);
            queued.Add(h);
            added++;
        }

        return added;
    }

    /// <summary>
    /// Drops what is not in the contacts any more (rule 3): entries, queued hashes and
    /// failures of addresses no contact has, given the complete list of the Owner's hashes
    /// </summary>
    /// <param name="doc">The document</param>
    /// <param name="all">Every hash of the Owner's contacts</param>
    /// <returns>Whether anything was dropped</returns>
    public static bool Prune(ProfileDoc doc, IReadOnlySet<string> all)
    {
        var before = doc.Entries.Count + doc.Queue.Count + doc.Failures.Count;
        foreach (var h in doc.Entries.Keys.Where(h => !all.Contains(h)).ToList())
        {
            doc.Entries.Remove(h);
        }

        doc.Queue.RemoveAll(h => !all.Contains(h));
        foreach (var h in doc.Failures.Keys.Where(h => !all.Contains(h)).ToList())
        {
            doc.Failures.Remove(h);
        }

        return doc.Entries.Count + doc.Queue.Count + doc.Failures.Count != before;
    }

    private static string Key(string owner) => $"profiles:{owner}";
}

/// <summary>The Owners with hashes to look up, and the worker's state</summary>
public class ProfileQueue
{
    private readonly ConcurrentDictionary<string, byte> _owners = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    /// <summary>Gravatar's allowance is used up until then</summary>
    public DateTime? LimitedUntil { get; set; }

    /// <summary>The Owner has hashes queued</summary>
    public void Add(string owner)
    {
        if (_owners.TryAdd(owner, 0))
        {
            _signal.Release();
        }
    }

    /// <summary>The Owner has none left (or turned it off)</summary>
    public void Remove(string owner) => _owners.TryRemove(owner, out _);

    /// <summary>The Owners in turn</summary>
    public IReadOnlyList<string> Owners => _owners.Keys.OrderBy(o => o, StringComparer.Ordinal).ToList();

    /// <summary>Waits for an Owner, at most the timeout</summary>
    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>
/// Looks up the queued hashes: Owners in turn, one hash each, one request at a time, at
/// least 2 s apart; sleeps until Gravatar's reset when the hour's allowance is used up
/// </summary>
public class ProfileWorker : BackgroundService
{
    /// <summary>Least time between two requests</summary>
    public static readonly TimeSpan Spacing = TimeSpan.FromSeconds(2);

    /// <summary>Wait after a failed request</summary>
    public static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(5);

    private readonly ProfileStore _store;
    private readonly ProfileQueue _queue;
    private readonly IHttpClientFactory _http;
    private readonly GravatarOptions _options;
    private readonly ILogger<ProfileWorker> _logger;

    /// <summary>Creates the worker</summary>
    public ProfileWorker(ProfileStore store, ProfileQueue queue, IHttpClientFactory http,
        IOptions<GravatarOptions> options, ILogger<ProfileWorker> logger)
    {
        _store = store;
        _queue = queue;
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var owners = _queue.Owners;
                if (owners.Count == 0)
                {
                    await _queue.WaitAsync(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                foreach (var owner in owners)
                {
                    await LookUpNextAsync(owner, stoppingToken);
                    await Task.Delay(Spacing, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Profile look-up round failed");
                await Task.Delay(AfterFailure, stoppingToken);
            }
        }
    }

    /// <summary>One hash of one Owner: looked up, then taken off the queue with its answer</summary>
    private async Task LookUpNextAsync(string owner, CancellationToken ct)
    {
        // The wait first, the read after it: a hash forgotten meanwhile never leaves (P-6)
        if (_queue.LimitedUntil is { } until && until > DateTime.UtcNow)
        {
            await Task.Delay(until - DateTime.UtcNow, ct);
        }

        var doc = await _store.GetAsync(owner);
        if (!doc.Enabled || doc.Queue.Count == 0)
        {
            _queue.Remove(owner);
            return;
        }

        var hash = doc.Queue[0];
        var client = new GravatarClient(_http.CreateClient(), _options.ApiKey);
        var answer = await client.LookUpAsync(hash, ct);
        // Used up: the next request waits until the reset (an hour when Gravatar does not say,
        // a minute at least when the reset it gives is already past)
        var now = DateTime.UtcNow;
        _queue.LimitedUntil = answer.Outcome == GravatarOutcome.Limited || answer.Remaining == 0
            ? new[] { answer.ResetAt ?? now.AddHours(1), now.AddMinutes(1) }.Max()
            : null;
        if (answer.Outcome == GravatarOutcome.Limited)
        {
            return;
        }

        if (answer.Outcome == GravatarOutcome.Failed)
        {
            await _store.UpdateAsync(owner, d =>
            {
                if (!d.Queue.Remove(hash))
                {
                    return false;
                }

                // The third failure counts as no profile; before it, to the end of the queue
                var failures = d.Failures.GetValueOrDefault(hash) + 1;
                if (failures >= ProfileStore.MaxFailures)
                {
                    d.Failures.Remove(hash);
                    d.Entries[hash] = new ProfileEntry { CheckedAt = DateTime.UtcNow, Found = false };
                }
                else
                {
                    d.Failures[hash] = failures;
                    d.Queue.Add(hash);
                }

                return true;
            });
            await Task.Delay(AfterFailure, ct);
            return;
        }

        var left = await _store.UpdateAsync(owner, d =>
        {
            if (!d.Enabled)
            {
                return false;
            }

            d.Queue.Remove(hash);
            d.Failures.Remove(hash);
            d.Entries[hash] = new ProfileEntry
            {
                CheckedAt = DateTime.UtcNow,
                Found = answer.Outcome == GravatarOutcome.Found,
                Profile = answer.Profile
            };
            return true;
        });
        if (left.Queue.Count == 0)
        {
            _queue.Remove(owner);
        }
    }
}
