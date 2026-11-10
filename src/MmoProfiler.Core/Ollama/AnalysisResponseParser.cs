using System.Text.Json;

namespace MmoProfiler.Core.Ollama;

/// <summary>
/// Turns the model's classification reply (the JSON text inside the chat reply) into one
/// <see cref="MessageAnalysis"/> per input message. Lenient: bad values become <c>Unknown</c>.
/// </summary>
public static class AnalysisResponseParser
{
    private static readonly Dictionary<string, Tone> Tones = NamesOf<Tone>();
    private static readonly Dictionary<string, Intent> Intents = NamesOf<Intent>();

    /// <param name="json">The JSON text produced by the model.</param>
    /// <param name="inputCount">How many messages were sent; valid ids are 0 to inputCount - 1.</param>
    /// <exception cref="AnalyzerUnavailableException">The text is not valid JSON or has no <c>results</c> array.</exception>
    public static IReadOnlyList<MessageAnalysis> Parse(string json, int inputCount)
    {
        var output = new MessageAnalysis[Math.Max(0, inputCount)];
        Array.Fill(output, MessageAnalysis.Unknown);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json ?? "");
        }
        catch (JsonException ex)
        {
            throw new AnalyzerUnavailableException("The model reply was not valid JSON.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                throw new AnalyzerUnavailableException("The model reply had no results array.");
            }

            var seen = new HashSet<int>();
            foreach (var item in results.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var idProp)
                    || idProp.ValueKind != JsonValueKind.Number
                    || !idProp.TryGetInt32(out var id)) continue;
                if (id < 0 || id >= output.Length) continue; // out of range
                if (!seen.Add(id)) continue;                 // duplicate: the first result for an id wins

                output[id] = new MessageAnalysis(
                    Lookup(item, "tone", Tones),
                    Lookup(item, "intent", Intents),
                    ParseLanguage(item),
                    ParseTopics(item));
            }
        }

        return output;
    }

    private static T Lookup<T>(JsonElement item, string property, Dictionary<string, T> names) where T : struct, Enum
    {
        if (item.TryGetProperty(property, out var p)
            && p.ValueKind == JsonValueKind.String
            && p.GetString()?.Trim() is { } text
            && names.TryGetValue(text, out var value))
        {
            return value;
        }
        return default; // Unknown is 0 in both enums
    }

    private static string ParseLanguage(JsonElement item)
    {
        if (!item.TryGetProperty("language", out var p) || p.ValueKind != JsonValueKind.String) return "und";
        var text = p.GetString()?.Trim().ToLowerInvariant();
        if (text is null || text.Length is < 2 or > 3) return "und";
        foreach (var c in text)
            if (c is < 'a' or > 'z') return "und";
        return text;
    }

    private static IReadOnlyList<string> ParseTopics(JsonElement item)
    {
        if (!item.TryGetProperty("topics", out var p) || p.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var topics = new List<string>();
        foreach (var t in p.EnumerateArray())
            if (t.ValueKind == JsonValueKind.String && t.GetString() is { } s) topics.Add(s);
        return topics;
    }

    // Names only, case-insensitive. Enum.TryParse would also accept numbers such as "3".
    private static Dictionary<string, T> NamesOf<T>() where T : struct, Enum =>
        Enum.GetValues<T>().ToDictionary(v => v.ToString(), v => v, StringComparer.OrdinalIgnoreCase);
}
