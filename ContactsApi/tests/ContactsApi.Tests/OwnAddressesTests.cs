using ContactsApi.Infrastructure.Activity;

namespace ContactsApi.Tests;

public class OwnAddressesTests
{
    [Fact]
    public void Adds_the_other_addresses_on_the_users_own_card_only()
    {
        var cards = new List<IReadOnlyCollection<string?>>
        {
            new[] { "Alon@Gmail.com", "alon@elta.co.il" },                         // the user's own card
            new[] { "dana@x.com" },                                                // someone else
            new[] { "alon@gmail.com", "a@f.com", "b@f.com", "c@f.com", "d@f.com", "e@f.com", "f@f.com" }, // a shared family card
        };
        var mine = OwnAddresses.Expand(new[] { " alon@gmail.com ", null, "" }, cards);
        Assert.Equal(new[] { "alon@elta.co.il", "alon@gmail.com" }, mine.OrderBy(e => e));
        Assert.True(OwnAddresses.IsAddress("alon@elta.co.il"));
        Assert.False(OwnAddresses.IsAddress("Alon <alon@elta.co.il>"));
    }

    [Fact]
    public void Mail_between_own_addresses_is_no_conversation()
    {
        var mine = OwnAddresses.Expand(new[] { "alon@gmail.com" }, new[] { (IReadOnlyCollection<string?>)new[] { "alon@gmail.com", "alon@elta.co.il" } });
        var aggregator = new ActivityAggregator(new[] { "dana@x.com" }, mine);
        var now = DateTime.UtcNow;
        aggregator.AddThreadMessage("t1", now.AddDays(-3), "Alon <alon@elta.co.il>", "alon@gmail.com", null, "note to self", false, true, false, now);
        aggregator.AddThreadMessage("t2", now.AddDays(-3), "Dana <dana@x.com>", "alon@gmail.com", null, "hi", false, true, false, now);
        Assert.DoesNotContain("t1", aggregator.Threads.Keys);
        Assert.Contains("t2", aggregator.Threads.Keys);
    }
}
