/**
 * @fileoverview Reads the Owner's Google "Other contacts" (the people Gmail saves by
 * itself) for the suggestions of FR-92, without the automated senders (AD-48)
 * @module ContactsApi.Infrastructure.Google
 */
using System.Text.RegularExpressions;
using ContactsApi.Infrastructure.Activity;
using Google.Apis.PeopleService.v1;
using Google.Apis.PeopleService.v1.Data;
using Google.Apis.Services;

namespace ContactsApi.Infrastructure.Google;

/// <summary>One Other contact as the suggestions use it</summary>
public class OtherContactEntry
{
    /// <summary>Google's resource name (otherContacts/…)</summary>
    public string ResourceName { get; set; } = string.Empty;

    /// <summary>The display name, or null when Gmail stored an address as the name</summary>
    public string? Name { get; set; }

    /// <summary>Plain addresses, lower case, without automated ones</summary>
    public List<string> Emails { get; set; } = new();

    /// <summary>Phones as Google gives them</summary>
    public List<string> Phones { get; set; } = new();
}

/// <summary>What one read found</summary>
public class OtherContactsRead
{
    /// <summary>Entries with at least one address left</summary>
    public List<OtherContactEntry> Entries { get; set; } = new();

    /// <summary>Entries read from Google</summary>
    public int Read { get; set; }

    /// <summary>Entries dropped because all their addresses are automated</summary>
    public int Automated { get; set; }

    /// <summary>The read stopped at <see cref="OtherContactsReader.MaxEntries"/></summary>
    public bool Capped { get; set; }
}

/// <summary>
/// Lists Other contacts (needs <c>contacts.other.readonly</c>). Only names,
/// addresses and phones are asked for; nothing is stored (AD-48 rule 2).
/// </summary>
public class OtherContactsReader
{
    /// <summary>Entries read at most</summary>
    public const int MaxEntries = 20_000;

    private static readonly Regex Plain = new(
        @"^[a-z0-9._%+'\-]+@[a-z0-9\-]+(\.[a-z0-9\-]+)+$", RegexOptions.Compiled);

    private static readonly Regex LongDigits = new(@"\d{8,}", RegexOptions.Compiled);

    /// <summary>Local parts of role mailboxes (AD-48 rule 3)</summary>
    private static readonly HashSet<string> RoleNames = new(StringComparer.Ordinal)
    {
        "noreply", "no-reply", "no_reply", "donotreply", "do-not-reply", "mailer-daemon", "postmaster",
        "bounce", "bounces", "notification", "notifications", "notify", "alert", "alerts",
        "newsletter", "newsletters", "news", "info", "support", "service", "billing", "invoice",
        "invoices", "receipt", "receipts", "order", "orders", "marketing", "updates", "calendar-notification"
    };

    /// <summary>Relay domains (AD-48 rule 3)</summary>
    private static readonly HashSet<string> RelayDomains = new(StringComparer.Ordinal)
    {
        "guest.booking.com", "facebookmail.com", "reply.github.com", "reply.linkedin.com", "docs.google.com",
        "calendar.google.com", "resource.calendar.google.com", "group.calendar.google.com"
    };

    private readonly PeopleServiceService _people;

    /// <summary>Creates a reader for one user</summary>
    /// <param name="accessToken">Returns a valid access token</param>
    public OtherContactsReader(Func<CancellationToken, Task<string>> accessToken)
    {
        _people = new PeopleServiceService(new BaseClientService.Initializer
        {
            HttpClientInitializer = new FreshTokenInitializer(accessToken),
            ApplicationName = "ContactsApi"
        });
    }

