namespace MatBu.Services;

/// <summary>
/// Disk-buffered flow control for one streaming transfer. The producer (archive build) reports how many
/// bytes it has written to the source cache; the consumer (upload / target sync) reports how many it has
/// drained. When the unconsumed backlog exceeds the high watermark the producer is held back until the
/// consumer drains it below the low watermark (hysteresis), preventing the cache from outgrowing the
/// transfer and exhausting the spool disk.
/// </summary>
public sealed class TransferBackpressureGate(long highWatermark, long lowWatermark, int pollMilliseconds = 50)
{
    private long _consumed;
    private long _paused;

    public bool IsPaused => Interlocked.Read(ref _paused) != 0;
    public long ConsumedOffset => Interlocked.Read(ref _consumed);

    /// <summary>Advance the consumed offset monotonically (never regresses).</summary>
    public void ReportConsumed(long offset)
    {
        long previous;
        do
        {
            previous = Interlocked.Read(ref _consumed);
            if (offset <= previous) return;
        }
        while (Interlocked.CompareExchange(ref _consumed, offset, previous) != previous);
    }

    /// <summary>
    /// Decide whether the producer must keep waiting for the given produced total, applying hysteresis:
    /// pause once the backlog reaches the high watermark, resume only once it falls to/below the low one.
    /// </summary>
    public bool MustWait(long producedBytes)
    {
        var backlog = producedBytes - Interlocked.Read(ref _consumed);
        var paused = Interlocked.Read(ref _paused) != 0;
        if (!paused && backlog < highWatermark) return false;
        if (paused && backlog <= lowWatermark)
        {
            Interlocked.Exchange(ref _paused, 0);
            return false;
        }
        Interlocked.Exchange(ref _paused, 1);
        return true;
    }

    /// <summary>Block the producing thread until the backlog is acceptable.</summary>
    public void WaitForCapacity(long producedBytes, CancellationToken cancellationToken)
    {
        while (MustWait(producedBytes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(pollMilliseconds);
        }
    }
}
