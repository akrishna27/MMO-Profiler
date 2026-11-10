using System.Text.Json;

namespace MmoProfiler.Core.Ollama;

/// <summary>
/// Turns the model's classification reply (the JSON text inside the chat reply) into one
/// <see cref="MessageAnalysis"/> per input message. Lenient: bad values are dropped.
/// </summary>
public static class AnalysisResponseParser
{
    /// <param name="json">The JSON text produced by the model.</param>
    /// <param name="inputCount">How many messages were sent; valid ids are 0 to inputCount - 1.</param>
    /// <param name="labels">Resolves label names (and aliases) to canonical ids.</param>
    /// <param name="options">Supplies the per-message maximums and the confidence threshold.</param>
    /// <exception cref="AnalyzerUnavailableException">The text is not valid JSON or has no <c>results</c> array.</exception>
    public static IReadOnlyList<MessageAnalysis> Parse(
        string json, int inputCount, LabelSet labels, ProfilerOptions options)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(options);

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

                var intents = ParseLabels(item, "intents", labels.ResolveIntent, labels.ReactionIntent,
                    options.MaxIntentsPerMessage, options.LabelConfidenceThreshold);
                var tones = ParseLabels(item, "tones", labels.ResolveTone, null,
                    options.MaxTonesPerMessage, options.LabelConfidenceThreshold);

                output[id] = new MessageAnalysis(intents, tones, ParseLanguage(item), ParseTopics(item))
                {
                    Flags = ParseFlags(item, labels.ResolveFlag),
                };
            }
        }

        return output;
    }

    /// <summary>
    /// Each entry is a string (a label with no confidence) or an object with a <c>label</c> and an
    /// optional numeric <c>confidence</c>. Resolve, drop unresolved, drop the excluded id (the reaction
    /// intent, which only code assigns), merge repeats.
    /// If no entry carries a numeric confidence, keep the first <paramref name="max"/> in the model's
    /// order. Otherwise clamp confidences (a missing one counts as 0), keep each label's highest, order
    /// strongest first (ties keep the model's order), keep those at or above the threshold up to the
    /// maximum, and if none reach it keep the single strongest.
    /// </summary>
    private static IReadOnlyList<string> ParseLabels(
        JsonElement item, string property, Func<string?, string?> resolve, string? exclude, int max, double threshold)
    {
        if (!item.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        // Insertion order is the model's order, which breaks confidence ties.
        var entries = new List<(string Id, double Confidence)>();
        var anyConfidence = false;
        foreach (var entry in list.EnumerateArray())
        {
            string? name;
            double? confidence = null;
            if (entry.ValueKind == JsonValueKind.String)
            {
                name = entry.GetString();
            }
            else if (entry.ValueKind == JsonValueKind.Object
                     && entry.TryGetProperty("label", out var labelProp) && labelProp.ValueKind == JsonValueKind.String)
            {
                name = labelProp.GetString();
                if (entry.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var d))
                    confidence = double.IsNaN(d) ? 0.0 : Math.Clamp(d, 0.0, 1.0);
            }
            else
            {
                continue;
            }

            var id = resolve(name);
            if (id is null) continue;
            if (exclude is not null && string.Equals(id, exclude, StringComparison.Ordinal)) continue;

            if (confidence is not null) anyConfidence = true;
            var value = confidence ?? 0.0;
            var existing = entries.FindIndex(e => string.Equals(e.Id, id, StringComparison.Ordinal));
            if (existing < 0) entries.Add((id, value));
            else if (value > entries[existing].Confidence) entries[existing] = (id, value);
        }

        if (entries.Count == 0) return Array.Empty<string>();

        var limit = Math.Max(1, max);
        if (!anyConfidence) return entries.Take(limit).Select(e => e.Id).ToList();

        var ordered = entries.OrderByDescending(e => e.Confidence).ToList(); // stable: ties keep the model's order
        var kept = ordered.Where(e => e.Confidence >= threshold).Take(limit).Select(e => e.Id).ToList();
        if (kept.Count == 0) kept.Add(ordered[0].Id);
        return kept;
    }

    /// <summary>Resolve, drop unresolved and repeated flags. A missing list is no flags.</summary>
    private static IReadOnlyList<string> ParseFlags(JsonElement item, Func<string?, string?> resolve)
    {
        if (!item.TryGetProperty("flags", out var list) || list.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var flags = new List<string>();
        foreach (var entry in list.EnumerateArray())
        {
            var name = entry.ValueKind switch
            {
                JsonValueKind.String => entry.GetString(),
                JsonValueKind.Object when entry.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String => l.GetString(),
                _ => null,
            };
            var id = resolve(name);
            if (id is not null && !flags.Contains(id, StringComparer.Ordinal)) flags.Add(id);
        }
        return flags;
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
}
