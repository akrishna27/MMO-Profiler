using MmoProfiler.Core;

namespace MmoProfiler.Tests;

public class ProfilerOptionsTests
{
    [Fact]
    public void Defaults_EnableTheSevenDefaultChannels_AndRawStorageOff()
    {
        var o = new ProfilerOptions();

        Assert.True(o.Enabled);
        Assert.False(o.StoreRawMessages);
        Assert.Equal(
            new[]
            {
                ChatChannel.Say, ChatChannel.Tell, ChatChannel.Party, ChatChannel.Shout,
                ChatChannel.Yell, ChatChannel.Linkshell, ChatChannel.CrossWorldLinkshell,
            }.OrderBy(c => c),
            o.EnabledChannels.OrderBy(c => c));
        Assert.DoesNotContain(ChatChannel.Guild, o.EnabledChannels);
        Assert.Equal(2000, o.QueueCapacity);
        Assert.Equal(TimeSpan.FromSeconds(60), o.BatchInterval);
        Assert.Equal("http://127.0.0.1:11434", o.OllamaEndpoint);
        Assert.Equal("llama3.2:3b", o.OllamaModel);
    }

    [Fact]
    public void Normalize_ClampsOutOfRangeValues()
    {
        var low = new ProfilerOptions
        {
            QueueCapacity = -5,
            BatchInterval = TimeSpan.FromSeconds(1),
            EarlyTriggerQueueDepth = 0,
            MaxMessagesPerCycle = 0,
            ClassificationChunkSize = 0,
            SummaryThreshold = 0,
            MaxSummariesPerCycle = 0,
            MaxMessageLength = 0,
            RawMessageRetentionDays = 0,
            OllamaTimeout = TimeSpan.Zero,
        }.Normalize();

        Assert.Equal(100, low.QueueCapacity);
        Assert.Equal(TimeSpan.FromSeconds(10), low.BatchInterval);
        Assert.True(low.EarlyTriggerQueueDepth >= 1);
        Assert.True(low.MaxMessagesPerCycle >= 1);
        Assert.Equal(1, low.ClassificationChunkSize);
        Assert.True(low.SummaryThreshold >= 1);
        Assert.Equal(1, low.MaxSummariesPerCycle);
        Assert.True(low.MaxMessageLength >= 1);
        Assert.Equal(1, low.RawMessageRetentionDays);
        Assert.True(low.OllamaTimeout > TimeSpan.Zero);

        var high = new ProfilerOptions
        {
            QueueCapacity = int.MaxValue,
            BatchInterval = TimeSpan.FromHours(5),
            EarlyTriggerQueueDepth = int.MaxValue,
            MaxMessagesPerCycle = int.MaxValue,
            ClassificationChunkSize = 9999,
            SummaryThreshold = int.MaxValue,
            MaxSummariesPerCycle = int.MaxValue,
            MaxMessageLength = int.MaxValue,
            RawMessageRetentionDays = int.MaxValue,
            OllamaTimeout = TimeSpan.FromDays(1),
        }.Normalize();

        Assert.Equal(20000, high.QueueCapacity);
        Assert.Equal(TimeSpan.FromMinutes(10), high.BatchInterval);
        Assert.True(high.EarlyTriggerQueueDepth <= high.QueueCapacity);
        Assert.Equal(50, high.ClassificationChunkSize);
        Assert.True(high.MaxMessagesPerCycle < int.MaxValue);
        Assert.True(high.MaxMessageLength < int.MaxValue);
        Assert.True(high.RawMessageRetentionDays < int.MaxValue);
        Assert.True(high.OllamaTimeout <= TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Normalize_KeepsValidValues()
    {
        var o = new ProfilerOptions { QueueCapacity = 500, ClassificationChunkSize = 10, OllamaModel = "qwen:7b" };

        var n = o.Normalize();

        Assert.Equal(500, n.QueueCapacity);
        Assert.Equal(10, n.ClassificationChunkSize);
        Assert.Equal("qwen:7b", n.OllamaModel);
        Assert.Equal(o.EnabledChannels.OrderBy(c => c), n.EnabledChannels.OrderBy(c => c));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com")]
    [InlineData("localhost:11434")]
    public void Normalize_InvalidEndpoint_FallsBackToDefault(string endpoint)
    {
        var n = new ProfilerOptions { OllamaEndpoint = endpoint }.Normalize();

        Assert.Equal(ProfilerOptions.DefaultEndpoint, n.OllamaEndpoint);
    }

    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://LOCALHOST", true)]
    [InlineData("http://127.0.0.1:11434", true)]
    [InlineData("http://127.5.6.7:11434", true)]
    [InlineData("http://[::1]:11434", true)]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("http://192.168.1.20:11434", false)]
    [InlineData("http://10.0.0.1", false)]
    [InlineData("https://ollama.example.com", false)]
    [InlineData("http://128.0.0.1", false)]
    [InlineData("http://localhost.evil.com", false)]
    [InlineData("http://[2001:db8::1]:11434", false)]
    [InlineData("", false)]
    public void IsLoopback_RecognisesLocalAndRemoteHosts(string endpoint, bool expected)
    {
        Assert.Equal(expected, OllamaEndpointInfo.IsLoopback(endpoint));
    }
}
