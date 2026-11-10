using System.Text;

namespace MmoProfiler.Core;

/// <summary>Pure functions that mutate a <see cref="Profile"/>. Callers serialise access.</summary>
public static class ProfileUpdater
{
    public const int MaxTopicLength = 40;
    public const int MaxTopicsPerMessage = 3;
    // Well above the ten topics the UI shows, so new interests can still enter a long-lived profile.
    public const int MaxTrackedTopics = 200;
    public const int MaxSummaryLength = 600;

    public static void RecordMessage(Profile p, ChatMessage m)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(m);

        var ts = m.TimestampUtc;

        // A fresh profile has default timestamps; treat that as "unset" rather than as the earliest value.
        if (p.MessageCount == 0 || p.FirstSeenUtc == default || ts < p.FirstSeenUtc) p.FirstSeenUtc = ts;
        if (p.MessageCount == 0 || ts > p.LastSeenUtc) p.LastSeenUtc = ts;

        p.MessageCount++;
        p.MessagesSinceSummary++;
        Increment(p.ChannelCounts, m.Channel);

        if (p.ActivityByHourOfWeekUtc is not { Length: Profile.HoursPerWeek })
        {
            var resized = new int[Profile.HoursPerWeek];
            if (p.ActivityByHourOfWeekUtc is not null)
                Array.Copy(p.ActivityByHourOfWeekUtc, resized, Math.Min(p.ActivityByHourOfWeekUtc.Length, resized.Length));
            p.ActivityByHourOfWeekUtc = resized;
        }

        var utc = ts.UtcDateTime;
        p.ActivityByHourOfWeekUtc[(int)utc.DayOfWeek * 24 + utc.Hour]++;
    }

    public static void ApplyAnalysis(Profile p, MessageAnalysis a)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(a);

        if (!a.HasLabels) return;

        p.AnalyzedCount++;
        // Each kept label counts once for the message, however many times it was listed.
        foreach (var intent in (a.Intents ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal))
            Increment(p.IntentCounts, intent);
        foreach (var tone in (a.Tones ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal))
            Increment(p.ToneCounts, tone);
        // "und" means undetermined (or an emoticon); it says nothing about the player's language.
        if (!string.IsNullOrWhiteSpace(a.Language) && a.Language != "und")
            Increment(p.LanguageCounts, a.Language);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in a.Topics ?? Array.Empty<string>())
        {
            if (seen.Count >= MaxTopicsPerMessage) break;
            var topic = raw is null ? null : CollapseWhitespace(raw).ToLowerInvariant();
            if (string.IsNullOrEmpty(topic) || topic.Length > MaxTopicLength) continue;
            if (seen.Add(topic)) Increment(p.TopicCounts, topic);
        }

        // Evict lowest counts; ties remove the alphabetically last key first.
        while (p.TopicCounts.Count > MaxTrackedTopics)
        {
            var victim = p.TopicCounts
                .OrderBy(kv => kv.Value)
                .ThenByDescending(kv => kv.Key, StringComparer.Ordinal)
                .First().Key;
            p.TopicCounts.Remove(victim);
        }
    }

    public static void ApplySummary(Profile p, string summary, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (summary is null) return;

        var cleaned = CollapseWhitespace(summary);
        if (cleaned.Length > MaxSummaryLength)
        {
            var cut = MaxSummaryLength;
            if (char.IsHighSurrogate(cleaned[cut - 1])) cut--; // do not split a surrogate pair
            cleaned = cleaned[..cut].TrimEnd();
        }
        if (cleaned.Length == 0) return;

        p.Summary = cleaned;
        p.SummaryUpdatedUtc = now;
        p.MessagesSinceSummary = 0;
    }

    /// <summary>
    /// Replaces every run of control characters and whitespace (including line breaks) with a single
    /// space and trims both ends, so words on separate lines are not glued together. Topics use it
    /// too: they become storage field names, which must not contain NUL (decision D21).
    /// </summary>
    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static void Increment<TKey>(Dictionary<TKey, long> dict, TKey key) where TKey : notnull
    {
        dict.TryGetValue(key, out var n);
        dict[key] = n + 1;
    }
}
