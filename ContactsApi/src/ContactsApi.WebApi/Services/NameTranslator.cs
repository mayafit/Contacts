/**
 * @fileoverview A Hebrew name's Latin spelling from Google Cloud Translation (AD-43 rule 4):
 * the name framed as "שמי …" ("My name is …") so it is read as a name, not as words
 * @module ContactsApi.WebApi.Services
 */
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ContactsApi.WebApi.Services;

/// <summary>Google Cloud Translation settings (GoogleTranslate__ApiKey comes from Vault)</summary>
public class GoogleTranslateOptions
{
    /// <summary>API key of a project with the Cloud Translation API enabled</summary>
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>The phonetic fill's Translation call</summary>
public class NameTranslator
{
    /// <summary>"My name is" in Hebrew: a name alone is translated as words ("טל שחר" → "dawn dew")</summary>
    public const string Frame = "שמי ";

    private const string Endpoint = "https://translation.googleapis.com/language/translate/v2";
    private static readonly Regex Answer = new(@"^\s*my name is\s+(.+?)[\s.!]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Spelling = new(@"^[A-Za-z][A-Za-z'\-]{0,39}$", RegexOptions.Compiled);

    private readonly IHttpClientFactory _http;
    private readonly GoogleTranslateOptions _options;

    /// <summary>Creates the translator</summary>
    public NameTranslator(IHttpClientFactory http, Microsoft.Extensions.Options.IOptions<GoogleTranslateOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    /// <summary>A key is configured</summary>
    public bool Enabled => !string.IsNullOrEmpty(_options.ApiKey);

    /// <summary>
    /// The name's words, translated in the frame: word i of the answer spells word i of
    /// the name, when the answer has as many words; otherwise nothing
    /// </summary>
    /// <param name="name">The name's words in order (first, middle, last)</param>
    /// <param name="ct">Cancellation</param>
    /// <returns>Hebrew word → spelling</returns>
    /// <exception cref="HttpRequestException">Translation refused or unreachable</exception>
    public async Task<Dictionary<string, string>> SpellAsync(IReadOnlyList<string> name, CancellationToken ct)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var body = new JsonObject { ["q"] = Frame + string.Join(' ', name), ["source"] = "he", ["target"] = "en", ["format"] = "text" };
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(body) };
        // The key in a header, never in a URL that could be logged
        request.Headers.Add("X-goog-api-key", _options.ApiKey);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Google Translate answered {(int)response.StatusCode}");
        }

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        var first = (json?["data"]?["translations"] as JsonArray)?.FirstOrDefault();
        var text = (first?["translatedText"] as JsonValue)?.TryGetValue<string>(out var t) == true ? t : null;
        return Align(name, text);
    }

    /// <summary>
    /// The spellings in a framed answer: "My name is Tal Shahar" for ["טל", "שחר"] gives
    /// טל → Tal, שחר → Shahar. Another sentence, another number of words, or a word that
    /// is not Latin letters gives nothing for the name (no guess at which word is which).
    /// </summary>
    /// <param name="name">The name's words sent</param>
    /// <param name="translated">The answer</param>
    /// <returns>Hebrew word → spelling</returns>
    public static Dictionary<string, string> Align(IReadOnlyList<string> name, string? translated)
    {
        var result = new Dictionary<string, string>();
        var match = Answer.Match(System.Net.WebUtility.HtmlDecode(translated ?? string.Empty));
        if (!match.Success)
        {
            return result;
        }

        var words = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != name.Count || !words.All(Spelling.IsMatch))
        {
            return result;
        }

        for (var i = 0; i < name.Count; i++)
        {
            if (Regex.IsMatch(name[i], @"[א-ת]") && !result.ContainsKey(name[i]))
            {
                result[name[i]] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
            }
        }

        return result;
    }
}
