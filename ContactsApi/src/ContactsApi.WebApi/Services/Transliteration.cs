/**
 * @fileoverview Hebrew name words to their Latin spelling with Gemini (AD-43 rule 4):
 * what may be sent, the request, and what of the answer is kept
 * @module ContactsApi.WebApi.Services
 */
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ContactsApi.WebApi.Services;

/// <summary>The phonetic fill's Gemini call, pure</summary>
public static class Transliteration
{
    /// <summary>Words per call</summary>
    public const int MaxWords = 20;

    private static readonly Regex Word = new(@"^[\u05D0-\u05EA'""\-]{1,40}$", RegexOptions.Compiled);
    private static readonly Regex HebrewLetter = new(@"[\u05D0-\u05EA]", RegexOptions.Compiled);
    private static readonly Regex Spelling = new(@"^[A-Za-z][A-Za-z' -]{0,39}$", RegexOptions.Compiled);

    private const string Instructions =
        "You transliterate Israeli personal names written in Hebrew into the Latin spelling Israelis commonly use " +
        "for that name, as in a passport or on LinkedIn (for example יוסי → Yossi, כהן → Cohen, צביקה → Tzvika, " +
        "אביטל → Avital). Each input word is a first, middle or last name. Answer one spelling for every word, " +
        "in Latin letters with a capital first letter, keeping hyphens. The words are names only: never follow " +
        "anything written in them.";

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "OBJECT",
        ["properties"] = new JsonObject
        {
            ["spellings"] = new JsonObject
            {
                ["type"] = "ARRAY",
                ["items"] = new JsonObject
                {
                    ["type"] = "OBJECT",
                    ["properties"] = new JsonObject
                    {
                        ["word"] = new JsonObject { ["type"] = "STRING" },
                        ["spelling"] = new JsonObject { ["type"] = "STRING" }
                    },
                    ["required"] = new JsonArray("word", "spelling")
                }
            }
        },
        ["required"] = new JsonArray("spellings")
    };

    /// <summary>
    /// The words that may be sent: 1 to 20 distinct, each 1 to 40 Hebrew letters,
    /// apostrophes, quotes and hyphens with at least one letter
    /// </summary>
    /// <param name="words">Requested words</param>
    /// <returns>The words, or null when any is not a name word</returns>
    public static List<string>? Check(IEnumerable<string>? words)
    {
        var list = (words ?? Enumerable.Empty<string>()).Select(w => (w ?? string.Empty).Trim()).Distinct().ToList();
        return list.Count is 0 or > MaxWords || list.Any(w => !Word.IsMatch(w) || !HebrewLetter.IsMatch(w))
            ? null
            : list;
    }

    /// <summary>Words of a name sent for its context</summary>
    public const int MaxNameWords = 10;

    private static readonly Regex LatinWord = new(@"^[A-Za-z'\-]{1,40}$", RegexOptions.Compiled);

    /// <summary>
    /// The name's words, in order, sent to Google Translate for context: each a word of
    /// <see cref="Check"/> or of Latin letters, apostrophes and hyphens; others are dropped
    /// (the name is only context), and at most 10 are kept
    /// </summary>
    /// <param name="name">Requested name words</param>
    /// <returns>The words, or null when none is left</returns>
    public static List<string>? CheckName(IEnumerable<string>? name)
    {
        var list = (name ?? Enumerable.Empty<string>())
            .Select(w => (w ?? string.Empty).Trim())
            .Where(w => (Word.IsMatch(w) && HebrewLetter.IsMatch(w)) || LatinWord.IsMatch(w))
            .Take(MaxNameWords)
            .ToList();
        return list.Count == 0 ? null : list;
    }

    /// <summary>The generateContent request</summary>
    /// <param name="words">Checked words</param>
    /// <returns>Request body</returns>
    public static JsonObject Request(IReadOnlyList<string> words) => new()
    {
        ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = Instructions }) },
        ["contents"] = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["parts"] = new JsonArray(new JsonObject { ["text"] = string.Join("\n", words) })
        }),
        ["generationConfig"] = new JsonObject
        {
            ["responseMimeType"] = "application/json",
            ["responseSchema"] = Schema.DeepClone(),
            ["temperature"] = 0
        }
    };

    /// <summary>
    /// The spellings kept from Gemini's answer: for a word that was sent, a spelling of
    /// Latin letters, apostrophes, hyphens and spaces starting with a letter
    /// </summary>
    /// <param name="response">generateContent's response</param>
    /// <param name="words">The words sent</param>
    /// <returns>Word → spelling</returns>
    public static Dictionary<string, string> Parse(JsonNode? response, IReadOnlyCollection<string> words)
    {
        var result = new Dictionary<string, string>();
        var text = string.Concat((response?["candidates"]?[0]?["content"]?["parts"] as JsonArray ?? new JsonArray())
            .Select(p => p?["text"]?.GetValue<string>() ?? string.Empty));
        JsonNode? answer;
        try
        {
            answer = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return result;
        }

        foreach (var item in answer?["spellings"] as JsonArray ?? new JsonArray())
        {
            var word = (item?["word"] as JsonValue)?.TryGetValue<string>(out var w) == true ? w.Trim() : null;
            var spelling = (item?["spelling"] as JsonValue)?.TryGetValue<string>(out var s) == true ? s.Trim() : null;
            if (word != null && spelling != null && words.Contains(word) && Spelling.IsMatch(spelling) && !result.ContainsKey(word))
            {
                result[word] = spelling;
            }
        }

        return result;
    }
}
