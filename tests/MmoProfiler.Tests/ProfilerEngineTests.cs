using Microsoft.Extensions.Time.Testing;
using MmoProfiler.Core;
using MmoProfiler.Tests.Fakes;
using static MmoProfiler.Tests.TestData;

namespace MmoProfiler.Tests;

public class ProfilerEngineTests : IAsyncDisposable
{
    private static readonly CharacterKey Alice = Key("Alice Example");
    private static readonly CharacterKey Bob = Key("Bob Example");

    private readonly FakeTimeProvider _time = new();
    private readonly InMemoryProfileStore _store = new();
    private readonly InMemoryMessageLog _log = new();
    private readonly FakeAnalyzer _analyzer = new();
    private readonly FakeSummarizer _summarizer = new();
    private readonly List<(string Message, Exception? Error)> _logged = new();
    private ProfilerOptions _options = new();
    private readonly MessageIntake _intake;
    private readonly ProfileService _service;
    private readonly ProfilerEngine _engine;

    public ProfilerEngineTests()
    {
        // Intake capacity is fixed at construction, so keep it large.
        _options = new ProfilerOptions { QueueCapacity = 5000, SummaryThreshold = 1000 };
        _intake = new MessageIntake(() => _options);
        _service = new ProfileService(_store, _log);
        _engine = new ProfilerEngine(_intake, _service, _analyzer, _summarizer, () => _options, _time,
            (m, e) => { lock (_logged) _logged.Add((m, e)); });
    }

    public async ValueTask DisposeAsync() => await _engine.DisposeAsync();

    private ChatMessage M(CharacterKey sender, string text = "hello", ChatChannel channel = ChatChannel.Say) =>
        Msg(sender, text, channel, _time.GetUtcNow());

    private void Enqueue(CharacterKey sender, int count, string prefix = "m")
    {
        for (var i = 0; i < count; i++) Assert.True(_intake.Accept(M(sender, $"{prefix}{i}")));
    }

    private Task Cycle() => _engine.RunCycleAsync(CancellationToken.None);

    /// <summary>Advances fake time one second at a time until the condition holds (real-time cap of ~10s).</summary>
    private async Task AdvanceUntilAsync(Func<bool> condition, int maxSeconds = 2000)
    {
        for (var i = 0; i < maxSeconds && !condition(); i++)
        {
            await TickAsync(condition);
        }
        Assert.True(condition(), "condition was not met");
    }

