using MmoProfiler.Core;

namespace MmoProfiler.Tests;

public class ProfileViewTests
{
    private static readonly CharacterKey Alice = CharacterKey.Create("ffxiv", "Alice Example", "Odin");

    private static Profile NewProfile() => Profile.Create(Alice);

    private static int Idx(DayOfWeek day, int hour) => (int)day * 24 + hour;

    private static readonly LabelSet Labels = LabelSet.Default;

    [Fact]
    public void Percentages_AreShareOfAnalysedMessages_AndMaySumOverOneHundred()
    {
        var p = NewProfile();
        p.AnalyzedCount = 30;
        p.ToneCounts["Friendly"] = 10;
        p.ToneCounts["Neutral"] = 10;
        p.ToneCounts["Hostile"] = 10;
        p.IntentCounts["Socializing"] = 20;
        p.IntentCounts["Trading"] = 7;

        var view = ProfileView.From(p, TimeSpan.Zero, Labels);

        Assert.Equal(3, view.Tones.Count);
        Assert.All(view.Tones, t => Assert.Equal(33.3, t.Percent));
        Assert.Equal("Socializing", view.Intents[0].Name);
        Assert.Equal(66.7, view.Intents[0].Percent);
        Assert.Equal(23.3, view.Intents[1].Percent);

        // Messages can carry two labels, so the shares of one list may add up to more than 100.
        var multi = NewProfile();
        multi.AnalyzedCount = 10;
        multi.ToneCounts["Friendly"] = 9;
        multi.ToneCounts["Humorous"] = 8;
        multi.ToneCounts["Excited"] = 5;
        var multiView = ProfileView.From(multi, TimeSpan.Zero, Labels);
        Assert.Equal([90.0, 80.0, 50.0], multiView.Tones.Select(t => t.Percent));
        Assert.True(multiView.Tones.Sum(t => t.Percent) > 100);
    }

    [Fact]
    public void LabelsNotInCurrentSet_AreOmitted()
    {
        var p = NewProfile();
        p.AnalyzedCount = 10;
        p.ToneCounts["Friendly"] = 4;
        p.ToneCounts["Unknown"] = 5;       // the old "unknown" value is not a label
        p.ToneCounts["Grumpy"] = 3;        // a retired id
        p.IntentCounts["Socializing"] = 2;
        p.IntentCounts["Bartering"] = 9;   // a retired id

        var view = ProfileView.From(p, TimeSpan.Zero, Labels);

        Assert.Equal(["Friendly"], view.Tones.Select(t => t.Name));
        Assert.Equal(["Socializing"], view.Intents.Select(i => i.Name));
        Assert.Equal(0, view.ReactionPercent);
        // The counts are untouched in the profile itself: they are only hidden.
        Assert.Equal(3, p.ToneCounts["Grumpy"]);
    }

    [Fact]
    public void Aliases_MergeCountsIntoTheCurrentLabel()
    {
        var labels = LabelSet.Parse("""
            {"version":1,
             "intents":[{"id":"Chat","definition":"d","examples":[]},{"id":"Reaction","definition":"d","examples":[]}],
             "tones":[{"id":"Warm","definition":"d","examples":[]},{"id":"Cold","definition":"d","examples":[]}],
             "aliases":{"intents":{"Socializing":"Chat"},"tones":{"Friendly":"Warm","Kind":"Warm"}},
             "reactionIntent":"Reaction","reactions":[]}
            """);
        var p = NewProfile();
        p.AnalyzedCount = 20;
        p.ToneCounts["Warm"] = 2;
        p.ToneCounts["Friendly"] = 3;
        p.ToneCounts["kind"] = 1;
        p.ToneCounts["Cold"] = 5;
        p.IntentCounts["Chat"] = 1;
        p.IntentCounts["Socializing"] = 4;

        var view = ProfileView.From(p, TimeSpan.Zero, labels);

        Assert.Equal(["Warm", "Cold"], view.Tones.Select(t => t.Name));
        Assert.Equal([6L, 5L], view.Tones.Select(t => t.Count));
        Assert.Equal(30.0, view.Tones[0].Percent);
        var chat = Assert.Single(view.Intents);
        Assert.Equal("Chat", chat.Name);
        Assert.Equal(5, chat.Count);
    }

