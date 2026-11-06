using System.Net;
using System.Text.Json.Nodes;
using ContactsApi.Infrastructure.Activity;
using ContactsApi.WebApi.Services;

namespace ContactsApi.Tests;

/// <summary>AD-46: Gravatar by hash, the queue, and the links in signatures</summary>
public class ProfilesTests
{
    private sealed class Fake : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
        public HttpRequestMessage? Last;
        public Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(_answer(request));
        }
    }

    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Keeps_the_profiles_text_and_https_links_only()
    {
        var json = JsonNode.Parse("""
            {"display_name":" Dana Cohen ","job_title":"CTO","company":"Acme","location":"Haifa",
             "description":"x","profile_url":"https://gravatar.com/dana",
             "verified_accounts":[{"service_label":"LinkedIn","url":"https://www.linkedin.com/in/dana"},
                                  {"service_label":"Old","url":"http://example.com/dana"},
                                  {"service_label":"Bad","url":"javascript:alert(1)"}],
             "links":[{"label":"Blog","url":"https://dana.blog"}]}
            """);
        var p = GravatarClient.Parse(json);
        Assert.Equal(("Dana Cohen", "CTO", "Acme", "Haifa"), (p.DisplayName, p.JobTitle, p.Company, p.Location));
        Assert.Equal("https://gravatar.com/dana", p.ProfileUrl);
        Assert.Equal(new[] { "LinkedIn" }, p.VerifiedAccounts.Select(a => a.Label));
        Assert.Equal("https://dana.blog/", p.Links.Single().Url);
        Assert.Equal(500, GravatarClient.Parse(JsonNode.Parse($$"""{"description":"{{new string('a', 900)}}"}""")).Description!.Length);
    }

    [Fact]
    public async Task Reads_the_answer_and_the_rate_limit()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        HttpResponseMessage With(HttpStatusCode code, string remaining, string body = "{}")
        {
            var r = new HttpResponseMessage(code) { Content = new StringContent(body) };
            r.Headers.Add("X-RateLimit-Remaining", remaining);
            r.Headers.Add("X-RateLimit-Reset", reset.ToString());
            return r;
        }

        var fake = new Fake(_ => With(HttpStatusCode.OK, "98", """{"display_name":"Dana"}"""));
        var found = await new GravatarClient(new HttpClient(fake), "key").LookUpAsync(Hash, default);
        Assert.Equal((GravatarOutcome.Found, "Dana", 98), (found.Outcome, found.Profile?.DisplayName, found.Remaining));
        Assert.Equal($"https://api.gravatar.com/v3/profiles/{Hash}", fake.Last!.RequestUri!.ToString());
        Assert.Equal("Bearer", fake.Last.Headers.Authorization?.Scheme);

        var none = await new GravatarClient(new HttpClient(new Fake(_ => With(HttpStatusCode.NotFound, "97"))), null).LookUpAsync(Hash, default);
        Assert.Equal(GravatarOutcome.NotFound, none.Outcome);

        var limited = await new GravatarClient(new HttpClient(new Fake(_ => With(HttpStatusCode.TooManyRequests, "0"))), null).LookUpAsync(Hash, default);
        Assert.Equal(GravatarOutcome.Limited, limited.Outcome);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(reset).UtcDateTime, limited.ResetAt);

        var failed = await new GravatarClient(new HttpClient(new Fake(_ => throw new HttpRequestException("down"))), null).LookUpAsync(Hash, default);
        Assert.Equal(GravatarOutcome.Failed, failed.Outcome);
    }

    [Fact]
    public void Queues_each_hash_once_and_again_after_30_days()
    {
        var now = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);
        var b = new string('b', 64);
        var c = new string('c', 64);
        var doc = new ProfileDoc
        {
            Queue = { Hash },
            Entries =
            {
                [b] = new ProfileEntry { CheckedAt = now.AddDays(-3) },
                [c] = new ProfileEntry { CheckedAt = now.AddDays(-31) }
            }
        };
        Assert.Equal(1, ProfileStore.Enqueue(doc, new[] { Hash, b, c, c }, now));
        Assert.Equal(new[] { Hash, c }, doc.Queue);
        Assert.True(GravatarClient.IsHash(Hash));
        Assert.False(GravatarClient.IsHash("dana@x.com"));
        Assert.False(GravatarClient.IsHash(Hash.ToUpperInvariant()));
    }

    [Fact]
    public void Drops_what_no_contact_has_any_more()
    {
        var b = new string('b', 64);
        var c = new string('c', 64);
        var doc = new ProfileDoc
        {
            Queue = { Hash, b },
            Entries = { [c] = new ProfileEntry(), [Hash] = new ProfileEntry() },
            Failures = { [b] = 2 }
        };
        Assert.True(ProfileStore.Prune(doc, new HashSet<string> { Hash }));
        Assert.Equal(new[] { Hash }, doc.Queue);
        Assert.Equal(new[] { Hash }, doc.Entries.Keys);
        Assert.Empty(doc.Failures);
        Assert.False(ProfileStore.Prune(doc, new HashSet<string> { Hash }));
    }

    [Fact]
    public void Takes_the_links_of_a_signature_by_pattern()
    {
        var links = SignatureLinks.Extract(new[]
        {
            "Dana Cohen | CTO\nwww.acme.com · linkedin.com/in/dana-cohen-123.\nhttps://x.com/dana?utm_source=mail",
            "Visit https://www.acme.com (our site) and il.linkedin.com/in/dana-cohen-123",
            "nothing here: dana@acme.com",
        });
        Assert.Equal(new[]
        {
            "https://www.acme.com",
            "https://x.com/dana",
            "https://linkedin.com/in/dana-cohen-123",
            "https://il.linkedin.com/in/dana-cohen-123",
        }, links);
    }
}
