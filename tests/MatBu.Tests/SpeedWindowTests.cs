using System.Diagnostics;
using MatBu.Services;

namespace MatBu.Tests;

public sealed class SpeedWindowTests
{
    [Fact]
    public void Sample_ReportsInstantaneousRateNotCumulativeAverage()
    {
        var window = new SpeedWindow(windowSeconds: 1.0);

        // Simulate a slow start followed by a fast burst. A cumulative average would still look slow;
        // the sliding window should reflect the recent fast rate.
        window.Sample(0);
        Spin(TimeSpan.FromMilliseconds(300));
        window.Sample(1_000);          // ~3 KB/s so far
        Spin(TimeSpan.FromMilliseconds(300));
        var fast = window.Sample(10_000_000); // huge burst in the last 300 ms

        Assert.True(fast > 1_000_000, $"expected a high instantaneous rate, got {fast} B/s");
    }

    [Fact]
    public void Sample_ReturnsZeroWhenNoProgress()
    {
        var window = new SpeedWindow(windowSeconds: 1.0);
        window.Sample(5_000);
        Spin(TimeSpan.FromMilliseconds(50));
        Assert.Equal(0, window.Sample(5_000));
    }

    private static void Spin(TimeSpan duration)
    {
        var until = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(50);
    }
}
