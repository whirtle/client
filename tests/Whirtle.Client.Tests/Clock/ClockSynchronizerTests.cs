using Whirtle.Client.Clock;
using Whirtle.Client.Protocol;
using Whirtle.Client.Tests.Protocol;

namespace Whirtle.Client.Tests.Clock;

public class ClockSynchronizerTests
{
    private static (ClockSynchronizer syncer, FakeClock clock, FakeTransport transport) Build(
        TimeSpan? syncTimeout       = null,
        TimeSpan? rapidSyncInterval = null)
    {
        var transport = new FakeTransport();
        var client    = new ProtocolClient(transport);
        var clock     = new FakeClock();
        return (new ClockSynchronizer(client, clock, syncTimeout, rapidSyncInterval), clock, transport);
    }

    // ── SyncOnceAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SyncOnceAsync_ReturnsCorrectRtt()
    {
        // t0 = 0 μs, t2 = 200 μs → RTT = 200 μs
        var (syncer, clock, _) = Build();
        clock.Set(0);
        var syncTask = syncer.SyncOnceAsync();
        clock.Set(200);
        syncer.Deliver(new ServerTimeMessage(ClientTransmitted: 0, ServerReceived: 100));

        var result = await syncTask;

        Assert.Equal(TimeSpan.FromMicroseconds(200), result.RoundTripTime);
    }

    [Fact]
    public async Task SyncOnceAsync_ZeroOffset_WhenClocksAgree()
    {
        // Symmetric RTT: t0=0, t1=100, t2=200 μs
        // offset = t1 - t0 - RTT/2 = 100 - 0 - 100 = 0
        var (syncer, clock, _) = Build();
        clock.Set(0);
        var syncTask = syncer.SyncOnceAsync();
        clock.Set(200);
        syncer.Deliver(new ServerTimeMessage(0, 100));

        var result = await syncTask;

        Assert.Equal(TimeSpan.Zero, result.ClockOffset);
    }

    [Fact]
    public async Task SyncOnceAsync_PositiveOffset_WhenServerAhead()
    {
        // t0=0, t1=600, t2=200 μs → RTT=200, offset = 600 - 0 - 100 = +500 μs
        var (syncer, clock, _) = Build();
        clock.Set(0);
        var syncTask = syncer.SyncOnceAsync();
        clock.Set(200);
        syncer.Deliver(new ServerTimeMessage(0, 600));

        var result = await syncTask;

        Assert.Equal(TimeSpan.FromMicroseconds(500), result.ClockOffset);
    }

    [Fact]
    public async Task SyncOnceAsync_NegativeOffset_WhenClientAhead()
    {
        // t0=0, t1=50, t2=200 μs → RTT=200, offset = 50 - 0 - 100 = -50 μs
        var (syncer, clock, _) = Build();
        clock.Set(0);
        var syncTask = syncer.SyncOnceAsync();
        clock.Set(200);
        syncer.Deliver(new ServerTimeMessage(0, 50));

        var result = await syncTask;

        Assert.Equal(TimeSpan.FromMicroseconds(-50), result.ClockOffset);
    }