    [Fact]
    public void Ties_FollowLabelFileOrder()
    {
        var p = NewProfile();
        p.AnalyzedCount = 10;
        // Set in reverse of the file order, with equal counts.
        foreach (var tone in Labels.Tones.Reverse()) p.ToneCounts[tone.Id] = 2;
        foreach (var intent in Labels.Intents.Reverse()) p.IntentCounts[intent.Id] = 1;

        var view = ProfileView.From(p, TimeSpan.Zero, Labels);

        Assert.Equal(Labels.Tones.Select(t => t.Id), view.Tones.Select(t => t.Name));
        Assert.Equal(Labels.Intents.Select(t => t.Id), view.Intents.Select(t => t.Name));
    }

    [Fact]
    public void ReactionPercent_IsShareOfAnalysedMessages()
    {
        var p = NewProfile();
        p.AnalyzedCount = 8;
        p.IntentCounts[Labels.ReactionIntent] = 3;
        p.IntentCounts["Socializing"] = 5;

        var view = ProfileView.From(p, TimeSpan.Zero, Labels);

        Assert.Equal(37.5, view.ReactionPercent);

        Assert.Equal(0, ProfileView.From(NewProfile(), TimeSpan.Zero, Labels).ReactionPercent);
    }

    [Fact]
    public void Percentages_SortedByCountDescending()
    {
        var p = NewProfile();
        p.AnalyzedCount = 10;
        p.ToneCounts["Sad"] = 1;
        p.ToneCounts["Excited"] = 6;
        p.ToneCounts["Formal"] = 3;

        var view = ProfileView.From(p, TimeSpan.Zero, Labels);

        Assert.Equal(["Excited", "Formal", "Sad"], view.Tones.Select(t => t.Name));
        Assert.Equal([6L, 3L, 1L], view.Tones.Select(t => t.Count));
        Assert.Equal([60.0, 30.0, 10.0], view.Tones.Select(t => t.Percent));
    }

    [Fact]
    public void TopTopics_LimitedToTen_SortedByCount()
    {
        var p = NewProfile();
        for (int i = 1; i <= 15; i++)
            p.TopicCounts[$"topic{i:00}"] = i;

        var view = ProfileView.From(p, TimeSpan.Zero, Labels);

        Assert.Equal(10, view.TopTopics.Count);
        Assert.Equal("topic15", view.TopTopics[0].Topic);
        Assert.Equal(15, view.TopTopics[0].Count);
        Assert.Equal("topic06", view.TopTopics[9].Topic);
        Assert.Equal(view.TopTopics.Select(t => t.Count).OrderByDescending(c => c), view.TopTopics.Select(t => t.Count));
    }

    [Fact]
    public void TopTopics_TiesBrokenAlphabetically()
    {
        var p = NewProfile();
        p.TopicCounts["zebra"] = 2;
        p.TopicCounts["apple"] = 2;
        p.TopicCounts["mango"] = 2;

        var view = ProfileView.From(p, TimeSpan.Zero, Labels);

        Assert.Equal(["apple", "mango", "zebra"], view.TopTopics.Select(t => t.Topic));
    }

    [Fact]
    public void HeatMap_ShiftsByOffset_WrappingAcrossDays()
    {
        var p = NewProfile();
        p.ActivityByHourOfWeekUtc[Idx(DayOfWeek.Saturday, 21)] = 5;
        p.ActivityByHourOfWeekUtc[Idx(DayOfWeek.Saturday, 23)] = 2;
        p.ActivityByHourOfWeekUtc[Idx(DayOfWeek.Sunday, 1)] = 3;

        // UTC+2: Saturday 21 -> Saturday 23; Saturday 23 -> Sunday 01 (wraps the week);
        // Sunday 01 -> Sunday 03.
        var plus2 = ProfileView.From(p, TimeSpan.FromHours(2), Labels);
        Assert.Equal(5, plus2.HeatMap[(int)DayOfWeek.Saturday, 23]);
        Assert.Equal(2, plus2.HeatMap[(int)DayOfWeek.Sunday, 1]);
        Assert.Equal(3, plus2.HeatMap[(int)DayOfWeek.Sunday, 3]);

        // UTC-5: Sunday 01 -> Saturday 20 (wraps backwards over the start of the week).
        var minus5 = ProfileView.From(p, TimeSpan.FromHours(-5), Labels);
        Assert.Equal(3, minus5.HeatMap[(int)DayOfWeek.Saturday, 20]);
        Assert.Equal(5, minus5.HeatMap[(int)DayOfWeek.Saturday, 16]);
        Assert.Equal(2, minus5.HeatMap[(int)DayOfWeek.Saturday, 18]);

        Assert.Equal(7, plus2.HeatMap.GetLength(0));
        Assert.Equal(24, plus2.HeatMap.GetLength(1));
    }

