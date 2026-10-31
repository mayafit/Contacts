using ContactsApi.Infrastructure.Activity;
using ContactsApi.WebApi.Services;
using Google.Apis.Calendar.v3.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContactsApi.Tests;

public class ContactHistoryTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Only_plain_addresses_are_searched_lower_case_and_distinct()
    {
        var addresses = ContactHistoryReader.Addresses(new[]
        {
            " Dror@Gmail.com ", "dror@gmail.com", "x OR from:*", "\"a\"@b.com", null, "", "d.magen@elta.co.il"
        });
        Assert.Equal(new[] { "dror@gmail.com", "d.magen@elta.co.il" }, addresses);
        Assert.Equal(ContactHistoryReader.MaxAddresses,
            ContactHistoryReader.Addresses(Enumerable.Range(0, 30).Select(i => $"p{i}@x.com")).Count);
    }

    [Fact]
    public void Queries_cover_every_address_without_a_date_limit()
    {
        var a = new[] { "dror@gmail.com", "d@elta.co.il" };
        Assert.Equal("from:(\"dror@gmail.com\" OR \"d@elta.co.il\") -in:chats", ContactHistoryReader.ReceivedQuery(a));
        Assert.Equal(
            "in:sent {to:\"dror@gmail.com\" cc:\"dror@gmail.com\" bcc:\"dror@gmail.com\" to:\"d@elta.co.il\" cc:\"d@elta.co.il\" bcc:\"d@elta.co.il\"} -in:chats",
            ContactHistoryReader.SentQuery(a));
        Assert.DoesNotContain("newer_than", ContactHistoryReader.ReceivedQuery(a));
    }

    private static Event Meeting(string id, DateTime start, params string[] guests) => new()
    {
        Id = id,
        Status = "confirmed",
        Start = new EventDateTime { DateTimeDateTimeOffset = new DateTimeOffset(start) },
        Attendees = guests.Select(g => new EventAttendee { Email = g })
            .Append(new EventAttendee { Email = "me@x.com", Self = true, ResponseStatus = "accepted" })
            .ToList()
    };

    [Fact]
    public void Meetings_count_only_real_meetings_with_the_person()
    {
        var a = new[] { "dror@gmail.com" };
        var first = new DateTime(2015, 11, 2, 9, 0, 0, DateTimeKind.Utc);
        var last = new DateTime(2016, 3, 7, 9, 0, 0, DateTimeKind.Utc);
        var next = new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
        var declined = Meeting("d", last.AddDays(1), "Dror@Gmail.com");
        declined.Attendees.Single(x => x.Self == true).ResponseStatus = "declined";
        var cancelled = Meeting("c", last.AddDays(2), "dror@gmail.com");
        cancelled.Status = "cancelled";
        var events = new[]
        {
            Meeting("1", first, "Dror@Gmail.com"),
            Meeting("2", last, "dror@gmail.com", "other@x.com"),
            Meeting("3", next, "dror@gmail.com"),
            Meeting("4", next.AddDays(30), "dror@gmail.com"),
            // Found by Calendar's free text (a mention), not with the person
            Meeting("t", last.AddDays(3), "someone@x.com"),
            Meeting("big", last.AddDays(4), Enumerable.Range(0, 25).Select(i => $"p{i}@x.com").Append("dror@gmail.com").ToArray()),
            declined,
            cancelled,
        };
        var m = ContactHistoryReader.Meetings(events, a, Now);
        Assert.Equal(2, m.Count);
        Assert.Equal(first, m.First);
        Assert.Equal(last, m.Last);
        Assert.Equal(next, m.Next);
    }

    private static ActivityStore Store(IDistributedCache cache) =>
        new(cache, new EphemeralDataProtectionProvider(), NullLogger<ActivityStore>.Instance);

    [Fact]
    public async Task The_period_defaults_to_a_year_and_survives_Forget()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var store = Store(cache);
        Assert.Equal(1, await store.GetWindowYearsAsync("u"));
        await store.SaveWindowYearsAsync("u", 5);
        await store.RemoveAsync("u");
        await store.RemoveHistoriesAsync("u");
        Assert.Equal(5, await store.GetWindowYearsAsync("u"));
        // A stored value outside the offered periods reads as the default
        await cache.SetStringAsync("activity-window:u", "4");
        Assert.Equal(1, await store.GetWindowYearsAsync("u"));
    }

    [Fact]
    public async Task Histories_are_kept_per_contact_and_forgotten()
    {
        var store = Store(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        var h = new ContactHistory
        {
            CheckedAt = Now,
            Mail = "searched",
            Received = new MailCount { Count = 143, First = new DateTime(2015, 11, 2), Last = new DateTime(2016, 3, 7) }
        };
        await store.SaveHistoryAsync("u", "people/c1", h);
        var all = await store.GetHistoriesAsync("u");
        Assert.Equal(143, all["people/c1"].Received!.Count);
        Assert.Empty(await store.GetHistoriesAsync("other"));
        await store.RemoveHistoriesAsync("u");
        Assert.Empty(await store.GetHistoriesAsync("u"));
    }
}
