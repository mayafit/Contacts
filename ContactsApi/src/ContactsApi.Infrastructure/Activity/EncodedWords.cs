/**
 * @fileoverview Decodes RFC 2047 encoded-words in mail headers (=?UTF-8?B?...?=)
 * @module ContactsApi.Infrastructure.Activity
 */
using System.Text;
using System.Text.RegularExpressions;

namespace ContactsApi.Infrastructure.Activity;

/// <summary>
/// Display names in mail headers arrive encoded when they are not ASCII, e.g.
/// <c>=?UTF-8?B?157XldeY15k=?= &lt;m@x.com&gt;</c> for a Hebrew name, often in
/// windows-1255 or iso-8859-8 for older Israeli mail. Gmail hands headers over
/// as written, so names have to be decoded before they can be searched.
/// </summary>
public static class EncodedWords
{
    private static readonly Regex Word = new(
        @"=\?([^?\s]+)\?([BbQq])\?([^?\s]*)\?=",
        RegexOptions.Compiled);

    // Whitespace between two encoded-words is not part of the text (RFC 2047 §6.2)
    private static readonly Regex Between = new(@"(\?=)\s+(=\?)", RegexOptions.Compiled);

    static EncodedWords() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// Decodes every encoded-word in a header value; text that is not encoded,
    /// and words that cannot be decoded, are left as they are.
    /// </summary>
    /// <param name="value">Header value</param>
    /// <returns>Decoded value</returns>
    public static string Decode(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("=?", StringComparison.Ordinal))
        {
            return value ?? string.Empty;
        }

        return Word.Replace(Between.Replace(value, "$1$2"), m =>
        {
            try
            {
                var charset = m.Groups[1].Value.Split('*')[0];
                var encoding = Encoding.GetEncoding(charset);
                var text = m.Groups[3].Value;
                var bytes = m.Groups[2].Value is "B" or "b" ? Convert.FromBase64String(text) : DecodeQ(text);
                return encoding.GetString(bytes);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                return m.Value;
            }
        });
    }

    /// <summary>"Q" encoding: _ is a space, =XX is a byte</summary>
    private static byte[] DecodeQ(string text)
    {
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '_')
            {
                bytes.Add((byte)' ');
            }
            else if (c == '=' && i + 2 < text.Length
                     && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.Add((byte)c);
            }
        }

        return bytes.ToArray();
    }
}
