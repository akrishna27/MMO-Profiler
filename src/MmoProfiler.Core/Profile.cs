namespace MmoProfiler.Core;

public sealed class Profile
{
    public const int HoursPerWeek = 168;

    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public CharacterKey Key { get; set; } = null!;
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public long MessageCount { get; set; }
    public long AnalyzedCount { get; set; }
    public Dictionary<ChatChannel, long> ChannelCounts { get; set; } = new();
    public Dictionary<Tone, long> ToneCounts { get; set; } = new();
    public Dictionary<Intent, long> IntentCounts { get; set; } = new();
    public Dictionary<string, long> LanguageCounts { get; set; } = new();
    public Dictionary<string, long> TopicCounts { get; set; } = new();
    public int[] ActivityByHourOfWeekUtc { get; set; } = new int[HoursPerWeek];
    public string? Summary { get; set; }
    public DateTimeOffset? SummaryUpdatedUtc { get; set; }
    public int MessagesSinceSummary { get; set; }
    public string UserNotes { get; set; } = "";

    public static Profile Create(CharacterKey key) => new() { Key = key, Id = key.Id };
}
