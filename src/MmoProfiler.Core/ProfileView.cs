namespace MmoProfiler.Core;

/// <summary>A tone or intent with its count and its share of the analysed messages.</summary>
public sealed record CountEntry(string Name, long Count, double Percent);

public sealed record TopicEntry(string Topic, long Count);

/// <summary>Read-only, UI-ready projection of a <see cref="Profile"/>. Pure, so it is testable without a host.</summary>
public sealed class ProfileView
{
    public const int TopTopicLimit = 10;
    public const int MostActiveLimit = 3;

    private static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    public required CharacterKey Key { get; init; }
    public required DateTimeOffset FirstSeenUtc { get; init; }
    public required DateTimeOffset LastSeenUtc { get; init; }
    public required long MessageCount { get; init; }
    public required long AnalyzedCount { get; init; }
    public required string? Summary { get; init; }
    public required string UserNotes { get; init; }
    public required IReadOnlyList<CountEntry> Tones { get; init; }
    public required IReadOnlyList<CountEntry> Intents { get; init; }

    /// <summary>Share of analysed messages that carried the reaction intent (emoticon-only messages), to one decimal place.</summary>
    public required double ReactionPercent { get; init; }
    public required IReadOnlyList<TopicEntry> TopTopics { get; init; }

    /// <summary>[day, hour] in local time. Day 0 is Sunday, as in <see cref="DayOfWeek"/>.</summary>
    public required int[,] HeatMap { get; init; }

    /// <summary>The busiest hours of the week in local time, for example "Sat 21:00". Empty when there is no activity.</summary>
    public required IReadOnlyList<string> MostActive { get; init; }

    /// <param name="p">The profile to project.</param>
    /// <param name="localOffset">Offset of local time from UTC; rounded to the nearest whole hour (half away from zero).</param>
    /// <param name="labels">The current labels. Aliases are merged; counts under retired ids stay in storage but are not shown.</param>
    public static ProfileView From(Profile p, TimeSpan localOffset, LabelSet labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        int shift = (int)Math.Round(localOffset.TotalHours, MidpointRounding.AwayFromZero);
        var heat = BuildHeatMap(p.ActivityByHourOfWeekUtc, shift);
        var intents = Rank(p.IntentCounts, labels.Intents, labels.ResolveIntent, p.AnalyzedCount);

        return new ProfileView
        {
            Key = p.Key,
            FirstSeenUtc = p.FirstSeenUtc,
            LastSeenUtc = p.LastSeenUtc,
            MessageCount = p.MessageCount,
            AnalyzedCount = p.AnalyzedCount,
            Summary = p.Summary,
            UserNotes = p.UserNotes,
            Tones = Rank(p.ToneCounts, labels.Tones, labels.ResolveTone, p.AnalyzedCount),
            Intents = intents,
            ReactionPercent = intents.FirstOrDefault(e => e.Name == labels.ReactionIntent)?.Percent ?? 0,
            TopTopics = p.TopicCounts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(TopTopicLimit)
                .Select(kv => new TopicEntry(kv.Key, kv.Value))
                .ToList(),
            HeatMap = heat,
            MostActive = BuildMostActive(heat),
        };
    }

    // Merges counts through the aliases, keeps only labels in the current set, sorts by count
    // descending with ties in label-file order. Percent is the share of analysed messages that
    // carried the label, so the percentages need not sum to 100.
    private static List<CountEntry> Rank(
        Dictionary<string, long>? counts, IReadOnlyList<LabelDefinition> order,
        Func<string?, string?> resolve, long analyzed)
    {
        var merged = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (name, count) in counts ?? new Dictionary<string, long>())
        {
            if (count <= 0 || resolve(name) is not { } id) continue;
            merged[id] = merged.GetValueOrDefault(id) + count;
        }

        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++) position[order[i].Id] = i;

        return merged
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => position[kv.Key])
            .Select(kv => new CountEntry(
                kv.Key,
                kv.Value,
                analyzed > 0 ? Math.Round(kv.Value * 100.0 / analyzed, 1, MidpointRounding.AwayFromZero) : 0))
            .ToList();
    }

    private static int[,] BuildHeatMap(int[]? utcHours, int shiftHours)
    {
        var map = new int[7, 24];
        int n = Math.Min(utcHours?.Length ?? 0, Profile.HoursPerWeek);
        for (int i = 0; i < n; i++)
        {
            int local = ((i + shiftHours) % Profile.HoursPerWeek + Profile.HoursPerWeek) % Profile.HoursPerWeek;
            map[local / 24, local % 24] = utcHours![i];
        }
        return map;
    }

    private static List<string> BuildMostActive(int[,] heat)
    {
        var cells = new List<(int Day, int Hour, int Count)>();
        for (int d = 0; d < 7; d++)
            for (int h = 0; h < 24; h++)
                if (heat[d, h] > 0)
                    cells.Add((d, h, heat[d, h]));

        return cells
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Day * 24 + c.Hour)
            .Take(MostActiveLimit)
            .Select(c => $"{DayNames[c.Day]} {c.Hour:00}:00")
            .ToList();
    }
}
