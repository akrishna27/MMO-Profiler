using MmoProfiler.Core;

namespace MmoProfiler.Tests.Fakes;

/// <summary>Scripted analyser. Records every chunk it receives.</summary>
public sealed class FakeAnalyzer : IMessageAnalyzer
{
    public static MessageAnalysis Friendly { get; } =
        new(Tone.Friendly, Intent.Socializing, "en", new[] { "chat" });

    private readonly object _gate = new();

    /// <summary>Called with the 1-based call number and the chunk. Throw to simulate failure.</summary>
    public Func<int, IReadOnlyList<ChatMessage>, IReadOnlyList<MessageAnalysis>> Handler { get; set; } =
        (_, chunk) => chunk.Select(_ => Friendly).ToList();

    public List<IReadOnlyList<ChatMessage>> Calls { get; } = new();

    public Task<IReadOnlyList<MessageAnalysis>> AnalyzeAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int n;
        lock (_gate)
        {
            Calls.Add(messages.ToList());
            n = Calls.Count;
        }
        return Task.FromResult(Handler(n, messages));
    }

    public static Func<int, IReadOnlyList<ChatMessage>, IReadOnlyList<MessageAnalysis>> AlwaysThrow() =>
        (_, _) => throw new AnalyzerUnavailableException("scripted failure");
}

public sealed class FakeSummarizer : IProfileSummarizer
{
    public Func<SummaryRequest, string> Handler { get; set; } = r => $"summary of {r.Character.Name}";

    public List<SummaryRequest> Requests { get; } = new();

    public Task<string> SummarizeAsync(SummaryRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(Handler(request));
    }
}
