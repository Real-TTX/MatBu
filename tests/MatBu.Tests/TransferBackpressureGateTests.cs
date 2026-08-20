using MatBu.Services;

namespace MatBu.Tests;

public sealed class TransferBackpressureGateTests
{
    [Fact]
    public void MustWait_PausesWhenBacklogReachesHighWatermark()
    {
        var gate = new TransferBackpressureGate(highWatermark: 1000, lowWatermark: 200);

        Assert.False(gate.MustWait(500));   // backlog 500 < high 1000 -> keep producing
        Assert.False(gate.IsPaused);

        Assert.True(gate.MustWait(1000));   // backlog 1000 >= high -> pause
        Assert.True(gate.IsPaused);
    }

    [Fact]
    public void MustWait_ResumesOnlyAfterDrainingBelowLowWatermark()
    {
        var gate = new TransferBackpressureGate(highWatermark: 1000, lowWatermark: 200);

        Assert.True(gate.MustWait(1000));   // paused
        Assert.True(gate.IsPaused);

        // Consumer drains, but backlog (1000 - 500 = 500) is still above the low watermark: stay paused.
        gate.ReportConsumed(500);
        Assert.True(gate.MustWait(1000));
        Assert.True(gate.IsPaused);

        // Consumer catches up: backlog (1000 - 850 = 150) drops to/below low -> resume.
        gate.ReportConsumed(850);
        Assert.False(gate.MustWait(1000));
        Assert.False(gate.IsPaused);
    }

    [Fact]
    public void ReportConsumed_NeverRegresses()
    {
        var gate = new TransferBackpressureGate(highWatermark: 1000, lowWatermark: 200);
        gate.ReportConsumed(800);
        gate.ReportConsumed(300); // stale/out-of-order report must be ignored
        Assert.Equal(800, gate.ConsumedOffset);
    }

    [Fact]
    public void WaitForCapacity_ReturnsImmediatelyWhenBacklogIsLow()
    {
        var gate = new TransferBackpressureGate(highWatermark: 1000, lowWatermark: 200);
        // Should not block: backlog 100 < high.
        gate.WaitForCapacity(100, CancellationToken.None);
        Assert.False(gate.IsPaused);
    }

    [Fact]
    public void WaitForCapacity_HonorsCancellationWhileThrottled()
    {
        var gate = new TransferBackpressureGate(highWatermark: 1000, lowWatermark: 200);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // Backlog 1000 forces a wait; a cancelled token must break out rather than spin forever.
        Assert.Throws<OperationCanceledException>(() => gate.WaitForCapacity(1000, cts.Token));
    }

    // Regression guard for the StreamLocalSourceToTargetAsync deadlock: a producer that crosses the high
    // watermark stalls forever UNLESS the consumer advances ReportConsumed. This is exactly the invariant
    // that broke when the throttle was wired in but ReportConsumed was not called on the local-stream path.
    [Fact]
    public void Producer_StallsForever_WhenConsumerNeverReports()
    {
        var gate = new TransferBackpressureGate(highWatermark: 1000, lowWatermark: 200, pollMilliseconds: 5);
        using var cts = new CancellationTokenSource();
        var producer = Task.Run(() =>
        {
            try { for (long produced = 100; produced <= 1_000_000; produced += 100) gate.WaitForCapacity(produced, cts.Token); }
            catch (OperationCanceledException) { }
        });

        Assert.False(producer.Wait(TimeSpan.FromMilliseconds(500)), "producer must stall once backlog exceeds the high watermark and nothing is consumed");
        Assert.True(gate.IsPaused);

        cts.Cancel();
        try { producer.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    [Fact]
    public void Producer_IsReleased_WhenConsumerReportsInLockstep()
    {
        var gate = new TransferBackpressureGate(highWatermark: 1000, lowWatermark: 200, pollMilliseconds: 5);
        using var cts = new CancellationTokenSource();
        var producer = Task.Run(() =>
        {
            for (long produced = 100; produced <= 5000; produced += 100) gate.WaitForCapacity(produced, cts.Token);
        });
        var consumer = Task.Run(async () =>
        {
            long consumed = 0;
            while (!producer.IsCompleted && !cts.IsCancellationRequested)
            {
                await Task.Delay(5, cts.Token);
                consumed += 400;
                gate.ReportConsumed(consumed);
            }
        });

        Assert.True(producer.Wait(TimeSpan.FromSeconds(5)), "producer must finish once the consumer keeps draining the backlog");
        cts.Cancel();
        try { consumer.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
}