    /// <summary>Whether an address belongs to a machine, not a person</summary>
    /// <param name="email">Lower-case plain address</param>
    /// <returns>True for no-reply, notification and relay addresses</returns>
    public static bool IsAutomated(string email)
    {
        var at = email.LastIndexOf('@');
        if (at <= 0)
        {
            return true;
        }

        var local = email[..at];
        var domain = email[(at + 1)..];
        var plus = local.IndexOf('+');
        var role = plus > 0 ? local[..plus] : local;
        return RoleNames.Contains(role)
            || role.StartsWith("noreply", StringComparison.Ordinal)
            || role.StartsWith("no-reply", StringComparison.Ordinal)
            || role.StartsWith("bounce", StringComparison.Ordinal)
            || LongDigits.IsMatch(local)
            || local.Length > 40
            || RelayDomains.Contains(domain)
            || domain.StartsWith("reply.", StringComparison.Ordinal)
            || domain.StartsWith("bounce.", StringComparison.Ordinal)
            || domain.StartsWith("bounces.", StringComparison.Ordinal);
    }

    /// <summary>
    /// An entry from a Google person: plain addresses without automated ones, the
    /// name unless it is an address; null when no address is left
    /// </summary>
    /// <param name="person">Other contact</param>
    /// <returns>Entry, or null</returns>
    public static OtherContactEntry? Map(Person person)
    {
        var emails = (person.EmailAddresses ?? new List<EmailAddress>())
            .Select(e => (e?.Value ?? string.Empty).Trim().ToLowerInvariant())
            .Where(e => Plain.IsMatch(e) && !IsAutomated(e))
            .Distinct()
            .ToList();
        if (emails.Count == 0)
        {
            return null;
        }

        var name = person.Names?.Select(n => n?.DisplayName?.Trim()).FirstOrDefault(n => !string.IsNullOrEmpty(n));
        return new OtherContactEntry
        {
            ResourceName = person.ResourceName ?? string.Empty,
            Name = name == null || name.Contains('@') ? null : name,
            Emails = emails,
            Phones = (person.PhoneNumbers ?? new List<PhoneNumber>())
                .Select(p => p?.Value?.Trim())
                .OfType<string>()
                .Where(p => p.Length > 0)
                .Distinct()
                .ToList()
        };
    }

    /// <summary>Reads every Other contact, at most <see cref="MaxEntries"/></summary>
    /// <param name="ct">Cancellation</param>
    /// <returns>The entries with an address left</returns>
    public async Task<OtherContactsRead> ReadAsync(CancellationToken ct)
    {
        var result = new OtherContactsRead();
        string? pageToken = null;
        do
        {
            var list = _people.OtherContacts.List();
            list.ReadMask = "names,emailAddresses,phoneNumbers";
            list.PageSize = 1000;
            list.PageToken = pageToken;
            var page = await Call(() => list.ExecuteAsync(ct), ct);
            foreach (var person in page?.OtherContacts ?? new List<Person>())
            {
                result.Read++;
                var entry = Map(person);
                if (entry == null)
                {
                    result.Automated += (person.EmailAddresses?.Count ?? 0) > 0 ? 1 : 0;
                    continue;
                }

                result.Entries.Add(entry);
            }

            pageToken = page?.NextPageToken;
            if (result.Read >= MaxEntries && pageToken != null)
            {
                result.Capped = true;
                break;
            }
        }
        while (pageToken != null);

        return result;
    }

    /// <summary>A rate limit is retried 3 times with back-off, then reported as busy</summary>
    private static async Task<T> Call<T>(Func<Task<T>> call, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (global::Google.GoogleApiException ex) when ((int)ex.HttpStatusCode == 429
                || ex.Error?.Errors?.Any(e => e.Reason is "rateLimitExceeded" or "userRateLimitExceeded" or "quotaExceeded") == true)
            {
                if (attempt == 3)
                {
                    throw new ActivityScanException("READ_FAILED", "Google Contacts is busy. Try again in a minute.", ex);
                }

                await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct);
            }
            catch (global::Google.GoogleApiException ex) when ((int)ex.HttpStatusCode is 401 or 403)
            {
                throw new ActivityScanException("NOT_CONNECTED", "Reading Other contacts was not allowed. Allow it again.", ex);
            }
            catch (global::Google.GoogleApiException ex)
            {
                throw new ActivityScanException("READ_FAILED", $"Google Contacts failed: {ex.Error?.Message ?? ex.Message}", ex);
            }
        }
    }
}
