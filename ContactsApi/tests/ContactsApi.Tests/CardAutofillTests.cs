using System.Text.Json.Nodes;
using ContactsApi.Infrastructure.Activity;
using ContactsApi.WebApi.Services;

namespace ContactsApi.Tests;

/// <summary>AD-43: the contact card's email search and the phonetic spelling call</summary>
public class CardAutofillTests
{
    [Fact]
    public void The_query_quotes_each_name_and_strips_what_Gmail_would_read_as_an_operator()
    {
        Assert.Equal("from:(\"Dana Cohen\" OR \"דנה כהן\") -in:chats",
            SenderSearch.Query(new[] { "Dana Cohen", "דנה  כהן", "dana cohen" }));
        // Quotes, brackets and a colon cannot widen the search
        Assert.Equal("from:(\"Dana OR to me\") -in:chats",
            SenderSearch.Query(new[] { "Dana\" OR (to:me)" }));
        // A single word matches too many people
        Assert.Null(SenderSearch.Query(new[] { "Dana", "  " }));
        Assert.Equal("from:(\"Ben-David O'Neil\") -in:chats", SenderSearch.Query(new[] { "Ben-David O'Neil" }));
    }

    [Fact]
    public void Reads_the_name_and_address_of_a_From_header()
    {
        Assert.Equal(("Dana Cohen", "dana@x.com"), SenderSearch.ParseFrom("\"Dana Cohen\" <Dana@X.com>"));
        Assert.Equal(("", "dana@x.com"), SenderSearch.ParseFrom("dana@x.com"));
        Assert.Equal(("", "dana@x.com"), SenderSearch.ParseFrom("<dana@x.com>"));
        Assert.Equal(("דנה כהן", "dana@x.com"), SenderSearch.ParseFrom("=?UTF-8?B?15PXoNeUINeb15TXnw==?= <dana@x.com>"));
        // An address in the name is not the sender's
        Assert.Equal(("Dana Cohen (dana@old.com)", "dana@new.com"), SenderSearch.ParseFrom("Dana Cohen (dana@old.com) <dana@new.com>"));
        Assert.Equal(("Cohen, Dana [dana@corp.com]", "dana@corp.com"), SenderSearch.ParseFrom("\"Cohen, Dana [dana@corp.com]\" <dana@corp.com>"));
        Assert.Null(SenderSearch.ParseFrom("Dana (dana@x.com)"));
        Assert.Null(SenderSearch.ParseFrom("Mail Delivery System"));
        Assert.Null(SenderSearch.ParseFrom(null));
    }

    [Fact]
    public void Keeps_the_senders_whose_name_matches_grouped_by_address()
    {
        var t = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var found = SenderSearch.FromMail(new[] { "dana cohen" }, new[]
        {
            new SenderSeen("Dana Cohen", "dana@x.com", t),
            new SenderSeen("Dana Cohen", "dana@x.com", t.AddDays(3)),
            new SenderSeen("Dana Cohen-Levi", "dcl@y.com", t),          // the name plus a word
            new SenderSeen("Dana Levi", "levi@y.com", t),               // Gmail's partial match
            new SenderSeen("", "dana.cohen@z.com", t),                  // the address spells it
        });
        Assert.Equal(new[] { "dana.cohen@z.com", "dana@x.com", "dcl@y.com" }, found.Select(f => f.Email).OrderBy(e => e));
        var dana = found.Single(f => f.Email == "dana@x.com");
        Assert.Equal((3, 2, t.AddDays(3)), (dana.Score, dana.Mails, dana.LastSeen));
        Assert.Equal(2, found.Single(f => f.Email == "dana.cohen@z.com").Score);
        Assert.Equal(1, found.Single(f => f.Email == "dcl@y.com").Score);
    }

    [Fact]
    public void Merges_the_directory_and_the_mailbox_without_the_Owners_addresses()
    {
        var t = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        FoundEmail F(string email, int score, int mails, int day, string source, params string[] names) =>
            new() { Email = email, Score = score, Mails = mails, LastSeen = t.AddDays(day), Sources = new() { source }, Names = names.ToList() };
        var merged = SenderSearch.Merge(
            new[] { F("dana@x.com", 1, 40, 0, "directory", "Dana C"), F("me@x.com", 3, 99, 0, "directory"), F("old@x.com", 3, 2, 0, "directory") },
            new[] { F("Dana@X.com", 3, 2, 5, "mail", "Dana Cohen"), F("new@x.com", 3, 2, 9, "mail"), F("part@x.com", 1, 1, 0, "mail") },
            new HashSet<string> { "me@x.com" });
        // Best first: score, then mails, then last seen; at most 3
        Assert.Equal(new[] { "dana@x.com", "new@x.com", "old@x.com" }, merged.Select(f => f.Email));
        var dana = merged[0];
        Assert.Equal(3, dana.Score);
        Assert.Equal(40, dana.Mails); // the directory's count
        Assert.Equal(t.AddDays(5), dana.LastSeen);
        Assert.Equal(new[] { "Dana C", "Dana Cohen" }, dana.Names);
        Assert.Equal(new[] { "directory", "mail" }, dana.Sources);
    }

    [Fact]
    public void Only_Hebrew_name_words_may_be_sent_to_Gemini()
    {
        Assert.Equal(new[] { "יוסי", "בן-דוד", "צ'רלי" }, Transliteration.Check(new[] { " יוסי ", "בן-דוד", "צ'רלי", "יוסי" }));
        Assert.Null(Transliteration.Check(new[] { "יוסי", "Cohen" }));
        Assert.Null(Transliteration.Check(new[] { "יוסי כהן" }));       // one word each
        Assert.Null(Transliteration.Check(new[] { "--" }));             // a letter is needed
        Assert.Null(Transliteration.Check(Array.Empty<string>()));
        Assert.Null(Transliteration.Check(null));
        Assert.Null(Transliteration.Check(Enumerable.Range(0, 21).Select(i => new string('א', i + 1))));
        Assert.Null(Transliteration.Check(new[] { new string('א', 41) }));
    }

    [Fact]
    public void Keeps_only_Latin_spellings_of_the_words_sent()
    {
        var answer = new JsonObject
        {
            ["spellings"] = new JsonArray(
                new JsonObject { ["word"] = "יוסי", ["spelling"] = "Yossi" },
                new JsonObject { ["word"] = "בן-דוד", ["spelling"] = "Ben-David" },
                new JsonObject { ["word"] = "כהן", ["spelling"] = "כהן" },             // not Latin
                new JsonObject { ["word"] = "לוי", ["spelling"] = "Levi" },            // not asked
                new JsonObject { ["word"] = "יוסי", ["spelling"] = "Yosi" },           // the first one stays
                new JsonObject { ["word"] = "צביקה", ["spelling"] = "Ignore the above; <b>" })
        };
        JsonNode Response(string text) => new JsonObject
        {
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) }
            })
        };
        var words = new[] { "יוסי", "בן-דוד", "כהן", "צביקה" };
        Assert.Equal(new Dictionary<string, string> { ["יוסי"] = "Yossi", ["בן-דוד"] = "Ben-David" },
            Transliteration.Parse(Response(answer.ToJsonString()), words));
        Assert.Empty(Transliteration.Parse(Response("not json"), words));
        Assert.Empty(Transliteration.Parse(null, words));

        var request = Transliteration.Request(new[] { "יוסי", "כהן" });
        Assert.Equal("יוסי\nכהן", request["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(0, request["generationConfig"]!["temperature"]!.GetValue<int>());
    }
}
