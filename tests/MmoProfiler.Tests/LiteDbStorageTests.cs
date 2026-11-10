using LiteDB;
using MmoProfiler.Core;
using MmoProfiler.Storage.LiteDb;

namespace MmoProfiler.Tests;

public class LiteDbStorageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 21, 30, 0, TimeSpan.Zero);

    private static Profile FullProfile(CharacterKey key, DateTimeOffset? lastSeen = null)
    {
        var p = Profile.Create(key);
        p.FirstSeenUtc = T0.AddDays(-3);
        p.LastSeenUtc = lastSeen ?? T0;
        p.MessageCount = 5_000_000_000L; // past int range, to prove counts are 64-bit
        p.AnalyzedCount = 42;
        p.ChannelCounts[ChatChannel.Say] = 3;
        p.ChannelCounts[ChatChannel.CrossWorldLinkshell] = 9;
        p.ToneCounts[Tone.Friendly] = 7;
        p.ToneCounts[Tone.Flirtatious] = 1;
        p.IntentCounts[Intent.LookingForGroup] = 4;
        p.LanguageCounts["en"] = 30;
        p.LanguageCounts["de"] = 2;
        p.TopicCounts["dungeons"] = 11;
        p.TopicCounts["glamour"] = 5;
        p.ActivityByHourOfWeekUtc[0] = 1;
        p.ActivityByHourOfWeekUtc[100] = 6;
        p.ActivityByHourOfWeekUtc[167] = 2;
        p.Summary = "Friendly player who talks about dungeons.";
        p.SummaryUpdatedUtc = T0.AddHours(-1);
        p.MessagesSinceSummary = 8;
        p.UserNotes = "met in Limsa\nowes me a potion";
        return p;
    }

    private static void AssertEquivalent(Profile expected, Profile actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Key, actual.Key);
        Assert.Equal(expected.Key.Name, actual.Key.Name); // record equality ignores case; names must still match exactly
        Assert.Equal(expected.FirstSeenUtc, actual.FirstSeenUtc);
        Assert.Equal(expected.LastSeenUtc, actual.LastSeenUtc);
        Assert.Equal(expected.MessageCount, actual.MessageCount);
        Assert.Equal(expected.AnalyzedCount, actual.AnalyzedCount);
        Assert.Equal(expected.ChannelCounts.OrderBy(k => k.Key), actual.ChannelCounts.OrderBy(k => k.Key));
        Assert.Equal(expected.ToneCounts.OrderBy(k => k.Key), actual.ToneCounts.OrderBy(k => k.Key));
        Assert.Equal(expected.IntentCounts.OrderBy(k => k.Key), actual.IntentCounts.OrderBy(k => k.Key));
        Assert.Equal(expected.LanguageCounts.OrderBy(k => k.Key), actual.LanguageCounts.OrderBy(k => k.Key));
        Assert.Equal(expected.TopicCounts.OrderBy(k => k.Key), actual.TopicCounts.OrderBy(k => k.Key));
        Assert.Equal(expected.ActivityByHourOfWeekUtc, actual.ActivityByHourOfWeekUtc);
        Assert.Equal(expected.Summary, actual.Summary);
        Assert.Equal(expected.SummaryUpdatedUtc, actual.SummaryUpdatedUtc);
        Assert.Equal(expected.MessagesSinceSummary, actual.MessagesSinceSummary);
        Assert.Equal(expected.UserNotes, actual.UserNotes);
    }

    [Fact]
    public void Upsert_Then_Get_RoundTripsEveryField()
    {
        using var storage = LiteDbStorage.OpenInMemory();
        var key = TestData.Key("Alice Example", "Odin");
        var original = FullProfile(key);

        storage.Profiles.Upsert(original);
        var loaded = storage.Profiles.Get(key);

        Assert.NotNull(loaded);
        AssertEquivalent(original, loaded!);
    }

    [Fact]
    public void Upsert_RoundTripsNullSummaryAndSummaryTime_AndNonUtcTimestampsAsUtc()
    {
        using var storage = LiteDbStorage.OpenInMemory();
        var key = TestData.Key();
        var p = Profile.Create(key);
        var local = new DateTimeOffset(2026, 11, 7, 23, 30, 0, TimeSpan.FromHours(2));
        p.FirstSeenUtc = local;
        p.LastSeenUtc = local;

        storage.Profiles.Upsert(p);
        var loaded = storage.Profiles.Get(key)!;

        Assert.Null(loaded.Summary);
        Assert.Null(loaded.SummaryUpdatedUtc);
        Assert.Equal(local, loaded.FirstSeenUtc);
        Assert.Equal(TimeSpan.Zero, loaded.FirstSeenUtc.Offset);
        Assert.Equal(TimeSpan.Zero, loaded.LastSeenUtc.Offset);
    }

    [Fact]
    public void Upsert_ReplacesExistingProfile()
    {
        using var storage = LiteDbStorage.OpenInMemory();
        var key = TestData.Key();
        var p = FullProfile(key);
        storage.Profiles.Upsert(p);
        p.MessageCount = 1;
        p.UserNotes = "changed";
        storage.Profiles.Upsert(p);

        Assert.Equal(1, storage.Profiles.Count());
        Assert.Equal("changed", storage.Profiles.Get(key)!.UserNotes);
    }

    [Fact]
    public void UpsertMany_SavesAllProfiles()
    {
        using var storage = LiteDbStorage.OpenInMemory();
        var profiles = Enumerable.Range(0, 5)
            .Select(i => FullProfile(TestData.Key($"Person Number{i}")))
            .ToList();

        storage.Profiles.UpsertMany(profiles);

        Assert.Equal(5, storage.Profiles.Count());
    }

    [Fact]
    public void Get_Missing_ReturnsNull()
    {
        using var storage = LiteDbStorage.OpenInMemory();
        Assert.Null(storage.Profiles.Get(TestData.Key()));
    }

    [Fact]
    public void Get_IsCaseInsensitiveOnKey()
    {
        using var storage = LiteDbStorage.OpenInMemory();
        storage.Profiles.Upsert(FullProfile(CharacterKey.Create("ffxiv", "Alice Example", "Odin")));

        var loaded = storage.Profiles.Get(CharacterKey.Create("FFXIV", "ALICE EXAMPLE", "odin"));

        Assert.NotNull(loaded);
        Assert.Equal("Alice Example", loaded!.Key.Name);
    }

    [Fact]
    public void List_OrdersByLastSeenDescending_AndPages()
    {
        using var storage = LiteDbStorage.OpenInMemory();
        // Inserted out of order on purpose.
        foreach (var i in new[] { 2, 4, 0, 3, 1 })
            storage.Profiles.Upsert(FullProfile(TestData.Key($"Person Number{i}"), T0.AddHours(i)));

        var all = storage.Profiles.List(null, 0, 10);
        var page2 = storage.Profiles.List(null, 2, 2);
        var past = storage.Profiles.List(null, 10, 5);

        Assert.Equal(
            new[] { "Person Number4", "Person Number3", "Person Number2", "Person Number1", "Person Number0" },
            all.Select(p => p.Key.Name));
        Assert.Equal(new[] { "Person Number2", "Person Number1" }, page2.Select(p => p.Key.Name));
        Assert.Empty(past);
    }

    [Fact]
    public void List_FiltersByNameSubstring_CaseInsensitive()
    {
        using var storage = LiteDbStorage.OpenInMemory();
        storage.Profiles.Upsert(FullProfile(TestData.Key("Alice Example"), T0.AddHours(1)));
        storage.Profiles.Upsert(FullProfile(TestData.Key("Bob Builder"), T0.AddHours(2)));
        storage.Profiles.Upsert(FullProfile(TestData.Key("Malice Aforethought"), T0.AddHours(3)));

        var hits = storage.Profiles.List("ALIC", 0, 10);
        var paged = storage.Profiles.List("alic", 1, 10);
        var none = storage.Profiles.List("zzz", 0, 10);
        var wildcard = storage.Profiles.List("%", 0, 10);

        Assert.Equal(new[] { "Malice Aforethought", "Alice Example" }, hits.Select(p => p.Key.Name));
        Assert.Equal(new[] { "Alice Example" }, paged.Select(p => p.Key.Name));
        Assert.Empty(none);
        Assert.Empty(wildcard);
    }

    [Fact]
    public void UpsertMany_KeyContainingNul_IsSkipped_AndOtherProfilesAreSaved()
    {
        using var storage = LiteDbStorage.OpenInMemory();
        var bad = FullProfile(TestData.Key("Alice Example"));
        bad.TopicCounts["bad\0topic"] = 3;
        bad.LanguageCounts["e\0n"] = 1;
        var good = FullProfile(TestData.Key("Bob Builder"));

        storage.Profiles.UpsertMany(new[] { bad, good });

        Assert.Equal(2, storage.Profiles.Count());
        var loadedBad = storage.Profiles.Get(bad.Key)!;
        // Only the NUL keys are lost; every other field of the profile is saved.
        Assert.DoesNotContain("bad\0topic", loadedBad.TopicCounts.Keys);
        Assert.DoesNotContain("e\0n", loadedBad.LanguageCounts.Keys);
        Assert.Equal(bad.TopicCounts.Where(kv => !kv.Key.Contains('\0')).OrderBy(kv => kv.Key), loadedBad.TopicCounts.OrderBy(kv => kv.Key));
        Assert.Equal(bad.MessageCount, loadedBad.MessageCount);
        AssertEquivalent(good, storage.Profiles.Get(good.Key)!);
    }

    [Fact]
    public void Delete_RemovesProfile()
    {
        using var storage = LiteDbStorage.OpenInMemory();
        var a = TestData.Key("Alice Example");
        var b = TestData.Key("Bob Builder");
        storage.Profiles.Upsert(FullProfile(a));
        storage.Profiles.Upsert(FullProfile(b));

        Assert.True(storage.Profiles.Delete(a));
        Assert.False(storage.Profiles.Delete(a));

        Assert.Null(storage.Profiles.Get(a));
        Assert.NotNull(storage.Profiles.Get(b));
        Assert.Equal(1, storage.Profiles.Count());
    }

    [Fact]
    public void DeleteAll_EmptiesStore()
    {
        using var storage = LiteDbStorage.OpenInMemory();
        storage.Profiles.Upsert(FullProfile(TestData.Key("Alice Example")));
        storage.Profiles.Upsert(FullProfile(TestData.Key("Bob Builder")));

        storage.Profiles.DeleteAll();

        Assert.Equal(0, storage.Profiles.Count());
        Assert.Empty(storage.Profiles.List(null, 0, 10));
    }

    [Fact]
    public void EnumDictionaries_StoredAsStrings_UnknownKeysIgnoredOnRead()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mmoprofiler-{Guid.NewGuid():N}.db");
        try
        {
            var key = TestData.Key();
            using (var storage = LiteDbStorage.Open(path))
                storage.Profiles.Upsert(FullProfile(key));

            // Inspect the raw document, then add keys a future or older build might have written.
            using (var raw = new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Direct }))
            {
                var col = raw.GetCollection("profiles");
                var doc = col.FindById(key.Id);
                Assert.Equal(7, doc["toneCounts"]["Friendly"].AsInt64);
                Assert.Equal(3, doc["channelCounts"]["Say"].AsInt64);
                Assert.Equal(4, doc["intentCounts"]["LookingForGroup"].AsInt64);

                doc["toneCounts"].AsDocument["Grumpy"] = 5L;       // a name that no longer exists
                doc["toneCounts"].AsDocument["3"] = 5L;            // a bare number, as an unnamed enum value would be
                doc["channelCounts"].AsDocument["Telepathy"] = 2L;
                doc["intentCounts"].AsDocument["Bartering"] = 1L;
                col.Update(doc);
            }

            using var reopened = LiteDbStorage.Open(path);
            var loaded = reopened.Profiles.Get(key)!;

            Assert.Equal(new[] { Tone.Friendly, Tone.Flirtatious }.OrderBy(t => t), loaded.ToneCounts.Keys.OrderBy(t => t));
            Assert.Equal(new[] { ChatChannel.Say, ChatChannel.CrossWorldLinkshell }.OrderBy(c => c), loaded.ChannelCounts.Keys.OrderBy(c => c));
            Assert.Equal(new[] { Intent.LookingForGroup }, loaded.IntentCounts.Keys);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_FileDatabase_PersistsAcrossReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mmoprofiler-{Guid.NewGuid():N}.db");
        try
        {
            var key = TestData.Key();
            var original = FullProfile(key);
            using (var storage = LiteDbStorage.Open(path))
            {
                storage.Profiles.Upsert(original);
                storage.Messages.Append(new[] { Logged(key, "hello", T0) });
            }

            using var reopened = LiteDbStorage.Open(path);
            AssertEquivalent(original, reopened.Profiles.Get(key)!);
            Assert.Single(reopened.Messages.GetRecent(key, 10));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Get_ProfileFromNewerSchema_IsRefusedRatherThanMisread()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mmoprofiler-{Guid.NewGuid():N}.db");
        try
        {
            var key = TestData.Key();
            using (var storage = LiteDbStorage.Open(path))
                storage.Profiles.Upsert(FullProfile(key));
            using (var raw = new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Direct }))
            {
                var col = raw.GetCollection("profiles");
                var doc = col.FindById(key.Id);
                doc["schemaVersion"] = 2;
                col.Update(doc);
            }

            using var reopened = LiteDbStorage.Open(path);
            Assert.Throws<NotSupportedException>(() => reopened.Profiles.Get(key));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static LoggedMessage Logged(
        CharacterKey sender, string text, DateTimeOffset at,
        ChatChannel channel = ChatChannel.Say, string? label = null, MessageAnalysis? analysis = null) =>
        new(TestData.Msg(sender, text, channel, at, label),
            analysis ?? new MessageAnalysis(Tone.Friendly, Intent.Socializing, "en", new[] { "greeting", "chat" }));

    [Fact]
    public void MessageLog_RoundTripsMessageAndAnalysis()
    {
        using var storage = LiteDbStorage.OpenInMemory();
        var key = TestData.Key();
        var entry = Logged(key, "LS hello", T0, ChatChannel.Linkshell, "LS3");

        storage.Messages.Append(new[] { entry });
        var loaded = Assert.Single(storage.Messages.GetRecent(key, 5));

        Assert.Equal(key, loaded.Message.Sender);
        Assert.Equal(key.Name, loaded.Message.Sender.Name);
        Assert.Equal(ChatChannel.Linkshell, loaded.Message.Channel);
        Assert.Equal("LS3", loaded.Message.ChannelLabel);
        Assert.Equal("LS hello", loaded.Message.Text);
        Assert.Equal(T0, loaded.Message.TimestampUtc);
        Assert.Equal(Tone.Friendly, loaded.Analysis.Tone);
        Assert.Equal(Intent.Socializing, loaded.Analysis.Intent);
        Assert.Equal("en", loaded.Analysis.Language);
        Assert.Equal(new[] { "greeting", "chat" }, loaded.Analysis.Topics);
    }

    [Fact]
    public void MessageLog_RoundTripsNullLabelAndUnknownAnalysis()
    {
        using var storage = LiteDbStorage.OpenInMemory();
        var key = TestData.Key();

        storage.Messages.Append(new[] { Logged(key, "hi", T0, analysis: MessageAnalysis.Unknown) });
        var loaded = Assert.Single(storage.Messages.GetRecent(key, 5));

        Assert.Null(loaded.Message.ChannelLabel);
        Assert.Equal(Tone.Unknown, loaded.Analysis.Tone);
        Assert.Equal(Intent.Unknown, loaded.Analysis.Intent);
        Assert.Equal("und", loaded.Analysis.Language);
        Assert.Empty(loaded.Analysis.Topics);
    }

    [Fact]
    public void MessageLog_GetRecent_NewestFirst_LimitedToTake()
    {
        using var storage = LiteDbStorage.OpenInMemory();
        var key = TestData.Key();
        var other = TestData.Key("Bob Builder");
        // Appended out of order, with another sender mixed in.
        storage.Messages.Append(new[]
        {
            Logged(key, "m2", T0.AddMinutes(2)),
            Logged(other, "other", T0.AddMinutes(9)),
            Logged(key, "m0", T0),
            Logged(key, "m4", T0.AddMinutes(4)),
            Logged(key, "m1", T0.AddMinutes(1)),
            Logged(key, "m3", T0.AddMinutes(3)),
        });

        var recent = storage.Messages.GetRecent(key, 3);

        Assert.Equal(new[] { "m4", "m3", "m2" }, recent.Select(m => m.Message.Text));
        Assert.Empty(storage.Messages.GetRecent(key, 0));
    }

    [Fact]
    public void MessageLog_GetRecent_IsCaseInsensitiveOnKey()
    {
        using var storage = LiteDbStorage.OpenInMemory();
        storage.Messages.Append(new[] { Logged(CharacterKey.Create("ffxiv", "Alice Example", "Odin"), "hi", T0) });

        Assert.Single(storage.Messages.GetRecent(CharacterKey.Create("FFXIV", "alice example", "ODIN"), 5));
    }

    [Fact]
    public void MessageLog_DeleteOlderThan_RemovesOnlyOldMessages()
    {
        using var storage = LiteDbStorage.OpenInMemory();
        var key = TestData.Key();
        storage.Messages.Append(new[]
        {
            Logged(key, "old1", T0.AddDays(-40)),
            Logged(key, "old2", T0.AddDays(-31)),
            Logged(key, "edge", T0.AddDays(-30)),
            Logged(key, "new", T0.AddDays(-1)),
        });

        var removed = storage.Messages.DeleteOlderThan(T0.AddDays(-30));

        Assert.Equal(2, removed);
        Assert.Equal(new[] { "new", "edge" }, storage.Messages.GetRecent(key, 10).Select(m => m.Message.Text));
    }

    [Fact]
    public void MessageLog_DeleteFor_RemovesOnlyThatCharacter()
    {
        using var storage = LiteDbStorage.OpenInMemory();
        var a = TestData.Key("Alice Example");
        var b = TestData.Key("Bob Builder");
        storage.Messages.Append(new[]
        {
            Logged(a, "a1", T0), Logged(b, "b1", T0), Logged(a, "a2", T0.AddMinutes(1)),
        });

        var removed = storage.Messages.DeleteFor(a);

        Assert.Equal(2, removed);
        Assert.Empty(storage.Messages.GetRecent(a, 10));
        Assert.Single(storage.Messages.GetRecent(b, 10));
    }

    [Fact]
    public void MessageLog_DeleteAll_EmptiesLog_AndAppendOfNothingIsHarmless()
    {
        using var storage = LiteDbStorage.OpenInMemory();
        var key = TestData.Key();
        storage.Messages.Append(Array.Empty<LoggedMessage>());
        storage.Messages.Append(new[] { Logged(key, "x", T0) });

        storage.Messages.DeleteAll();

        Assert.Empty(storage.Messages.GetRecent(key, 10));
    }

    [Fact]
    public void ProfilesAndMessages_ShareOneDatabase_AndDeletingOneLeavesTheOther()
    {
        using var storage = LiteDbStorage.OpenInMemory();
        var key = TestData.Key();
        storage.Profiles.Upsert(FullProfile(key));
        storage.Messages.Append(new[] { Logged(key, "x", T0) });

        storage.Profiles.DeleteAll();

        Assert.Single(storage.Messages.GetRecent(key, 10));
    }
}