    /// <summary>Advances fake time one second, then gives the loop a moment to react without a 15 ms timer wait.</summary>
    private async Task TickAsync(Func<bool>? until = null)
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3 && !(until?.Invoke() ?? false)) await Task.Yield();
    }

    // ---- cycles ----

    [Fact]
    public async Task Cycle_EmptyQueue_DoesNothing()
    {
        await Cycle();

        Assert.Empty(_analyzer.Calls);
        Assert.Empty(_summarizer.Requests);
        Assert.Equal(0, _store.Count());
        Assert.Equal(0, _store.UpsertManyCalls);
        var status = _engine.Status;
        Assert.Equal(0, status.Processed);
        Assert.Null(status.LastCycleUtc);
    }

    [Fact]
    public async Task Cycle_RecordsDeterministicStats_EvenWhenAnalyzerThrows()
    {
        _analyzer.Handler = FakeAnalyzer.AlwaysThrow();
        Enqueue(Alice, 3);

        await Cycle();

        var p = _service.Get(Alice)!;
        Assert.Equal(3, p.MessageCount);
        Assert.Equal(0, p.AnalyzedCount);
        Assert.Equal(3, p.ChannelCounts[ChatChannel.Say]);
        var status = _engine.Status;
        Assert.Equal(3, status.Processed);
        Assert.False(status.AnalyzerAvailable);
        Assert.NotNull(status.LastError);
        Assert.NotNull(status.LastCycleUtc);
    }

    [Fact]
    public async Task Cycle_ChunksMessagesByChunkSize()
    {
        _options = _options with { ClassificationChunkSize = 20 };
        Enqueue(Alice, 45);

        await Cycle();

        Assert.Equal(new[] { 20, 20, 5 }, _analyzer.Calls.Select(c => c.Count));
        Assert.Equal(45, _service.Get(Alice)!.AnalyzedCount);
        Assert.Equal(45, _engine.Status.Analyzed);
        // Order is preserved across chunks.
        Assert.Equal("m0", _analyzer.Calls[0][0].Text);
        Assert.Equal("m20", _analyzer.Calls[1][0].Text);
        Assert.Equal("m40", _analyzer.Calls[2][0].Text);
    }

    [Fact]
    public async Task Cycle_AppliesAnalysisToCorrectSender()
    {
        _analyzer.Handler = (_, chunk) => chunk
            .Select(m => m.Text == "grr"
                ? new MessageAnalysis(Tone.Hostile, Intent.Other, "en", new[] { "anger" })
                : new MessageAnalysis(Tone.Friendly, Intent.Socializing, "de", new[] { "hallo" }))
            .ToList();
        _intake.Accept(M(Alice, "grr"));
        _intake.Accept(M(Bob, "hi"));
        _intake.Accept(M(Alice, "grr"));

        await Cycle();

        var a = _service.Get(Alice)!;
        var b = _service.Get(Bob)!;
        Assert.Equal(2, a.ToneCounts[Tone.Hostile]);
        Assert.False(a.ToneCounts.ContainsKey(Tone.Friendly));
        Assert.Equal(2, a.TopicCounts["anger"]);
        Assert.Equal(1, b.ToneCounts[Tone.Friendly]);
        Assert.Equal(1, b.LanguageCounts["de"]);
        Assert.False(b.ToneCounts.ContainsKey(Tone.Hostile));
        Assert.Equal(1, _store.UpsertManyCalls);
    }

    [Fact]
    public async Task Cycle_AnalyzerFailure_SkipsRemainingChunks_AndCountsFailure()
    {
        _options = _options with { ClassificationChunkSize = 2 };
        var calls = 0;
        _analyzer.Handler = (n, chunk) =>
        {
            calls = n;
            return n == 1
                ? chunk.Select(_ => FakeAnalyzer.Friendly).ToList()
                : throw new AnalyzerUnavailableException("down");
        };
        Enqueue(Alice, 6);

        await Cycle();

        // Chunk 1 succeeded, chunk 2 failed, chunk 3 was skipped and is not retried.
        Assert.Equal(2, calls);
        var p = _service.Get(Alice)!;
        Assert.Equal(6, p.MessageCount);
        Assert.Equal(2, p.AnalyzedCount);
        Assert.False(_engine.Status.AnalyzerAvailable);
        Assert.Equal(0, _intake.Count);

        // Two more failing cycles take the failure count (1 + 2) to three, which opens the breaker.
        for (var i = 0; i < 2; i++)
        {
            Enqueue(Alice, 1);
            await Cycle();
        }
        var callsWhenOpen = _analyzer.Calls.Count;
        Enqueue(Alice, 1);
        await Cycle();
        Assert.Equal(callsWhenOpen, _analyzer.Calls.Count);
    }

    [Fact]
    public async Task Cycle_BreakerOpen_DoesNotCallAnalyzer()
    {
        _analyzer.Handler = FakeAnalyzer.AlwaysThrow();
        for (var i = 0; i < 3; i++)
        {
            Enqueue(Alice, 1);
            await Cycle();
        }
        Assert.Equal(3, _analyzer.Calls.Count);

        Enqueue(Alice, 4);
        await Cycle();

        Assert.Equal(3, _analyzer.Calls.Count);
        Assert.Equal(7, _service.Get(Alice)!.MessageCount); // deterministic stats still recorded
        Assert.False(_engine.Status.AnalyzerAvailable);

        // After the open period the analyser is tried again.
        _analyzer.Handler = (_, chunk) => chunk.Select(_ => FakeAnalyzer.Friendly).ToList();
        _time.Advance(TimeSpan.FromSeconds(61));
        Enqueue(Alice, 2);
        await Cycle();

        Assert.Equal(4, _analyzer.Calls.Count);
        Assert.True(_engine.Status.AnalyzerAvailable);
        Assert.Equal(2, _service.Get(Alice)!.AnalyzedCount);
    }

    [Fact]
    public async Task Cycle_StoreRawOff_WritesNoMessages()
    {
        _options = _options with { StoreRawMessages = false };
        Enqueue(Alice, 3);

        await Cycle();

        Assert.Equal(0, _log.AppendCalls);
        Assert.Empty(_log.All);
        Assert.Empty(_log.PurgeCutoffs);
    }

    [Fact]
    public async Task Cycle_StoreRawOn_AppendsMessagesWithAnalysis()
    {
        _options = _options with { StoreRawMessages = true, ClassificationChunkSize = 2 };
        _analyzer.Handler = (n, chunk) => n == 1
            ? chunk.Select(_ => FakeAnalyzer.Friendly).ToList()
            : throw new AnalyzerUnavailableException("down");
        _intake.Accept(M(Alice, "a1"));
        _intake.Accept(M(Bob, "b1"));
        _intake.Accept(M(Alice, "a2"));

        await Cycle();

        var logged = _log.All;
        Assert.Equal(new[] { "a1", "b1", "a2" }, logged.Select(l => l.Message.Text));
        Assert.Equal(FakeAnalyzer.Friendly, logged[0].Analysis);
        Assert.Equal(FakeAnalyzer.Friendly, logged[1].Analysis);
        Assert.Same(MessageAnalysis.Unknown, logged[2].Analysis); // chunk 2 failed
    }

    [Fact]
    public async Task Cycle_StoreRawOn_PurgesOldMessagesOncePerHour()
    {
        _options = _options with { StoreRawMessages = true, RawMessageRetentionDays = 30 };

        Enqueue(Alice, 1);
        await Cycle();
        Assert.Single(_log.PurgeCutoffs);
        Assert.Equal(_time.GetUtcNow() - TimeSpan.FromDays(30), _log.PurgeCutoffs[0]);

        _time.Advance(TimeSpan.FromMinutes(30));
        Enqueue(Alice, 1);
        await Cycle();
        Assert.Single(_log.PurgeCutoffs);

        _time.Advance(TimeSpan.FromMinutes(31));
        Enqueue(Alice, 1);
        await Cycle();
        Assert.Equal(2, _log.PurgeCutoffs.Count);
    }

    // ---- summaries ----

    [Fact]
    public async Task Cycle_SummarisesOnlyProfilesOverThreshold()
    {
        _options = _options with { SummaryThreshold = 5 };
        Enqueue(Alice, 6, "a");
        Enqueue(Bob, 3, "b");

        await Cycle();

        var request = Assert.Single(_summarizer.Requests);
        Assert.Equal(Alice, request.Character);
        Assert.Null(request.PreviousSummary);
        Assert.Equal(6, request.RecentMessages.Count);
        var a = _service.Get(Alice)!;
        Assert.Equal("summary of Alice Example", a.Summary);
        Assert.Equal(0, a.MessagesSinceSummary);
        Assert.Equal(_time.GetUtcNow(), a.SummaryUpdatedUtc);
        Assert.Null(_service.Get(Bob)!.Summary);
        Assert.Equal(3, _service.Get(Bob)!.MessagesSinceSummary);
        Assert.Empty(_service.Pending.Take(Alice));
        Assert.Equal(3, _service.Pending.Take(Bob).Count);
    }

    [Fact]
    public async Task Cycle_LimitsSummariesPerCycle_HighestBacklogFirst()
    {
        _options = _options with { SummaryThreshold = 5, MaxSummariesPerCycle = 2 };
        var people = new[] { ("Low Five", 5), ("High Nine", 9), ("Mid Seven", 7), ("Six Six", 6) };
        foreach (var (name, count) in people) Enqueue(Key(name), count);

        await Cycle();

        Assert.Equal(new[] { "High Nine", "Mid Seven" }, _summarizer.Requests.Select(r => r.Character.Name));
        Assert.NotNull(_service.Get(Key("High Nine"))!.Summary);
        Assert.NotNull(_service.Get(Key("Mid Seven"))!.Summary);
        Assert.Null(_service.Get(Key("Six Six"))!.Summary);
        Assert.Equal(6, _service.Get(Key("Six Six"))!.MessagesSinceSummary);
    }

    [Fact]
    public async Task Cycle_SummaryFailure_KeepsOldSummaryAndCounter()
    {
        _options = _options with { SummaryThreshold = 5 };
        await _service.MutateAsync(Alice, p =>
        {
            p.Summary = "old summary";
            p.SummaryUpdatedUtc = _time.GetUtcNow().AddDays(-1);
        });
        _summarizer.Handler = _ => throw new AnalyzerUnavailableException("model gone");
        Enqueue(Alice, 6, "a");
        Enqueue(Bob, 6, "b");

        await Cycle();

        // The first failure stops further summaries this cycle.
        Assert.Single(_summarizer.Requests);
        var a = _service.Get(Alice)!;
        Assert.Equal("old summary", a.Summary);
        Assert.Equal(6, a.MessagesSinceSummary);
        Assert.Equal(6, _service.Get(Bob)!.MessagesSinceSummary);
        Assert.Equal(6, _service.Pending.Take(Alice).Count); // buffer kept for the next attempt
        Assert.NotNull(_engine.Status.LastError);
    }

    [Fact]
    public async Task Cycle_RespectsMaxMessagesPerCycle_LeavingRestQueued()
    {
        _options = _options with { MaxMessagesPerCycle = 10 };
        Enqueue(Alice, 25);

        await Cycle();

        Assert.Equal(10, _service.Get(Alice)!.MessageCount);
        Assert.Equal(15, _intake.Count);
        Assert.Equal(15, _engine.Status.QueueDepth);
        Assert.Equal(10, _engine.Status.Processed);

        await Cycle();
        Assert.Equal(20, _service.Get(Alice)!.MessageCount);
    }

    // ---- background loop ----

    [Fact]
    public async Task Loop_RunsCycleAfterBatchInterval()
    {
        _options = _options with { BatchInterval = TimeSpan.FromSeconds(60), EarlyTriggerQueueDepth = 1000 };
        Enqueue(Alice, 3);
        _engine.Start();

        // Well before the interval nothing has run.
        for (var i = 0; i < 30; i++)
        {
            await TickAsync();
        }
        Assert.Equal(0, _engine.Status.Processed);

        await AdvanceUntilAsync(() => _engine.Status.Processed == 3);

        Assert.Equal(3, _service.Get(Alice)!.MessageCount);
        Assert.True(_time.GetUtcNow() - new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero) >= TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Loop_RunsEarly_WhenQueueDepthReachesTrigger()
    {
        _options = _options with { BatchInterval = TimeSpan.FromMinutes(10), EarlyTriggerQueueDepth = 100 };
        Enqueue(Alice, 99);
        _engine.Start();

        for (var i = 0; i < 10; i++)
        {
            await TickAsync();
        }
        Assert.Equal(0, _engine.Status.Processed);

        Enqueue(Alice, 1);
        await AdvanceUntilAsync(() => _engine.Status.Processed == 100, maxSeconds: 20);

        Assert.Equal(100, _service.Get(Alice)!.MessageCount);
    }

    [Fact]
    public async Task Loop_SurvivesCycleException_AndReportsLastError()
    {
        _options = _options with { BatchInterval = TimeSpan.FromSeconds(10), EarlyTriggerQueueDepth = 1000 };
        _store.ThrowOnNextUpsertMany = new InvalidOperationException("disk full");
        Enqueue(Alice, 2);
        _engine.Start();

        await AdvanceUntilAsync(() => _engine.Status.LastError is not null, maxSeconds: 60);

        Assert.Contains("disk full", _engine.Status.LastError);
        lock (_logged) Assert.Contains(_logged, l => l.Error is InvalidOperationException);

        // The loop keeps going and processes the next batch.
        Enqueue(Bob, 2);
        await AdvanceUntilAsync(() => _service.Get(Bob)?.MessageCount == 2, maxSeconds: 60);
    }

    [Fact]
    public async Task Dispose_StopsLoopWithinTimeout()
    {
        _engine.Start();
        await Task.Delay(20);

        var dispose = _engine.DisposeAsync().AsTask();
        var winner = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(dispose, winner);
        await dispose;

        // Further disposal is harmless, and the engine cannot be restarted.
        await _engine.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => _engine.Start());
    }
}
