using ContactsApi.Infrastructure.Google;
using ContactsApi.WebApi.Services;
using Google.Apis.PeopleService.v1.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContactsApi.Tests;

public class OtherContactsTests
{
    [Theory]
    [InlineData("noreply@example.com")]
    [InlineData("no-reply@accounts.google.com")]
    [InlineData("noreply-calendar@example.com")]
    [InlineData("info+il@shop.co.il")]
    [InlineData("mailer-daemon@googlemail.com")]
    [InlineData("bounces-1234@mail.example.com")]
    [InlineData("1323111618-zkwq.bfvm.kmj3.pbtf@property.booking.com")]
    [InlineData("dana@guest.booking.com")]
    [InlineData("x@reply.github.com")]
    [InlineData("someone@reply.example.com")]
    [InlineData("a-very-long-generated-local-part-for-tracking-1x@example.com")]
    public void Machine_addresses_are_automated(string email) =>
        Assert.True(OtherContactsReader.IsAutomated(email));

    [Theory]
    [InlineData("edna.abergel@bankleumi.co.il")]
    [InlineData("a.nagler@sheba.health.gov.il")]
    [InlineData("aharon.leviner@gmail.com")]
    [InlineData("dana+work@gmail.com")]
    [InlineData("support.team.lead@example.com")]
    [InlineData("david1975@walla.co.il")]
    [InlineData("0521234567@gmail.com")]
    public void People_are_not_automated(string email) =>
        Assert.False(OtherContactsReader.IsAutomated(email));

    [Fact]
    public void An_entry_keeps_plain_person_addresses_and_drops_a_name_that_is_an_address()
    {
        var entry = OtherContactsReader.Map(new Person
        {
            ResourceName = "otherContacts/c1",
            Names = new List<Name> { new() { DisplayName = "<ahavaparva@gmail.com>" } },
            EmailAddresses = new List<EmailAddress>
            {
                new() { Value = " AhavaParva@Gmail.com " },
                new() { Value = "ahavaparva@gmail.com" },
                new() { Value = "noreply@gmail.com" },
                new() { Value = "not an address" }
            },
            PhoneNumbers = new List<PhoneNumber> { new() { Value = "050-1234567" }, new() { Value = " " } }
        });

        Assert.NotNull(entry);
        Assert.Null(entry!.Name);
        Assert.Equal(new[] { "ahavaparva@gmail.com" }, entry.Emails);
        Assert.Equal(new[] { "050-1234567" }, entry.Phones);
        Assert.Equal("otherContacts/c1", entry.ResourceName);
    }

    [Fact]
    public void An_entry_with_only_automated_addresses_is_dropped()
    {
        Assert.Null(OtherContactsReader.Map(new Person
        {
            Names = new List<Name> { new() { DisplayName = "205-Pismo Getaway via Booking.com" } },
            EmailAddresses = new List<EmailAddress> { new() { Value = "3373081995-ggqc.f3vy.sdt3.2p@property.booking.com" } }
        }));
        Assert.Null(OtherContactsReader.Map(new Person { Names = new List<Name> { new() { DisplayName = "No mail" } } }));
    }

    [Fact]
    public void A_name_is_kept()
    {
        var entry = OtherContactsReader.Map(new Person
        {
            Names = new List<Name> { new() { DisplayName = "Abergel Edna" } },
            EmailAddresses = new List<EmailAddress> { new() { Value = "edna.abergel@bankleumi.co.il" } }
        });
        Assert.Equal("Abergel Edna", entry!.Name);
    }

    [Fact]
    public void The_opt_in_asks_for_the_read_only_scope_and_is_known_from_a_grant()
    {
        var scopes = ScopePreferences.ScopesFor(new[] { "openid" }, new OptIns { OtherContacts = true });
        Assert.Contains(OtherContactsScopes.Read, scopes);
        Assert.DoesNotContain(OtherContactsScopes.Read, ScopePreferences.ScopesFor(new[] { "openid" }, new OptIns { Activity = true }));
        Assert.True(ScopePreferences.FromScopes(new[] { OtherContactsScopes.Read }).OtherContacts);
        Assert.True(new OptIns().With(new OptIns { OtherContacts = true }).OtherContacts);
    }

    [Fact]
    public void Only_a_refused_token_or_scope_asks_to_allow_again()
    {
        static global::Google.GoogleApiException Error(int status, string message, string? reason = null) =>
            new("people", message)
            {
                HttpStatusCode = (System.Net.HttpStatusCode)status,
                Error = new global::Google.Apis.Requests.RequestError
                {
                    Message = message,
                    Errors = reason == null
                        ? null
                        : new List<global::Google.Apis.Requests.SingleError> { new() { Reason = reason } }
                }
            };

        Assert.True(OtherContactsReader.IsRefused(Error(401, "Invalid Credentials")));
        Assert.True(OtherContactsReader.IsRefused(Error(403, "Request had insufficient authentication scopes.")));
        Assert.True(OtherContactsReader.IsRefused(Error(403, "Forbidden", "insufficientPermissions")));
        Assert.False(OtherContactsReader.IsRefused(Error(403, "People API has not been used in project 1 before or it is disabled", "accessNotConfigured")));
        Assert.False(OtherContactsReader.IsRefused(Error(500, "Backend Error")));
    }

    private static OtherContactsStore Store(IDistributedCache cache) =>
        new(cache, new EphemeralDataProtectionProvider(), NullLogger<OtherContactsStore>.Instance);

    [Fact]
    public async Task Dismissals_are_added_once_capped_and_cleared()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var store = Store(cache);

        await store.AddDismissedAsync("u1", new[] { "n:a@x.com", "u:people/c1:b@x.com", "n:a@x.com", "", new string('k', 601) });
        Assert.Equal(new[] { "n:a@x.com", "u:people/c1:b@x.com" }, await store.GetDismissedAsync("u1"));
        Assert.Empty(await store.GetDismissedAsync("u2"));

        // Stored encrypted: the address is not in the cache in clear
        var raw = await cache.GetStringAsync("other-contacts-dismissed:u1");
        Assert.DoesNotContain("a@x.com", raw);

        var many = Enumerable.Range(0, OtherContactsStore.MaxKeys + 10).Select(i => $"n:{i}@x.com");
        var all = await store.AddDismissedAsync("u1", many);
        Assert.Equal(OtherContactsStore.MaxKeys, all.Count);
        Assert.DoesNotContain("n:a@x.com", all);
        Assert.Equal($"n:{OtherContactsStore.MaxKeys + 9}@x.com", all[^1]);

        await store.ClearDismissedAsync("u1");
        Assert.Empty(await store.GetDismissedAsync("u1"));
    }
}
