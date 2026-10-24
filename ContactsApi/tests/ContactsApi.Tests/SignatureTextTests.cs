using ContactsApi.Infrastructure.Activity;
using Google.Apis.Gmail.v1.Data;

namespace ContactsApi.Tests;

public class SignatureTextTests
{
    [Fact]
    public void Keeps_the_senders_last_lines_and_drops_quoted_history()
    {
        var body = "Hi Alon,\n\nSounds good, see you Tuesday.\n\nBest,\nMotti Gat\nVP R&D | Acme Ltd.\nMobile: +972-54-123-4567\nwww.acme.co.il\n\n" +
                   "On Mon, Oct 19, 2026 at 10:00 Alon <alon@x.com> wrote:\n> Shall we meet?\n> Alon\n> 050-999-9999";
        var tail = SignatureText.AuthorTail(body);
        Assert.Contains("Mobile: +972-54-123-4567", tail);
        Assert.Contains("VP R&D | Acme Ltd.", tail);
        Assert.DoesNotContain("050-999-9999", tail);
        Assert.DoesNotContain("Shall we meet", tail);
    }

    [Theory]
    [InlineData("בתאריך יום ב׳, 19 באוק׳ 2026 ב-10:00 מאת אלון <alon@x.com>‏ כתב:")]
    [InlineData("-----Original Message-----")]
    [InlineData("From: Alon <alon@x.com>")]
    [InlineData("________________________________")]
    public void Recognizes_quote_headers(string header)
    {
        var tail = SignatureText.AuthorTail($"Thanks\nמוטי גת\n052-1234567\n{header}\nOLD TEXT 03-1234567");
        Assert.Contains("052-1234567", tail);
        Assert.DoesNotContain("03-1234567", tail);
    }

    [Fact]
    public void Limits_lines_and_converts_html()
    {
        var text = SignatureText.HtmlToText("<div>Hello</div><p>Dana Cohen<br>CTO &amp; founder</p><style>.x{}</style>");
        Assert.Equal("Dana Cohen\nCTO & founder", SignatureText.AuthorTail(text, maxLines: 2));
    }

    [Fact]
    public void Prefers_the_plain_text_part()
    {
        static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var msg = new MessagePart
        {
            MimeType = "multipart/alternative",
            Parts = new List<MessagePart>
            {
                new() { MimeType = "text/html", Body = new MessagePartBody { Data = B64("<b>html</b>") } },
                new() { MimeType = "text/plain", Body = new MessagePartBody { Data = B64("plain דנה") } }
            }
        };
        Assert.Equal("plain דנה", SignatureReader.BodyText(msg));
    }

    [Fact]
    public void Recognizes_a_wrapped_gmail_quote_header()
    {
        var tail = SignatureText.AuthorTail(
            "Thanks\nDana Cohen | CTO\nOn Mon, Oct 19, 2026 at 10:00 AM Alon Mayaffit <\nalon@x.com> wrote:\n> Alon 050-1111111");
        Assert.Equal("Thanks\nDana Cohen | CTO", tail);
    }

    [Fact]
    public void Removes_newsletter_tracking_from_links()
    {
        Assert.Equal("http://lobby99.org.il",
            SignatureText.CleanUrl("http://lobby99.org.il?utm_source=x&utm_campaign=6c3&utm_medium=email&utm_term=0_f3"));
        Assert.Equal("https://www.youtube.com/@Lobby99",
            SignatureText.CleanUrl(" https://www.youtube.com/@Lobby99?utm_source=a&mc_cid=b "));
        Assert.Equal("https://shop.example.com/item?id=7#top",
            SignatureText.CleanUrl("https://shop.example.com/item?utm_source=a&id=7#top"));
        Assert.Equal("https://example.com/", SignatureText.CleanUrl("https://example.com/"));
    }
}
