/**
 * @fileoverview Validates files attached to an assistant message before they are
 * forwarded to Gemini. The browser cannot be trusted, so this is the real
 * boundary: it allowlists MIME types, checks the bytes actually match the claimed
 * type (magic bytes / text decoding), and bounds size and count so a hostile or
 * careless upload cannot exhaust memory.
 * @module ContactsApi.WebApi.Services
 */
using System.Text;
using System.Text.Json.Nodes;

namespace ContactsApi.WebApi.Services;

/// <summary>Limits and allowlist for assistant file uploads (config section "Gemini:Uploads")</summary>
public class UploadOptions
{
    /// <summary>Attachments allowed in one request</summary>
    public int MaxFiles { get; set; } = 5;

    /// <summary>Largest single file after base64 decoding</summary>
    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Largest total of all files after decoding. Kept so the base64-encoded body
    /// (~1.37x) stays under the nginx 20MB client_max_body_size and Gemini's ~20MB
    /// inline cap.
    /// </summary>
    public long MaxTotalBytes { get; set; } = 13 * 1024 * 1024;

    /// <summary>
    /// Allowed MIME types. Empty means "use the built-in default set". A deployment
    /// can narrow (or widen) this through configuration without a code change.
    /// </summary>
    public List<string> AllowedMimeTypes { get; set; } = new();
}

/// <summary>Why an upload was refused, or null when it is fine</summary>
public record UploadCheck(bool Ok, string? Message)
{
    /// <summary>A passing result</summary>
    public static readonly UploadCheck Pass = new(true, null);

    /// <summary>A failing result with a user-facing reason</summary>
    public static UploadCheck Fail(string message) => new(false, message);
}

/// <summary>
/// Walks a generateContent request and validates every attached file (inlineData
/// part). Nothing is written to disk or executed here; the bytes are only
/// inspected and then left in place to be forwarded to Gemini.
/// </summary>
public class UploadValidator
{
    private readonly UploadOptions _options;
    private readonly HashSet<string> _allowed;

    /// <summary>The MIME types accepted by default (Gemini reads all of these natively)</summary>
    public static readonly string[] DefaultAllowed =
    {
        // Images
        "image/png", "image/jpeg", "image/webp", "image/gif", "image/heic", "image/heif",
        // PDF
        "application/pdf",
        // Microsoft Office (OOXML)
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.template",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        // Microsoft Office (legacy OLE)
        "application/msword", "application/vnd.ms-excel", "application/vnd.ms-powerpoint",
        // OpenDocument
        "application/vnd.oasis.opendocument.text",
        "application/vnd.oasis.opendocument.spreadsheet",
        "application/vnd.oasis.opendocument.presentation",
        // Text
        "text/plain", "text/markdown", "text/csv", "text/html", "text/xml",
        "application/xml", "application/json",
    };

    /// <summary>Part keys that are legitimate in a request from the assistant</summary>
    private static readonly HashSet<string> AllowedPartKeys = new(StringComparer.Ordinal)
    {
        "text", "thought", "thoughtSignature", "functionCall", "functionResponse",
        "inlineData", "inline_data",
    };

