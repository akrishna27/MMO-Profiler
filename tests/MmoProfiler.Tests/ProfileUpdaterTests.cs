using MmoProfiler.Core;

namespace MmoProfiler.Tests;

public class ProfileUpdaterTests
{
    private static readonly CharacterKey Alice = CharacterKey.Create("ffxiv", "Alice Example", "Odin");
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 21, 30, 0, TimeSpan.Zero); // Saturday

    private static Profile NewProfile() => Profile.Create(Alice);

    private static ChatMessage Msg(DateTimeOffset at, ChatChannel ch = ChatChannel.Say, string text = "hi") =>
        new(Alice, ch, null, text, at);

    private static MessageAnalysis Analysis(
        string tone = "Friendly", string intent = "Socializing", string lang = "en", params string[] topics) =>
        new(intent is null ? Array.Empty<string>() : new[] { intent },
            tone is null ? Array.Empty<string>() : new[] { tone },
            lang, topics);

    [Fact]
    public void RecordMessage_IncrementsCountsAndChannel()
    {
        var p = NewProfile();

        ProfileUpdater.RecordMessage(p, Msg(T0, ChatChannel.Say));
        ProfileUpdater.RecordMessage(p, Msg(T0, ChatChannel.Say));
        ProfileUpdater.RecordMessage(p, Msg(T0, ChatChannel.Party));

        Assert.Equal(3, p.MessageCount);
        Assert.Equal(3, p.MessagesSinceSummary);
        Assert.Equal(2, p.ChannelCounts[ChatChannel.Say]);
        Assert.Equal(1, p.ChannelCounts[ChatChannel.Party]);
        Assert.Equal(0, p.AnalyzedCount);
    }

    [Fact]
    public void RecordMessage_SetsFirstAndLastSeen_OutOfOrderTimestamps()
    {
        var p = NewProfile();

        ProfileUpdater.RecordMessage(p, Msg(T0));
        ProfileUpdater.RecordMessage(p, Msg(T0.AddHours(5)));
        ProfileUpdater.RecordMessage(p, Msg(T0.AddHours(-3)));
        ProfileUpdater.RecordMessage(p, Msg(T0.AddHours(2)));

        Assert.Equal(T0.AddHours(-3), p.FirstSeenUtc);
        Assert.Equal(T0.AddHours(5), p.LastSeenUtc);
    }

    [Fact]
    public void RecordMessage_BucketsActivityByUtcHourOfWeek()
    {
        var p = NewProfile();
        // Saturday 21:30 UTC => 6 * 24 + 21 = 165. Supplied with a non-UTC offset to prove conversion.
        var saturdayEveningPlusTwo = new DateTimeOffset(2026, 11, 7, 23, 30, 0, TimeSpan.FromHours(2));
        var sundayMidnight = new DateTimeOffset(2026, 11, 8, 0, 5, 0, TimeSpan.Zero); // bucket 0

        ProfileUpdater.RecordMessage(p, Msg(T0));
        ProfileUpdater.RecordMessage(p, Msg(saturdayEveningPlusTwo));
        ProfileUpdater.RecordMessage(p, Msg(sundayMidnight));

        Assert.Equal(Profile.HoursPerWeek, p.ActivityByHourOfWeekUtc.Length);
        Assert.Equal(2, p.ActivityByHourOfWeekUtc[165]);
        Assert.Equal(1, p.ActivityByHourOfWeekUtc[0]);
        Assert.Equal(3, p.ActivityByHourOfWeekUtc.Sum());
    }

    [Fact]
    public void ApplyAnalysis_Unknown_DoesNothing()
    {
        var p = NewProfile();

        ProfileUpdater.ApplyAnalysis(p, MessageAnalysis.Unknown);
        ProfileUpdater.ApplyAnalysis(p, new MessageAnalysis(Array.Empty<string>(), Array.Empty<string>(), "en", new[] { "dungeons" }));

        Assert.Equal(0, p.AnalyzedCount);
        Assert.Empty(p.ToneCounts);
        Assert.Empty(p.IntentCounts);
        Assert.Empty(p.LanguageCounts);
        Assert.Empty(p.TopicCounts);
    }

    [Fact]
    public void ApplyAnalysis_IncrementsToneIntentLanguage()
    {
        var p = NewProfile();

        ProfileUpdater.ApplyAnalysis(p, Analysis("Friendly", "Socializing", "en"));
        ProfileUpdater.ApplyAnalysis(p, Analysis("Friendly", "Recruiting", "de"));
        ProfileUpdater.ApplyAnalysis(p, Analysis("Hostile", null!, "en")); // one side known is enough

        Assert.Equal(3, p.AnalyzedCount);
        Assert.Equal(2, p.ToneCounts["Friendly"]);
        Assert.Equal(1, p.ToneCounts["Hostile"]);
        Assert.Equal(1, p.IntentCounts["Socializing"]);
        Assert.Equal(1, p.IntentCounts["Recruiting"]);
        Assert.Equal(2, p.LanguageCounts["en"]);
        Assert.Equal(1, p.LanguageCounts["de"]);
    }

    [Fact]
    public void ApplyAnalysis_CountsEachKeptLabelOnce_AndAnalyzedOnce()
    {
        var p = NewProfile();
        var twoAndTwo = new MessageAnalysis(
            new[] { "Trading", "Advertising" }, new[] { "Friendly", "Humorous" }, "en", Array.Empty<string>());

        ProfileUpdater.ApplyAnalysis(p, twoAndTwo);
        ProfileUpdater.ApplyAnalysis(p, Analysis("Friendly", "Trading", "en"));
        // A label listed twice in one analysis still counts once for that message.
        ProfileUpdater.ApplyAnalysis(p, new MessageAnalysis(
            new[] { "Spam", "Spam" }, new[] { "Hostile" }, "en", Array.Empty<string>()));

        Assert.Equal(3, p.AnalyzedCount);
        Assert.Equal(2, p.IntentCounts["Trading"]);
        Assert.Equal(1, p.IntentCounts["Advertising"]);
        Assert.Equal(1, p.IntentCounts["Spam"]);
        Assert.Equal(2, p.ToneCounts["Friendly"]);
        Assert.Equal(1, p.ToneCounts["Humorous"]);
        Assert.Equal(1, p.ToneCounts["Hostile"]);
        Assert.Equal(3, p.LanguageCounts["en"]);
    }

    [Fact]
    public void ApplyAnalysis_NoLabels_DoesNothing()
    {
        var p = NewProfile();

        ProfileUpdater.ApplyAnalysis(p, new MessageAnalysis(
            Array.Empty<string>(), Array.Empty<string>(), "en", new[] { "raids" }));

        Assert.Equal(0, p.AnalyzedCount);
        Assert.Empty(p.IntentCounts);
        Assert.Empty(p.ToneCounts);
        Assert.Empty(p.LanguageCounts);
        Assert.Empty(p.TopicCounts);
    }

    [Fact]
    public void ApplyAnalysis_DoesNotCountUndLanguage()
    {
        var p = NewProfile();

        ProfileUpdater.ApplyAnalysis(p, Analysis("Friendly", "Reaction", "und"));
        ProfileUpdater.ApplyAnalysis(p, Analysis("Friendly", "Socializing", "en"));

        Assert.Equal(2, p.AnalyzedCount);
        Assert.False(p.LanguageCounts.ContainsKey("und"));
        Assert.Equal(1, p.LanguageCounts["en"]);
    }

    [Fact]
    public void ApplyAnalysis_NormalisesAndCapsTopicsPerMessage()
    {
        var p = NewProfile();
        var tooLong = new string('x', 41);

        ProfileUpdater.ApplyAnalysis(p, Analysis(
            topics: new[] { "  Dungeons ", "dungeons", "", "   ", tooLong, "Crafting", "Raids", "Housing" }));

        Assert.Equal(new[] { "crafting", "dungeons", "raids" }, p.TopicCounts.Keys.OrderBy(k => k));
        Assert.All(p.TopicCounts.Values, v => Assert.Equal(1, v));

        // Exactly 40 characters is allowed.
        var forty = new string('y', 40);
        ProfileUpdater.ApplyAnalysis(p, Analysis(topics: new[] { forty }));
        Assert.True(p.TopicCounts.ContainsKey(forty));
    }

    [Fact]
    public void ApplyAnalysis_ReplacesControlCharactersInTopics()
    {
        var p = NewProfile();

        ProfileUpdater.ApplyAnalysis(p, Analysis(
            topics: new[] { "raid\0night", "  Hard\t\r\n Mode  ", "\0\0", "\u0001" }));

        // NUL and other control characters become a single space; topics that were only control
        // characters are dropped as empty.
        Assert.Equal(new[] { "hard mode", "raid night" }, p.TopicCounts.Keys.OrderBy(k => k));
        Assert.DoesNotContain(p.TopicCounts.Keys, k => k.Any(char.IsControl));
    }

    [Fact]
    public void ApplyAnalysis_EvictsLowestTopics_WhenOverCap()
    {
        const int cap = ProfileUpdater.MaxTrackedTopics;
        Assert.Equal(200, cap);

        var p = NewProfile();
        for (var i = 0; i < cap; i++)
            p.TopicCounts[$"topic{i:000}"] = 5;
        p.TopicCounts["topic010"] = 1; // lowest
        p.TopicCounts["topic020"] = 1; // tied lowest, alphabetically later

        ProfileUpdater.ApplyAnalysis(p, Analysis(topics: new[] { "newcomer" }));

        // newcomer (count 1) is one over the cap; ties at 1 are topic010, topic020, newcomer -> alphabetically last is topic020.
        Assert.Equal(cap, p.TopicCounts.Count);
        Assert.False(p.TopicCounts.ContainsKey("topic020"));
        Assert.True(p.TopicCounts.ContainsKey("topic010"));
        Assert.True(p.TopicCounts.ContainsKey("newcomer"));

        ProfileUpdater.ApplyAnalysis(p, Analysis(topics: new[] { "another" }));

        // Now ties at 1 are another, newcomer, topic010 -> topic010 is last alphabetically.
        Assert.Equal(cap, p.TopicCounts.Count);
        Assert.False(p.TopicCounts.ContainsKey("topic010"));
        Assert.True(p.TopicCounts.ContainsKey("another"));
        Assert.True(p.TopicCounts.ContainsKey("newcomer"));
    }

    [Fact]
    public void ApplySummary_TruncatesStripsControlChars_AndResetsCounter()
    {
        var p = NewProfile();
        p.MessagesSinceSummary = 17;
        var now = T0.AddDays(1);
        var raw = "  \u0007Hello\u0000 world\r\n" + new string('a', 700) + "  ";

        ProfileUpdater.ApplySummary(p, raw, now);

        Assert.NotNull(p.Summary);
        Assert.Equal(600, p.Summary!.Length);
        Assert.StartsWith("Hello world aaaa", p.Summary);
        Assert.DoesNotContain(p.Summary, char.IsControl);
        Assert.Equal(now, p.SummaryUpdatedUtc);
        Assert.Equal(0, p.MessagesSinceSummary);
    }

    [Theory]
    [InlineData("Friendly.\nTalks about raids", "Friendly. Talks about raids")]
    [InlineData("Friendly.\r\n\r\nTalks\tabout   raids\n", "Friendly. Talks about raids")]
    [InlineData("Hel\u0000lo", "Hel lo")]
    public void ApplySummary_ReplacesLineBreaksAndControlCharsWithSingleSpace(string raw, string expected)
    {
        var p = NewProfile();

        ProfileUpdater.ApplySummary(p, raw, T0);

        Assert.Equal(expected, p.Summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u0001\u0002\r\n\t")]
    public void ApplySummary_Empty_LeavesProfileUnchanged(string summary)
    {
        var p = NewProfile();
        p.Summary = "existing";
        p.SummaryUpdatedUtc = T0;
        p.MessagesSinceSummary = 9;

        ProfileUpdater.ApplySummary(p, summary, T0.AddDays(1));

        Assert.Equal("existing", p.Summary);
        Assert.Equal(T0, p.SummaryUpdatedUtc);
        Assert.Equal(9, p.MessagesSinceSummary);
    }
}
