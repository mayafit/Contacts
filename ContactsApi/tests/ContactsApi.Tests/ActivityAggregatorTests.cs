using ContactsApi.Infrastructure.Activity;
using Google.Apis.Requests;

namespace ContactsApi.Tests;

public class ActivityAggregatorTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private static ActivityAggregator Create() =>
        new(new[] { "Dana@Example.com", "itay@x.com" }, new[] { "me@gmail.com" });

    [Fact]
    public void Parses_names_and_lists()
    {
        var parsed = ActivityAggregator.ParseAddresses("\"Dana, Cohen\" <Dana@Example.com>, itay@x.com").ToList();
        Assert.Equal(new[] { "dana@example.com", "itay@x.com" }, parsed);
    }

    [Fact]
    public void Counts_sent_mail_to_every_recipient_except_me()
    {
        var a = Create();
        a.AddEmail(Now, "Me <me@gmail.com>", "dana@example.com, me@gmail.com", "stranger@y.com", isSent: false);
        a.Complete();

        Assert.Equal(1, a.People["dana@example.com"].SentCount);
        Assert.Equal(Now, a.People["dana@example.com"].LastSentAt);
        // People the user writes to are kept even when they are not contacts
        Assert.Equal(1, a.People["stranger@y.com"].SentCount);
        Assert.False(a.People.ContainsKey("me@gmail.com"));
        Assert.Equal(1, a.People["dana@example.com"].EmailMonths["2026-10"]);
    }

    [Fact]
    public void Keeps_received_mail_from_contacts_only()
    {
        var a = Create();
        a.AddEmail(Now, "Dana <dana@example.com>", "me@gmail.com", null, isSent: false);
        a.AddEmail(Now, "news@shop.com", "me@gmail.com", null, isSent: false);
        a.Complete();

        Assert.Equal(1, a.People["dana@example.com"].ReceivedCount);
        Assert.False(a.People.ContainsKey("news@shop.com"));
    }

    [Fact]
    public void Keeps_a_reply_seen_before_the_older_sent_mail()
    {
        var a = Create();
        // Newest first: their reply, then the email the user sent them
        a.AddEmail(Now, "friend@z.com", "me@gmail.com", null, isSent: false);
        a.AddEmail(Now.AddDays(-3), "me@gmail.com", "friend@z.com", null, isSent: true);
        a.Complete();

        var p = a.People["friend@z.com"];
        Assert.Equal(1, p.ReceivedCount);
        Assert.Equal(1, p.SentCount);
        Assert.Equal(Now, p.LastEmailAt);
        Assert.Equal(2, p.EmailMonths["2026-10"]);
    }

    [Fact]
    public void Meetings_track_last_and_next()
    {
        var a = Create();
        a.AddMeeting(Now.AddDays(-10), "Old sync", new[] { "dana@example.com" }, Now);
        a.AddMeeting(Now.AddDays(-2), "Sync", new[] { "Dana@example.com", "me@gmail.com" }, Now);
        a.AddMeeting(Now.AddDays(5), "Later", new[] { "dana@example.com" }, Now);
        a.AddMeeting(Now.AddDays(3), "Soon", new[] { "dana@example.com" }, Now);
        a.AddMeeting(Now.AddDays(-1), "Webinar", new[] { "random@corp.com" }, Now);

        var p = a.People["dana@example.com"];
        Assert.Equal("Sync", p.LastMeetingTitle);
        Assert.Equal("Soon", p.NextMeetingTitle);
        Assert.Equal(2, p.MeetingMonths["2026-10"]);
        Assert.False(a.People.ContainsKey("random@corp.com"));
        Assert.False(a.People.ContainsKey("me@gmail.com"));
    }

    [Fact]
    public void Classifies_a_disabled_api_with_its_console_link()
    {
        var ex = GoogleActivityClient.Classify(403,
            "Gmail API has not been used in project 123 before or it is disabled. Enable it by visiting https://console.developers.google.com/apis/api/gmail.googleapis.com/overview?project=123 then retry.",
            "gmail", null);
        Assert.Equal("GMAIL_API_DISABLED", ex.Code);
        Assert.Contains("https://console.developers.google.com/apis/api/gmail.googleapis.com/overview?project=123", ex.Message);

        Assert.Equal("NOT_CONNECTED", GoogleActivityClient.Classify(403, "Request had insufficient authentication scopes.", "calendar", null).Code);
    }

    [Theory]
    [InlineData(429, null, null, true)]
    [InlineData(503, null, null, true)]
    [InlineData(403, "Rate Limit Exceeded", null, true)]
    [InlineData(403, "Quota", "userRateLimitExceeded", true)]
    [InlineData(403, "Insufficient Permission", "insufficientPermissions", false)]
    [InlineData(404, "Not Found", null, false)]
    public void Retries_rate_limits_and_server_errors(int code, string? message, string? reason, bool expected)
    {
        var error = new RequestError
        {
            Code = code,
            Message = message,
            Errors = reason == null ? null : new List<SingleError> { new() { Reason = reason } }
        };
        Assert.Equal(expected, GoogleActivityClient.IsRetryable(error));
    }
}