    [Fact]
    public void HeatMap_OffsetRoundedToNearestWholeHour_AndTotalPreserved()
    {
        var p = NewProfile();
        p.ActivityByHourOfWeekUtc[Idx(DayOfWeek.Monday, 10)] = 4;

        // +5:30 rounds to +6 (half away from zero); -3:30 rounds to -4; +5:29 rounds to +5.
        Assert.Equal(4, ProfileView.From(p, new TimeSpan(5, 30, 0), Labels).HeatMap[(int)DayOfWeek.Monday, 16]);
        Assert.Equal(4, ProfileView.From(p, new TimeSpan(-3, -30, 0), Labels).HeatMap[(int)DayOfWeek.Monday, 6]);
        Assert.Equal(4, ProfileView.From(p, new TimeSpan(5, 29, 0), Labels).HeatMap[(int)DayOfWeek.Monday, 15]);

        int total = 0;
        foreach (int v in ProfileView.From(p, TimeSpan.FromHours(9), Labels).HeatMap)
            total += v;
        Assert.Equal(4, total);
    }

    [Fact]
    public void MostActive_ReturnsThreeBusiestHours()
    {
        var p = NewProfile();
        p.ActivityByHourOfWeekUtc[Idx(DayOfWeek.Saturday, 21)] = 9;
        p.ActivityByHourOfWeekUtc[Idx(DayOfWeek.Monday, 8)] = 7;
        p.ActivityByHourOfWeekUtc[Idx(DayOfWeek.Friday, 5)] = 4;
        p.ActivityByHourOfWeekUtc[Idx(DayOfWeek.Tuesday, 12)] = 1;

        var view = ProfileView.From(p, TimeSpan.Zero, Labels);

        Assert.Equal(["Sat 21:00", "Mon 08:00", "Fri 05:00"], view.MostActive);
    }

    [Fact]
    public void MostActive_UsesLocalTime()
    {
        var p = NewProfile();
        p.ActivityByHourOfWeekUtc[Idx(DayOfWeek.Saturday, 21)] = 9;

        var view = ProfileView.From(p, TimeSpan.FromHours(3), Labels);

        Assert.Equal(["Sun 00:00"], view.MostActive);
    }

    [Fact]
    public void MostActive_Ties_EarlierInWeekFirst_AndZeroHoursOmitted()
    {
        var p = NewProfile();
        p.ActivityByHourOfWeekUtc[Idx(DayOfWeek.Wednesday, 3)] = 2;
        p.ActivityByHourOfWeekUtc[Idx(DayOfWeek.Monday, 3)] = 2;

        var view = ProfileView.From(p, TimeSpan.Zero, Labels);

        Assert.Equal(["Mon 03:00", "Wed 03:00"], view.MostActive);
    }

    [Fact]
    public void EmptyProfile_ProducesEmptyListsWithoutDividingByZero()
    {
        var view = ProfileView.From(NewProfile(), TimeSpan.FromHours(1), Labels);

        Assert.Empty(view.Tones);
        Assert.Empty(view.Intents);
        Assert.Empty(view.TopTopics);
        Assert.Empty(view.MostActive);
        Assert.Equal(7, view.HeatMap.GetLength(0));
        Assert.Equal(24, view.HeatMap.GetLength(1));
        foreach (int v in view.HeatMap)
            Assert.Equal(0, v);
    }

    [Fact]
    public void ZeroAnalyzedCount_WithCounts_GivesZeroPercentNotNaN()
    {
        var p = NewProfile();
        p.AnalyzedCount = 0;
        p.ToneCounts["Friendly"] = 3;

        var view = ProfileView.From(p, TimeSpan.Zero, Labels);

        Assert.Single(view.Tones);
        Assert.Equal(0, view.Tones[0].Percent);
    }
}
