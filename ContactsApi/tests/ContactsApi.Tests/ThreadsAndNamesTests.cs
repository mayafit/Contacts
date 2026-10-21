using ContactsApi.Infrastructure.Activity;

namespace ContactsApi.Tests;

public class ThreadsAndNamesTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    private static ActivityAggregator Create() =>
        new(new[] { "dana@x.com", "avi@y.com" }, new[] { "me@gmail.com" });

    [Fact]
    public void Keeps_the_latest_message_of_each_thread()
    {
        var a = Create();
        // Newest first, as the scan reads
        a.AddThreadMessage("t1", Now.AddDays(-2), "Dana <dana@x.com>", "me@gmail.com", null, "Re: proposal", false, true, false, Now);
        a.AddThreadMessage("t1", Now.AddDays(-5), "me@gmail.com", "dana@x.com", null, "proposal", true, false, false, Now);
        a.AddThreadMessage("t2", Now.AddDays(-8), "me@gmail.com", "Avi <avi@y.com>", null, "=?UTF-8?B?16nXnNeV150=?=", true, false, false, Now);
        a.PruneThreads(Now);

        var t1 = a.Threads["t1"];
        Assert.False(t1.LastFromMe);
        Assert.Equal(new[] { "dana@x.com" }, t1.Counterparts);
        Assert.Equal("Re: proposal", t1.Subject);
        Assert.Equal(2, t1.Messages);
        Assert.True(a.Threads["t2"].LastFromMe);
        Assert.Equal("שלום", a.Threads["t2"].Subject);
    }

    [Fact]
    public void Skips_bulk_noreply_crowded_old_and_unknown_threads()
    {
        var a = Create();
        a.AddThreadMessage("bulk", Now.AddDays(-1), "dana@x.com", "me@gmail.com", null, "News", false, true, true, Now);
        a.AddThreadMessage("noreply", Now.AddDays(-1), "no-reply@x.com", "me@gmail.com", null, "Alert", false, true, false, Now);
        a.AddThreadMessage("crowd", Now.AddDays(-1), "me@gmail.com", "a@1.com, b@1.com, c@1.com, d@1.com, dana@x.com", null, "All", true, false, false, Now);
        a.AddThreadMessage("old", Now.AddDays(-60), "dana@x.com", "me@gmail.com", null, "Old", false, true, false, Now);
        a.AddThreadMessage("stranger", Now.AddDays(-1), "stranger@z.com", "me@gmail.com", null, "Hi", false, true, false, Now);
        a.PruneThreads(Now);
        Assert.Empty(a.Threads);
    }

    [Theory]
    [InlineData("dana cohen", "d1@corp.com", "Dana Cohen", 3)]
    [InlineData("dana cohen", "d1@corp.com", "Cohen, Dana", 3)]
    [InlineData("dana cohen", "dana.cohen@corp.com", "", 2)]
    [InlineData("dana cohen", "d1@corp.com", "Dana R. Cohen", 1)]
    [InlineData("dana cohen", "d1@corp.com", "Dana Levi", 0)]
    [InlineData("דנה כהן", "d1@corp.com", "דנה כהן", 3)]
    public void Scores_name_matches(string contact, string address, string seen, int expected) =>
        Assert.Equal(expected, NameMatcher.Score(new[] { NameMatcher.Normalize(contact) }, address, new[] { seen }));

    [Fact]
    public void Crowded_received_mail_and_archiving()
    {
        var a = Create();
        a.AddThreadMessage("crowd", Now.AddDays(-1), "dana@x.com", "me@gmail.com, a@1.com, b@1.com, c@1.com, d@1.com", null, "All hands", false, true, false, Now, "m1");
        a.AddThreadMessage("t", Now.AddDays(-1), "dana@x.com", "me@gmail.com", null, "Q", false, true, false, Now, "m2");
        a.AddThreadMessage("t", Now.AddDays(-3), "me@gmail.com", "dana@x.com", null, "Q", true, false, false, Now, "m3");
        a.SetInbox("m2", false);
        a.PruneThreads(Now);
        Assert.False(a.Threads.ContainsKey("crowd"));
        Assert.False(a.Threads["t"].InInbox);
        Assert.Equal(2, a.Threads["t"].Messages);
    }
}
