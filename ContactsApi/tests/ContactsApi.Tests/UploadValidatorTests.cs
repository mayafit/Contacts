using System.Text;
using System.Text.Json.Nodes;
using ContactsApi.WebApi.Services;
using Microsoft.Extensions.Options;

namespace ContactsApi.Tests;

/// <summary>
/// The upload validator is the trust boundary for assistant file attachments: it
/// must accept real files of allowed types and reject disguised, oversized, too
/// many, or disallowed uploads before they reach Gemini.
/// </summary>
public class UploadValidatorTests
{
    private static UploadValidator Create(UploadOptions? opts = null) =>
        new(Options.Create(opts ?? new UploadOptions()));

    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0 };
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n...");
    private static readonly byte[] Zip = { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4 };
    private static readonly byte[] Ole = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
    private static readonly byte[] Exe = { 0x4D, 0x5A, 0x90, 0x00 }; // "MZ"

    private static JsonObject Request(params JsonObject[] parts)
    {
        var arr = new JsonArray();
        foreach (var p in parts)
        {
            arr.Add(p);
        }

        return new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = arr }),
        };
    }

    private static JsonObject Text(string t) => new() { ["text"] = t };

    private static JsonObject Inline(string mime, byte[] bytes) => new()
    {
        ["inlineData"] = new JsonObject
        {
            ["mimeType"] = mime,
            ["data"] = Convert.ToBase64String(bytes),
        },
    };

    [Fact]
    public void Accepts_a_real_png()
    {
        var r = Create().Validate(Request(Text("look"), Inline("image/png", Png)));
        Assert.True(r.Ok);
    }

    [Fact]
    public void Accepts_pdf_office_and_legacy_office()
    {
        var v = Create();
        Assert.True(v.Validate(Request(Inline("application/pdf", Pdf))).Ok);
        Assert.True(v.Validate(Request(Inline(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document", Zip))).Ok);
        Assert.True(v.Validate(Request(Inline("application/msword", Ole))).Ok);
    }

    [Fact]
    public void Accepts_valid_text_and_json()
    {
        var v = Create();
        Assert.True(v.Validate(Request(Inline("text/plain", Encoding.UTF8.GetBytes("hello שלום")))).Ok);
        Assert.True(v.Validate(Request(Inline("application/json", Encoding.UTF8.GetBytes("{\"a\":1}")))).Ok);
    }

    [Fact]
    public void Rejects_a_type_outside_the_allowlist()
    {
        var r = Create().Validate(Request(Inline("application/x-msdownload", Exe)));
        Assert.False(r.Ok);
        Assert.Contains("not supported", r.Message);
    }

    [Fact]
    public void Rejects_an_executable_disguised_as_an_image()
    {
        // Allowed MIME type, but the bytes are not a PNG
        var r = Create().Validate(Request(Inline("image/png", Exe)));
        Assert.False(r.Ok);
        Assert.Contains("do not look like", r.Message);
    }

    [Fact]
    public void Rejects_a_jpeg_claiming_to_be_png()
    {
        var r = Create().Validate(Request(Inline("image/png", Jpeg)));
        Assert.False(r.Ok);
    }

    [Fact]
    public void Rejects_binary_bytes_claiming_to_be_text()
    {
        var binary = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE };
        var r = Create().Validate(Request(Inline("text/plain", binary)));
        Assert.False(r.Ok);
    }

    [Fact]
    public void Rejects_too_many_files()
    {
        var v = Create(new UploadOptions { MaxFiles = 2 });
        var r = v.Validate(Request(
            Inline("image/png", Png), Inline("image/png", Png), Inline("image/png", Png)));
        Assert.False(r.Ok);
        Assert.Contains("at most 2", r.Message);
    }

    [Fact]
    public void Rejects_a_file_over_the_size_cap()
    {
        var v = Create(new UploadOptions { MaxFileBytes = 16 });
        var big = new byte[64];
        big[0] = 0x89; big[1] = 0x50; big[2] = 0x4E; big[3] = 0x47; // png-ish prefix, but too big
        var r = v.Validate(Request(Inline("image/png", big)));
        Assert.False(r.Ok);
    }

    [Fact]
    public void Rejects_total_over_the_aggregate_cap()
    {
        var v = Create(new UploadOptions { MaxFileBytes = 1024, MaxTotalBytes = 16 });
        var r = v.Validate(Request(Inline("image/png", Png), Inline("image/png", Png)));
        Assert.False(r.Ok);
        Assert.Contains("too large together", r.Message);
    }

    [Fact]
    public void Rejects_a_disallowed_part_shape()
    {
        var part = new JsonObject
        {
            ["fileData"] = new JsonObject { ["fileUri"] = "https://evil.example/x" },
        };
        var r = Create().Validate(Request(part));
        Assert.False(r.Ok);
        Assert.Contains("not allowed", r.Message);
    }

    [Fact]
    public void Rejects_oversize_payload_before_decoding()
    {
        // A huge base64 string must be refused on its length, without allocating the decode
        var v = Create(new UploadOptions { MaxFileBytes = 1024 });
        var huge = new string('A', 1024 * 1024);
        var part = new JsonObject
        {
            ["inlineData"] = new JsonObject { ["mimeType"] = "image/png", ["data"] = huge },
        };
        var r = v.Validate(Request(part));
        Assert.False(r.Ok);
    }

    [Fact]
    public void Also_checks_files_hidden_in_the_system_instruction()
    {
        var req = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(Inline("image/png", Exe)),
            },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(Text("hi")),
            }),
        };
        Assert.False(Create().Validate(req).Ok);
    }

    [Fact]
    public void Allows_a_plain_text_only_request()
    {
        var r = Create().Validate(Request(Text("no files here")));
        Assert.True(r.Ok);
    }
}
