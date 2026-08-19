using System.Diagnostics;

namespace MatBu.Services;

/// <summary>
/// Computes an <em>instantaneous</em> throughput ("wie schnell gerade") from a series of cumulative
/// byte counts, using a sliding time window instead of the total-since-start average. Thread-safe for
/// a single producer; each transfer stage keeps its own instance.
/// </summary>
public sealed class SpeedWindow(double windowSeconds = 3.0)
{
    private readonly Queue<(long Ticks, long Bytes)> _samples = new();
    private readonly long _windowTicks = (long)(Math.Max(0.5, windowSeconds) * Stopwatch.Frequency);

    /// <summary>Record the current cumulative byte total and return the speed in bytes/second over the window.</summary>
    public long Sample(long cumulativeBytes)
    {
        var now = Stopwatch.GetTimestamp();
        _samples.Enqueue((now, cumulativeBytes));
        var cutoff = now - _windowTicks;
        while (_samples.Count > 2 && _samples.Peek().Ticks < cutoff) _samples.Dequeue();
        var oldest = _samples.Peek();
        var elapsedSeconds = (now - oldest.Ticks) / (double)Stopwatch.Frequency;
        if (elapsedSeconds <= 0.0001) return 0;
        var delta = cumulativeBytes - oldest.Bytes;
        return delta <= 0 ? 0 : (long)(delta / elapsedSeconds);
    }
}