    [Fact]
    public async Task SyncOnceAsync_TimeoutBeforeDeliver_Throws()
    {
        var (syncer, _, _) = Build(syncTimeout: TimeSpan.FromMilliseconds(1));

        var ex = await Record.ExceptionAsync(() => syncer.SyncOnceAsync());
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    // ── Deliver ───────────────────────────────────────────────────────────────

    [Fact]
    public void Deliver_ReturnsFalse_WhenNoPendingSync()
    {
        var (syncer, _, _) = Build();

        var delivered = syncer.Deliver(new ServerTimeMessage(0, 100));

        Assert.False(delivered);
    }

    [Fact]
    public async Task Deliver_ReturnsTrue_WhenSyncIsPending()
    {
        var (syncer, clock, _) = Build();
        clock.Set(0);
        var syncTask = syncer.SyncOnceAsync();

        var delivered = syncer.Deliver(new ServerTimeMessage(0, 100));
        await syncTask;

        Assert.True(delivered);
    }

    // ── RunAsync ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_InvokesCallbackForEachDeliveredSync()
    {
        var (syncer, clock, _) = Build();
        var results = new List<ClockSyncResult>();
        using var cts = new CancellationTokenSource();

        clock.Set(0);
        var runTask = syncer.RunAsync(
            results.Add,
            interval: TimeSpan.FromMilliseconds(10),
            cancellationToken: cts.Token);

        // Drive two sync rounds: wait until a pending sync is registered, then deliver.
        for (var round = 0; round < 2; round++)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!syncer.Deliver(new ServerTimeMessage(0, 100)))
                await Task.Delay(1, deadline.Token);
            await Task.Delay(20); // wait past the inter-round interval so RunAsync loops
        }

        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => runTask);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task RunAsync_PerformsRapidSyncs_BeforeSteadyState()
    {
        // RunAsync must complete 3 rapid syncs before entering the steady-state loop.
        // We use a very long steady-state interval so that if the rapid phase is skipped
        // or delayed the test would time out waiting for the 3rd callback.
        var (syncer, clock, _) = Build(rapidSyncInterval: TimeSpan.FromMilliseconds(5));
        var results = new List<ClockSyncResult>();
        using var cts = new CancellationTokenSource();

        clock.Set(0);
        var runTask = syncer.RunAsync(
            results.Add,
            interval: TimeSpan.FromHours(1), // steady-state never fires within the test
            cancellationToken: cts.Token);

        // Deliver 3 replies to satisfy the rapid phase.
        for (int i = 0; i < 3; i++)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!syncer.Deliver(new ServerTimeMessage(0, 100)))
                await Task.Delay(1, deadline.Token);
            await Task.Delay(10); // give RunAsync time to advance between rounds
        }

        // All 3 rapid-phase callbacks must have fired well before the 1-hour steady-state delay.
        Assert.Equal(3, results.Count);

        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => runTask);
    }

    [Fact]
    public void AcceptResult_ReturnsMinRttSample_NotMostRecent()
    {
        // AcceptResult maintains a rolling window and returns the entry with the
        // lowest RTT (NTP min-RTT heuristic) rather than the most recent entry.
        var (syncer, _, _) = Build();

        static ClockSyncResult Sample(long rttUs, long offsetUs) =>
            new(TimeSpan.FromMicroseconds(offsetUs), TimeSpan.FromMicroseconds(rttUs));

        // First sample: only entry, returned as-is.
        var r1 = syncer.AcceptResult(Sample(rttUs: 200, offsetUs: 400));
        Assert.Equal(TimeSpan.FromMicroseconds(200), r1.RoundTripTime);

        // Second sample has lower RTT — becomes the new winner.
        var r2 = syncer.AcceptResult(Sample(rttUs: 10, offsetUs: 90));
        Assert.Equal(TimeSpan.FromMicroseconds(10),  r2.RoundTripTime);
        Assert.Equal(TimeSpan.FromMicroseconds(90),  r2.ClockOffset);

        // Third sample has high RTT again — window still contains the 10 μs sample.
        var r3 = syncer.AcceptResult(Sample(rttUs: 200, offsetUs: 400));
        Assert.Equal(TimeSpan.FromMicroseconds(10),  r3.RoundTripTime);
        Assert.Equal(TimeSpan.FromMicroseconds(90),  r3.ClockOffset);
    }

    [Fact]
    public void AcceptResult_DiscardsHighRttOutlier()
    {
        // A sample whose RTT exceeds 2× the window median is discarded: it is not
        // added to the window and the existing best is returned unchanged.
        var (syncer, _, _) = Build();

        static ClockSyncResult Sample(long rttUs, long offsetUs) =>
            new(TimeSpan.FromMicroseconds(offsetUs), TimeSpan.FromMicroseconds(rttUs));

        // Seed window with two low-RTT samples (median = 100 μs).
        syncer.AcceptResult(Sample(rttUs: 100, offsetUs: 50));
        syncer.AcceptResult(Sample(rttUs: 100, offsetUs: 50));

        // An outlier at 2001 μs (> 2×100) must be rejected.
        var result = syncer.AcceptResult(Sample(rttUs: 2001, offsetUs: 999));

        // Window is unchanged — still has 2 entries at 100 μs RTT.
        Assert.Equal(TimeSpan.FromMicroseconds(100), result.RoundTripTime);
        Assert.Equal(TimeSpan.FromMicroseconds(50),  result.ClockOffset);
    }

    [Fact]
    public void AcceptResult_AcceptsModerateRttWithinGate()
    {
        // A sample at exactly 2× median is within the gate (not strictly greater) and
        // should be accepted into the window.
        var (syncer, _, _) = Build();

        static ClockSyncResult Sample(long rttUs, long offsetUs) =>
            new(TimeSpan.FromMicroseconds(offsetUs), TimeSpan.FromMicroseconds(rttUs));

        syncer.AcceptResult(Sample(rttUs: 100, offsetUs: 50));
        syncer.AcceptResult(Sample(rttUs: 100, offsetUs: 50));

        // Exactly 2× median (200 μs) — accepted.
        var result = syncer.AcceptResult(Sample(rttUs: 200, offsetUs: 80));

        // The new sample has higher RTT so it doesn't become the min-RTT winner, but
        // it is in the window (no discarding occurred).
        Assert.Equal(TimeSpan.FromMicroseconds(100), result.RoundTripTime);
    }

    [Fact]
    public void AcceptResult_EvictsOldestWhenWindowFull()
    {
        // After the window fills (capacity = 8) the oldest sample is evicted.
        // Once the best (low-RTT) sample ages out, the next-best takes over.
        var (syncer, _, _) = Build();

        static ClockSyncResult Sample(long rttUs, long offsetUs) =>
            new(TimeSpan.FromMicroseconds(offsetUs), TimeSpan.FromMicroseconds(rttUs));

        // Fill window with one low-RTT sample followed by 7 high-RTT samples.
        var best = syncer.AcceptResult(Sample(rttUs: 5, offsetUs: 50));
        Assert.Equal(TimeSpan.FromMicroseconds(5), best.RoundTripTime);

        for (int i = 0; i < 6; i++)
            syncer.AcceptResult(Sample(rttUs: 200, offsetUs: 400));

        // 7 samples in: best is still the 5 μs one.
        var stillBest = syncer.AcceptResult(Sample(rttUs: 200, offsetUs: 400));
        Assert.Equal(TimeSpan.FromMicroseconds(5), stillBest.RoundTripTime);

        // 8th high-RTT sample evicts the 5 μs sample (window = 8 entries).
        var evicted = syncer.AcceptResult(Sample(rttUs: 200, offsetUs: 400));
        Assert.Equal(TimeSpan.FromMicroseconds(200), evicted.RoundTripTime);
    }

    [Fact]
    public async Task RunAsync_RetriesAfterTimeout_UntilCancelled()
    {
        // With a 1 ms sync timeout and no server delivering replies,
        // RunAsync should absorb the repeated timeouts and exit cleanly on cancel.
        var (syncer, _, _) = Build(syncTimeout: TimeSpan.FromMilliseconds(1));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => syncer.RunAsync(_ => { }, interval: TimeSpan.Zero, cts.Token));
    }
}
