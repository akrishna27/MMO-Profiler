using MmoProfiler.Core;

namespace MmoProfiler.Tests.Fakes;

/// <summary>Scripted analyser. Records every chunk it receives.</summary>
public sealed class FakeAnalyzer : IMessageAnalyzer
{
    public static MessageAnalysis Friendly { get; } =
        new(new[] { "Socializing" }, new[] { "Friendly" }, "en", new[] { "chat" });

    private readonly object _gate = new();

    /// <summary>Called with the 1-based call number and the chunk. Throw to simulate failure.</summary>
    public Func<int, IReadOnlyList<ChatMessage>, IReadOnlyList<MessageAnalysis>> Handler { get; set; } =
        (_, chunk) => chunk.Select(_ => Friendly).ToList();

    public List<IReadOnlyList<ChatMessage>> Calls { get; } = new();

    /// <summary>Completed when the first call has started. Lets a test wait until a cycle is mid-call.</summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When set, every call waits for this to complete before it returns. The test releases it.</summary>
    public TaskCompletionSource? Blocker { get; set; }

    /// <summary>If true the blocked call stops waiting when its token is cancelled; by default it ignores cancellation.</summary>
    public bool ObserveCancellation { get; set; }

    public async Task<IReadOnlyList<MessageAnalysis>> AnalyzeAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int n;
        lock (_gate)
        {
            Calls.Add(messages.ToList());
            n = Calls.Count;
        }
        Entered.TrySetResult();
        if (Blocker is { } blocker)
        {
            if (ObserveCancellation) await blocker.Task.WaitAsync(ct).ConfigureAwait(false);
            else await blocker.Task.ConfigureAwait(false);
        }
        return Handler(n, messages);
    }

    public static Func<int, IReadOnlyList<ChatMessage>, IReadOnlyList<MessageAnalysis>> AlwaysThrow() =>
        (_, _) => throw new AnalyzerUnavailableException("scripted failure");
}

public sealed class FakeSummarizer : IProfileSummarizer
{
    public Func<SummaryRequest, string> Handler { get; set; } = r => $"summary of {r.Character.Name}";

    public List<SummaryRequest> Requests { get; } = new();

    /// <summary>Completed when the first request has started.</summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When set, every request waits for this to complete before it returns. The test releases it.</summary>
    public TaskCompletionSource? Blocker { get; set; }

    public async Task<string> SummarizeAsync(SummaryRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Requests.Add(request);
        Entered.TrySetResult();
        if (Blocker is { } blocker) await blocker.Task.ConfigureAwait(false);
        return Handler(request);
    }
}
