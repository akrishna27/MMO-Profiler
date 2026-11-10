namespace MmoProfiler.Core;

public sealed record EngineStatus(
    int QueueDepth, long Dropped, long Processed, long Analyzed,
    bool AnalyzerAvailable, DateTimeOffset? LastCycleUtc, string? LastError);

/// <summary>Background worker: drains the intake, updates profiles, asks the model for labels and summaries.</summary>
public sealed class ProfilerEngine : IAsyncDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(2);

    private readonly MessageIntake _intake;
    private readonly ProfileService _profiles;
    private readonly IMessageAnalyzer _analyzer;
    private readonly IProfileSummarizer _summarizer;
    private readonly Func<ProfilerOptions> _options;
    private readonly TimeProvider _time;
    private readonly Action<string, Exception?>? _log;
    private readonly CircuitBreaker _breaker;
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly object _statusGate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    private long _processed;
    private long _analyzed;
    private bool _analyzerAvailable = true;
    private DateTimeOffset? _lastCycleUtc;
    private string? _lastError;
    private DateTimeOffset? _lastPurgeUtc;

    public ProfilerEngine(
        MessageIntake intake, ProfileService profiles,
        IMessageAnalyzer analyzer, IProfileSummarizer summarizer,
        Func<ProfilerOptions> options, TimeProvider time, Action<string, Exception?>? log = null)
    {
        _intake = intake ?? throw new ArgumentNullException(nameof(intake));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _summarizer = summarizer ?? throw new ArgumentNullException(nameof(summarizer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _log = log;
        _breaker = new CircuitBreaker(time);
    }

    public EngineStatus Status
    {
        get
        {
            lock (_statusGate)
            {
                return new EngineStatus(
                    _intake.Count, _intake.DroppedCount, _processed, _analyzed,
                    _analyzerAvailable, _lastCycleUtc, _lastError);
            }
        }
    }

    /// <summary>Starts the background loop. Calling it again has no effect.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => LoopAsync(token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var lastCycle = _time.GetUtcNow();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Tick, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var o = _options();
                var depth = _intake.Count;
                if (depth == 0) continue;

                var now = _time.GetUtcNow();
                if (now - lastCycle < o.BatchInterval && depth < o.EarlyTriggerQueueDepth) continue;

                lastCycle = now;
                await RunCycleAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetError(ex.Message);
                _log?.Invoke("Profiler cycle failed.", ex);
            }
        }
    }

    /// <summary>Runs one cycle. Cycles never overlap; a second caller waits for the first.</summary>
    public async Task RunCycleAsync(CancellationToken ct)
    {
        await _cycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RunCycleCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    private async Task RunCycleCoreAsync(CancellationToken ct)
    {
        var o = _options();

        // 1. Drain.
        var batch = new List<ChatMessage>();
        _intake.Drain(batch, o.MaxMessagesPerCycle);
        if (batch.Count == 0) return;

        // 2. Deterministic stats, recorded whether or not the model is available.
        var perProfile = new Dictionary<CharacterKey, List<Action<Profile>>>();
        foreach (var m in batch)
        {
            Queue(perProfile, m.Sender, p => ProfileUpdater.RecordMessage(p, m));
            _profiles.Pending.Add(m);
        }

        // 3. Classification, chunk by chunk.
        var analyses = new MessageAnalysis[batch.Count];
        Array.Fill(analyses, MessageAnalysis.Unknown);
        long analyzedThisCycle = 0;

        if (_breaker.IsOpen)
        {
            SetAnalyzerAvailable(false);
        }
        else
        {
            var chunkSize = Math.Max(1, o.ClassificationChunkSize);
            for (var start = 0; start < batch.Count; start += chunkSize)
            {
                var chunk = batch.GetRange(start, Math.Min(chunkSize, batch.Count - start));
                IReadOnlyList<MessageAnalysis> results;
                try
                {
                    results = await _analyzer.AnalyzeAsync(chunk, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _breaker.RecordFailure();
                    SetAnalyzerAvailable(false);
                    SetError("Analyzer: " + ex.Message);
                    _log?.Invoke("Message analysis failed; skipping the rest of this cycle's chunks.", ex);
                    break; // skipped messages are not retried
                }

                _breaker.RecordSuccess();
                SetAnalyzerAvailable(true);

                for (var i = 0; i < chunk.Count && i < results.Count; i++)
                {
                    var analysis = results[i] ?? MessageAnalysis.Unknown;
                    analyses[start + i] = analysis;
                    if (analysis.Tone != Tone.Unknown || analysis.Intent != Intent.Unknown) analyzedThisCycle++;
                    var sender = chunk[i].Sender;
                    Queue(perProfile, sender, p => ProfileUpdater.ApplyAnalysis(p, analysis));
                }
            }
        }

        // 4. Apply everything with one batched save.
        var combined = new Dictionary<CharacterKey, Action<Profile>>(perProfile.Count);
        foreach (var (key, actions) in perProfile)
        {
            combined[key] = p =>
            {
                foreach (var a in actions) a(p);
            };
        }
        await _profiles.ApplyBatchAsync(combined, ct).ConfigureAwait(false);

        // 5. Optional raw message log and hourly retention purge.
        var now = _time.GetUtcNow();
        if (o.StoreRawMessages && _profiles.MessageLog is { } log)
        {
            log.Append(batch.Select((m, i) => new LoggedMessage(m, analyses[i])).ToList());

            if (_lastPurgeUtc is null || now - _lastPurgeUtc.Value >= PurgeInterval)
            {
                log.DeleteOlderThan(now - TimeSpan.FromDays(o.RawMessageRetentionDays));
                _lastPurgeUtc = now;
            }
        }

        lock (_statusGate)
        {
            _processed += batch.Count;
            _analyzed += analyzedThisCycle;
            _lastCycleUtc = now;
        }

        // 6. Summaries.
        await SummariseAsync(perProfile.Keys, o, ct).ConfigureAwait(false);

        lock (_statusGate)
        {
            if (_analyzerAvailable && _lastError is not null && _lastError.StartsWith("Analyzer:", StringComparison.Ordinal))
                _lastError = null;
        }
    }

    private async Task SummariseAsync(IEnumerable<CharacterKey> touched, ProfilerOptions o, CancellationToken ct)
    {
        if (_breaker.IsOpen) return;

        var due = new List<Profile>();
        foreach (var key in touched)
        {
            var profile = _profiles.Get(key);
            if (profile is not null && profile.MessagesSinceSummary >= o.SummaryThreshold) due.Add(profile);
        }

        foreach (var profile in due
                     .OrderByDescending(p => p.MessagesSinceSummary)
                     .Take(Math.Max(0, o.MaxSummariesPerCycle)))
        {
            var recent = _profiles.Pending.Take(profile.Key);
            string text;
            try
            {
                text = await _summarizer
                    .SummarizeAsync(new SummaryRequest(profile.Key, profile.Summary, recent), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _breaker.RecordFailure();
                SetAnalyzerAvailable(false);
                SetError("Summariser: " + ex.Message);
                _log?.Invoke("Summary generation failed; skipping remaining summaries this cycle.", ex);
                return;
            }

            _breaker.RecordSuccess();
            var now = _time.GetUtcNow();
            await _profiles.MutateAsync(profile.Key, p => ProfileUpdater.ApplySummary(p, text, now), ct)
                .ConfigureAwait(false);
            _profiles.Pending.Clear(profile.Key);
        }
    }

    private static void Queue(
        Dictionary<CharacterKey, List<Action<Profile>>> map, CharacterKey key, Action<Profile> action)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<Action<Profile>>();
        list.Add(action);
    }

    private void SetAnalyzerAvailable(bool value)
    {
        lock (_statusGate) _analyzerAvailable = value;
    }

    private void SetError(string message)
    {
        lock (_statusGate) _lastError = message;
    }

    /// <summary>Stops the loop, waiting at most two seconds for an in-flight cycle. Queued messages are discarded.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        var cts = _cts;
        var loop = _loop;
        if (cts is null) return;

        cts.Cancel();
        var finished = true;
        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(DisposeTimeout, _time).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                finished = false;
                _log?.Invoke("Profiler engine did not stop within 2 seconds.", null);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (finished) cts.Dispose();
    }
}
