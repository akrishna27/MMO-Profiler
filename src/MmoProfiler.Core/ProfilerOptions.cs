using System.Collections.Frozen;
using System.Net;

namespace MmoProfiler.Core;

public sealed record ProfilerOptions
{
    public const string DefaultEndpoint = "http://127.0.0.1:11434";
    public const string DefaultModel = "llama3.2:3b";
    public const double DefaultLabelConfidenceThreshold = 0.6;

    public static IReadOnlySet<ChatChannel> DefaultChannels { get; } = new[]
    {
        ChatChannel.Say, ChatChannel.Tell, ChatChannel.Party, ChatChannel.Shout,
        ChatChannel.Yell, ChatChannel.Linkshell, ChatChannel.CrossWorldLinkshell,
    }.ToFrozenSet();

    public bool Enabled { get; init; } = true;
    public IReadOnlySet<ChatChannel> EnabledChannels { get; init; } = DefaultChannels;
    public int QueueCapacity { get; init; } = 2000;
    public TimeSpan BatchInterval { get; init; } = TimeSpan.FromSeconds(60);
    public int EarlyTriggerQueueDepth { get; init; } = 100;
    public int MaxMessagesPerCycle { get; init; } = 200;
    public int ClassificationChunkSize { get; init; } = 20;
    public int SummaryThreshold { get; init; } = 15;
    public int MaxSummariesPerCycle { get; init; } = 3;
    public int MaxMessageLength { get; init; } = 500;
    public bool StoreRawMessages { get; init; } = false;
    public int RawMessageRetentionDays { get; init; } = 30;
    public string OllamaEndpoint { get; init; } = DefaultEndpoint;
    public string OllamaModel { get; init; } = DefaultModel;
    public TimeSpan OllamaTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public int OllamaContextTokens { get; init; } = 8192;
    public int ClassificationChunkMaxChars { get; init; } = 4000;
    public int SummaryMaxInputChars { get; init; } = 6000;
    public int MaxIntentsPerMessage { get; init; } = 2;
    public int MaxTonesPerMessage { get; init; } = 2;
    public double LabelConfidenceThreshold { get; init; } = DefaultLabelConfidenceThreshold;
    public int LabelExamplesInPrompt { get; init; } = 1;

    /// <summary>
    /// Off by default (decision D26). When on, the model is asked for a confidence with every intent and
    /// tone and <see cref="LabelConfidenceThreshold"/> applies. It exists so the evaluation can test it.
    /// </summary>
    public bool IncludeLabelConfidence { get; init; } = false;

    /// <summary>Returns a copy with every value clamped to a sane range.</summary>
    public ProfilerOptions Normalize()
    {
        var queueCapacity = Math.Clamp(QueueCapacity, 100, 20000);
        var endpoint = OllamaEndpoint?.Trim();
        var model = OllamaModel?.Trim();

        return this with
        {
            EnabledChannels = EnabledChannels is null ? DefaultChannels : EnabledChannels.ToFrozenSet(),
            QueueCapacity = queueCapacity,
            BatchInterval = Clamp(BatchInterval, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10)),
            EarlyTriggerQueueDepth = Math.Clamp(EarlyTriggerQueueDepth, 10, queueCapacity),
            MaxMessagesPerCycle = Math.Clamp(MaxMessagesPerCycle, 10, 1000),
            ClassificationChunkSize = Math.Clamp(ClassificationChunkSize, 1, 50),
            SummaryThreshold = Math.Clamp(SummaryThreshold, 5, 200),
            MaxSummariesPerCycle = Math.Clamp(MaxSummariesPerCycle, 1, 20),
            MaxMessageLength = Math.Clamp(MaxMessageLength, 20, 2000),
            RawMessageRetentionDays = Math.Clamp(RawMessageRetentionDays, 1, 365),
            OllamaEndpoint = IsValidEndpoint(endpoint) ? endpoint! : DefaultEndpoint,
            OllamaModel = string.IsNullOrEmpty(model) ? DefaultModel : model,
            OllamaTimeout = Clamp(OllamaTimeout, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10)),
            OllamaContextTokens = Math.Clamp(OllamaContextTokens, 2048, 32768),
            ClassificationChunkMaxChars = Math.Clamp(ClassificationChunkMaxChars, 500, 20000),
            SummaryMaxInputChars = Math.Clamp(SummaryMaxInputChars, 500, 20000),
            MaxIntentsPerMessage = Math.Clamp(MaxIntentsPerMessage, 1, 2),
            MaxTonesPerMessage = Math.Clamp(MaxTonesPerMessage, 1, 2),
            LabelConfidenceThreshold = double.IsNaN(LabelConfidenceThreshold)
                ? DefaultLabelConfidenceThreshold
                : Math.Clamp(LabelConfidenceThreshold, 0.0, 1.0),
            LabelExamplesInPrompt = Math.Clamp(LabelExamplesInPrompt, 0, 3),
        };
    }

    private static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max) => v < min ? min : v > max ? max : v;

    internal static bool IsValidEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);
}

public static class OllamaEndpointInfo
{
    /// <summary>True for localhost, 127.0.0.0/8 and ::1. Accepts a full URL or a bare host.</summary>
    public static bool IsLoopback(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        var text = endpoint.Trim();

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            if (!Uri.TryCreate("http://" + text, UriKind.Absolute, out uri)) return false;
        }

        var host = uri.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }
}
