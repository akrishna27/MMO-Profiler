using MmoProfiler.Core;

namespace MmoProfiler.Tests;

public class ProfileViewTests
{
    private static readonly CharacterKey Alice = CharacterKey.Create("ffxiv", "Alice Example", "Odin");

    private static Profile NewProfile() => Profile.Create(Alice);

    private static int Idx(DayOfWeek day, int hour) => (int)day * 24 + hour;

    [Fact]
    public void Percentages_ExcludeUnknown_AndSumToAboutOneHundred()
    {
        var p = NewProfile();
        p.AnalyzedCount = 30;
        p.ToneCounts[Tone.Friendly] = 10;
        p.ToneCounts[Tone.Neutral] = 10;
        p.ToneCounts[Tone.Hostile] = 10;
        p.ToneCounts[Tone.Unknown] = 5;
        p.IntentCounts[Intent.Socializing] = 20;
        p.IntentCounts[Intent.Trading] = 7;
        p.IntentCounts[Intent.Unknown] = 3;

        var view = ProfileView.From(p, TimeSpan.Zero);

        Assert.DoesNotContain(view.Tones, t => t.Name == nameof(Tone.Unknown));
        Assert.DoesNotContain(view.Intents, i => i.Name == nameof(Intent.Unknown));
        Assert.Equal(3, view.Tones.Count);
        Assert.All(view.Tones, t => Assert.Equal(33.3, t.Percent));
        Assert.InRange(view.Tones.Sum(t => t.Percent), 99.0, 100.0);

        Assert.Equal(nameof(Intent.Socializing), view.Intents[0].Name);
        Assert.Equal(66.7, view.Intents[0].Percent);
        Assert.Equal(23.3, view.Intents[1].Percent);
    }

    [Fact]
    public void Percentages_SortedByCountDescending()
    {
        var p = NewProfile();
        p.AnalyzedCount = 10;
        p.ToneCounts[Tone.Sad] = 1;
        p.ToneCounts[Tone.Excited] = 6;
        p.ToneCounts[Tone.Formal] = 3;

        var view = ProfileView.From(p, TimeSpan.Zero);

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

        var view = ProfileView.From(p, TimeSpan.Zero);

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

        var view = ProfileView.From(p, TimeSpan.Zero);

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
        var plus2 = ProfileView.From(p, TimeSpan.FromHours(2));
        Assert.Equal(5, plus2.HeatMap[(int)DayOfWeek.Saturday, 23]);
        Assert.Equal(2, plus2.HeatMap[(int)DayOfWeek.Sunday, 1]);
        Assert.Equal(3, plus2.HeatMap[(int)DayOfWeek.Sunday, 3]);

        // UTC-5: Sunday 01 -> Saturday 20 (wraps backwards over the start of the week).
        var minus5 = ProfileView.From(p, TimeSpan.FromHours(-5));
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
        Assert.Equal(4, ProfileView.From(p, new TimeSpan(5, 30, 0)).HeatMap[(int)DayOfWeek.Monday, 16]);
        Assert.Equal(4, ProfileView.From(p, new TimeSpan(-3, -30, 0)).HeatMap[(int)DayOfWeek.Monday, 6]);
        Assert.Equal(4, ProfileView.From(p, new TimeSpan(5, 29, 0)).HeatMap[(int)DayOfWeek.Monday, 15]);

        int total = 0;
        foreach (int v in ProfileView.From(p, TimeSpan.FromHours(9)).HeatMap)
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

        var view = ProfileView.From(p, TimeSpan.Zero);

        Assert.Equal(["Sat 21:00", "Mon 08:00", "Fri 05:00"], view.MostActive);
    }

    [Fact]
    public void MostActive_UsesLocalTime()
    {
        var p = NewProfile();
        p.ActivityByHourOfWeekUtc[Idx(DayOfWeek.Saturday, 21)] = 9;

        var view = ProfileView.From(p, TimeSpan.FromHours(3));

        Assert.Equal(["Sun 00:00"], view.MostActive);
    }

    [Fact]
    public void MostActive_Ties_EarlierInWeekFirst_AndZeroHoursOmitted()
    {
        var p = NewProfile();
        p.ActivityByHourOfWeekUtc[Idx(DayOfWeek.Wednesday, 3)] = 2;
        p.ActivityByHourOfWeekUtc[Idx(DayOfWeek.Monday, 3)] = 2;

        var view = ProfileView.From(p, TimeSpan.Zero);

        Assert.Equal(["Mon 03:00", "Wed 03:00"], view.MostActive);
    }

    [Fact]
    public void EmptyProfile_ProducesEmptyListsWithoutDividingByZero()
    {
        var view = ProfileView.From(NewProfile(), TimeSpan.FromHours(1));

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
        p.ToneCounts[Tone.Friendly] = 3;

        var view = ProfileView.From(p, TimeSpan.Zero);

        Assert.Single(view.Tones);
        Assert.Equal(0, view.Tones[0].Percent);
    }
}