    /// <summary>Creates the validator</summary>
    public UploadValidator(Microsoft.Extensions.Options.IOptions<UploadOptions> options)
    {
        _options = options.Value;
        _allowed = new HashSet<string>(
            _options.AllowedMimeTypes.Count > 0 ? _options.AllowedMimeTypes : DefaultAllowed,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The MIME types this validator accepts (for the client's file picker)</summary>
    public IReadOnlyCollection<string> Allowed => _allowed;

    /// <summary>
    /// Validates the files in a request. Checks the parts of both the system
    /// instruction and the conversation so a large payload cannot be smuggled in
    /// through either.
    /// </summary>
    /// <param name="request">generateContent request</param>
    /// <returns>Pass, or a failure with the reason to show the user</returns>
    public UploadCheck Validate(JsonObject request)
    {
        var state = new Counters();

        if (request["systemInstruction"]?["parts"] is JsonArray sys)
        {
            var c = ValidateParts(sys, state);
            if (!c.Ok)
            {
                return c;
            }
        }

        foreach (var content in request["contents"] as JsonArray ?? new JsonArray())
        {
            if (content?["parts"] is JsonArray parts)
            {
                var c = ValidateParts(parts, state);
                if (!c.Ok)
                {
                    return c;
                }
            }
        }

        return UploadCheck.Pass;
    }

    private sealed class Counters
    {
        public int Files;
        public long TotalBytes;
    }

    private UploadCheck ValidateParts(JsonArray parts, Counters state)
    {
        foreach (var part in parts)
        {
            if (part is not JsonObject obj)
            {
                continue;
            }

            // Reject any part shape we do not explicitly allow (e.g. fileData/fileUri,
            // executableCode) so the model can never be handed a server-fetched URL
            // or other unvetted content.
            foreach (var kv in obj)
            {
                if (!AllowedPartKeys.Contains(kv.Key))
                {
                    return UploadCheck.Fail($"That attachment type is not allowed ('{kv.Key}').");
                }
            }

            var inline = obj["inlineData"] as JsonObject ?? obj["inline_data"] as JsonObject;
            if (inline == null)
            {
                continue;
            }

            var check = ValidateInline(inline, state);
            if (!check.Ok)
            {
                return check;
            }
        }

        return UploadCheck.Pass;
    }

    private UploadCheck ValidateInline(JsonObject inline, Counters state)
    {
        state.Files += 1;
        if (state.Files > _options.MaxFiles)
        {
            return UploadCheck.Fail($"Attach at most {_options.MaxFiles} files per message.");
        }

        var mime = (inline["mimeType"]?.GetValue<string>() ?? inline["mime_type"]?.GetValue<string>() ?? string.Empty).Trim();
        // Drop any parameters (e.g. "text/plain; charset=utf-8") before matching
        var bare = mime.Split(';', 2)[0].Trim();
        if (bare.Length == 0 || !_allowed.Contains(bare))
        {
            return UploadCheck.Fail($"Files of type '{(bare.Length == 0 ? "unknown" : bare)}' are not supported.");
        }

        var data = inline["data"]?.GetValue<string>();
        if (string.IsNullOrEmpty(data))
        {
            return UploadCheck.Fail("An attached file was empty.");
        }

        // Bound memory before decoding: base64 of N bytes is ~4/3 N characters.
        if ((long)data.Length > (_options.MaxFileBytes / 3 + 1) * 4 + 4)
        {
            return UploadCheck.Fail($"Each file must be under {_options.MaxFileBytes / (1024 * 1024)} MB.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(data);
        }
        catch (FormatException)
        {
            return UploadCheck.Fail("An attached file was not encoded correctly.");
        }

        if (bytes.Length == 0)
        {
            return UploadCheck.Fail("An attached file was empty.");
        }

        if (bytes.Length > _options.MaxFileBytes)
        {
            return UploadCheck.Fail($"Each file must be under {_options.MaxFileBytes / (1024 * 1024)} MB.");
        }

        state.TotalBytes += bytes.Length;
        if (state.TotalBytes > _options.MaxTotalBytes)
        {
            return UploadCheck.Fail($"The attachments are too large together (limit {_options.MaxTotalBytes / (1024 * 1024)} MB).");
        }

        return ContentMatchesType(bare, bytes)
            ? UploadCheck.Pass
            : UploadCheck.Fail($"An attached file's contents do not look like a valid {Describe(bare)}.");
    }

    /// <summary>True when the bytes match the signature expected for the MIME type</summary>
    public static bool ContentMatchesType(string mime, byte[] b)
    {
        if (mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            mime is "application/json" or "application/xml")
        {
            return IsText(b);
        }

        return mime switch
        {
            "image/png" => Starts(b, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A),
            "image/jpeg" => Starts(b, 0xFF, 0xD8, 0xFF),
            "image/gif" => Ascii(b, 0, "GIF87a") || Ascii(b, 0, "GIF89a"),
            "image/webp" => Ascii(b, 0, "RIFF") && Ascii(b, 8, "WEBP"),
            "image/heic" or "image/heif" => Ascii(b, 4, "ftyp"),
            "application/pdf" => Ascii(b, 0, "%PDF-"),
            // OOXML and OpenDocument are ZIP containers
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" or
            "application/vnd.openxmlformats-officedocument.wordprocessingml.template" or
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" or
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" or
            "application/vnd.oasis.opendocument.text" or
            "application/vnd.oasis.opendocument.spreadsheet" or
            "application/vnd.oasis.opendocument.presentation" => IsZip(b),
            // Legacy Office is an OLE compound file
            "application/msword" or "application/vnd.ms-excel" or "application/vnd.ms-powerpoint" =>
                Starts(b, 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1),
            _ => false,
        };
    }

    private static bool IsZip(byte[] b) =>
        Starts(b, 0x50, 0x4B, 0x03, 0x04) || // normal
        Starts(b, 0x50, 0x4B, 0x05, 0x06) || // empty archive
        Starts(b, 0x50, 0x4B, 0x07, 0x08);   // spanned

    private static bool Starts(byte[] b, params byte[] sig)
    {
        if (b.Length < sig.Length)
        {
            return false;
        }

        for (var i = 0; i < sig.Length; i++)
        {
            if (b[i] != sig[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool Ascii(byte[] b, int offset, string sig)
    {
        if (b.Length < offset + sig.Length)
        {
            return false;
        }

        for (var i = 0; i < sig.Length; i++)
        {
            if (b[offset + i] != (byte)sig[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A file passes as text when it decodes as UTF-8 (BOM allowed) with no NUL
    /// byte: that rejects binaries mislabelled as text without parsing the content.
    /// </summary>
    private static bool IsText(byte[] b)
    {
        foreach (var t in b)
        {
            if (t == 0)
            {
                return false;
            }
        }

        try
        {
            // Throws on invalid UTF-8 sequences
            _ = new UTF8Encoding(false, true).GetString(b);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string Describe(string mime) => mime switch
    {
        "application/pdf" => "PDF",
        _ when mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => "image",
        _ when mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) => "text file",
        "application/json" => "JSON file",
        "application/xml" => "XML file",
        _ => "document",
    };
}
