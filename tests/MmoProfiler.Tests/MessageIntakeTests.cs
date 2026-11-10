using MmoProfiler.Core;
using static MmoProfiler.Tests.TestData;

namespace MmoProfiler.Tests;

public class MessageIntakeTests
{
    private static MessageIntake Create(ProfilerOptions? options = null) => new(Opts(options ?? new ProfilerOptions()));

    [Fact]
    public void Accept_EnabledChannel_Queues()
    {
        var intake = Create();

        Assert.True(intake.Accept(Msg(Key(), "hi", ChatChannel.Say)));

        Assert.Equal(1, intake.Count);
        Assert.Equal(0, intake.DroppedCount);
    }

    [Fact]
    public void Accept_DisabledChannel_Rejected()
    {
        var intake = Create();

        Assert.False(intake.Accept(Msg(Key(), "hi", ChatChannel.Guild)));

        Assert.Equal(0, intake.Count);
    }

    [Fact]
    public void Accept_WhenDisabled_Rejected()
    {
        var intake = Create(new ProfilerOptions { Enabled = false });

        Assert.False(intake.Accept(Msg(Key())));

        Assert.Equal(0, intake.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Accept_EmptyOrWhitespaceText_Rejected(string text)
    {
        var intake = Create();

        Assert.False(intake.Accept(Msg(Key(), text)));

        Assert.Equal(0, intake.Count);
    }

    [Fact]
    public void Accept_FromSelf_Rejected()
    {
        var me = Key("Me Myself");
        var intake = Create();
        intake.SelfKey = me;

        Assert.False(intake.Accept(Msg(CharacterKey.Create("FFXIV", "me  MYSELF", "odin"))));
        Assert.True(intake.Accept(Msg(Key("Someone Else"))));

        Assert.Equal(1, intake.Count);
    }

    [Fact]
    public void Accept_LongText_Truncated()
    {
        var intake = Create(new ProfilerOptions { MaxMessageLength = 50 });

        Assert.True(intake.Accept(Msg(Key(), new string('x', 200))));

        var drained = new List<ChatMessage>();
        intake.Drain(drained, 10);
        Assert.Equal(50, Assert.Single(drained).Text.Length);
    }

    [Fact]
    public void Accept_OverCapacity_DropsOldest_AndCountsDrops()
    {
        var intake = Create(new ProfilerOptions { QueueCapacity = 3 });

        for (var i = 1; i <= 5; i++) Assert.True(intake.Accept(Msg(Key(), $"m{i}")));

        Assert.Equal(3, intake.Count);
        Assert.Equal(2, intake.DroppedCount);
        var drained = new List<ChatMessage>();
        intake.Drain(drained, 10);
        Assert.Equal(new[] { "m3", "m4", "m5" }, drained.Select(m => m.Text));
    }

    [Fact]
    public void Drain_ReturnsInOrder_UpToMax()
    {
        var intake = Create();
        for (var i = 1; i <= 5; i++) intake.Accept(Msg(Key(), $"m{i}"));
        var into = new List<ChatMessage>();

        var n = intake.Drain(into, 3);

        Assert.Equal(3, n);
        Assert.Equal(new[] { "m1", "m2", "m3" }, into.Select(m => m.Text));
        Assert.Equal(2, intake.Count);

        Assert.Equal(2, intake.Drain(into, 10));
        Assert.Equal(new[] { "m1", "m2", "m3", "m4", "m5" }, into.Select(m => m.Text));
        Assert.Equal(0, intake.Drain(into, 10));
    }

    [Fact]
    public async Task Accept_FromManyThreads_LosesNothingUnderCapacity()
    {
        const int threads = 8, perThread = 200;
        var intake = Create(new ProfilerOptions { QueueCapacity = 5000 });
        var accepted = 0;

        await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            var sender = Key($"Thread{t} Person");
            for (var i = 0; i < perThread; i++)
                if (intake.Accept(Msg(sender, $"t{t}-{i}"))) Interlocked.Increment(ref accepted);
        })));

        Assert.Equal(threads * perThread, accepted);
        Assert.Equal(threads * perThread, intake.Count);
        Assert.Equal(0, intake.DroppedCount);

        var drained = new List<ChatMessage>();
        intake.Drain(drained, int.MaxValue);
        Assert.Equal(threads * perThread, drained.Select(m => m.Text).Distinct().Count());
        // Per-sender order is preserved.
        foreach (var group in drained.GroupBy(m => m.Sender))
        {
            var indexes = group.Select(m => int.Parse(m.Text[(m.Text.IndexOf('-') + 1)..])).ToList();
            Assert.Equal(indexes.OrderBy(i => i), indexes);
        }
    }
}
