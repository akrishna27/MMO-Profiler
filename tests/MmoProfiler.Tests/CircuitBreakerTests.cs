using Microsoft.Extensions.Time.Testing;
using MmoProfiler.Core;

namespace MmoProfiler.Tests;

public class CircuitBreakerTests
{
    private readonly FakeTimeProvider _time = new();

    private CircuitBreaker Create() => new(_time);

    private static void Fail(CircuitBreaker b, int times)
    {
        for (var i = 0; i < times; i++) b.RecordFailure();
    }

    [Fact]
    public void OpensAfterThreeConsecutiveFailures()
    {
        var b = Create();
        Assert.False(b.IsOpen);

        Fail(b, 2);
        Assert.False(b.IsOpen);

        b.RecordFailure();
        Assert.True(b.IsOpen);
    }

    [Fact]
    public void SuccessResetsFailureCount()
    {
        var b = Create();
        Fail(b, 2);
        b.RecordSuccess();

        Fail(b, 2);

        Assert.False(b.IsOpen);
        b.RecordFailure();
        Assert.True(b.IsOpen);
    }

    [Fact]
    public void ClosesAfterOpenPeriodElapses()
    {
        var b = Create();
        Fail(b, 3);

        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.True(b.IsOpen);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(b.IsOpen);
    }

    [Fact]
    public void OpenPeriodDoubles_UpToTenMinutes()
    {
        var b = Create();
        Fail(b, 3);
        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.False(b.IsOpen);

        // Each failure after the period elapses re-opens immediately with double the period.
        foreach (var seconds in new[] { 120, 240, 480, 600, 600 })
        {
            b.RecordFailure();
            Assert.True(b.IsOpen);

            _time.Advance(TimeSpan.FromSeconds(seconds - 1));
            Assert.True(b.IsOpen);

            _time.Advance(TimeSpan.FromSeconds(1));
            Assert.False(b.IsOpen);
        }
    }

    [Fact]
    public void SuccessResetsOpenPeriod()
    {
        var b = Create();
        Fail(b, 3);
        _time.Advance(TimeSpan.FromSeconds(60));
        b.RecordFailure(); // second open: 120s
        _time.Advance(TimeSpan.FromSeconds(120));

        b.RecordSuccess();
        Assert.False(b.IsOpen);
        Fail(b, 3);

        Assert.True(b.IsOpen);
        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.False(b.IsOpen); // back to the 60 second period
    }
}
