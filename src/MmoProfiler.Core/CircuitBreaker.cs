namespace MmoProfiler.Core;

/// <summary>
/// Stops calls to a failing backend. Three consecutive failures open it for 60 seconds;
/// each consecutive re-open doubles the period up to 10 minutes. Success resets everything.
/// </summary>
public sealed class CircuitBreaker
{
    public const int FailureThreshold = 3;
    public static readonly TimeSpan InitialOpenPeriod = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxOpenPeriod = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private int _failures;
    private TimeSpan? _period;
    private DateTimeOffset? _openUntil;

    public CircuitBreaker(TimeProvider time) => _time = time ?? throw new ArgumentNullException(nameof(time));

    public bool IsOpen
    {
        get
        {
            lock (_gate) return IsOpenLocked();
        }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            if (IsOpenLocked()) return; // callers should not call while open; do not double the period twice

            _failures++;
            if (_failures < FailureThreshold) return;

            // Once the failure count has reached the threshold, every further failure re-opens at once.
            _period = _period is null
                ? InitialOpenPeriod
                : TimeSpan.FromTicks(Math.Min(_period.Value.Ticks * 2, MaxOpenPeriod.Ticks));
            _openUntil = _time.GetUtcNow() + _period.Value;
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _failures = 0;
            _period = null;
            _openUntil = null;
        }
    }

    private bool IsOpenLocked() => _openUntil is { } until && _time.GetUtcNow() < until;
}
